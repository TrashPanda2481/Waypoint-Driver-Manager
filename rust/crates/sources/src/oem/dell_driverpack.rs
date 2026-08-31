//! Dell's per-model driver pack catalog. Port of
//! `sources/oem/dell_driverpack.py`.
//!
//! <https://downloads.dell.com/catalog/DriverPackCatalog.cab> — verified
//! by downloading and inspecting the real file (2026-08-30, per the
//! Python source's docstring). Each `DriverPackage` lists
//! `SupportedSystems/Brand/Model` entries with a `systemID` attribute
//! (Dell's SMBIOS-reported system ID) and, unlike the per-device
//! `CatalogPC.cab`, a real `Cryptography/Hash algorithm="SHA256"` per
//! package — so `DriverPack.hash_algorithm` here is honestly `"sha256"`,
//! not a fallback.
//!
//! Same lazy-cache-via-`RefCell` note as `dell_catalog.rs` applies here:
//! `packs_for_model` self-heals by loading from `catalog_xml_path` if
//! nothing has been loaded yet.

use std::cell::RefCell;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use time::Date;

use crate::oem::cab::extract_cab;
use crate::oem::dell_catalog::decode_xml_bytes;
use crate::oem::http::download_file;
use crate::oem::model_pack::{DriverPack, ModelDriverPackSource};

pub const DEFAULT_CATALOG_URL: &str = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";

type Downloader = dyn Fn(&str, &Path) -> Result<PathBuf, String>;
type Extractor = dyn Fn(&Path, &Path) -> Result<Vec<PathBuf>, String>;

pub struct DellDriverPackSource {
    pub cache_dir: PathBuf,
    catalog_url: String,
    catalog_xml_path: PathBuf,
    downloader: Box<Downloader>,
    extractor: Box<Extractor>,
    index: RefCell<Option<HashMap<String, Vec<DriverPack>>>>,
}

impl DellDriverPackSource {
    pub fn new(cache_dir: impl Into<PathBuf>) -> Result<Self, String> {
        Self::with_catalog_url(cache_dir, DEFAULT_CATALOG_URL)
    }

    pub fn with_catalog_url(
        cache_dir: impl Into<PathBuf>,
        catalog_url: impl Into<String>,
    ) -> Result<Self, String> {
        let cache_dir = cache_dir.into();
        fs::create_dir_all(&cache_dir).map_err(|e| e.to_string())?;
        let catalog_xml_path = cache_dir.join("DriverPackCatalog.xml");
        Ok(Self {
            cache_dir,
            catalog_url: catalog_url.into(),
            catalog_xml_path,
            downloader: Box::new(|url, dest| download_file(url, dest).map(|_| dest.to_path_buf())),
            extractor: Box::new(|cab, dest| extract_cab(cab, dest)),
            index: RefCell::new(None),
        })
    }

    pub fn set_downloader(&mut self, downloader: impl Fn(&str, &Path) -> Result<PathBuf, String> + 'static) {
        self.downloader = Box::new(downloader);
    }

    pub fn set_extractor(&mut self, extractor: impl Fn(&Path, &Path) -> Result<Vec<PathBuf>, String> + 'static) {
        self.extractor = Box::new(extractor);
    }

    pub fn refresh(&self, force: bool) -> Result<(), String> {
        if force || !self.catalog_xml_path.exists() {
            let dir = make_tempdir()?;
            let cab_path = dir.join("DriverPackCatalog.cab");
            (self.downloader)(&self.catalog_url, &cab_path)?;
            let extracted = (self.extractor)(&cab_path, &dir)?;
            let xml_file = extracted
                .iter()
                .find(|p| p.extension().map(|e| e.eq_ignore_ascii_case("xml")).unwrap_or(false))
                .ok_or_else(|| format!("No .xml payload found inside {}", self.catalog_url))?;
            fs::rename(xml_file, &self.catalog_xml_path).map_err(|e| e.to_string())?;
            let _ = fs::remove_dir_all(&dir);
        }
        self.load_from_xml(&self.catalog_xml_path)
    }

    pub fn load_from_xml(&self, xml_path: &Path) -> Result<(), String> {
        let raw = fs::read(xml_path).map_err(|e| e.to_string())?;
        let text = decode_xml_bytes(&raw)?;
        let doc = roxmltree::Document::parse(&text).map_err(|e| e.to_string())?;
        let root = doc.root_element();
        let base_location = root.attribute("baseLocation").unwrap_or("downloads.dell.com");

        let mut index: HashMap<String, Vec<DriverPack>> = HashMap::new();

        for package in root.descendants().filter(|n| n.has_tag_name("DriverPackage")) {
            let models: Vec<_> = package
                .descendants()
                .filter(|n| n.has_tag_name("Model") && n.parent().map(|p| p.has_tag_name("Brand")).unwrap_or(false))
                .collect();
            if models.is_empty() {
                continue;
            }

            let os_names: Vec<String> = package
                .descendants()
                .filter(|n| n.has_tag_name("OperatingSystem"))
                .filter_map(|os_el| {
                    os_el
                        .children()
                        .find(|c| c.has_tag_name("Display"))
                        .and_then(|d| d.text())
                        .map(|t| t.trim().to_string())
                })
                .collect();
            let os_label = if os_names.iter().any(|s| !s.is_empty()) {
                os_names.into_iter().filter(|s| !s.is_empty()).collect::<Vec<_>>().join(", ")
            } else {
                "unknown".to_string()
            };

            let (hash_algo, hash_value) = best_hash(package);
            let path = package.attribute("path").unwrap_or("");
            let url = format!("https://{base_location}/{path}");
            let release_dt = parse_dell_driverpack_date(package.attribute("dateTime").unwrap_or(""));
            let pack_id = package.attribute("releaseID").unwrap_or("").to_string();
            let version = package.attribute("dellVersion").unwrap_or("unknown").to_string();
            let size_bytes: u64 = package.attribute("size").and_then(|s| s.parse().ok()).unwrap_or(0);

            for model in models {
                let system_id = model.attribute("systemID").unwrap_or("");
                if system_id.is_empty() {
                    continue;
                }
                let pack = DriverPack {
                    pack_id: pack_id.clone(),
                    model_name: model.attribute("name").unwrap_or("unknown").to_string(),
                    model_key: system_id.to_string(),
                    os_label: os_label.clone(),
                    version: version.clone(),
                    release_date: release_dt,
                    url: url.clone(),
                    hash_algorithm: hash_algo.clone(),
                    hash_value: hash_value.clone(),
                    size_bytes,
                    source_id: "dell_driverpack".to_string(),
                };
                index.entry(system_id.to_uppercase()).or_default().push(pack);
            }
        }

        *self.index.borrow_mut() = Some(index);
        Ok(())
    }
}

impl ModelDriverPackSource for DellDriverPackSource {
    fn source_id(&self) -> &str {
        "dell_driverpack"
    }

    fn packs_for_model(&self, model_key: &str) -> Result<Vec<DriverPack>, String> {
        if self.index.borrow().is_none() {
            self.load_from_xml(&self.catalog_xml_path)?;
        }
        let index = self.index.borrow();
        Ok(index
            .as_ref()
            .unwrap()
            .get(&model_key.to_uppercase())
            .cloned()
            .unwrap_or_default())
    }
}

/// Prefer SHA256, fall back to SHA1, then MD5 — whatever the catalog
/// actually published for this specific package (older packages in this
/// catalog sometimes only carry weaker hashes).
fn best_hash(package: roxmltree::Node) -> (String, String) {
    let Some(crypto) = package.descendants().find(|n| n.has_tag_name("Cryptography")) else {
        return ("md5".to_string(), package.attribute("hashMD5").unwrap_or("").to_string());
    };
    let mut hashes: HashMap<String, String> = HashMap::new();
    for h in crypto.children().filter(|n| n.has_tag_name("Hash")) {
        let algo = h.attribute("algorithm").unwrap_or("").to_uppercase();
        let value = h.text().unwrap_or("").trim().to_string();
        hashes.insert(algo, value);
    }
    for algo in ["SHA256", "SHA1", "MD5"] {
        if let Some(v) = hashes.get(algo) {
            if !v.is_empty() {
                return (algo.to_lowercase(), v.clone());
            }
        }
    }
    ("md5".to_string(), package.attribute("hashMD5").unwrap_or("").to_string())
}

fn parse_dell_driverpack_date(value: &str) -> Option<Date> {
    // Dell's driver-pack catalog dateTime attribute is either a full
    // "YYYY-MM-DDTHH:MM:SS" timestamp or a bare "YYYY-MM-DD" date — no
    // timezone published either way, so this only ever extracts the date
    // part. Parsed manually (rather than via time's format-description
    // combinators) to keep this a simple, dependency-free string split,
    // mirroring the Python version's `datetime.strptime` fallback chain.
    if value.is_empty() {
        return None;
    }
    let date_part = value.get(0..10)?;
    let mut segments = date_part.split('-');
    let year: i32 = segments.next()?.parse().ok()?;
    let month_num: u8 = segments.next()?.parse().ok()?;
    let day: u8 = segments.next()?.parse().ok()?;
    let month = time::Month::try_from(month_num).ok()?;
    time::Date::from_calendar_date(year, month, day).ok()
}

fn make_tempdir() -> Result<PathBuf, String> {
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
