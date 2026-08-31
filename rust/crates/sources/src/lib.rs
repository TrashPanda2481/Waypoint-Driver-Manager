//! `waypoint-sources`: driver source plugins (`DriverSource` trait +
//! implementations). Mirrors `src/waypoint/sources/` from the Python
//! implementation.
//!
//! **Scope note (2026-08-30 pass):** `base` and `local_cache` are fully
//! ported and real. `windows_update` is a trait-shaped placeholder with
//! an honest "not yet implemented" error body (see its module docs).
//! `oem::dell_catalog`, `oem::dell_driverpack`, `oem::lenovo_driverpack`,
//! and `oem::hp_platform` are now ported and real — see `oem` for the
//! module-shape rationale. Only `DellCatalogSource` is wired into the
//! engine's default `--oem` flag path (`engine::factory::build_oem_sources`),
//! matching the Python build's own scope: Dell's per-device catalog is
//! the only OEM source shaped like a `DriverSource`. The per-model pack
//! sources (`DellDriverPackSource`, `LenovoDriverPackSource`) and the HP
//! platform-support lookup (`HpPlatformCatalogSource`) are ported as
//! library code, callable directly, but not auto-included in a scan —
//! see `docs/TODO-rust-port.md`.

pub mod base;
pub mod local_cache;
pub mod oem;
pub mod windows_update;

pub use base::DriverSource;
pub use local_cache::LocalCacheSource;
pub use windows_update::WindowsUpdateCatalogSource;
