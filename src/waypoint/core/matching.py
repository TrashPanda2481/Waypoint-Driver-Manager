"""Pure matching/ranking logic: Device + candidates -> DeviceAssessment.

This module has zero I/O. Every function here is deterministic and takes its
inputs as plain data, which is what lets tests/test_matching.py cover the
"don't guess on ambiguous matches" and "don't recommend downgrades" behavior
without touching real hardware, WMI, or udev.
"""

from __future__ import annotations

from collections import Counter
from datetime import date as _date

from waypoint.core.models import (
    Device,
    DeviceAssessment,
    DeviceStatus,
    DriverCandidate,
    SignatureType,
)

# Device Manager problem codes that mean "no driver at all" vs "driver present
# but broken." Code 28 = drivers not installed. Code 1 = device not
# configured correctly (often *some* driver present). This distinction drives
# the MISSING vs PROBLEM triage split described in docs/Architecture.md.
_NO_DRIVER_CODES = {28}


def rank_candidates(
    candidates: list[DriverCandidate],
    min_signature: SignatureType = SignatureType.ATTESTATION,
) -> list[DriverCandidate]:
    """Sort candidates by trust tier first, then recency, filtering out
    anything below the minimum signature policy. Never silently allows an
    unsigned/test-signed driver through — that requires an explicit,
    logged override at the engine layer, not a ranking default.
    """
    allowed = [c for c in candidates if c.signature_type.trust_rank <= min_signature.trust_rank]
    return sorted(
        allowed,
        key=lambda c: (
            c.signature_type.trust_rank,
            c.driver_date or _MIN_DATE,
        ),
        reverse=False,
    )


_MIN_DATE = _date(1970, 1, 1)


def assess_device(
    device: Device,
    candidates_by_hwid: dict[str, list[DriverCandidate]],
    min_signature: SignatureType = SignatureType.ATTESTATION,
) -> DeviceAssessment:
    """Build the triage assessment for a single device.

    `candidates_by_hwid` maps a hardware ID to every candidate any source
    returned for it — the engine is responsible for merging sources before
    calling this; matching logic itself stays source-agnostic.
    """
    notes: list[str] = []

    all_candidates: list[DriverCandidate] = []
    for hwid in device.hwids:
        all_candidates.extend(candidates_by_hwid.get(hwid, []))

    ranked = rank_candidates(all_candidates, min_signature=min_signature)

    if device.problem_code in _NO_DRIVER_CODES or device.installed is None:
        status = DeviceStatus.MISSING
    elif device.problem_code is not None:
        status = DeviceStatus.PROBLEM
        notes.append(f"Device reports problem code {device.problem_code}.")
    elif device.installed.signature_type.trust_rank > min_signature.trust_rank:
        status = DeviceStatus.PROBLEM
        notes.append(
            f"Installed driver signature tier '{device.installed.signature_type.value}' "
            f"fails current policy (minimum '{min_signature.value}')."
        )
    else:
        newer = [c for c in ranked if c.is_newer_than(device.installed)]
        if newer:
            status = DeviceStatus.UPGRADE_AVAILABLE
            ranked = newer
        else:
            status = DeviceStatus.UP_TO_DATE
            ranked = []

    return DeviceAssessment(
        device=device,
        status=status,
        candidates=ranked,
        notes=notes,
    )


def find_ambiguous_hwids(devices: list[Device]) -> set[str]:
    """Return every HWID that appears on more than one installed device in
    this scan. SDI's "installed touchpad drivers on a desktop" class of bug
    comes from resolving this kind of overlap automatically; Waypoint's rule
    is: never do that silently. See docs/Architecture.md section 3.1.
    """
    counter: Counter[str] = Counter()
    for device in devices:
        for hwid in device.hwids:
            counter[hwid] += 1
    return {hwid for hwid, count in counter.items() if count > 1}


def assess_all(
    devices: list[Device],
    candidates_by_hwid: dict[str, list[DriverCandidate]],
    min_signature: SignatureType = SignatureType.ATTESTATION,
) -> list[DeviceAssessment]:
    """Convenience wrapper: assess every device and flag ambiguous HWIDs."""
    ambiguous_hwids = find_ambiguous_hwids(devices)
    results = []
    for device in devices:
        assessment = assess_device(device, candidates_by_hwid, min_signature=min_signature)
        if ambiguous_hwids.intersection(device.hwids):
            assessment.ambiguous = True
            assessment.notes.append(
                "Hardware ID shared with another detected device — requires "
                "manual per-device confirmation before install."
            )
        results.append(assessment)
    return results
