// Core data models. Ported from src/waypoint/core/models.py.

namespace Waypoint.Core;

// Driver trust tier. Weakest first, so default() is Unsigned — an unset
// signature must fail the policy gate, not sail through it. TrustRank orders.
[System.Text.Json.Serialization.JsonConverter(typeof(SignatureTypeJsonConverter))]
public enum SignatureType
{
    Unsigned,
    TestSigned,
    Attestation,
    Whql,
}

public static class SignatureTypeExtensions
{
    // Lower = more trusted.
    public static int TrustRank(this SignatureType signatureType) => signatureType switch
    {
        SignatureType.Whql => 0,
        SignatureType.Attestation => 1,
        SignatureType.TestSigned => 2,
        SignatureType.Unsigned => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(signatureType)),
    };

    // Python enum string values, for CLI JSON parity.
    public static string ToWireString(this SignatureType signatureType) => signatureType switch
    {
        SignatureType.Whql => "whql",
        SignatureType.Attestation => "attestation",
        SignatureType.TestSigned => "test_signed",
        SignatureType.Unsigned => "unsigned",
        _ => throw new ArgumentOutOfRangeException(nameof(signatureType)),
    };
}

// UI triage tier. See docs/Architecture.md 3.1.
public enum DeviceStatus
{
    Missing,
    Problem,
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

// Driver currently bound to a device.
public sealed record InstalledDriver(
    string Version,
    DateOnly? DriverDate,
    string Publisher,
    SignatureType SignatureType,
    string? InfPath = null);

// One enumerated device.
public sealed record Device(
    IReadOnlyList<string> Hwids, // most-specific first
    string ClassGuid,
    string ClassName, // e.g. "Display", "Net"
    string FriendlyName,
    string InstanceId,
    int? ProblemCode = null,
    InstalledDriver? Installed = null)
{
    // Copied because a record only freezes the reference; the caller's list stays writable.
    public IReadOnlyList<string> Hwids { get; } = [.. Hwids];

    // Hand-rolled because record equality reference-compares collection members.
    public bool Equals(Device? other) =>
        other is not null
        && Hwids.SequenceEqual(other.Hwids)
        && ClassGuid == other.ClassGuid
        && ClassName == other.ClassName
        && FriendlyName == other.FriendlyName
        && InstanceId == other.InstanceId
        && ProblemCode == other.ProblemCode
        && Installed == other.Installed;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var hwid in Hwids)
        {
            hash.Add(hwid);
        }

        hash.Add(ClassGuid);
        hash.Add(ClassName);
        hash.Add(FriendlyName);
        hash.Add(InstanceId);
        hash.Add(ProblemCode);
        hash.Add(Installed);
        return hash.ToHashCode();
    }
}

// A candidate driver offered by a source for one HWID.
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

        // no dates on either side — version compare, not trusted blindly
        return Version != installed.Version;
    }
}

// One device's triage result — feeds the GUI diff card and CLI JSON plan.
public sealed class DeviceAssessment
{
    public required Device Device { get; init; }

    public required DeviceStatus Status { get; set; }

    public List<DriverCandidate> Candidates { get; init; } = [];

    // HWID shared with another device this scan — needs manual confirmation.
    public bool Ambiguous { get; set; }

    public List<string> Notes { get; init; } = [];
}
