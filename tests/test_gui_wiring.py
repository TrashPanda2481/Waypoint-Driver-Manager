"""Headless test of the GUI's scan wiring: MainWindow -> QThread -> ScanWorker
-> WaypointEngine.scan() -> render_assessments(). Runs with the mock backend
so it's deterministic and doesn't require Windows/real hardware or a real
display (uses the Qt "offscreen" platform plugin).

This does not replace hardware validation of `WindowsDeviceBackend` itself —
it proves the GUI calls the engine correctly and updates the tree from real
`DeviceAssessment` objects, which is the part that's easy to get wrong
(thread lifecycle, signal wiring, tier grouping) independent of which
backend produced the data.
"""

from __future__ import annotations

import os
from datetime import date

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

pytest.importorskip("PySide6")

from PySide6.QtCore import QCoreApplication
from PySide6.QtWidgets import QApplication

from waypoint.core.models import (
    Device,
    DeviceStatus,
    InstalledDriver,
    SignatureType,
)
from waypoint.engine.audit import AuditLog
from waypoint.engine.session import WaypointEngine
from waypoint.gui.app import MainWindow
from waypoint.platform.mock import MockDeviceBackend
from waypoint.sources.local_cache import LocalCacheSource


@pytest.fixture(scope="module")
def qapp():
    app = QApplication.instance() or QApplication([])
    yield app


def _pump(app, condition, timeout_ms=5000):
    """Process the Qt event loop until `condition()` is true or we time out —
    stands in for a real event loop without needing app.exec()."""
    import time

    elapsed = 0
    while not condition() and elapsed < timeout_ms:
        QCoreApplication.processEvents()
        time.sleep(0.01)
        elapsed += 10
    assert condition(), "condition not met before timeout"


def test_scan_button_populates_tree_from_real_engine_scan(qapp, tmp_path):
    device_missing = Device(
        hwids=("HWID_MISSING",),
        class_guid="{class}",
        class_name="Net",
        friendly_name="Fake NIC",
        instance_id="DEV_MISSING",
        problem_code=28,
        installed=None,
    )
    device_ok = Device(
        hwids=("HWID_OK",),
        class_guid="{class}",
        class_name="Display",
        friendly_name="Fake GPU",
        instance_id="DEV_OK",
        problem_code=None,
        installed=InstalledDriver(
            version="1.0", driver_date=date(2026, 1, 1), publisher="Acme", signature_type=SignatureType.WHQL
        ),
    )
    backend = MockDeviceBackend([device_missing, device_ok])
    source = LocalCacheSource(str(tmp_path / "cache"))
    audit = AuditLog(str(tmp_path / "audit.jsonl"))
    engine = WaypointEngine(backend, [source], audit)

    window = MainWindow(engine=engine)
    assert window.backend_label.text() == "Backend: MockDeviceBackend"

    window.on_scan_clicked()
    assert not window.scan_button.isEnabled()  # disabled while scan is in flight

    _pump(qapp, lambda: len(window.assessments) > 0 and window.scan_button.isEnabled())

    assert window.scan_button.isEnabled()  # re-enabled once the scan completes
    assert window.devices_by_instance_id["DEV_MISSING"] is device_missing
    statuses = {a.device.instance_id: a.status for a in window.assessments}
    assert statuses["DEV_MISSING"] == DeviceStatus.MISSING
    assert statuses["DEV_OK"] == DeviceStatus.UP_TO_DATE

    # Tree has the three triage tiers as top-level nodes, with the missing
    # device nested under "Missing" — up-to-date devices aren't shown.
    top_level_labels = [window.tree.topLevelItem(i).text(0) for i in range(window.tree.topLevelItemCount())]
    assert any(label.startswith("Missing") for label in top_level_labels)
    missing_node = next(
        window.tree.topLevelItem(i)
        for i in range(window.tree.topLevelItemCount())
        if window.tree.topLevelItem(i).text(0).startswith("Missing")
    )
    assert missing_node.childCount() == 1
    assert missing_node.child(0).text(0) == "Fake NIC"


def test_scan_failure_is_surfaced_without_crashing(qapp, tmp_path, monkeypatch):
    backend = MockDeviceBackend([])

    def _boom():
        raise RuntimeError("simulated enumeration failure")

    monkeypatch.setattr(backend, "enumerate_devices", _boom)
    source = LocalCacheSource(str(tmp_path / "cache"))
    audit = AuditLog(str(tmp_path / "audit.jsonl"))
    engine = WaypointEngine(backend, [source], audit)

    window = MainWindow(engine=engine)
    window.on_scan_clicked()

    _pump(
        qapp,
        lambda: "Scan failed" in window.status_label.text() and window.scan_button.isEnabled(),
    )
    assert window.scan_button.isEnabled()


def test_oem_checkbox_defaults_unchecked_and_scan_matches_nothing_without_it(qapp, tmp_path):
    """Sanity check mirroring test_cli_oem_flag.py's without-oem case: a
    device only matched by the Dell fixture catalog should not be found
    when the OEM checkbox is left unchecked (the default)."""
    import os
    import shutil

    from waypoint.core.models import Device

    fixtures = os.path.join(os.path.dirname(__file__), "fixtures", "oem")
    dell_hwid = "PCI\\VEN_8086&DEV_7745"  # same fixture hwid used in test_oem_sources.py

    cache_dir = tmp_path / "cache"
    os.makedirs(cache_dir, exist_ok=True)
    shutil.copy(
        os.path.join(fixtures, "dell_catalog_sample.xml"),
        os.path.join(cache_dir, "CatalogPC.xml"),
    )

    device = Device(
        hwids=(dell_hwid,),
        class_guid="{class}",
        class_name="Sensor",
        friendly_name="Fake Intel Sensor",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    backend = MockDeviceBackend([device])
    local_source = LocalCacheSource(str(cache_dir))
    audit = AuditLog(str(tmp_path / "audit.jsonl"))
    engine = WaypointEngine(backend, [local_source], audit)

    window = MainWindow(engine=engine)
    assert window.oem_checkbox.isChecked() is False  # opt-in, off by default

    window.on_scan_clicked()
    _pump(qapp, lambda: len(window.assessments) > 0 and window.scan_button.isEnabled())

    assert window.assessments[0].status == DeviceStatus.MISSING
    assert window.assessments[0].candidates == []


def test_oem_checkbox_checked_wires_dell_source_into_scan(qapp, tmp_path):
    """With the checkbox checked, the same device (only matched by the Dell
    fixture catalog) should be found -- proves the checkbox actually
    reaches ScanWorker and gets merged into engine.sources for the scan."""
    import os
    import shutil

    from waypoint.core.models import Device, SignatureType

    fixtures = os.path.join(os.path.dirname(__file__), "fixtures", "oem")
    dell_hwid = "PCI\\VEN_8086&DEV_7745"

    cache_dir = tmp_path / "cache"
    os.makedirs(cache_dir, exist_ok=True)
    shutil.copy(
        os.path.join(fixtures, "dell_catalog_sample.xml"),
        os.path.join(cache_dir, "CatalogPC.xml"),
    )

    device = Device(
        hwids=(dell_hwid,),
        class_guid="{class}",
        class_name="Sensor",
        friendly_name="Fake Intel Sensor",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    backend = MockDeviceBackend([device])
    local_source = LocalCacheSource(str(cache_dir))
    audit = AuditLog(str(tmp_path / "audit.jsonl"))
    # Dell's catalog carries no signature metadata and is treated as
    # UNSIGNED by design (see dell_catalog.py's _ASSUMED_SIGNATURE) --
    # loosen the policy so the candidate isn't filtered back out by the
    # (correct, conservative) default signature gate, same reasoning as
    # tests/test_cli_oem_flag.py.
    engine = WaypointEngine(backend, [local_source], audit, min_signature=SignatureType.UNSIGNED)

    window = MainWindow(engine=engine)
    window.oem_checkbox.setChecked(True)

    window.on_scan_clicked()
    assert "OEM" in window.status_label.text()
    _pump(qapp, lambda: len(window.assessments) > 0 and window.scan_button.isEnabled())

    assert len(window.assessments[0].candidates) == 1
    assert window.assessments[0].candidates[0].source_id == "dell_catalog"

    # engine.sources must be restored to its original state after the
    # scan -- OEM sources are per-scan, not a permanent mutation.
    assert engine.sources == [local_source]


def test_oem_checkbox_download_not_called_when_cache_already_seeded(qapp, tmp_path, monkeypatch):
    """Mirrors test_cli_oem_flag.py's cache-reuse proof: a pre-seeded
    cache_dir must not trigger a real network download."""
    import os
    import shutil

    from waypoint.sources.oem import dell_catalog as dell_catalog_module

    fixtures = os.path.join(os.path.dirname(__file__), "fixtures", "oem")
    cache_dir = tmp_path / "cache"
    os.makedirs(cache_dir, exist_ok=True)
    shutil.copy(
        os.path.join(fixtures, "dell_catalog_sample.xml"),
        os.path.join(cache_dir, "CatalogPC.xml"),
    )

    def _fail_if_called(url, dest):
        raise AssertionError("download_file() should not be called when the catalog is already cached")

    monkeypatch.setattr(dell_catalog_module, "download_file", _fail_if_called)

    backend = MockDeviceBackend([])
    local_source = LocalCacheSource(str(cache_dir))
    audit = AuditLog(str(tmp_path / "audit.jsonl"))
    engine = WaypointEngine(backend, [local_source], audit)

    window = MainWindow(engine=engine)
    window.oem_checkbox.setChecked(True)
    window.on_scan_clicked()

    _pump(qapp, lambda: window.scan_button.isEnabled())
    assert "Scan failed" not in window.status_label.text()
