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
