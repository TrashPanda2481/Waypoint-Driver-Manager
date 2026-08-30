"""Core data models for Waypoint Driver Manager.

These models are the shared vocabulary between platform backends, driver
sources, the engine, the CLI, and the GUI. Nothing in this module performs
I/O — it is pure data + small pure-function helpers, which is what keeps the
matching/ranking logic unit-testable without real hardware or a real OS.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date
from enum import Enum


class SignatureType(str, Enum):
    """Trust tier of a driver package, strongest first.

    WHQL = passed Microsoft's Windows Hardware Quality Labs certification.
    ATTESTATION = Microsoft attestation-signed (modern driver signing, not
        full WHQL but still Microsoft-countersigned).
    TEST_SIGNED = signed with a test certificate; only valid with test
        signing / Secure Boot exceptions enabled. Never installed by default.
    UNSIGNED = no valid signature chain. Blocked by default policy.
    """

    WHQL = "whql"
    ATTESTATION = "attestation"
    TEST_SIGNED = "test_signed"
    UNSIGNED = "unsigned"

    @property
    def trust_rank(self) -> int:
        """Lower is more trusted. Used for default sorting/gating."""
        order = {
            SignatureType.WHQL: 0,
            SignatureType.ATTESTATION: 1,
            SignatureType.TEST_SIGNED: 2,
            SignatureType.UNSIGNED: 3,
        }
        return order[self]


class DeviceStatus(str, Enum):
    """Triage tier shown in the UI. See docs/Architecture.md section 3.1."""

    MISSING = "missing"       # No driver bound / device has an error code
    PROBLEM = "problem"       # Driver bound but device reports a fault, or
                               # bound driver fails current signature policy
    UPGRADE_AVAILABLE = "upgrade_available"  # Working fine; newer candidate exists
    UP_TO_DATE = "up_to_date"


@dataclass(frozen=True)
class InstalledDriver:
    """The driver currently bound to a device, as reported by the platform
    backend (Win32_PnPSignedDriver on Windows, /sys + udev on Linux)."""

    version: str
    driver_date: date | None
    publisher: str
    signature_type: SignatureType
    inf_path: str | None = None


@dataclass(frozen=True)
class Device:
    """One physical/logical device as enumerated by a platform backend."""

    hwids: tuple[str, ...]     # Hardware IDs, most-specific first (Windows PnP order)
    class_guid: str            # PnP device setup class GUID
    class_name: str            # Human-readable class, e.g. "Display", "Net"
    friendly_name: str
    instance_id: str           # Unique per physical device instance
    problem_code: int | None = None  # Windows Device Manager error code, if any
    installed: InstalledDriver | None = None


@dataclass(frozen=True)
class DriverCandidate:
    """A driver a DriverSource is offering as a possible match for one or
    more hardware IDs. Never installed directly from this object — the
    engine resolves it to a downloaded, hash-verified local package first.
    """

    hwid: str
    class_guid: str
    version: str
    driver_date: date | None
    publisher: str
    signature_type: SignatureType
    sha256: str
    size_bytes: int
    source_id: str
    source_url: str
    download_uri: str

    def is_newer_than(self, installed: InstalledDriver | None) -> bool:
        if installed is None:
            return True
        if self.driver_date and installed.driver_date:
            return self.driver_date > installed.driver_date
        # Fall back to lexicographic version compare only when dates are
        # unavailable from either side — flagged, not trusted blindly.
        return self.version != installed.version


@dataclass
class DeviceAssessment:
    """Result of matching one Device against all available candidates.
    This is what the GUI diff card and the CLI JSON plan are built from.
    """

    device: Device
    status: DeviceStatus
    candidates: list[DriverCandidate] = field(default_factory=list)
    ambiguous: bool = False  # True if this device's HWIDs also match other
                              # installed devices in the same scan — requires
                              # explicit manual confirmation, never auto-picked.
    notes: list[str] = field(default_factory=list)
