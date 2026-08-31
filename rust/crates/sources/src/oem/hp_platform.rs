//! HP platform support lookup — deliberately NOT a driver source. Port
//! of `sources/oem/hp_platform.py`.
//!
//! HP's public update feed (`HpCatalogForSms.latest.cab`,
//! <https://hpia.hpcloud.hp.com/downloads/sccmcatalog/HpCatalogForSms.latest.cab>)
//! is a WSUS Software Distribution Package (SDP) feed. Verified by
//! downloading and inspecting the real file (2026-08-30, per the Python
//! source's docstring): applicability for each update is expressed as
//! `bar:WmiQuery` elements containing arbitrary WQL, e.g.
//!
//! ```text
//! select * from Win32_ComputerSystem
//! where (Manufacturer='Hewlett-Packard' and not (Model like '%Proliant%'))
//!    or (Manufacturer='HP')
//! ```
//!
//! combined with further WMI queries against `Win32_BaseBoard` and
//! others via `lar:And`/`lar:Or` logical-rule XML. There is no flat,
//! declarative hardware-ID or model-ID list to parse the way there is
//! for Dell or Lenovo — correctly resolving "does update X apply to this
//! machine" means evaluating arbitrary WQL against live system state,
//! which is a WMI query interpreter, not a catalog parser.
//!
//! Building and trusting that interpreter is out of scope for this
//! milestone. Rather than fake per-device matching on top of a format
//! that doesn't support it (which would silently violate the "do not
//! present guessed results as fact" principle this project holds itself
//! to), this module only implements the one thing HP *does* publish in
//! a clean, declarative form: the platform/support list at
//! <https://hpia.hpcloud.hp.com/ref/platformList.cab> (the same list HP
//! Image Assistant itself uses to look up per-platform reference
//! bundles by `SystemID` — HP's equivalent of Dell's/Lenovo's
//! system-model identifier, read from `Win32_BaseBoard.Product` on real
//! HP hardware).
//!
//! `HpPlatformCatalogSource::is_supported(system_id)` answers "is this
//! exact HP model in HP's supported-platform list, and for which OS
//! versions" — useful for the GUI to say "your model is covered by HP,
//! driver-pack sourcing not yet implemented" instead of silently doing
//! nothing. This deliberately does NOT implement `DriverSource` or
//! `ModelDriverPackSource`, and callers must not treat it as one.

use std::cell::RefCell;
use std::collections::{BTreeSet, HashMap};
use std::fs;
use std::path::{Path, PathBuf};

use crate::oem::cab::extract_cab;
use crate::oem::http::download_file;

pub const DEFAULT_PLATFORM_LIST_URL: &str = "https://hpia.hpcloud.hp.com/ref/platformList.cab";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HpPlatformInfo {
    pub system_id: String,
    pub product_name: String,
    /// Sorted, deduplicated.
    pub supported_os_descriptions: Vec<String>,
}

type Downloader = dyn Fn(&str, &Path) -> Result<PathBuf, String>;
type Extractor = dyn Fn(&Path, &Path) -> Result<Vec<PathBuf>, String>;

/// Not a `DriverSource` / `ModelDriverPackSource` — see module docs.
pub struct HpPlatformCatalogSource {
    pub cache_dir: PathBuf,
    catalog_url: String,
    catalog_xml_path: PathBuf,
    downloader: Box<Downloader>,
    extractor: Box<Extractor>,
    index: RefCell<Option<HashMap<String, HpPlatformInfo>>>,
}

impl HpPlatformCatalogSource {
    pub const SOURCE_ID: &'static str = "hp_platform";

    pub fn new(cache_dir: impl Into<PathBuf>) -> Result<Self, String> {
        Self::with_catalog_url(cache_dir, DEFAULT_PLATFORM_LIST_URL)
    }

    pub fn with_catalog_url(
        cache_dir: impl Into<PathBuf>,
        catalog_url: impl Into<String>,
    ) -> Result<Self, String> {
        let cache_dir = cache_dir.into();
        fs::create_dir_all(&cache_dir).map_err(|e| e.to_string())?;
        let catalog_xml_path = cache_dir.join("platformList.xml");
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
            let cab_path = dir.join("platformList.cab");
            (self.downloader)(&self.catalog_url, &cab_path)?;
            let extracted = (self.extractor)(&cab_path, &dir)?;
            let xml_file = extracted
                .iter()
                .find(|p| p.extension().map(|e| e.eq_ignore_ascii_case("xml")).unwrap_or(false))
                .ok_or_else(|| format!("No .xml payload found inside {}", self.catalog_url))?;
            fs::rename(xml_file, &self.catalog_xml_path).map_err(|e| e.to_string())?;
            let _ = fs::remove_dir_all(&dir);
        }
        *self.index.borrow_mut() = None;
        self.load_from_xml(&self.catalog_xml_path)
    }

    pub fn load_from_xml(&self, xml_path: &Path) -> Result<(), String> {
        let text = fs::read_to_string(xml_path).map_err(|e| e.to_string())?;
        let doc = roxmltree::Document::parse(&text).map_err(|e| e.to_string())?;
        let root = doc.root_element();

        let mut index: HashMap<String, HpPlatformInfo> = HashMap::new();

        for platform in root.children().filter(|n| n.has_tag_name("Platform")) {
            let Some(system_id_text) = platform
                .children()
                .find(|n| n.has_tag_name("SystemID"))
                .and_then(|n| n.text())
                .map(|s| s.trim())
                .filter(|s| !s.is_empty())
            else {
                continue;
            };
            let system_id = system_id_text.to_uppercase();

            let product_name = platform
                .children()
                .find(|n| n.has_tag_name("ProductName"))
                .and_then(|n| n.text())
                .map(|s| s.trim().to_string())
                .filter(|s| !s.is_empty())
                .unwrap_or_else(|| "unknown".to_string());

            let mut os_descriptions: BTreeSet<String> = BTreeSet::new();
            for os_el in platform.children().filter(|n| n.has_tag_name("OS")) {
                if let Some(desc) = os_el
                    .children()
                    .find(|n| n.has_tag_name("OSDescription"))
                    .and_then(|n| n.text())
                    .map(|s| s.trim().to_string())
                    .filter(|s| !s.is_empty())
                {
                    os_descriptions.insert(desc);
                }
            }

            index.insert(
                system_id.clone(),
                HpPlatformInfo {
                    system_id,
                    product_name,
                    supported_os_descriptions: os_descriptions.into_iter().collect(),
                },
            );
        }

        *self.index.borrow_mut() = Some(index);
        Ok(())
    }

    /// Returns platform info if HP lists this `SystemID` as supported,
    /// else `None`. This confirms the platform is *known to HP* — it
    /// does not, and cannot from this data alone, tell you which drivers
    /// apply to a specific device on that platform. See module docs.
    pub fn is_supported(&self, system_id: &str) -> Result<Option<HpPlatformInfo>, String> {
        if self.index.borrow().is_none() {
            self.load_from_xml(&self.catalog_xml_path)?;
        }
        let key = system_id.trim().to_uppercase();
        let index = self.index.borrow();
        Ok(index.as_ref().unwrap().get(&key).cloned())
    }
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
