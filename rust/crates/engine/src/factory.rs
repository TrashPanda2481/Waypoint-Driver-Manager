//! Single place that decides which backend and sources a real (non-test)
//! Waypoint session uses. Direct port of `engine/factory.py`.
//!
//! Both the CLI and the GUI call this — if backend selection lived in two
//! places, they could silently drift apart, which is exactly the GUI/CLI-
//! disagreement problem docs/Architecture.md section 3.4 calls out about
//! SDI's `-autoinstall` flag vs. its interactive flow.
//!
use waypoint_core::SignatureType;
use waypoint_platform::{self, DeviceBackend};
use waypoint_sources::oem::DellCatalogSource;
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

/// OEM per-device catalog sources (currently: Dell's `CatalogPC.cab`).
/// Direct port of `engine/factory.py`'s `build_oem_sources()`.
///
/// Deliberately NOT included in `build_default_sources()`. Two reasons:
///
/// 1. Cost. `DellCatalogSource::refresh()` downloads and parses a ~57MB
///    uncompressed XML catalog — not something to trigger on every
///    default scan, consistent with the user instruction to limit
///    credit/resource-consuming activity to what's actually needed.
/// 2. `refresh()` requires network access and is not idempotent-cheap
///    the way `LocalCacheSource` is; a caller (CLI flag, GUI settings
///    toggle) should opt into it explicitly and control when the
///    catalog is refreshed vs. reused from `cache_dir`.
///
/// Only Dell is exposed here because only Dell's per-device catalog
/// (`oem::dell_catalog::DellCatalogSource`) implements the
/// `DriverSource` trait (search-by-hardware-ID). Lenovo and Dell's
/// driver-pack catalogs, and HP's platform list, are a different shape
/// (`ModelDriverPackSource` / platform-support-only) and are not
/// `DriverSource`s — see `waypoint_sources::oem` for why, and expose
/// those through separate model-based lookups rather than folding them
/// into this list.
///
/// Callers must call `.refresh()` on the returned source(s) before the
/// first `search()` (or point `cache_dir` at a location where a
/// previous `refresh()` already ran) — this function only constructs
/// the source, it never triggers a download itself.
pub fn build_oem_sources(cache_dir: Option<&str>) -> Result<Vec<Box<dyn DriverSource>>, String> {
    let cache_dir = cache_dir
        .map(std::path::PathBuf::from)
        .unwrap_or_else(default_cache_dir);
    Ok(vec![Box::new(DellCatalogSource::new(cache_dir)?)])
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
