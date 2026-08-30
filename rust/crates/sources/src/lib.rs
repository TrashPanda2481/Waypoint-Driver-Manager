//! `waypoint-sources`: driver source plugins (`DriverSource` trait +
//! implementations). Mirrors `src/waypoint/sources/` from the Python
//! implementation.
//!
//! **Scope note (2026-08-30 scaffold pass):** `base` and `local_cache` are
//! fully ported and real. `windows_update` is a trait-shaped placeholder
//! with an honest "not yet implemented" error body (see its module docs).
//! The `sources/oem/*` catalogs (Dell CatalogPC.cab, Dell/Lenovo
//! driver-pack ZIPs, HP platform list — ~770 lines of Python across 7
//! files) have **not been ported in this pass** — they involve nontrivial
//! CAB/XML parsing and HTTP client logic that deserves its own dedicated,
//! tested session rather than being rushed alongside the rest of this
//! scaffold. See `docs/TODO-rust-port.md` for the tracked follow-up.

pub mod base;
pub mod local_cache;
pub mod windows_update;

pub use base::DriverSource;
pub use local_cache::LocalCacheSource;
pub use windows_update::WindowsUpdateCatalogSource;
