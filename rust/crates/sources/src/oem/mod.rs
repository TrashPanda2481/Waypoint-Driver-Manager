//! OEM catalog driver sources. Port of `sources/oem/__init__.py`.
//!
//! OEM vendors publish two genuinely different kinds of catalog, and
//! Waypoint treats them as two different things rather than forcing one
//! shape onto both (see `docs/Architecture.md` section 3.2 for the
//! pluggable-source rationale):
//!
//! 1. Per-device catalogs — a flat list of (hardware ID -> driver)
//!    entries that plug directly into the existing `DriverSource` trait
//!    used by `local_cache` and `windows_update`. As of this writing,
//!    Dell is the only OEM that publishes this shape in a clean,
//!    declarative form (`downloads.dell.com/catalog/CatalogPC.cab`, PCI
//!    vendor/device IDs per component). See `dell_catalog`.
//!
//! 2. Per-model driver packs — "give me the one bundle for this exact
//!    system model," keyed by a model/system identifier rather than a
//!    hardware ID. Dell (`DriverPackCatalog.cab`) and Lenovo
//!    (`catalogv2.xml`) both publish this shape cleanly. See
//!    `model_pack`, `dell_driverpack`, `lenovo_driverpack`.
//!
//! HP's public catalog (`HpCatalogForSms.latest.cab`) is neither: it's a
//! WSUS Software Distribution Package feed whose applicability is
//! expressed as WQL queries evaluated against live system state (see
//! `hp_platform` for what is and is not implemented, and why).

pub mod cab;
pub mod dell_catalog;
pub mod dell_driverpack;
pub mod hp_platform;
pub mod http;
pub mod lenovo_driverpack;
pub mod model_pack;

pub use dell_catalog::DellCatalogSource;
pub use dell_driverpack::DellDriverPackSource;
pub use hp_platform::{HpPlatformCatalogSource, HpPlatformInfo};
pub use lenovo_driverpack::LenovoDriverPackSource;
pub use model_pack::{DriverPack, ModelDriverPackSource};
