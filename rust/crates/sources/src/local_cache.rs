//! Local, content-addressed driver cache. Direct port of
//! `sources/local_cache.py`.
//!
//! This is the one source that runs fully today (no network dependency),
//! and it's the direct fix for SDI's "one monolithic 20-60GB blob" model:
//! a technician adds drivers here incrementally, one at a time, keyed by
//! their SHA-256 hash rather than by filename/folder convention. Re-scans
//! across many machines dedupe automatically because the key *is* the
//! content hash.
//!
//! Layout on disk:
//! ```text
//! <cache_root>/
//!   manifest.json          # list of DriverCandidate records
//!   blobs/<sha256>/        # the actual driver package contents
//! ```

use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use waypoint_core::{DriverCandidate, SignatureType};

use crate::base::DriverSource;

pub struct LocalCacheSource {
    pub cache_root: PathBuf,
    manifest_path: PathBuf,
}

/// Everything `DriverCandidate` needs for on-disk (de)serialization, kept
/// as its own struct rather than deriving directly on `DriverCandidate` so
/// `waypoint-core` doesn't have to know about this source's manifest
/// format.
#[derive(Serialize, Deserialize)]
struct ManifestEntry {
    hwid: String,
    class_guid: String,
    version: String,
    driver_date: Option<time::Date>,
    publisher: String,
    signature_type: SignatureType,
    sha256: String,
    size_bytes: u64,
    source_id: String,
    source_url: String,
    download_uri: String,
}

impl From<&DriverCandidate> for ManifestEntry {
    fn from(c: &DriverCandidate) -> Self {
        ManifestEntry {
            hwid: c.hwid.clone(),
            class_guid: c.class_guid.clone(),
            version: c.version.clone(),
            driver_date: c.driver_date,
            publisher: c.publisher.clone(),
            signature_type: c.signature_type,
            sha256: c.sha256.clone(),
            size_bytes: c.size_bytes,
            source_id: c.source_id.clone(),
            source_url: c.source_url.clone(),
            download_uri: c.download_uri.clone(),
        }
    }
}

impl From<ManifestEntry> for DriverCandidate {
    fn from(e: ManifestEntry) -> Self {
        DriverCandidate {
            hwid: e.hwid,
            class_guid: e.class_guid,
            version: e.version,
            driver_date: e.driver_date,
            publisher: e.publisher,
            signature_type: e.signature_type,
            sha256: e.sha256,
            size_bytes: e.size_bytes,
            source_id: e.source_id,
            source_url: e.source_url,
            download_uri: e.download_uri,
        }
    }
}

impl LocalCacheSource {
    pub fn new(cache_root: impl AsRef<Path>) -> Result<Self, String> {
        let cache_root = cache_root.as_ref().to_path_buf();
        fs::create_dir_all(&cache_root).map_err(|e| e.to_string())?;
        fs::create_dir_all(cache_root.join("blobs")).map_err(|e| e.to_string())?;
        let manifest_path = cache_root.join("manifest.json");
        if !manifest_path.exists() {
            fs::write(&manifest_path, "[]").map_err(|e| e.to_string())?;
        }
        Ok(Self {
            cache_root,
            manifest_path,
        })
    }

    fn load_manifest(&self) -> Result<Vec<ManifestEntry>, String> {
        let raw = fs::read_to_string(&self.manifest_path).map_err(|e| e.to_string())?;
        serde_json::from_str(&raw).map_err(|e| e.to_string())
    }

    fn save_manifest(&self, entries: &[ManifestEntry]) -> Result<(), String> {
        let raw = serde_json::to_string_pretty(entries).map_err(|e| e.to_string())?;
        fs::write(&self.manifest_path, raw).map_err(|e| e.to_string())
    }

    /// Register a driver package a technician has vetted, keyed by its
    /// content hash. This is the explicit, opt-in alternative to trusting
    /// an opaque third-party archive.
    #[allow(clippy::too_many_arguments)]
    pub fn add_package(
        &self,
        source_file: impl AsRef<Path>,
        hwid: &str,
        class_guid: &str,
        version: &str,
        driver_date: Option<time::Date>,
        publisher: &str,
        signature_type: SignatureType,
    ) -> Result<DriverCandidate, String> {
        let src_path = source_file.as_ref();
        let sha256 = hash_file(src_path)?;
        let blob_dir = self.cache_root.join("blobs").join(&sha256);
        if !blob_dir.exists() {
            fs::create_dir_all(&blob_dir).map_err(|e| e.to_string())?;
            let file_name = src_path
                .file_name()
                .ok_or_else(|| "source_file has no file name".to_string())?;
            fs::copy(src_path, blob_dir.join(file_name)).map_err(|e| e.to_string())?;
        }
        let size_bytes = fs::metadata(src_path).map_err(|e| e.to_string())?.len();
        let file_name = src_path
            .file_name()
            .ok_or_else(|| "source_file has no file name".to_string())?;

        let candidate = DriverCandidate {
            hwid: hwid.to_string(),
            class_guid: class_guid.to_string(),
            version: version.to_string(),
            driver_date,
            publisher: publisher.to_string(),
            signature_type,
            sha256: sha256.clone(),
            size_bytes,
            source_id: self.source_id().to_string(),
            source_url: src_path.to_string_lossy().to_string(),
            download_uri: blob_dir.join(file_name).to_string_lossy().to_string(),
        };

        let mut entries = self.load_manifest()?;
        entries.push(ManifestEntry::from(&candidate));
        self.save_manifest(&entries)?;
        Ok(candidate)
    }
}

impl DriverSource for LocalCacheSource {
    fn source_id(&self) -> &str {
        "local_cache"
    }

    fn search(&self, hwids: &[String]) -> Result<Vec<DriverCandidate>, String> {
        let entries = self.load_manifest()?;
        let hwid_set: std::collections::HashSet<&String> = hwids.iter().collect();
        Ok(entries
            .into_iter()
            .filter(|e| hwid_set.contains(&e.hwid))
            .map(DriverCandidate::from)
            .collect())
    }

    fn fetch(&self, candidate: &DriverCandidate, dest_dir: &str) -> Result<String, String> {
        let src = Path::new(&candidate.download_uri);
        let actual_hash = hash_file(src)?;
        if actual_hash != candidate.sha256 {
            return Err(format!(
                "Hash mismatch for {}: cache is corrupt or candidate metadata is stale.",
                src.display()
            ));
        }
        let dest_dir = Path::new(dest_dir);
        fs::create_dir_all(dest_dir).map_err(|e| e.to_string())?;
        let file_name = src
            .file_name()
            .ok_or_else(|| "download_uri has no file name".to_string())?;
        let dest_path = dest_dir.join(file_name);
        fs::copy(src, &dest_path).map_err(|e| e.to_string())?;
        Ok(dest_path.to_string_lossy().to_string())
    }
}

fn hash_file(path: &Path) -> Result<String, String> {
    let mut file = fs::File::open(path).map_err(|e| e.to_string())?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 1 << 20];
    loop {
        let n = file.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}
