"""Unit tests for core.matching — the logic that directly encodes the
"never guess on ambiguous matches, never silently recommend a downgrade"
rules from docs/Architecture.md. These run on any OS since core/ has no
platform dependency.
"""

from datetime import date

from waypoint.core.matching import assess_all, find_ambiguous_hwids, rank_candidates
from waypoint.core.models import (
    Device,
    DeviceStatus,
    DriverCandidate,
    InstalledDriver,
    SignatureType,
)


def _candidate(hwid="HWID1", version="2.0", driver_date=date(2026, 1, 1), sig=SignatureType.WHQL, source="local_cache"):
    return DriverCandidate(
        hwid=hwid,
        class_guid="{class}",
        version=version,
        driver_date=driver_date,
        publisher="Acme",
        signature_type=sig,
        sha256="abc123",
        size_bytes=1024,
        source_id=source,
        source_url="https://example.test",
        download_uri="/cache/blobs/abc123/driver.inf",
    )


def _device(hwids=("HWID1",), instance_id="DEV1", installed=None, problem_code=None, class_name="Display"):
    return Device(
        hwids=hwids,
        class_guid="{class}",
        class_name=class_name,
        friendly_name=f"Fake {class_name} device",
        instance_id=instance_id,
        problem_code=problem_code,
        installed=installed,
    )


def test_missing_device_is_flagged_missing_even_without_candidates():
    device = _device(installed=None, problem_code=28)
    [assessment] = assess_all([device], {})
    assert assessment.status == DeviceStatus.MISSING
    assert assessment.candidates == []


def test_problem_code_flags_problem_not_missing_when_driver_present():
    installed = InstalledDriver(version="1.0", driver_date=date(2025, 1, 1), publisher="Acme", signature_type=SignatureType.WHQL)
    device = _device(installed=installed, problem_code=1)
    [assessment] = assess_all([device], {})
    assert assessment.status == DeviceStatus.PROBLEM


def test_older_installed_driver_yields_upgrade_available_not_auto_applied():
    installed = InstalledDriver(version="1.0", driver_date=date(2024, 1, 1), publisher="Acme", signature_type=SignatureType.WHQL)
    device = _device(installed=installed)
    candidate = _candidate(driver_date=date(2026, 1, 1))
    [assessment] = assess_all([device], {"HWID1": [candidate]})
    assert assessment.status == DeviceStatus.UPGRADE_AVAILABLE
    assert assessment.candidates == [candidate]


def test_up_to_date_when_no_newer_candidate_exists():
    installed = InstalledDriver(version="2.0", driver_date=date(2026, 1, 1), publisher="Acme", signature_type=SignatureType.WHQL)
    device = _device(installed=installed)
    candidate = _candidate(driver_date=date(2024, 1, 1))  # older than installed
    [assessment] = assess_all([device], {"HWID1": [candidate]})
    assert assessment.status == DeviceStatus.UP_TO_DATE
    assert assessment.candidates == []


def test_unsigned_candidate_is_excluded_by_default_policy():
    unsigned = _candidate(sig=SignatureType.UNSIGNED)
    whql = _candidate(sig=SignatureType.WHQL, version="3.0")
    ranked = rank_candidates([unsigned, whql], min_signature=SignatureType.ATTESTATION)
    assert unsigned not in ranked
    assert whql in ranked


def test_installed_driver_below_signature_policy_is_flagged_problem():
    installed = InstalledDriver(version="1.0", driver_date=date(2025, 1, 1), publisher="Acme", signature_type=SignatureType.UNSIGNED)
    device = _device(installed=installed)
    [assessment] = assess_all([device], {}, min_signature=SignatureType.ATTESTATION)
    assert assessment.status == DeviceStatus.PROBLEM
    assert any("signature" in note for note in assessment.notes)


def test_shared_hwid_across_devices_is_flagged_ambiguous_not_auto_resolved():
    device_a = _device(hwids=("SHARED_HWID",), instance_id="A")
    device_b = _device(hwids=("SHARED_HWID",), instance_id="B")
    shared = find_ambiguous_hwids([device_a, device_b])
    assert "SHARED_HWID" in shared

    assessments = assess_all([device_a, device_b], {})
    assert all(a.ambiguous for a in assessments)
    assert all("manual" in " ".join(a.notes).lower() for a in assessments)


def test_non_shared_hwid_is_not_flagged_ambiguous():
    device_a = _device(hwids=("HWID_A",), instance_id="A")
    device_b = _device(hwids=("HWID_B",), instance_id="B")
    assessments = assess_all([device_a, device_b], {})
    assert not any(a.ambiguous for a in assessments)
