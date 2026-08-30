//! Core data models for Waypoint Driver Manager.
//!
//! Direct port of `src/waypoint/core/models.py`. Nothing in this module
//! performs I/O — pure data + small pure-function helpers, which is what
//! keeps the matching/ranking logic unit-testable without real hardware or
//! a real OS. Field names and semantics are kept identical to the Python
//! version so the two implementations can be validated against each other.

use serde::{Deserialize, Serialize};
use time::Date;

/// Trust tier of a driver package, strongest first.
///
/// - `Whql` = passed Microsoft's Windows Hardware Quality Labs certification.
/// - `Attestation` = Microsoft attestation-signed (modern driver signing,
///   not full WHQL but still Microsoft-countersigned).
/// - `TestSigned` = signed with a test certificate; only valid with test
///   signing / Secure Boot exceptions enabled. Never installed by default.
/// - `Unsigned` = no valid signature chain. Blocked by default policy.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SignatureType {
    Whql,
    Attestation,
    TestSigned,
    Unsigned,
}

impl SignatureType {
    /// Lower is more trusted. Used for default sorting/gating.
    pub fn trust_rank(self) -> u8 {
        match self {
            SignatureType::Whql => 0,
            SignatureType::Attestation => 1,
            SignatureType::TestSigned => 2,
            SignatureType::Unsigned => 3,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            SignatureType::Whql => "whql",
            SignatureType::Attestation => "attestation",
            SignatureType::TestSigned => "test_signed",
            SignatureType::Unsigned => "unsigned",
        }
    }

    /// Inverse of `as_str` — used to parse `--min-signature` on the CLI.
    /// Kept in core (not the cli crate) so any caller gets the same set of
    /// accepted spellings as the enum's own serde representation.
    pub fn parse(value: &str) -> Result<Self, String> {
        match value {
            "whql" => Ok(SignatureType::Whql),
            "attestation" => Ok(SignatureType::Attestation),
            "test_signed" => Ok(SignatureType::TestSigned),
            "unsigned" => Ok(SignatureType::Unsigned),
            other => Err(format!(
                "invalid signature type '{other}' (expected one of: whql, attestation, test_signed, unsigned)"
            )),
        }
    }
}

/// Triage tier shown in the UI. See docs/Architecture.md section 3.1.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DeviceStatus {
    /// No driver bound / device has an error code.
    Missing,
    /// Driver bound but device reports a fault, or bound driver fails
    /// current signature policy.
    Problem,
    /// Working fine; newer candidate exists.
    UpgradeAvailable,
    UpToDate,
}

impl DeviceStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            DeviceStatus::Missing => "missing",
            DeviceStatus::Problem => "problem",
            DeviceStatus::UpgradeAvailable => "upgrade_available",
            DeviceStatus::UpToDate => "up_to_date",
        }
    }
}

/// The driver currently bound to a device, as reported by the platform
/// backend (Win32_PnPSignedDriver on Windows, /sys + udev on Linux).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct InstalledDriver {
    pub version: String,
    pub driver_date: Option<Date>,
    pub publisher: String,
    pub signature_type: SignatureType,
    pub inf_path: Option<String>,
}

/// One physical/logical device as enumerated by a platform backend.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Device {
    /// Hardware IDs, most-specific first (Windows PnP order).
    pub hwids: Vec<String>,
    /// PnP device setup class GUID.
    pub class_guid: String,
    /// Human-readable class, e.g. "Display", "Net".
    pub class_name: String,
    pub friendly_name: String,
    /// Unique per physical device instance.
    pub instance_id: String,
    /// Windows Device Manager error code, if any.
    pub problem_code: Option<i32>,
    pub installed: Option<InstalledDriver>,
}

/// A driver a `DriverSource` is offering as a possible match for one or more
/// hardware IDs. Never installed directly from this object — the engine
/// resolves it to a downloaded, hash-verified local package first.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DriverCandidate {
    pub hwid: String,
    pub class_guid: String,
    pub version: String,
    pub driver_date: Option<Date>,
    pub publisher: String,
    pub signature_type: SignatureType,
    pub sha256: String,
    pub size_bytes: u64,
    pub source_id: String,
    pub source_url: String,
    pub download_uri: String,
}

impl DriverCandidate {
    /// Direct port of `DriverCandidate.is_newer_than` from `core/models.py`.
    pub fn is_newer_than(&self, installed: Option<&InstalledDriver>) -> bool {
        match installed {
            None => true,
            Some(installed) => {
                if let (Some(candidate_date), Some(installed_date)) =
                    (self.driver_date, installed.driver_date)
                {
                    return candidate_date > installed_date;
                }
                // Fall back to lexicographic version compare only when
                // dates are unavailable from either side — flagged, not
                // trusted blindly.
                self.version != installed.version
            }
        }
    }
}

/// Result of matching one `Device` against all available candidates. This is
/// what the GUI diff card and the CLI JSON plan are built from.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceAssessment {
    pub device: Device,
    pub status: DeviceStatus,
    pub candidates: Vec<DriverCandidate>,
    /// True if this device's HWIDs also match other installed devices in
    /// the same scan — requires explicit manual confirmation, never
    /// auto-picked.
    pub ambiguous: bool,
    pub notes: Vec<String>,
}
