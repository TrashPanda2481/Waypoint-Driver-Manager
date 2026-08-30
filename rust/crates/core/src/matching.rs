//! Pure matching/ranking logic: `Device` + candidates -> `DeviceAssessment`.
//!
//! Direct port of `src/waypoint/core/matching.py`. Zero I/O. Every function
//! here is deterministic and takes its inputs as plain data, mirrored by
//! `tests/matching_tests.rs` which ports `tests/test_matching.py`'s cases
//! one-for-one.

use std::collections::{HashMap, HashSet};

use time::macros::date;

use crate::models::{Device, DeviceAssessment, DeviceStatus, DriverCandidate, SignatureType};

/// Device Manager problem codes that mean "no driver at all" vs "driver
/// present but broken." Code 28 = drivers not installed. Code 1 = device
/// not configured correctly (often *some* driver present). This
/// distinction drives the Missing vs Problem triage split described in
/// docs/Architecture.md.
const NO_DRIVER_CODES: &[i32] = &[28];

/// Sort candidates by trust tier first, then recency, filtering out
/// anything below the minimum signature policy. Never silently allows an
/// unsigned/test-signed driver through — that requires an explicit, logged
/// override at the engine layer, not a ranking default.
pub fn rank_candidates(
    candidates: &[DriverCandidate],
    min_signature: SignatureType,
) -> Vec<DriverCandidate> {
    let min_rank = min_signature.trust_rank();
    let mut allowed: Vec<DriverCandidate> = candidates
        .iter()
        .filter(|c| c.signature_type.trust_rank() <= min_rank)
        .cloned()
        .collect();

    let min_date = date!(1970 - 01 - 01);
    allowed.sort_by_key(|c| (c.signature_type.trust_rank(), c.driver_date.unwrap_or(min_date)));
    allowed
}

/// Build the triage assessment for a single device.
///
/// `candidates_by_hwid` maps a hardware ID to every candidate any source
/// returned for it — the engine is responsible for merging sources before
/// calling this; matching logic itself stays source-agnostic.
pub fn assess_device(
    device: &Device,
    candidates_by_hwid: &HashMap<String, Vec<DriverCandidate>>,
    min_signature: SignatureType,
) -> DeviceAssessment {
    let mut notes: Vec<String> = Vec::new();

    let mut all_candidates: Vec<DriverCandidate> = Vec::new();
    for hwid in &device.hwids {
        if let Some(found) = candidates_by_hwid.get(hwid) {
            all_candidates.extend(found.iter().cloned());
        }
    }

    let mut ranked = rank_candidates(&all_candidates, min_signature);

    let status = if device
        .problem_code
        .map(|code| NO_DRIVER_CODES.contains(&code))
        .unwrap_or(false)
        || device.installed.is_none()
    {
        DeviceStatus::Missing
    } else if device.problem_code.is_some() {
        notes.push(format!(
            "Device reports problem code {}.",
            device.problem_code.unwrap()
        ));
        DeviceStatus::Problem
    } else if device
        .installed
        .as_ref()
        .map(|installed| installed.signature_type.trust_rank() > min_signature.trust_rank())
        .unwrap_or(false)
    {
        let installed = device.installed.as_ref().unwrap();
        notes.push(format!(
            "Installed driver signature tier '{}' fails current policy (minimum '{}').",
            installed.signature_type.as_str(),
            min_signature.as_str()
        ));
        DeviceStatus::Problem
    } else {
        let newer: Vec<DriverCandidate> = ranked
            .iter()
            .filter(|c| c.is_newer_than(device.installed.as_ref()))
            .cloned()
            .collect();
        if !newer.is_empty() {
            ranked = newer;
            DeviceStatus::UpgradeAvailable
        } else {
            ranked = Vec::new();
            DeviceStatus::UpToDate
        }
    };

    DeviceAssessment {
        device: device.clone(),
        status,
        candidates: ranked,
        ambiguous: false,
        notes,
    }
}

/// Return every HWID that appears on more than one installed device in this
/// scan. SDI's "installed touchpad drivers on a desktop" class of bug comes
/// from resolving this kind of overlap automatically; Waypoint's rule is:
/// never do that silently. See docs/Architecture.md section 3.1.
pub fn find_ambiguous_hwids(devices: &[Device]) -> HashSet<String> {
    let mut counts: HashMap<&str, usize> = HashMap::new();
    for device in devices {
        for hwid in &device.hwids {
            *counts.entry(hwid.as_str()).or_insert(0) += 1;
        }
    }
    counts
        .into_iter()
        .filter(|(_, count)| *count > 1)
        .map(|(hwid, _)| hwid.to_string())
        .collect()
}

/// Convenience wrapper: assess every device and flag ambiguous HWIDs.
pub fn assess_all(
    devices: &[Device],
    candidates_by_hwid: &HashMap<String, Vec<DriverCandidate>>,
    min_signature: SignatureType,
) -> Vec<DeviceAssessment> {
    let ambiguous_hwids = find_ambiguous_hwids(devices);
    devices
        .iter()
        .map(|device| {
            let mut assessment = assess_device(device, candidates_by_hwid, min_signature);
            if device.hwids.iter().any(|h| ambiguous_hwids.contains(h)) {
                assessment.ambiguous = true;
                assessment.notes.push(
                    "Hardware ID shared with another detected device — requires manual \
                     per-device confirmation before install."
                        .to_string(),
                );
            }
            assessment
        })
        .collect()
}
