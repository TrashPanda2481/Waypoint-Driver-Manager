// The real Windows backend holds every P/Invoke, the restore point, the driver
// export and the install path, and had no test project at all. These cover the
// paths that are safe to exercise without elevation or touching hardware:
//
//   - the export refusal, which throws before pnputil is ever called;
//   - the install dry-run, which returns before pnputil is ever called;
//   - that CreateRestorePoint fails closed (returns, never throws) when it
//     cannot run, which is what makes "false blocks the batch" safe;
//   - that enumeration reads the tree without throwing.
//
// The success paths — a real export, a real install, an actually-created
// restore point — still need elevation and real hardware and remain unproven
// here; see docs/TODO.md. The backend is [SupportedOSPlatform("windows")], so
// each Windows-only test guards on OperatingSystem.IsWindows() (which also
// satisfies the platform-compatibility analyzer under -warnaserror) and is an
// inert pass on the Linux CI leg, where the MockDeviceBackend tests still run.

using System.Runtime.Versioning;
using Waypoint.Core;

namespace Waypoint.Platform.Tests;

// [SupportedOSPlatform] clears CA1416 for the Windows-only calls below,
// including the ones inside Assert.Throws/Record.Exception lambdas that a
// method-body IsWindows() guard does not reach. The per-test runtime guards
// still keep these inert on the Linux CI leg; xunit invokes regardless of the
// attribute.
[SupportedOSPlatform("windows")]
public class WindowsDeviceBackendTests
{
    private static Device DeviceWithoutInstalledDriver() =>
        new(
            Hwids: ["PCI\\VEN_1234&DEV_5678"],
            ClassGuid: "{4d36e97d-e325-11ce-bfc1-08002be10318}",
            ClassName: "System",
            FriendlyName: "Test Device",
            InstanceId: "ROOT\\TEST\\0000",
            Installed: null);

    [Fact]
    public void ExportDriverBackup_WithNoInstalledInf_RefusesBeforeTouchingPnputil()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WindowsDeviceBackend();

        var ex = Assert.Throws<InvalidOperationException>(
            () => backend.ExportDriverBackup(DeviceWithoutInstalledDriver(), Path.GetTempPath()));
        Assert.Contains("cannot back up", ex.Message);
    }

    [Fact]
    public void InstallDriver_DryRun_EchoesCommandAndMakesNoChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WindowsDeviceBackend();

        var result = backend.InstallDriver(DeviceWithoutInstalledDriver(), @"C:\pkg\driver.inf", dryRun: true);

        Assert.True(result.Success);
        Assert.Equal("dry-run: no changes made", result.Message);
        Assert.Equal(@"pnputil /add-driver ""C:\pkg\driver.inf"" /install", Assert.Single(result.CommandsRun));
    }

    [Fact]
    public void CreateRestorePoint_FailsClosedWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WindowsDeviceBackend();

        // Returns false when not elevated / System Restore is off, true if it
        // actually created one. The contract that matters here is that it never
        // throws — a thrown exception would crash the batch instead of blocking
        // it, defeating the SDI-#108 safeguard.
        var ex = Record.Exception(() => backend.CreateRestorePoint("Waypoint.Platform.Tests restore point"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnumerateDevices_ReadsTheTreeWithoutThrowingAndFieldsAreWellFormed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new WindowsDeviceBackend();

        var devices = backend.EnumerateDevices();

        // Read-only, and already validated at scale on real hardware (TODO.md).
        // Assert structure, not a count, to stay robust on a minimal CI VM.
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.InstanceId));
            Assert.NotEmpty(device.Hwids);
            // ClassGuid is intentionally not asserted: devices with no setup
            // class (e.g. VMBUS synthetic devices on a CI VM) legitimately
            // return "", which the backend reports as class "Unknown".
        });
    }
}
