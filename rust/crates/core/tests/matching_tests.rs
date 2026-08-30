//! Port of `tests/test_matching.py`, one test per Python test, same names
//! translated to snake_case Rust convention. If a case here diverges from
//! the Python behavior, that is a real bug in the port, not a rewrite.

use std::collections::HashMap;

use time::macros::date;

use waypoint_core::models::{Device, DeviceStatus, DriverCandidate, InstalledDriver, SignatureType};
use waypoint_core::{assess_all, find_ambiguous_hwids, rank_candidates};

fn candidate(
    hwid: &str,
    version: &str,
    driver_date: Option<time::Date>,
    sig: SignatureType,
    source: &str,
) -> DriverCandidate {
    DriverCandidate {
        hwid: hwid.to_string(),
        class_guid: "{class}".to_string(),
        version: version.to_string(),
        driver_date,
        publisher: "Acme".to_string(),
        signature_type: sig,
        sha256: "abc123".to_string(),
        size_bytes: 1024,
        source_id: source.to_string(),
        source_url: "https://example.test".to_string(),
        download_uri: "/cache/blobs/abc123/driver.inf".to_string(),
    }
}

fn device(
    hwids: &[&str],
    instance_id: &str,
    installed: Option<InstalledDriver>,
    problem_code: Option<i32>,
    class_name: &str,
) -> Device {
    Device {
        hwids: hwids.iter().map(|s| s.to_string()).collect(),
        class_guid: "{class}".to_string(),
        class_name: class_name.to_string(),
        friendly_name: format!("Fake {class_name} device"),
        instance_id: instance_id.to_string(),
        problem_code,
        installed,
    }
}

#[test]
fn test_missing_device_is_flagged_missing_even_without_candidates() {
    let dev = device(&["HWID1"], "DEV1", None, Some(28), "Display");
    let assessments = assess_all(&[dev], &HashMap::new(), SignatureType::Attestation);
    assert_eq!(assessments.len(), 1);
    assert_eq!(assessments[0].status, DeviceStatus::Missing);
    assert!(assessments[0].candidates.is_empty());
}

#[test]
fn test_problem_code_flags_problem_not_missing_when_driver_present() {
    let installed = InstalledDriver {
        version: "1.0".to_string(),
        driver_date: Some(date!(2025 - 01 - 01)),
        publisher: "Acme".to_string(),
        signature_type: SignatureType::Whql,
        inf_path: None,
    };
    let dev = device(&["HWID1"], "DEV1", Some(installed), Some(1), "Display");
    let assessments = assess_all(&[dev], &HashMap::new(), SignatureType::Attestation);
    assert_eq!(assessments[0].status, DeviceStatus::Problem);
}

#[test]
fn test_older_installed_driver_yields_upgrade_available_not_auto_applied() {
    let installed = InstalledDriver {
        version: "1.0".to_string(),
        driver_date: Some(date!(2024 - 01 - 01)),
        publisher: "Acme".to_string(),
        signature_type: SignatureType::Whql,
        inf_path: None,
    };
    let dev = device(&["HWID1"], "DEV1", Some(installed), None, "Display");
    let cand = candidate(
        "HWID1",
        "2.0",
        Some(date!(2026 - 01 - 01)),
        SignatureType::Whql,
        "local_cache",
    );
    let mut by_hwid = HashMap::new();
    by_hwid.insert("HWID1".to_string(), vec![cand.clone()]);
    let assessments = assess_all(&[dev], &by_hwid, SignatureType::Attestation);
    assert_eq!(assessments[0].status, DeviceStatus::UpgradeAvailable);
    assert_eq!(assessments[0].candidates, vec![cand]);
}

#[test]
fn test_up_to_date_when_no_newer_candidate_exists() {
    let installed = InstalledDriver {
        version: "2.0".to_string(),
        driver_date: Some(date!(2026 - 01 - 01)),
        publisher: "Acme".to_string(),
        signature_type: SignatureType::Whql,
        inf_path: None,
    };
    let dev = device(&["HWID1"], "DEV1", Some(installed), None, "Display");
    // older than installed
    let cand = candidate(
        "HWID1",
        "2.0",
        Some(date!(2024 - 01 - 01)),
        SignatureType::Whql,
        "local_cache",
    );
    let mut by_hwid = HashMap::new();
    by_hwid.insert("HWID1".to_string(), vec![cand]);
    let assessments = assess_all(&[dev], &by_hwid, SignatureType::Attestation);
    assert_eq!(assessments[0].status, DeviceStatus::UpToDate);
    assert!(assessments[0].candidates.is_empty());
}

#[test]
fn test_unsigned_candidate_is_excluded_by_default_policy() {
    let unsigned = candidate(
        "HWID1",
        "2.0",
        Some(date!(2026 - 01 - 01)),
        SignatureType::Unsigned,
        "local_cache",
    );
    let whql = candidate(
        "HWID1",
        "3.0",
        Some(date!(2026 - 01 - 01)),
        SignatureType::Whql,
        "local_cache",
    );
    let ranked = rank_candidates(&[unsigned.clone(), whql.clone()], SignatureType::Attestation);
    assert!(!ranked.contains(&unsigned));
    assert!(ranked.contains(&whql));
}

#[test]
fn test_installed_driver_below_signature_policy_is_flagged_problem() {
    let installed = InstalledDriver {
        version: "1.0".to_string(),
        driver_date: Some(date!(2025 - 01 - 01)),
        publisher: "Acme".to_string(),
        signature_type: SignatureType::Unsigned,
        inf_path: None,
    };
    let dev = device(&["HWID1"], "DEV1", Some(installed), None, "Display");
    let assessments = assess_all(&[dev], &HashMap::new(), SignatureType::Attestation);
    assert_eq!(assessments[0].status, DeviceStatus::Problem);
    assert!(assessments[0]
        .notes
        .iter()
        .any(|n| n.to_lowercase().contains("signature")));
}

#[test]
fn test_shared_hwid_across_devices_is_flagged_ambiguous_not_auto_resolved() {
    let device_a = device(&["SHARED_HWID"], "A", None, None, "Display");
    let device_b = device(&["SHARED_HWID"], "B", None, None, "Display");
    let shared = find_ambiguous_hwids(&[device_a.clone(), device_b.clone()]);
    assert!(shared.contains("SHARED_HWID"));

    let assessments = assess_all(
        &[device_a, device_b],
        &HashMap::new(),
        SignatureType::Attestation,
    );
    assert!(assessments.iter().all(|a| a.ambiguous));
    assert!(assessments
        .iter()
        .all(|a| a.notes.join(" ").to_lowercase().contains("manual")));
}

#[test]
fn test_non_shared_hwid_is_not_flagged_ambiguous() {
    let device_a = device(&["HWID_A"], "A", None, None, "Display");
    let device_b = device(&["HWID_B"], "B", None, None, "Display");
    let assessments = assess_all(
        &[device_a, device_b],
        &HashMap::new(),
        SignatureType::Attestation,
    );
    assert!(!assessments.iter().any(|a| a.ambiguous));
}
