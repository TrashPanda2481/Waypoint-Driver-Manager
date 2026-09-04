// Guards the model invariants the Python dataclasses gave us for free.

using Xunit;

namespace Waypoint.Core.Tests;

public class ModelsTests
{
    private static Device DeviceWith(IReadOnlyList<string> hwids) =>
        new(hwids, "{class}", "Display", "Fake Display device", "DEV1");

    [Fact]
    public void Device_ComparesByValue_NotByHwidListReference()
    {
        var a = DeviceWith(["HWID1", "HWID2"]);
        var b = DeviceWith(["HWID1", "HWID2"]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<Device> { a, b });
    }

    [Fact]
    public void Device_WithDifferentHwids_IsNotEqual()
    {
        Assert.NotEqual(DeviceWith(["HWID1"]), DeviceWith(["HWID2"]));
    }

    [Fact]
    public void Device_CopiesHwids_SoLaterMutationOfTheSourceListCannotChangeIt()
    {
        var source = new List<string> { "HWID1" };
        var device = DeviceWith(source);

        source.Add("HWID2");

        Assert.Equal(["HWID1"], device.Hwids);
    }

    [Fact]
    public void DefaultSignatureType_IsUnsigned_SoAnUnsetSignatureFailsThePolicyGate()
    {
        Assert.Equal(SignatureType.Unsigned, default);

        var unset = new DriverCandidate(
            Hwid: "HWID1",
            ClassGuid: "{class}",
            Version: "2.0",
            DriverDate: new DateOnly(2026, 1, 1),
            Publisher: "Acme",
            SignatureType: default,
            Sha256: "abc123",
            SizeBytes: 1024,
            SourceId: "local_cache",
            SourceUrl: "https://example.test",
            DownloadUri: "/cache/blobs/abc123/driver.inf");

        Assert.Empty(Matching.RankCandidates([unset]));
        Assert.Empty(Matching.RankCandidates([unset], minSignature: SignatureType.Whql));
    }

    [Fact]
    public void IsNewerThan_WithNoInstalledDriver_IsAlwaysTrue()
    {
        Assert.True(CandidateDated(new DateOnly(2020, 1, 1)).IsNewerThan(null));
    }

    [Theory]
    [InlineData("1.0", true)]  // differing version with no dates to compare -> flagged, not trusted
    [InlineData("2.0", false)] // identical version -> not newer
    public void IsNewerThan_FallsBackToVersionCompare_WhenEitherDateIsMissing(string installedVersion, bool expected)
    {
        var installed = new InstalledDriver(installedVersion, null, "Acme", SignatureType.Whql);

        Assert.Equal(expected, CandidateDated(null).IsNewerThan(installed));
        Assert.Equal(expected, CandidateDated(new DateOnly(2026, 1, 1)).IsNewerThan(installed));
    }

    private static DriverCandidate CandidateDated(DateOnly? driverDate) =>
        new(
            Hwid: "HWID1",
            ClassGuid: "{class}",
            Version: "2.0",
            DriverDate: driverDate,
            Publisher: "Acme",
            SignatureType: SignatureType.Whql,
            Sha256: "abc123",
            SizeBytes: 1024,
            SourceId: "local_cache",
            SourceUrl: "https://example.test",
            DownloadUri: "/cache/blobs/abc123/driver.inf");
}
