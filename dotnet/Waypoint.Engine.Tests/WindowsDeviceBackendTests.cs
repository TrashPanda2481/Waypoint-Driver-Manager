// Signature resolution and the safety behaviour of the Windows backend.
// The pnputil fixture is real captured output, not hand-written.

using System.Runtime.Versioning;
using Waypoint.Core;
using Waypoint.Platform;
using Waypoint.Platform.Windows;

namespace Waypoint.Engine.Tests;

public class DriverSignatureTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "pnputil-enum-drivers.txt");

    private static Dictionary<string, string> Signers() =>
        DriverSignature.ParseSigners(File.ReadAllText(FixturePath));

    [Fact]
    public void ParseSigners_ReadsPublishedNameAndSignerPairs()
    {
        var signers = Signers();

        Assert.NotEmpty(signers);
        Assert.Equal("Automotive Data Solutions", signers["oem100.inf"]);
    }

    [Fact]
    public void ParseSigners_IsCaseInsensitiveOnTheInfName()
    {
        Assert.True(Signers().ContainsKey("OEM100.INF"));
    }

    [Fact]
    public void ParseSigners_IgnoresTheHeaderAndBlankLines()
    {
        // "Microsoft PnP Utility" has no colon; nothing should key off it.
        Assert.DoesNotContain(Signers(), pair => pair.Key.Contains("PnP Utility"));
    }

    [Fact]
    public void SignedThirdPartyPackage_IsAttestation()
    {
        Assert.Equal(SignatureType.Attestation, DriverSignature.Resolve("oem100.inf", Signers()));
    }

    [Fact]
    public void ThirdPartyPackageWithNoSignerLine_IsUnsigned()
    {
        var signers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["oem99.inf"] = string.Empty,
        };

        Assert.Equal(SignatureType.Unsigned, DriverSignature.Resolve("oem99.inf", signers));
    }

    [Fact]
    public void OemPackageAbsentFromPnputil_IsUnsigned()
    {
        // An oemNNN package that the DriverStore does not list is genuinely
        // unaccounted for, unlike an inbox INF.
        Assert.Equal(SignatureType.Unsigned, DriverSignature.Resolve("oem404.inf", Signers()));
    }

    [Fact]
    public void InboxInfAbsentFromPnputil_IsAttestation()
    {
        // Verified on Windows 11: inbox INFs never appear in pnputil
        // /enum-drivers, so absence there must not read as unsigned.
        Assert.Equal(SignatureType.Attestation, DriverSignature.Resolve("acpi.inf", Signers()));
        Assert.Equal(SignatureType.Attestation, DriverSignature.Resolve("basicdisplay.inf", Signers()));
    }

    [Fact]
    public void NothingResolvesToWhql()
    {
        // WHQL and attestation share a signer name; claiming WHQL would assert
        // trust this backend has not verified.
        string[] infs = ["oem100.inf", "acpi.inf", "oem404.inf"];
        Assert.DoesNotContain(infs, inf => DriverSignature.Resolve(inf, Signers()) == SignatureType.Whql);
    }
}

[SupportedOSPlatform("windows")]
public class WindowsDeviceBackendTests
{
    private static Device DeviceWithoutDriver() =>
        new(["HWID1"], "{class}", "Display", "Fake device", "DEV1");

    [Fact]
    public void ExportDriverBackup_RefusesWhenNoInfIsKnown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WindowsDeviceBackend().ExportDriverBackup(DeviceWithoutDriver(), Path.GetTempPath()));
        Assert.Contains("cannot back up", ex.Message);
    }

    [Fact]
    public void InstallDriver_DryRun_ReportsTheCommandAndRunsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = new WindowsDeviceBackend().InstallDriver(DeviceWithoutDriver(), @"C:\nope\driver.inf", dryRun: true);

        Assert.True(result.Success);
        Assert.Contains("pnputil /add-driver", Assert.Single(result.CommandsRun));
        Assert.Contains("dry-run", result.Message);
    }

    // Reads the live device tree. Asserts invariants that hold on any Windows
    // machine rather than anything specific to this one.
    [Fact]
    public void EnumerateDevices_ReturnsTheLiveTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var devices = new WindowsDeviceBackend().EnumerateDevices();

        Assert.NotEmpty(devices);
        Assert.All(devices, d =>
        {
            Assert.NotEmpty(d.InstanceId);
            Assert.NotEmpty(d.Hwids); // devices without hardware IDs are skipped
            Assert.NotEmpty(d.FriendlyName);
        });

        // Instance IDs are the engine's dedupe key, so collisions would be a
        // correctness problem, not a cosmetic one.
        Assert.Equal(devices.Count, devices.Select(d => d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // A real machine has at least one driver bound with a version string.
        Assert.Contains(devices, d => d.Installed is { Version.Length: > 0 });
    }
}
