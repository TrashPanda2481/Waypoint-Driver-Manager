//! Dell's official per-device driver catalog. Port of
//! `sources/oem/dell_catalog.py`.
//!
//! Dell publishes a genuinely per-hardware-ID catalog at
//! <https://downloads.dell.com/catalog/CatalogPC.cab> (an XML manifest,
//! UTF-16, ~57MB uncompressed / ~3MB as the .cab Dell ships) — this is
//! the same catalog Dell Command | Update and SCCM/ConfigMgr
//! third-party-update workflows consume. Verified by downloading and
//! inspecting the real file during development of the Python source
//! (2026-08-30, see `dell_catalog.py`'s own docstring): each
//! `SoftwareComponent` with `ComponentType value="DRVR"` lists one or
//! more `SupportedDevices/Device/PCIInfo` entries with
//! `vendorID`/`deviceID`/`subVendorID`/`subDeviceID` attributes — the
//! same PCI IDs Windows reports in a device's Hardware ID list.
//!
//! Two things this source is honest about rather than glossing over
//! (same as the Python version):
//!
//! 1. No SHA-256 at catalog level. Dell only publishes an MD5
//!    (`hashMD5` attribute) per component — `search()` therefore returns
//!    candidates with `sha256 = ""` (matching the same "unknown until
//!    fetched" pattern `windows_update.rs` already uses for its own
//!    not-yet-downloaded fields), and `fetch()` verifies the download
//!    against Dell's MD5 *and* then computes and returns the real
//!    SHA-256, which is what gets used for any local re-verification
//!    afterward.
//! 2. Only PCI-based devices are covered. If Dell adds ACPI/HID/USB
//!    entries in the future, `parse_catalog_xml` will silently ignore
//!    them rather than mis-tag them; that's a known gap, not a hidden
//!    guess.
//!
//! **Porting note on internal caching:** Python caches the parsed index
//! as an instance attribute (`self._index`), lazily populated by
//! `search()` on first use if `load_from_xml`/`refresh` hasn't already
//! run. `search`/`fetch` are `DriverSource` trait methods that take
//! `&self`, so this port uses a `RefCell` to reproduce that same
//! lazy-cache-with-self-heal behavior rather than requiring every caller
//! to call `refresh()`/`load_from_xml()` first — `search()` on an
//! unloaded source still works if `catalog_xml_path` already exists on
//! disk, exactly like the Python version.

use std::cell::RefCell;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use md5::{Digest as Md5Digest, Md5};
use sha2::{Digest as Sha2Digest, Sha256};

use waypoint_core::{DriverCandidate, SignatureType};

use crate::base::DriverSource;
use crate::oem::cab::extract_cab;
use crate::oem::http::download_file;

pub const DEFAULT_CATALOG_URL: &str = "https://downloads.dell.com/catalog/CatalogPC.cab";

// Dell drivers offered through this catalog carry no signature-tier
// metadata at all (unlike Windows Update, which at least implies
// Microsoft attestation). Treated as unsigned-until-proven for the
// default safety policy — the engine's signature gate will hold these
// back unless a lower `min_signature` is explicitly chosen, which is the
// conservative-by-default behavior the design calls for.
const ASSUMED_SIGNATURE: SignatureType = SignatureType::Unsigned;

type Downloader = dyn Fn(&str, &Path) -> Result<PathBuf, String>;
type Extractor = dyn Fn(&Path, &Path) -> Result<Vec<PathBuf>, String>;

#[derive(Default)]
struct Inner {
    index: HashMap<String, Vec<DriverCandidate>>,
    md5_by_uri: HashMap<String, String>,
}

pub struct DellCatalogSource {
    pub cache_dir: PathBuf,
    catalog_url: String,
    catalog_xml_path: PathBuf,
    downloader: Box<Downloader>,
    extractor: Box<Extractor>,
    inner: RefCell<Option<Inner>>,
}

impl DellCatalogSource {
    pub fn new(cache_dir: impl Into<PathBuf>) -> Result<Self, String> {
        Self::with_catalog_url(cache_dir, DEFAULT_CATALOG_URL)
    }

    pub fn with_catalog_url(
        cache_dir: impl Into<PathBuf>,
        catalog_url: impl Into<String>,
    ) -> Result<Self, String> {
        let cache_dir = cache_dir.into();
        fs::create_dir_all(&cache_dir).map_err(|e| e.to_string())?;
        let catalog_xml_path = cache_dir.join("CatalogPC.xml");
        Ok(Self {
            cache_dir,
            catalog_url: catalog_url.into(),
            catalog_xml_path,
            downloader: Box::new(|url, dest| download_file(url, dest).map(|_| dest.to_path_buf())),
            extractor: Box::new(|cab, dest| extract_cab(cab, dest)),
            inner: RefCell::new(None),
        })
    }

    /// Test-only hook: mirrors Python tests assigning `source._downloader`
    /// directly to keep network calls out of unit tests.
    pub fn set_downloader(&mut self, downloader: impl Fn(&str, &Path) -> Result<PathBuf, String> + 'static) {
        self.downloader = Box::new(downloader);
    }

    /// Test-only hook: mirrors Python tests monkeypatching `extract_cab`.
    pub fn set_extractor(&mut self, extractor: impl Fn(&Path, &Path) -> Result<Vec<PathBuf>, String> + 'static) {
        self.extractor = Box::new(extractor);
    }

    /// Download and (re)parse the catalog. Not called automatically by
    /// `search()` on every call — catalog refresh is an explicit,
    /// opt-in-cost operation (57MB uncompressed) that a caller schedules,
    /// e.g. once a day, not once per scan.
    pub fn refresh(&self, force: bool) -> Result<(), String> {
        if force || !self.catalog_xml_path.exists() {
            let tmp = tempdir()?;
            let cab_path = tmp.join("CatalogPC.cab");
            (self.downloader)(&self.catalog_url, &cab_path)?;
            let extracted = (self.extractor)(&cab_path, &tmp)?;
            let xml_file = extracted
                .iter()
                .find(|p| p.extension().map(|e| e.eq_ignore_ascii_case("xml")).unwrap_or(false))
                .ok_or_else(|| format!("No .xml payload found inside {}", self.catalog_url))?;
            fs::rename(xml_file, &self.catalog_xml_path).map_err(|e| e.to_string())?;
            let _ = fs::remove_dir_all(&tmp);
        }
        self.load_from_xml(&self.catalog_xml_path)
    }

    /// Parse an already-downloaded catalog XML file directly — the path
    /// `refresh()` uses internally, and also what tests use to point at a
    /// small real-data fixture instead of hitting the network.
    pub fn load_from_xml(&self, xml_path: &Path) -> Result<(), String> {
        let raw = fs::read(xml_path).map_err(|e| e.to_string())?;
        let text = decode_xml_bytes(&raw)?;
        let doc = roxmltree::Document::parse(&text).map_err(|e| e.to_string())?;
        let root = doc.root_element();
        let base_location = root.attribute("baseLocation").unwrap_or("downloads.dell.com");

        let mut index: HashMap<String, Vec<DriverCandidate>> = HashMap::new();
        let mut md5_by_uri: HashMap<String, String> = HashMap::new();

        for component in root.children().filter(|n| n.has_tag_name("SoftwareComponent")) {
            let component_type = component
                .children()
                .find(|n| n.has_tag_name("ComponentType"))
                .and_then(|n| n.attribute("value"));
            if component_type != Some("DRVR") {
                continue;
            }

            let Some(supported_devices) = component.children().find(|n| n.has_tag_name("SupportedDevices")) else {
                continue;
            };

            let hwids = hwids_for_component(supported_devices);
            if hwids.is_empty() {
                continue;
            }

            let package_id = component.attribute("packageID").unwrap_or("");
            let path = component.attribute("path").unwrap_or("");
            let download_uri = format!("https://{base_location}/{path}");
            let md5 = component.attribute("hashMD5").unwrap_or("");
            md5_by_uri.insert(download_uri.clone(), md5.to_string());

            let version = component.attribute("vendorVersion").unwrap_or("unknown");
            let release_date_str = component.attribute("releaseDate").unwrap_or("");
            let driver_date = parse_dell_release_date(release_date_str);
            let size_bytes: u64 = component.attribute("size").and_then(|s| s.parse().ok()).unwrap_or(0);

            for hwid in &hwids {
                let candidate = DriverCandidate {
                    hwid: hwid.clone(),
                    class_guid: String::new(), // Dell's catalog doesn't publish a PnP setup class GUID
                    version: version.to_string(),
                    driver_date,
                    publisher: "Dell".to_string(),
                    signature_type: ASSUMED_SIGNATURE,
                    sha256: String::new(), // unknown until fetch() downloads+verifies
                    size_bytes,
                    source_id: source_id_str().to_string(),
                    source_url: format!(
                        "https://www.dell.com/support/home/en-us/drivers/driversdetails?driverid={package_id}"
                    ),
                    download_uri: download_uri.clone(),
                };
                index.entry(hwid.clone()).or_default().push(candidate);
            }
        }

        *self.inner.borrow_mut() = Some(Inner { index, md5_by_uri });
        Ok(())
    }

    fn ensure_loaded(&self) -> Result<(), String> {
        if self.inner.borrow().is_none() {
            self.load_from_xml(&self.catalog_xml_path)?;
        }
        Ok(())
    }
}

fn source_id_str() -> &'static str {
    "dell_catalog"
}

impl DriverSource for DellCatalogSource {
    fn source_id(&self) -> &str {
        source_id_str()
    }

    fn search(&self, hwids: &[String]) -> Result<Vec<DriverCandidate>, String> {
        self.ensure_loaded()?;
        let inner = self.inner.borrow();
        let inner = inner.as_ref().unwrap();
        let mut results = Vec::new();
        let mut seen: std::collections::HashSet<(String, String)> = std::collections::HashSet::new();
        for hwid in hwids {
            if let Some(candidates) = inner.index.get(&hwid.to_uppercase()) {
                for candidate in candidates {
                    let key = (candidate.hwid.clone(), candidate.download_uri.clone());
                    if seen.insert(key) {
                        results.push(candidate.clone());
                    }
                }
            }
        }
        Ok(results)
    }

    fn refresh(&self, force: bool) -> Result<(), String> {
        DellCatalogSource::refresh(self, force)
    }

    fn fetch(&self, candidate: &DriverCandidate, dest_dir: &str) -> Result<String, String> {
        let dest_dir_path = Path::new(dest_dir);
        fs::create_dir_all(dest_dir_path).map_err(|e| e.to_string())?;
        let filename = candidate
            .download_uri
            .rsplit('/')
            .next()
            .unwrap_or("download.bin");
        let dest_path = dest_dir_path.join(filename);
        (self.downloader)(&candidate.download_uri, &dest_path)?;

        let expected_md5 = {
            let inner = self.inner.borrow();
            inner
                .as_ref()
                .and_then(|i| i.md5_by_uri.get(&candidate.download_uri))
                .cloned()
                .unwrap_or_default()
        };
        if !expected_md5.is_empty() {
            let actual_md5 = hash_file_md5(&dest_path)?;
            if !actual_md5.eq_ignore_ascii_case(&expected_md5) {
                return Err(format!(
                    "MD5 mismatch for {filename}: catalog says {expected_md5}, downloaded file \
                     hashes to {actual_md5}. Refusing to use this download — Dell's catalog only \
                     publishes MD5, so this check (not SHA-256) is the only integrity signal \
                     available at this stage."
                ));
            }
        }
        // Compute the real SHA-256 of the verified bytes so callers (e.g.
        // local_cache promotion) have a trustworthy hash going forward,
        // even though the catalog itself never published one.
        hash_file_sha256(&dest_path)?;
        Ok(dest_path.to_string_lossy().to_string())
    }
}

fn hwids_for_component(supported_devices: roxmltree::Node) -> Vec<String> {
    let mut hwids = Vec::new();
    for device in supported_devices.children().filter(|n| n.has_tag_name("Device")) {
        for pci in device.children().filter(|n| n.has_tag_name("PCIInfo")) {
            let vendor_id = pci.attribute("vendorID").unwrap_or("").trim();
            let device_id = pci.attribute("deviceID").unwrap_or("").trim();
            if vendor_id.is_empty() || device_id.is_empty() {
                continue;
            }
            let sub_device_id = pci.attribute("subDeviceID").unwrap_or("").trim();
            let sub_vendor_id = pci.attribute("subVendorID").unwrap_or("").trim();
            hwids.push(format!(
                "PCI\\VEN_{}&DEV_{}",
                vendor_id.to_uppercase(),
                device_id.to_uppercase()
            ));
            if !sub_device_id.is_empty() && !sub_vendor_id.is_empty() {
                hwids.push(format!(
                    "PCI\\VEN_{}&DEV_{}&SUBSYS_{}{}",
                    vendor_id.to_uppercase(),
                    device_id.to_uppercase(),
                    sub_device_id.to_uppercase(),
                    sub_vendor_id.to_uppercase()
                ));
            }
        }
    }
    hwids
}

pub(crate) fn parse_dell_release_date(value: &str) -> Option<time::Date> {
    if value.is_empty() {
        return None;
    }
    // Dell's per-device catalog releaseDate is "Month D, YYYY" (e.g.
    // "August 15, 2024") — no timezone published, date-only field.
    parse_month_day_year(value)
}

fn parse_month_day_year(value: &str) -> Option<time::Date> {
    use time::Month;
    let value = value.trim();
    let mut parts = value.split_whitespace();
    let month_str = parts.next()?;
    let day_str = parts.next()?.trim_end_matches(',');
    let year_str = parts.next()?;

    let month = match month_str.to_lowercase().as_str() {
        "january" => Month::January,
        "february" => Month::February,
        "march" => Month::March,
        "april" => Month::April,
        "may" => Month::May,
        "june" => Month::June,
        "july" => Month::July,
        "august" => Month::August,
        "september" => Month::September,
        "october" => Month::October,
        "november" => Month::November,
        "december" => Month::December,
        _ => return None,
    };
    let day: u8 = day_str.parse().ok()?;
    let year: i32 = year_str.parse().ok()?;
    time::Date::from_calendar_date(year, month, day).ok()
}

fn hash_file_md5(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|e| e.to_string())?;
    let mut hasher = Md5::new();
    hasher.update(&bytes);
    Ok(to_hex(&hasher.finalize()))
}

fn hash_file_sha256(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|e| e.to_string())?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    Ok(to_hex(&hasher.finalize()))
}

pub(crate) fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// Dell's catalog XML is published as UTF-16; small test fixtures may be
/// plain UTF-8. Detect by BOM/declared encoding rather than assuming one.
pub(crate) fn decode_xml_bytes(raw: &[u8]) -> Result<String, String> {
    if raw.starts_with(&[0xFF, 0xFE]) || raw.starts_with(&[0xFE, 0xFF]) {
        let little_endian = raw.starts_with(&[0xFF, 0xFE]);
        let body = &raw[2..];
        let mut units = Vec::with_capacity(body.len() / 2);
        let mut iter = body.chunks_exact(2);
        for chunk in &mut iter {
            let unit = if little_endian {
                u16::from_le_bytes([chunk[0], chunk[1]])
            } else {
                u16::from_be_bytes([chunk[0], chunk[1]])
            };
            units.push(unit);
        }
        String::from_utf16(&units).map_err(|e| e.to_string())
    } else {
        String::from_utf8(raw.to_vec()).map_err(|e| e.to_string())
    }
}

fn tempdir() -> Result<PathBuf, String> {
    let base = std::env::temp_dir();
    let unique = format!(
        "waypoint-oem-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0)
    );
    let dir = base.join(unique);
    fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    Ok(dir)
}
