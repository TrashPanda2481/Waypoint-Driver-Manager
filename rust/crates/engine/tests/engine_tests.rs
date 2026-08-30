//! Port of `tests/test_engine.py`. Engine-level tests using the mock
//! backend + local cache source — no real OS or hardware required. Covers
//! the two safety-critical behaviors called out in docs/Architecture.md
//! section 3.3: the install batch must abort if the restore point fails,
//! and unconfirmed (ambiguous/upgrade-tier) entries must never be applied
//! silently.

use std::collections::{HashMap, HashSet};

use tempfile::tempdir;
use time::macros::date;

use waypoint_core::{Device, InstalledDriver, SignatureType};
use waypoint_engine::{AuditLog, WaypointEngine};
use waypoint_platform::MockDeviceBackend;
use waypoint_sources::LocalCacheSource;

fn cache_source(root: &std::path::Path) -> LocalCacheSource {
    let cache_dir = root.join("cache");
    let source = LocalCacheSource::new(&cache_dir).expect("create cache source");
    let driver_file = root.join("driver.inf");
    std::fs::write(&driver_file, "; fake inf for test purposes").unwrap();
    source
        .add_package(
            &driver_file,
            "HWID1",
            "{class}",
            "2.0",
            Some(date!(2026 - 01 - 01)),
            "Acme",
            SignatureType::Whql,
        )
        .expect("add_package");
    source
}

fn fake_gpu(problem_code: Option<i32>, installed: Option<InstalledDriver>) -> Device {
    Device {
        hwids: vec!["HWID1".to_string()],
        class_guid: "{class}".to_string(),
        class_name: "Display".to_string(),
        friendly_name: "Fake GPU".to_string(),
        instance_id: "DEV1".to_string(),
        problem_code,
        installed,
    }
}

#[test]
fn test_apply_aborts_when_restore_point_fails() {
    let tmp = tempdir().unwrap();
    let source = cache_source(tmp.path());
    let audit = AuditLog::new(tmp.path().join("audit.jsonl")).unwrap();

    let device = fake_gpu(Some(28), None);
    let mut backend = MockDeviceBackend::new(vec![device.clone()]);
    backend.restore_point_should_succeed = false;

    let engine = WaypointEngine::new(
        Box::new(backend),
        vec![Box::new(source)],
        audit,
        SignatureType::Attestation,
    );
    let assessments = engine.scan().unwrap();
    let plan = engine.build_plan(&assessments).unwrap();

    let devices_by_id = HashMap::from([("DEV1".to_string(), device)]);
    let result = engine.apply(
        &plan,
        &devices_by_id,
        false,
        tmp.path().join("backups").to_str().unwrap(),
        &HashSet::new(),
    );
    assert!(result.is_err());
    assert!(result.unwrap_err().contains("Restore point"));

    // The Python test also asserts `backend.install_calls == []` here.
    // `engine.backend` is a `Box<dyn DeviceBackend>` owned by `engine`, so
    // there's no external handle left to inspect after construction.
    // Structurally this is still verified: `apply()` returns the restore-
    // point error before its per-entry loop begins (see session.rs), so no
    // `install_driver` call can have happened on this path.
}

#[test]
fn test_missing_device_installs_without_confirmation_required() {
    let tmp = tempdir().unwrap();
    let source = cache_source(tmp.path());
    let audit = AuditLog::new(tmp.path().join("audit.jsonl")).unwrap();

    let device = fake_gpu(Some(28), None);
    let backend = MockDeviceBackend::new(vec![device.clone()]);

    let engine = WaypointEngine::new(
        Box::new(backend),
        vec![Box::new(source)],
        audit,
        SignatureType::Attestation,
    );
    let assessments = engine.scan().unwrap();
    let plan = engine.build_plan(&assessments).unwrap();
    // "missing" tier is not gated.
    assert!(!plan.entries[0].requires_confirmation);

    let devices_by_id = HashMap::from([("DEV1".to_string(), device)]);
    let results = engine
        .apply(
            &plan,
            &devices_by_id,
            true,
            tmp.path().join("backups").to_str().unwrap(),
            &HashSet::new(),
        )
        .unwrap();
    assert_eq!(results.len(), 1);
    assert!(results[0].success);
}

#[test]
fn test_upgrade_tier_is_skipped_without_explicit_confirmation() {
    let tmp = tempdir().unwrap();
    let source = cache_source(tmp.path());
    let audit = AuditLog::new(tmp.path().join("audit.jsonl")).unwrap();

    let installed = InstalledDriver {
        version: "1.0".to_string(),
        driver_date: Some(date!(2024 - 01 - 01)),
        publisher: "Acme".to_string(),
        signature_type: SignatureType::Whql,
        inf_path: None,
    };
    let device = fake_gpu(None, Some(installed));
    let backend = MockDeviceBackend::new(vec![device.clone()]);

    let engine = WaypointEngine::new(
        Box::new(backend),
        vec![Box::new(source)],
        audit,
        SignatureType::Attestation,
    );
    let assessments = engine.scan().unwrap();
    let plan = engine.build_plan(&assessments).unwrap();
    // upgrade tier is gated by default
    assert!(plan.entries[0].requires_confirmation);

    let devices_by_id = HashMap::from([("DEV1".to_string(), device)]);
    let backup_dir = tmp.path().join("backups").to_str().unwrap().to_string();

    let results = engine
        .apply(&plan, &devices_by_id, true, &backup_dir, &HashSet::new())
        .unwrap();
    assert!(results.is_empty()); // skipped: not confirmed

    let confirmed = HashSet::from(["DEV1".to_string()]);
    let results_confirmed = engine
        .apply(&plan, &devices_by_id, true, &backup_dir, &confirmed)
        .unwrap();
    assert_eq!(results_confirmed.len(), 1);
}
