//! `DriverSource` plugin interface. Direct port of `sources/base.py`.
//!
//! Every driver source — Windows Update Catalog, an OEM catalog, a local
//! technician-built cache — implements this one trait. The engine treats
//! all sources identically: it asks each enabled source for candidates by
//! hardware ID and merges the results before handing them to
//! `waypoint_core::assess_all`. Adding a new source never requires
//! touching core matching logic or the GUI.

use waypoint_core::DriverCandidate;

pub trait DriverSource {
    fn source_id(&self) -> &str;

    /// Return every candidate this source has for any of `hwids`. Must not
    /// download anything — search returns metadata only (version,
    /// signature, sha256, download_uri). Download happens later, only for
    /// candidates the user/script actually selects, and the downloaded
    /// bytes are hash-verified against `sha256` before use.
    fn search(&self, hwids: &[String]) -> Result<Vec<DriverCandidate>, String>;

    /// Download `candidate` into `dest_dir`, verify its SHA-256 against
    /// `candidate.sha256`, and return the local path. Must error if the
    /// hash does not match — never install an unverified download.
    fn fetch(&self, candidate: &DriverCandidate, dest_dir: &str) -> Result<String, String>;
}
