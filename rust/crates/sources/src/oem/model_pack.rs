//! Per-model driver pack sourcing — a different shape from
//! `DriverSource`. Port of `sources/oem/model_pack.py`.
//!
//! `DriverSource` (`base.rs`) answers "what drivers exist for this
//! hardware ID?" Some real OEM catalogs don't publish at that
//! granularity — Dell's `DriverPackCatalog.cab` and Lenovo's
//! `catalogv2.xml` instead answer "what's the one bundle for this exact
//! system model?", keyed by a model/system identifier read from firmware
//! (Dell's SMBIOS systemID, Lenovo's 4-character machine-type prefix),
//! not per-device hardware IDs.
//!
//! This is a legitimate, commonly-used alternative to per-device
//! matching — it's how MDT/SCCM/OSDCloud driver-pack injection works in
//! practice — so Waypoint models it as its own small trait rather than
//! stretching `DriverSource` to cover something it wasn't designed for.

use time::Date;

/// One downloadable bundle covering (ideally) every device on a specific
/// system model, for a specific OS. Not a single-device `DriverCandidate`
/// — resolving a `DriverPack` down to individual driver files/versions is
/// the OEM installer's job, not Waypoint's, at least for this milestone.
#[derive(Debug, Clone, PartialEq)]
pub struct DriverPack {
    pub pack_id: String,
    pub model_name: String,
    /// The vendor-specific identifier this was matched on.
    pub model_key: String,
    pub os_label: String,
    pub version: String,
    pub release_date: Option<Date>,
    pub url: String,
    /// e.g. "sha256", "md5" — whatever the vendor actually publishes.
    pub hash_algorithm: String,
    pub hash_value: String,
    pub size_bytes: u64,
    pub source_id: String,
}

pub trait ModelDriverPackSource {
    fn source_id(&self) -> &str;

    /// Return every driver pack this source has for `model_key` (a
    /// vendor-specific model/system identifier — see each implementation's
    /// docs for exactly what that identifier is and where it comes from on
    /// a real machine).
    ///
    /// `&self`, not `&mut self` — matches the `DriverSource::search`
    /// convention (`base.rs`) and `local_cache.rs`'s fully-stateless
    /// re-read-from-disk-on-each-call pattern, rather than Python's
    /// in-memory `self._index` instance attribute. Implementations that
    /// need a lazily-populated cache (mirroring Python's self-healing
    /// `load_from_xml` on first use) hold it behind a `RefCell` rather
    /// than requiring `&mut self` here.
    fn packs_for_model(&self, model_key: &str) -> Result<Vec<DriverPack>, String>;
}
