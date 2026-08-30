//! Single place that decides which backend and sources a real (non-test)
//! Waypoint session uses. Direct port of `engine/factory.py`.
//!
//! Both the CLI and the GUI call this — if backend selection lived in two
//! places, they could silently drift apart, which is exactly the GUI/CLI-
//! disagreement problem docs/Architecture.md section 3.4 calls out about
//! SDI's `-autoinstall` flag vs. its interactive flow.
//!
//! **Scope note:** `build_oem_sources()` from the Python version (Dell
//! CatalogPC.cab source) is not ported yet — see `waypoint-sources`'
//! crate docs for why OEM catalogs are deferred.

use waypoint_core::SignatureType;
use waypoint_platform::{self, DeviceBackend};
use waypoint_sources::{DriverSource, LocalCacheSource};

use crate::audit::AuditLog;
use crate::paths::{default_audit_log_path, default_cache_dir};
use crate::session::WaypointEngine;

pub fn build_default_backend() -> Result<Box<dyn DeviceBackend>, String> {
    waypoint_platform::build_default_backend()
}

pub fn build_default_sources(cache_dir: Option<&str>) -> Result<Vec<Box<dyn DriverSource>>, String> {
    let cache_dir = cache_dir
        .map(std::path::PathBuf::from)
        .unwrap_or_else(default_cache_dir);
    #[allow(unused_mut)]
    let mut sources: Vec<Box<dyn DriverSource>> = vec![Box::new(LocalCacheSource::new(cache_dir)?)];

    #[cfg(target_os = "windows")]
    {
        sources.push(Box::new(waypoint_sources::WindowsUpdateCatalogSource::new()));
    }

    Ok(sources)
}

pub fn build_default_engine(
    cache_dir: Option<&str>,
    audit_log_path: Option<&str>,
    min_signature: SignatureType,
) -> Result<WaypointEngine, String> {
    let backend = build_default_backend()?;
    let sources = build_default_sources(cache_dir)?;
    let audit_path = audit_log_path
        .map(std::path::PathBuf::from)
        .unwrap_or_else(default_audit_log_path);
    let audit = AuditLog::new(audit_path)?;
    Ok(WaypointEngine::new(backend, sources, audit, min_signature))
}
