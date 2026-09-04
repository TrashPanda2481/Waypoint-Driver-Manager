// Core data models for Waypoint Driver Manager.
//
// These are the shared vocabulary between platform backends, driver sources,
// the engine, the CLI, and the GUI. Nothing here performs I/O — pure data +
// small pure-function helpers — which is what keeps matching/ranking logic
// unit-testable without real hardware or a real OS.
//
// Ported from src/waypoint/core/models.py (see ADR-0001).

namespace Waypoint.Core;

/// <summary>
/// Trust tier of a driver package, strongest first.
/// Whql = passed Microsoft's Windows Hardware Quality Labs certification.
/// Attestation = Microsoft attestation-signed (modern driver signing, not
///     full WHQL but still Microsoft-countersigned).
/// TestSigned = signed with a test certificate; only valid with test
///     signing / Secure Boot exceptions enabled. Never installed by default.
/// Unsigned = no valid signature chain. Blocked by default policy.
/// </summary>
public enum SignatureType
{
    Whql,
    Attestation,
    TestSigned,
    Unsigned,
}

public static class SignatureTypeExtensions
{
    /// <summary>Lower is more trusted. Used for default sorting/gating.</summary>
    public static int TrustRank(this SignatureType signatureType) => signatureType switch
    {
        SignatureType.Whql => 0,
        SignatureType.Attestation => 1,
        SignatureType.TestSigned => 2,
        SignatureType.Unsigned => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(signatureType)),
    };

    /// <summary>Matches the Python enum's string value — kept for CLI JSON parity.</summary>
    public static string ToWireString(this SignatureType signatureType) => signatureType switch
    {
        SignatureType.Whql => "whql",
        SignatureType.Attestation => "attestation",
        SignatureType.TestSigned => "test_signed",
        SignatureType.Unsigned => "unsigned",
        _ => throw new ArgumentOutOfRangeException(nameof(signatureType)),
    };
}

/// <summary>Triage tier shown in the UI. See docs/Architecture.md section 3.1.</summary>
public enum DeviceStatus
{
    /// <summary>No driver bound / device has an error code.</summary>
    Missing,

    /// <summary>Driver bound but device reports a fault, or bound driver fails current signature policy.</summary>
    Problem,

    /// <summary>Working fine; newer candidate exists.</summary>
    UpgradeAvailable,

    UpToDate,
}

public static class DeviceStatusExtensions
{
    public static string ToWireString(this DeviceStatus status) => status switch
    {
        DeviceStatus.Missing => "missing",
        DeviceStatus.Problem => "problem",
        DeviceStatus.UpgradeAvailable => "upgrade_available",
        DeviceStatus.UpToDate => "up_to_date",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <summary>
/// The driver currently bound to a device, as reported by the platform
/// backend (Win32_PnPSignedDriver on Windows).
/// </summary>
public sealed record InstalledDriver(
    string Version,
    DateOnly? DriverDate,
    string Publisher,
    SignatureType SignatureType,
    string? InfPath = null);

/// <summary>One physical/logical device as enumerated by a platform backend.</summary>
public sealed record Device(
    IReadOnlyList<string> Hwids, // Hardware IDs, most-specific first (Windows PnP order).
    string ClassGuid, // PnP device setup class GUID.
    string ClassName, // Human-readable class, e.g. "Display", "Net".
    string FriendlyName,
    string InstanceId, // Unique per physical device instance.
    int? ProblemCode = null, // Windows Device Manager error code, if any.
    InstalledDriver? Installed = null);

/// <summary>
/// A driver a DriverSource is offering as a possible match for one or more
/// hardware IDs. Never installed directly from this object — the engine
/// resolves it to a downloaded, hash-verified local package first.
/// </summary>
public sealed record DriverCandidate(
    string Hwid,
    string ClassGuid,
    string Version,
    DateOnly? DriverDate,
    string Publisher,
    SignatureType SignatureType,
    string Sha256,
    long SizeBytes,
    string SourceId,
    string SourceUrl,
    string DownloadUri)
{
    public bool IsNewerThan(InstalledDriver? installed)
    {
        if (installed is null)
        {
            return true;
        }

        if (DriverDate is not null && installed.DriverDate is not null)
        {
            return DriverDate > installed.DriverDate;
        }

        // Fall back to lexicographic version compare only when dates are
        // unavailable from either side — flagged, not trusted blindly.
        return Version != installed.Version;
    }
}

/// <summary>
/// Result of matching one Device against all available candidates. This is
/// what the GUI diff card and the CLI JSON plan are built from.
/// </summary>
public sealed class DeviceAssessment
{
    public required Device Device { get; init; }

    public required DeviceStatus Status { get; set; }

    public List<DriverCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// True if this device's HWIDs also match other installed devices in the
    /// same scan — requires explicit manual confirmation, never auto-picked.
    /// </summary>
    public bool Ambiguous { get; set; }

    public List<string> Notes { get; init; } = [];
}
