//! Port of `tests/test_cli_oem_flag.py`. Uses the dependency-injection
//! seam in `waypoint_cli::lib.rs` (`run_with_backend`,
//! `build_engine_with_injected_backend`) instead of Python's
//! `monkeypatch.setattr("...build_default_backend", ...)` + `capsys`:
//! `MockDeviceBackend` is passed in directly, and stdout/stderr are
//! captured into `Vec<u8>` buffers instead of the real process streams.
//!
//! **No real network access**, same as the Python suite: the Dell OEM
//! source's on-disk cache is pre-seeded with the same real-data fixture
//! `oem_tests.rs` uses, so `refresh(force=false)` finds the catalog
//! "already downloaded" and just parses it.
//!
//! **Two Python tests are NOT ported here** —
//! `test_oem_cache_dir_is_reused_not_redownloaded` and
//! `test_force_oem_refresh_forces_redownload_even_when_cached`. Both
//! monkeypatch the *module-level* `download_file`/`extract_cab`
//! functions to prove whether a download was attempted. This crate's
//! seam only injects the *backend*, not the OEM source's download layer,
//! and `build_oem_sources()` has no parameter for overriding that.
//! `DellCatalogSource` already exposes its own DI seam for this
//! (`set_downloader`/`set_extractor`, see `dell_catalog.rs`) — plumbing
//! a second override through `build_oem_sources()` and the CLI just to
//! satisfy these two assertions would test the same `refresh()` cache/
//! force branch a second time through more machinery, not add real
//! coverage. Both are ported instead as unit tests directly against
//! `DellCatalogSource::refresh()` using that existing seam — see
//! `crates/sources/tests/oem_tests.rs`:
//! `test_dell_catalog_refresh_skips_download_when_cache_exists_and_not_forced`
//! and
//! `test_dell_catalog_refresh_redownloads_when_forced_even_if_cached`.

use std::fs;

use clap::Parser;
use tempfile::tempdir;

use waypoint_core::Device;
use waypoint_platform::MockDeviceBackend;

const FIXTURE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/oem/dell_catalog_sample.xml");

// Matches the Intel Integrated Sensor Solution Driver component in
// dell_catalog_sample.xml (see crates/sources/tests/oem_tests.rs for the
// same hwid).
const DELL_FIXTURE_HWID: &str = "PCI\\VEN_8086&DEV_7745";

/// Pre-seed cache_dir/CatalogPC.xml so DellCatalogSource::refresh(force=false)
/// treats the catalog as already downloaded and skips the network call.
/// `build_oem_sources(cache_dir)` passes the *same* cache_dir straight to
/// `DellCatalogSource::new(cache_dir)`, which stores `CatalogPC.xml`
/// directly under it — the same directory `LocalCacheSource` uses for
/// `manifest.json`/`blobs/`. No collision (different filenames), but it
/// does mean both sources currently share one `--cache-dir` root.
fn seed_dell_cache(cache_dir: &std::path::Path) {
    fs::create_dir_all(cache_dir).expect("create cache_dir");
    fs::copy(FIXTURE, cache_dir.join("CatalogPC.xml")).expect("seed CatalogPC.xml");
}

fn fake_sensor_device() -> Device {
    Device {
        hwids: vec![DELL_FIXTURE_HWID.to_string()],
        class_guid: "{class}".to_string(),
        class_name: "Sensor".to_string(),
        friendly_name: "Fake Intel Sensor".to_string(),
        instance_id: "DEV1".to_string(),
        problem_code: Some(28),
        installed: None,
    }
}

#[test]
fn test_oem_flag_defaults_to_off() {
    let cli = waypoint_cli::Cli::try_parse_from(["waypoint", "scan"]).expect("parse");
    assert!(!cli.oem);
}

#[test]
fn test_oem_flag_parses_before_subcommand() {
    let cli =
        waypoint_cli::Cli::try_parse_from(["waypoint", "--oem", "scan", "--json"]).expect("parse");
    assert!(cli.oem);
    assert!(matches!(cli.command, waypoint_cli::Command::Scan { json: true }));
}

#[test]
fn test_force_oem_refresh_flag_defaults_to_off() {
    let cli = waypoint_cli::Cli::try_parse_from(["waypoint", "scan"]).expect("parse");
    assert!(!cli.force_oem_refresh);
}

/// Sanity check: without --oem, a device whose only driver source is the
/// Dell fixture catalog should NOT get matched (proves the OEM source
/// genuinely isn't in play by default, not just that it's a no-op).
#[test]
fn test_scan_without_oem_does_not_match_dell_only_hwid() {
    let tmp = tempdir().expect("tempdir");
    let cache_dir = tmp.path().join("cache");
    seed_dell_cache(&cache_dir);
    let audit_log = tmp.path().join("audit.jsonl");

    let backend: Box<dyn waypoint_platform::DeviceBackend> =
        Box::new(MockDeviceBackend::new(vec![fake_sensor_device()]));

    let cli = waypoint_cli::Cli::try_parse_from([
        "waypoint",
        "--cache-dir",
        cache_dir.to_str().unwrap(),
        "--audit-log",
        audit_log.to_str().unwrap(),
        "scan",
        "--json",
    ])
    .expect("parse");

    let mut out = Vec::new();
    let mut err = Vec::new();
    let rc = waypoint_cli::run_with_backend(cli, backend, &mut out, &mut err);

    let parsed: serde_json::Value =
        serde_json::from_slice(&out).expect("stdout should be valid JSON");
    assert_eq!(rc, 1); // missing driver, no source has it
    assert_eq!(parsed[0]["status"], "missing");
    assert_eq!(parsed[0]["candidate_count"], 0);
}

#[test]
fn test_scan_with_oem_flag_wires_dell_source() {
    let tmp = tempdir().expect("tempdir");
    let cache_dir = tmp.path().join("cache");
    seed_dell_cache(&cache_dir);
    let audit_log = tmp.path().join("audit.jsonl");

    let backend: Box<dyn waypoint_platform::DeviceBackend> =
        Box::new(MockDeviceBackend::new(vec![fake_sensor_device()]));

    let cli = waypoint_cli::Cli::try_parse_from([
        "waypoint",
        "--cache-dir",
        cache_dir.to_str().unwrap(),
        "--audit-log",
        audit_log.to_str().unwrap(),
        "--oem",
        // Dell's assumed signature tier is UNSIGNED (see dell_catalog.rs's
        // ASSUMED_SIGNATURE); the default min-signature policy
        // (attestation) would filter it back out before it ever reached
        // the JSON output, so this test has to loosen the policy
        // explicitly to prove the *source* wiring works, independently
        // of the (correct, conservative) default signature gate.
        "--min-signature",
        "unsigned",
        "scan",
        "--json",
    ])
    .expect("parse");

    let mut out = Vec::new();
    let mut err = Vec::new();
    let rc = waypoint_cli::run_with_backend(cli, backend, &mut out, &mut err);

    let parsed: serde_json::Value =
        serde_json::from_slice(&out).expect("stdout should be valid JSON");
    assert_eq!(rc, 1); // still "missing" per Device Manager problem code 28,
    // but a real candidate was found this time
    assert_eq!(parsed[0]["candidate_count"], 1);
}

/// --force-oem-refresh without --oem should not silently pretend to do
/// something -- it must warn on stderr, per the honesty instruction
/// (don't let a flag look like it did something when it didn't).
#[test]
fn test_force_oem_refresh_without_oem_warns_but_does_not_error() {
    let tmp = tempdir().expect("tempdir");
    let cache_dir = tmp.path().join("cache");
    let audit_log = tmp.path().join("audit.jsonl");

    let backend: Box<dyn waypoint_platform::DeviceBackend> =
        Box::new(MockDeviceBackend::new(vec![]));

    let cli = waypoint_cli::Cli::try_parse_from([
        "waypoint",
        "--cache-dir",
        cache_dir.to_str().unwrap(),
        "--audit-log",
        audit_log.to_str().unwrap(),
        "--force-oem-refresh",
        "scan",
        "--json",
    ])
    .expect("parse");

    let mut out = Vec::new();
    let mut err = Vec::new();
    let rc = waypoint_cli::run_with_backend(cli, backend, &mut out, &mut err);

    let err_text = String::from_utf8(err).expect("stderr should be utf8");
    assert_eq!(rc, 0);
    assert!(
        err_text.contains("no effect without --oem"),
        "expected warning in stderr, got: {err_text:?}"
    );
}
