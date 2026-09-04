// Ported from tests/test_matching.py.

using Waypoint.Core;
using Xunit;

namespace Waypoint.Core.Tests;

public class MatchingTests
{
    // Split from CandidateOn so an explicit null date can't be silently swallowed by a default.
    private static DriverCandidate Candidate(
        string hwid = "HWID1",
        string version = "2.0",
        SignatureType sig = SignatureType.Whql,
        string source = "local_cache")
        => CandidateOn(new DateOnly(2026, 1, 1), hwid, version, sig, source);

    private static DriverCandidate CandidateOn(
        DateOnly? driverDate,
        string hwid = "HWID1",
        string version = "2.0",
        SignatureType sig = SignatureType.Whql,
        string source = "local_cache")
    {
        return new DriverCandidate(
            Hwid: hwid,
            ClassGuid: "{class}",
            Version: version,
            DriverDate: driverDate,
            Publisher: "Acme",
            SignatureType: sig,
            Sha256: "abc123",
            SizeBytes: 1024,
            SourceId: source,
            SourceUrl: "https://example.test",
            DownloadUri: "/cache/blobs/abc123/driver.inf");
    }

    private static Device MakeDevice(
        string[]? hwids = null,
        string instanceId = "DEV1",
        InstalledDriver? installed = null,
        int? problemCode = null,
        string className = "Display")
    {
        return new Device(
            Hwids: hwids ?? ["HWID1"],
            ClassGuid: "{class}",
            ClassName: className,
            FriendlyName: $"Fake {className} device",
            InstanceId: instanceId,
            ProblemCode: problemCode,
            Installed: installed);
    }

    [Fact]
    public void MissingDevice_IsFlaggedMissing_EvenWithoutCandidates()
    {
        var device = MakeDevice(installed: null, problemCode: 28);
        var assessment = Assert.Single(Matching.AssessAll([device], new Dictionary<string, List<DriverCandidate>>()));
        Assert.Equal(DeviceStatus.Missing, assessment.Status);
        Assert.Empty(assessment.Candidates);
    }

    [Fact]
    public void ProblemCode_FlagsProblem_NotMissing_WhenDriverPresent()
    {
        var installed = new InstalledDriver("1.0", new DateOnly(2025, 1, 1), "Acme", SignatureType.Whql);
        var device = MakeDevice(installed: installed, problemCode: 1);
        var assessment = Assert.Single(Matching.AssessAll([device], new Dictionary<string, List<DriverCandidate>>()));
        Assert.Equal(DeviceStatus.Problem, assessment.Status);
    }

    [Fact]
    public void OlderInstalledDriver_YieldsUpgradeAvailable_NotAutoApplied()
    {
        var installed = new InstalledDriver("1.0", new DateOnly(2024, 1, 1), "Acme", SignatureType.Whql);
        var device = MakeDevice(installed: installed);
        var candidate = CandidateOn(new DateOnly(2026, 1, 1));
        var assessment = Assert.Single(Matching.AssessAll(
            [device],
            new Dictionary<string, List<DriverCandidate>> { ["HWID1"] = [candidate] }));
        Assert.Equal(DeviceStatus.UpgradeAvailable, assessment.Status);
        Assert.Equal([candidate], assessment.Candidates);
    }

    [Fact]
    public void UpToDate_WhenNoNewerCandidateExists()
    {
        var installed = new InstalledDriver("2.0", new DateOnly(2026, 1, 1), "Acme", SignatureType.Whql);
        var device = MakeDevice(installed: installed);
        var candidate = CandidateOn(new DateOnly(2024, 1, 1)); // older than installed
        var assessment = Assert.Single(Matching.AssessAll(
            [device],
            new Dictionary<string, List<DriverCandidate>> { ["HWID1"] = [candidate] }));
        Assert.Equal(DeviceStatus.UpToDate, assessment.Status);
        Assert.Empty(assessment.Candidates);
    }

    [Fact]
    public void UnsignedCandidate_IsExcludedByDefaultPolicy()
    {
        var unsigned = Candidate(sig: SignatureType.Unsigned);
        var whql = Candidate(sig: SignatureType.Whql, version: "3.0");
        var ranked = Matching.RankCandidates([unsigned, whql], minSignature: SignatureType.Attestation);
        Assert.DoesNotContain(unsigned, ranked);
        Assert.Contains(whql, ranked);
    }

    [Fact]
    public void InstalledDriverBelowSignaturePolicy_IsFlaggedProblem()
    {
        var installed = new InstalledDriver("1.0", new DateOnly(2025, 1, 1), "Acme", SignatureType.Unsigned);
        var device = MakeDevice(installed: installed);
        var assessment = Assert.Single(Matching.AssessAll(
            [device],
            new Dictionary<string, List<DriverCandidate>>(),
            minSignature: SignatureType.Attestation));
        Assert.Equal(DeviceStatus.Problem, assessment.Status);
        Assert.Contains(assessment.Notes, note => note.Contains("signature"));
    }

    [Fact]
    public void SharedHwidAcrossDevices_IsFlaggedAmbiguous_NotAutoResolved()
    {
        var deviceA = MakeDevice(hwids: ["SHARED_HWID"], instanceId: "A");
        var deviceB = MakeDevice(hwids: ["SHARED_HWID"], instanceId: "B");
        var shared = Matching.FindAmbiguousHwids([deviceA, deviceB]);
        Assert.Contains("SHARED_HWID", shared);

        var assessments = Matching.AssessAll([deviceA, deviceB], new Dictionary<string, List<DriverCandidate>>());
        Assert.All(assessments, a => Assert.True(a.Ambiguous));
        Assert.All(assessments, a => Assert.Contains("manual", string.Join(" ", a.Notes).ToLowerInvariant()));
    }

    [Fact]
    public void NonSharedHwid_IsNotFlaggedAmbiguous()
    {
        var deviceA = MakeDevice(hwids: ["HWID_A"], instanceId: "A");
        var deviceB = MakeDevice(hwids: ["HWID_B"], instanceId: "B");
        var assessments = Matching.AssessAll([deviceA, deviceB], new Dictionary<string, List<DriverCandidate>>());
        Assert.All(assessments, a => Assert.False(a.Ambiguous));
    }
}
