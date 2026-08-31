//! Tests for OEM catalog sources, using small real-data fixtures trimmed
//! from the actual Dell/Lenovo/HP catalogs (downloaded and inspected
//! 2026-08-30 — see each source module's docstring for the verification
//! notes). No network access required: fixtures under
//! `tests/fixtures/oem/` stand in for a `refresh()` call.
//!
//! Direct port of `tests/test_oem_sources.py`.

use std::path::PathBuf;

use waypoint_sources::oem::{
    DellCatalogSource, DellDriverPackSource, HpPlatformCatalogSource, LenovoDriverPackSource, ModelDriverPackSource,
};
use waypoint_sources::DriverSource;

fn fixture(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/oem").join(name)
}

fn tmp_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "waypoint-oem-test-{}-{}-{}",
        label,
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

#[test]
fn test_dell_catalog_matches_generic_pci_hwid() {
    let tmp = tmp_dir("dell-generic");
    let source = DellCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_catalog_sample.xml")).unwrap();

    // Intel Integrated Sensor Solution Driver component lists PCI\VEN_8086&DEV_7745
    let results = source.search(&["PCI\\VEN_8086&DEV_7745".to_string()]).unwrap();
    assert_eq!(results.len(), 1);
    let candidate = &results[0];
    assert_eq!(candidate.source_id, "dell_catalog");
    assert_eq!(candidate.version, "3.11.100.7733");
    assert_eq!(candidate.sha256, ""); // honestly unknown until fetch() downloads+verifies
    assert!(candidate.download_uri.starts_with("https://downloads.dell.com/"));
}

#[test]
fn test_dell_catalog_matches_specific_subsys_hwid() {
    let tmp = tmp_dir("dell-subsys");
    let source = DellCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_catalog_sample.xml")).unwrap();

    // AMD Radeon component has vendorID=1002 deviceID=6900 subDeviceID=079D subVendorID=1028
    let generic = source.search(&["PCI\\VEN_1002&DEV_6900".to_string()]).unwrap();
    let specific = source
        .search(&["PCI\\VEN_1002&DEV_6900&SUBSYS_079D1028".to_string()])
        .unwrap();
    assert_eq!(generic.len(), 1);
    assert_eq!(specific.len(), 1);
    assert_eq!(generic[0].version, "16.400.2701");
    assert_eq!(specific[0].version, "16.400.2701");
}

#[test]
fn test_dell_catalog_unmatched_hwid_returns_nothing() {
    let tmp = tmp_dir("dell-unmatched");
    let source = DellCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_catalog_sample.xml")).unwrap();
    assert_eq!(source.search(&["PCI\\VEN_FFFF&DEV_FFFF".to_string()]).unwrap(), vec![]);
}

#[test]
fn test_dell_catalog_fetch_verifies_md5_and_returns_sha256() {
    let tmp = tmp_dir("dell-fetch");
    let mut source = DellCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_catalog_sample.xml")).unwrap();
    let candidate = source.search(&["PCI\\VEN_8086&DEV_7745".to_string()]).unwrap()[0].clone();

    let payload = b"pretend driver package bytes".to_vec();
    source.set_downloader(move |_url, dest| {
        std::fs::write(dest, &payload).map_err(|e| e.to_string())?;
        Ok(dest.to_path_buf())
    });

    let out_dir = tmp.join("out");
    let err = source
        .fetch(&candidate, out_dir.to_str().unwrap())
        .expect_err("MD5 mismatch should be reported as an error, not silently accepted");
    assert!(err.contains("MD5 mismatch"), "unexpected error message: {err}");
}

#[test]
fn test_dell_driverpack_matches_system_id() {
    let tmp = tmp_dir("dell-dp-match");
    let source = DellDriverPackSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_driverpack_sample.xml")).unwrap();

    let packs = source.packs_for_model("092F").unwrap(); // OptiPlex 5070 in the fixture
    assert_eq!(packs.len(), 1);
    assert_eq!(packs[0].model_name, "OptiPlex 5070");
    assert_eq!(packs[0].hash_algorithm, "sha256");
    assert_eq!(packs[0].hash_value.len(), 64);
}

#[test]
fn test_dell_driverpack_unknown_model_returns_empty() {
    let tmp = tmp_dir("dell-dp-unknown");
    let source = DellDriverPackSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("dell_driverpack_sample.xml")).unwrap();
    assert_eq!(source.packs_for_model("ZZZZ").unwrap(), vec![]);
}

#[test]
fn test_lenovo_driverpack_matches_machine_type() {
    let tmp = tmp_dir("lenovo-match");
    let source = LenovoDriverPackSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("lenovo_catalog_sample.xml")).unwrap();

    let packs = source.packs_for_model("10M4").unwrap(); // ThinkCentre M715Q in the fixture
    assert!(!packs.is_empty());
    assert_eq!(packs[0].model_name, "ThinkCentre M715Q");
    assert_eq!(packs[0].source_id, "lenovo_driverpack");
    // Lenovo's "crc" attribute is actually SHA-256 (64 hex chars) —
    // confirmed against real catalog data, reported honestly rather
    // than as "crc".
    assert_eq!(packs[0].hash_algorithm, "sha256");
    assert_eq!(packs[0].hash_value.len(), 64);
}

#[test]
fn test_lenovo_driverpack_matches_case_insensitively_and_full_serial() {
    let tmp = tmp_dir("lenovo-case");
    let source = LenovoDriverPackSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("lenovo_catalog_sample.xml")).unwrap();

    // A real system's SMBIOS product name is longer than the 4-char type
    // code (e.g. "10M4S00100") — packs_for_model must truncate correctly.
    let packs = source.packs_for_model("10m4s00100").unwrap();
    assert!(!packs.is_empty());
}

#[test]
fn test_hp_platform_reports_known_system_id() {
    let tmp = tmp_dir("hp-known");
    let source = HpPlatformCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("hp_platformlist_sample.xml")).unwrap();

    let info = source.is_supported("1909").unwrap(); // HP ZBook 15 Mobile Workstation in the fixture
    let info = info.expect("expected known system ID to resolve");
    assert!(info.product_name.contains("ZBook 15"));
    assert!(!info.supported_os_descriptions.is_empty());
}

#[test]
fn test_hp_platform_unknown_system_id_returns_none() {
    let tmp = tmp_dir("hp-unknown");
    let source = HpPlatformCatalogSource::new(&tmp).unwrap();
    source.load_from_xml(&fixture("hp_platformlist_sample.xml")).unwrap();
    assert!(source.is_supported("ZZZZ").unwrap().is_none());
}

// Port of the two `tests/test_cli_oem_flag.py` cases that monkeypatch
// the module-level `download_file`/`extract_cab` functions
// (`test_oem_cache_dir_is_reused_not_redownloaded` and
// `test_force_oem_refresh_forces_redownload_even_when_cached`) — moved
// here, against `DellCatalogSource::refresh()` directly, using its own
// `set_downloader`/`set_extractor` seam instead of a CLI-level one. See
// `crates/cli/tests/oem_flag_tests.rs`'s module doc comment for why.

#[test]
fn test_dell_catalog_refresh_skips_download_when_cache_exists_and_not_forced() {
    let tmp = tmp_dir("dell-refresh-cached");
    std::fs::copy(fixture("dell_catalog_sample.xml"), tmp.join("CatalogPC.xml")).unwrap();

    let mut source = DellCatalogSource::new(&tmp).unwrap();
    let download_calls = std::rc::Rc::new(std::cell::Cell::new(0u32));
    let download_calls_clone = download_calls.clone();
    source.set_downloader(move |_url, _dest| {
        download_calls_clone.set(download_calls_clone.get() + 1);
        Err("download_file() should not be called when the catalog is already cached on disk (force=false)".to_string())
    });

    source.refresh(false).expect("refresh(force=false) with an existing cache should succeed without downloading");
    assert_eq!(download_calls.get(), 0);

    // Confirms it actually parsed the pre-seeded file, not just "no error".
    let results = source.search(&["PCI\\VEN_8086&DEV_7745".to_string()]).unwrap();
    assert_eq!(results.len(), 1);
}

#[test]
fn test_dell_catalog_refresh_redownloads_when_forced_even_if_cached() {
    let tmp = tmp_dir("dell-refresh-forced");
    std::fs::copy(fixture("dell_catalog_sample.xml"), tmp.join("CatalogPC.xml")).unwrap();

    let mut source = DellCatalogSource::new(&tmp).unwrap();
    let download_calls = std::rc::Rc::new(std::cell::Cell::new(0u32));
    let extract_calls = std::rc::Rc::new(std::cell::Cell::new(0u32));
    let download_calls_clone = download_calls.clone();
    let extract_calls_clone = extract_calls.clone();
    let fixture_xml = fixture("dell_catalog_sample.xml");

    source.set_downloader(move |_url, dest| {
        download_calls_clone.set(download_calls_clone.get() + 1);
        // Simulate a real download landing at `dest` (a .cab path inside a
        // tempdir) — contents don't matter since the extractor below is
        // also faked, mirroring the Python test's "fake-cab-bytes" stand-in.
        std::fs::write(dest, b"fake-cab-bytes").map_err(|e| e.to_string())?;
        Ok(dest.to_path_buf())
    });
    source.set_extractor(move |_cab_path, dest_dir| {
        extract_calls_clone.set(extract_calls_clone.get() + 1);
        let target = dest_dir.join("CatalogPC.xml");
        std::fs::copy(&fixture_xml, &target).map_err(|e| e.to_string())?;
        Ok(vec![target])
    });

    source.refresh(true).expect("refresh(force=true) should redownload even with a valid cache present");
    assert_eq!(download_calls.get(), 1); // download_file WAS called despite the pre-seeded cache
    assert_eq!(extract_calls.get(), 1);

    let results = source.search(&["PCI\\VEN_8086&DEV_7745".to_string()]).unwrap();
    assert_eq!(results.len(), 1);
}
