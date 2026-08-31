//! Lenovo's per-model driver pack catalog. Port of
//! `sources/oem/lenovo_driverpack.py`.
//!
//! <https://download.lenovo.com/cdrt/td/catalogv2.xml> — verified by
//! downloading and inspecting the real file (2026-08-30, per the Python
//! source's docstring). Each `<Model>` lists one or more 4-character
//! `<Types>/<Type>` machine-type codes (the same code that prefixes a
//! real Lenovo `Win32_ComputerSystem.Model` / SMBIOS system-product-name
//! string, e.g. "20U9..." for a ThinkPad), alongside `<SCCM>` elements
//! (full driver-pack bundles per OS/version) and `<BIOS>` elements for
//! firmware.
//!
//! Only `<SCCM>` (driver pack) entries are exposed through
//! `packs_for_model`. `<BIOS>` entries are intentionally excluded:
//! firmware flashing is a categorically higher-risk operation than
//! installing a device driver (a bad BIOS flash can brick a machine in a
//! way no System Restore / driver rollback fixes), and Waypoint doesn't
//! offer it through the same lower-friction path used for drivers until
//! there's an explicit, separately confirmed firmware workflow —
//! consistent with the mandatory-restore-point/per-driver-backup safety
//! posture in `docs/Architecture.md` section 3.3.
//!
//! Vendor-naming quirk worth flagging rather than silently trusting: the
//! `crc` attribute Lenovo publishes on each `<SCCM>`/`<BIOS>` element is
//! a 64-hex-character digest — i.e. actually a SHA-256 hash despite the
//! attribute name, not a CRC32. Confirmed by length (64 hex chars = 32
//! bytes) against real catalog data. `DriverPack.hash_algorithm` reports
//! this correctly as `"sha256"`, not `"crc"`.
//!
//! Same lazy-cache-via-`RefCell` note as `dell_catalog.rs` applies:
//! `packs_for_model` self-heals by loading from `catalog_xml_path` if
//! nothing has been loaded yet.

use std::cell::RefCell;
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use time::Date;

use crate::oem::http::download_file;
use crate::oem::model_pack::{DriverPack, ModelDriverPackSource};

pub const DEFAULT_CATALOG_URL: &str = "https://download.lenovo.com/cdrt/td/catalogv2.xml";

type Downloader = dyn Fn(&str, &Path) -> Result<PathBuf, String>;

pub struct LenovoDriverPackSource {
    pub cache_dir: PathBuf,
    catalog_url: String,
    catalog_xml_path: PathBuf,
    downloader: Box<Downloader>,
    index: RefCell<Option<HashMap<String, Vec<DriverPack>>>>,
}

impl LenovoDriverPackSource {
    pub fn new(cache_dir: impl Into<PathBuf>) -> Result<Self, String> {
        Self::with_catalog_url(cache_dir, DEFAULT_CATALOG_URL)
    }

    pub fn with_catalog_url(
        cache_dir: impl Into<PathBuf>,
        catalog_url: impl Into<String>,
    ) -> Result<Self, String> {
        let cache_dir = cache_dir.into();
        fs::create_dir_all(&cache_dir).map_err(|e| e.to_string())?;
        let catalog_xml_path = cache_dir.join("catalogv2.xml");
        Ok(Self {
            cache_dir,
            catalog_url: catalog_url.into(),
            catalog_xml_path,
            downloader: Box::new(|url, dest| download_file(url, dest).map(|_| dest.to_path_buf())),
            index: RefCell::new(None),
        })
    }

    pub fn set_downloader(&mut self, downloader: impl Fn(&str, &Path) -> Result<PathBuf, String> + 'static) {
        self.downloader = Box::new(downloader);
    }

    /// Lenovo publishes this catalog as plain XML, not a `.cab` — no
    /// extraction step needed, just a direct download.
    pub fn refresh(&self, force: bool) -> Result<(), String> {
        if force || !self.catalog_xml_path.exists() {
            (self.downloader)(&self.catalog_url, &self.catalog_xml_path)?;
        }
        *self.index.borrow_mut() = None;
        self.load_from_xml(&self.catalog_xml_path)
    }

    pub fn load_from_xml(&self, xml_path: &Path) -> Result<(), String> {
        let text = fs::read_to_string(xml_path).map_err(|e| e.to_string())?;
        let doc = roxmltree::Document::parse(&text).map_err(|e| e.to_string())?;
        let root = doc.root_element();

        let mut index: HashMap<String, Vec<DriverPack>> = HashMap::new();

        for model in root.children().filter(|n| n.has_tag_name("Model")) {
            let model_name = model.attribute("name").unwrap_or("unknown").to_string();
            let types: Vec<String> = model
                .children()
                .filter(|n| n.has_tag_name("Types"))
                .flat_map(|t| t.children().filter(|c| c.has_tag_name("Type")))
                .filter_map(|t| t.text().map(|s| s.trim().to_string()))
                .filter(|s| !s.is_empty())
                .collect();
            if types.is_empty() {
                continue;
            }

            for sccm in model.children().filter(|n| n.has_tag_name("SCCM")) {
                let url = sccm.text().unwrap_or("").trim().to_string();
                if url.is_empty() {
                    continue;
                }
                let crc = sccm.attribute("crc").unwrap_or("");
                let md5 = sccm.attribute("md5").unwrap_or("");
                let (hash_algo, hash_value) = if crc.len() == 64 {
                    ("sha256".to_string(), crc.to_string())
                } else {
                    ("md5".to_string(), md5.to_string())
                };
                let pack_id = url.rsplit('/').next().unwrap_or(&url).to_string();
                let os_label = sccm.attribute("os").unwrap_or("unknown").to_string();
                let version = sccm.attribute("version").unwrap_or("unknown").to_string();
                let release_date = parse_lenovo_date(sccm.attribute("date").unwrap_or(""));

                for type_code in &types {
                    let key = type_code.to_uppercase();
                    let pack = DriverPack {
                        pack_id: pack_id.clone(),
                        model_name: model_name.clone(),
                        model_key: key.clone(),
                        os_label: os_label.clone(),
                        version: version.clone(),
                        release_date,
                        url: url.clone(),
                        hash_algorithm: hash_algo.clone(),
                        hash_value: hash_value.clone(),
                        size_bytes: 0, // not published by this catalog
                        source_id: "lenovo_driverpack".to_string(),
                    };
                    index.entry(key).or_default().push(pack);
                }
            }
        }

        *self.index.borrow_mut() = Some(index);
        Ok(())
    }
}

impl ModelDriverPackSource for LenovoDriverPackSource {
    fn source_id(&self) -> &str {
        "lenovo_driverpack"
    }

    /// `model_key` is the 4-character Lenovo machine-type code — the
    /// first 4 characters of the real system's SMBIOS product name.
    /// Case-insensitive and tolerant of a full product-name string being
    /// passed in (only the first 4 characters are used for lookup).
    fn packs_for_model(&self, model_key: &str) -> Result<Vec<DriverPack>, String> {
        if self.index.borrow().is_none() {
            self.load_from_xml(&self.catalog_xml_path)?;
        }
        let trimmed = model_key.trim().to_uppercase();
        let lookup_key: String = trimmed.chars().take(4).collect();
        let index = self.index.borrow();
        Ok(index
            .as_ref()
            .unwrap()
            .get(&lookup_key)
            .cloned()
            .unwrap_or_default())
    }
}

fn parse_lenovo_date(value: &str) -> Option<Date> {
    if value.is_empty() {
        return None;
    }
    let mut parts = value.split('-');
    let year: i32 = parts.next()?.parse().ok()?;
    let month_num: u8 = parts.next()?.parse().ok()?;
    let day: u8 = parts.next()?.parse().ok()?;
    let month = time::Month::try_from(month_num).ok()?;
    Date::from_calendar_date(year, month, day).ok()
}
