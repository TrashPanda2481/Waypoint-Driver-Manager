"""Engine-level tests using the mock backend + local cache source — no real
OS or hardware required. Covers the two safety-critical behaviors called
out in docs/Architecture.md section 3.3: the install batch must abort if
the restore point fails, and unconfirmed (ambiguous/upgrade-tier) entries
must never be applied silently.
"""

from datetime import date

import pytest

from waypoint.core.models import Device, InstalledDriver, SignatureType
from waypoint.engine.audit import AuditLog
from waypoint.engine.session import WaypointEngine
from waypoint.platform.mock import MockDeviceBackend
from waypoint.sources.local_cache import LocalCacheSource


@pytest.fixture
def cache_source(tmp_path):
    source = LocalCacheSource(str(tmp_path / "cache"))
    driver_file = tmp_path / "driver.inf"
    driver_file.write_text("; fake inf for test purposes")
    source.add_package(
        str(driver_file),
        hwid="HWID1",
        class_guid="{class}",
        version="2.0",
        driver_date=date(2026, 1, 1),
        publisher="Acme",
        signature_type=SignatureType.WHQL,
    )
    return source


@pytest.fixture
def audit(tmp_path):
    return AuditLog(str(tmp_path / "audit.jsonl"))


def test_apply_aborts_when_restore_point_fails(cache_source, audit, tmp_path):
    device = Device(
        hwids=("HWID1",),
        class_guid="{class}",
        class_name="Display",
        friendly_name="Fake GPU",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    backend = MockDeviceBackend([device])
    backend.restore_point_should_succeed = False

    engine = WaypointEngine(backend, [cache_source], audit)
    assessments = engine.scan()
    plan = engine.build_plan(assessments)

    with pytest.raises(RuntimeError, match="Restore point"):
        engine.apply(plan, {"DEV1": device}, dry_run=False, backup_dir=str(tmp_path / "backups"))

    assert backend.install_calls == []  # nothing installed once the restore point failed


def test_missing_device_installs_without_confirmation_required(cache_source, audit, tmp_path):
    device = Device(
        hwids=("HWID1",),
        class_guid="{class}",
        class_name="Display",
        friendly_name="Fake GPU",
        instance_id="DEV1",
        problem_code=28,
        installed=None,
    )
    backend = MockDeviceBackend([device])

    engine = WaypointEngine(backend, [cache_source], audit)
    assessments = engine.scan()
    plan = engine.build_plan(assessments)
    assert not plan.entries[0].requires_confirmation  # "missing" tier is not gated

    results = engine.apply(plan, {"DEV1": device}, dry_run=True, backup_dir=str(tmp_path / "backups"))
    assert len(results) == 1
    assert results[0]["success"] is True


def test_upgrade_tier_is_skipped_without_explicit_confirmation(cache_source, audit, tmp_path):
    installed = InstalledDriver(
        version="1.0", driver_date=date(2024, 1, 1), publisher="Acme", signature_type=SignatureType.WHQL
    )
    device = Device(
        hwids=("HWID1",),
        class_guid="{class}",
        class_name="Display",
        friendly_name="Fake GPU",
        instance_id="DEV1",
        problem_code=None,
        installed=installed,
    )
    backend = MockDeviceBackend([device])

    engine = WaypointEngine(backend, [cache_source], audit)
    assessments = engine.scan()
    plan = engine.build_plan(assessments)
    assert plan.entries[0].requires_confirmation  # upgrade tier is gated by default

    results = engine.apply(plan, {"DEV1": device}, dry_run=True, backup_dir=str(tmp_path / "backups"))
    assert results == []  # skipped: not confirmed

    results_confirmed = engine.apply(
        plan,
        {"DEV1": device},
        dry_run=True,
        backup_dir=str(tmp_path / "backups"),
        confirmed_instance_ids={"DEV1"},
    )
    assert len(results_confirmed) == 1
