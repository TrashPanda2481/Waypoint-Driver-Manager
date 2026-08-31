//! Tests for `waypoint driverpack` -- the model-based OEM lookup path
//! wired up in this session. No Python reference implementation exists
//! for this command (see `waypoint_engine::factory::
//! build_oem_model_pack_sources`'s doc comment), so this is new coverage
//! rather than a port.
//!
//! Uses `cmd_driverpack_with_model` (the DI seam for this command) with
//! a directly-constructed `SystemModel` instead of
//! `waypoint_platform::detect_system_model()` -- this sandbox has no
//! real SMBIOS/DMI data to detect in the first place (confirmed: no
//! `/sys/class/dmi/id` at all here), so injecting the value is the only
//! way to exercise this path at all, not just a convenience.
//!
//! **No real network access**, same convention as `oem_flag_tests.rs`:
//! Dell/Lenovo/HP's on-disk cache files are pre-seeded with the same
//! real-data fixtures `crates/sources/tests/oem_tests.rs` uses, so
//! `refresh(force=false)` finds each catalog "already downloaded" and
//! just parses it -- no download is attempted.

use std::fs;

use waypoint_cli::cmd_driverpack_with_model;
use waypoint_platform::SystemModel;

const DELL_FIXTURE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/oem/dell_driverpack_sample.xml");
const LENOVO_FIXTURE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/oem/lenovo_catalog_sample.xml");
const HP_FIXTURE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/oem/hp_platformlist_sample.xml");

/// Seed all three caches with real fixture data (the "happy path" seed).
/// Individual tests overwrite one file afterward when they need a
/// different scenario for that one source.
fn seed_all(cache_dir: &std::path::Path) {
    fs::create_dir_all(cache_dir).expect("create cache_dir");
    fs::copy(DELL_FIXTURE, cache_dir.join("DriverPackCatalog.xml")).expect("seed Dell fixture");
    fs::copy(LENOVO_FIXTURE, cache_dir.join("catalogv2.xml")).expect("seed Lenovo fixture");
    fs::copy(HP_FIXTURE, cache_dir.join("platformList.xml")).expect("seed HP fixture");
}

#[test]
fn test_driverpack_found_via_dell_sku() {
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    let model = SystemModel {
        product_name: None,
        sku_number: Some("092F".to_string()), // one of the two OptiPlex 5070 systemIDs in the fixture
        baseboard_product: None,
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, true, &mut out, &mut err).unwrap();

    assert_eq!(code, waypoint_cli::EXIT_CLEAN);
    let json: serde_json::Value = serde_json::from_slice(&out).expect("valid JSON");
    let dell = json["dell"].as_array().unwrap();
    assert_eq!(dell.len(), 1);
    assert_eq!(dell[0]["model_key"], "092F");
    assert!(json["lenovo"].as_array().unwrap().is_empty());
    assert!(json["hp_supported"].is_null());
}

#[test]
fn test_driverpack_dell_falls_back_to_name_when_sku_unmatched() {
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    // sku_number deliberately doesn't match anything in the catalog;
    // product_name matches two systemIDs (092F and 0932) that both
    // display as "OptiPlex 5070" in the fixture.
    let model = SystemModel {
        product_name: Some("OptiPlex 5070".to_string()),
        sku_number: Some("ZZZZ".to_string()),
        baseboard_product: None,
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, true, &mut out, &mut err).unwrap();

    assert_eq!(code, waypoint_cli::EXIT_CLEAN);
    let json: serde_json::Value = serde_json::from_slice(&out).expect("valid JSON");
    assert_eq!(json["dell"].as_array().unwrap().len(), 2);
}

#[test]
fn test_driverpack_found_via_lenovo_machine_type() {
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    let model = SystemModel {
        // Full SMBIOS serial; packs_for_model truncates to the 4-char
        // machine-type code internally (same as
        // test_lenovo_driverpack_matches_case_insensitively_and_full_serial).
        product_name: Some("10M4S00100".to_string()),
        sku_number: None,
        baseboard_product: None,
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, true, &mut out, &mut err).unwrap();

    assert_eq!(code, waypoint_cli::EXIT_CLEAN);
    let json: serde_json::Value = serde_json::from_slice(&out).expect("valid JSON");
    assert!(!json["lenovo"].as_array().unwrap().is_empty());
}

#[test]
fn test_driverpack_found_via_hp_is_supported() {
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    let model = SystemModel {
        product_name: None,
        sku_number: None,
        baseboard_product: Some("1909".to_string()), // HP ZBook 15 Mobile Workstation in the fixture
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, true, &mut out, &mut err).unwrap();

    assert_eq!(code, waypoint_cli::EXIT_CLEAN);
    let json: serde_json::Value = serde_json::from_slice(&out).expect("valid JSON");
    assert!(json["hp_supported"]["product_name"]
        .as_str()
        .unwrap()
        .contains("ZBook 15"));
}

#[test]
fn test_driverpack_none_found_returns_action_needed_exit_code() {
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    // No field matches anything in any of the three seeded catalogs.
    let model = SystemModel {
        product_name: Some("NoSuchModel".to_string()),
        sku_number: Some("ZZZZ".to_string()),
        baseboard_product: Some("0000".to_string()),
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, false, &mut out, &mut err).unwrap();

    assert_eq!(code, waypoint_cli::EXIT_ACTION_NEEDED);
    let text = String::from_utf8(out).unwrap();
    assert!(text.contains("Dell: no driver pack found"));
    assert!(text.contains("Lenovo: no driver pack found"));
    assert!(text.contains("HP: not found in platform list"));
    assert!(err.is_empty(), "no source should have failed in this scenario");
}

#[test]
fn test_driverpack_partial_refresh_failure_still_reports_other_sources() {
    // Dell and Lenovo get real, valid fixtures. HP's cached file is
    // corrupted (not valid XML) -- HpPlatformCatalogSource::refresh()
    // sees the file already exists (so it skips any network call) but
    // then fails to parse it, so refresh() itself returns Err. This
    // exercises the partial-failure path with zero network access, not
    // by actually going offline.
    let tmp = tempfile::tempdir().unwrap();
    seed_all(tmp.path());
    fs::write(tmp.path().join("platformList.xml"), b"not valid xml at all").unwrap();
    let cache_dir = tmp.path().to_str().unwrap().to_string();

    let model = SystemModel {
        product_name: Some("OptiPlex 5070".to_string()),
        sku_number: None,
        baseboard_product: Some("1909".to_string()),
    };
    let mut out = Vec::new();
    let mut err = Vec::new();
    let code =
        cmd_driverpack_with_model(&model, Some(&cache_dir), false, true, &mut out, &mut err).unwrap();

    // Dell still resolves via name fallback -> still EXIT_CLEAN overall.
    assert_eq!(code, waypoint_cli::EXIT_CLEAN);
    let json: serde_json::Value = serde_json::from_slice(&out).expect("valid JSON");
    assert_eq!(json["dell"].as_array().unwrap().len(), 2);
    assert!(json["hp_supported"].is_null());

    let err_text = String::from_utf8(err).unwrap();
    assert!(
        err_text.contains("HP platform-list refresh failed"),
        "expected an HP refresh warning, got: {err_text}"
    );
}
