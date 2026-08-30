//! `waypoint-core`: pure data model + matching/ranking logic, no I/O.
//!
//! Mirrors `src/waypoint/core/` from the Python implementation. Anything
//! platform- or network-specific lives in `waypoint-platform` /
//! `waypoint-sources` instead, exactly as in the Python package layout —
//! this crate must stay dependency-free of both so it can be unit-tested
//! without a real OS or network access.

pub mod matching;
pub mod models;

pub use matching::{assess_all, assess_device, find_ambiguous_hwids, rank_candidates};
pub use models::{
    Device, DeviceAssessment, DeviceStatus, DriverCandidate, InstalledDriver, SignatureType,
};
