// The in-memory backend the engine tests and GUI/CLI development run against.
// It carried no tests of its own, yet the engine's safety behaviour is only as
// trustworthy as the double it is verified against — so pin its contract here.

using Waypoint.Core;

namespace Waypoint.Platform.Tests;

public class MockDeviceBackendTests
{
    private static Device Dev(string instanceId = "ROOT\\TEST\\0000") =>
        new(
            Hwids: ["PCI\\VEN_1234&DEV_5678"],
            ClassGuid: "{4d36e97d-e325-11ce-bfc1-08002be10318}",
            ClassName: "System",
            FriendlyName: "Test Device",
            InstanceId: instanceId);

    [Fact]
    public void EnumerateDevices_ReturnsSeededDevices()
    {
        var device = Dev();
        var backend = new MockDeviceBackend([device]);

        Assert.Equal(new[] { device }, backend.EnumerateDevices());
    }

    [Fact]
    public void EnumerateDevices_SnapshotsTheCallersListAtConstruction()
    {
        var list = new List<Device> { Dev() };
        var backend = new MockDeviceBackend(list);

        list.Add(Dev("ROOT\\TEST\\0001")); // mutate the source after construction

        Assert.Single(backend.EnumerateDevices()); // a snapshot, not a live view
    }

    [Fact]
    public void EnumerateDevices_DefaultsToEmpty()
    {
        Assert.Empty(new MockDeviceBackend().EnumerateDevices());
    }

    [Fact]
    public void ExportDriverBackup_RecordsCallAndReturnsPath()
    {
        var backend = new MockDeviceBackend();

        var dest = backend.ExportDriverBackup(Dev("ID\\A"), "/tmp/backups");

        Assert.Equal("/tmp/backups/ID\\A.backup", dest);
        Assert.Equal(("ID\\A", "/tmp/backups"), Assert.Single(backend.BackupCalls));
    }

    [Fact]
    public void InstallDriver_DryRun_ReportsNoChangeAndRecordsTheCall()
    {
        var backend = new MockDeviceBackend();

        var result = backend.InstallDriver(Dev(), "C:\\pkg.inf", dryRun: true);

        Assert.True(result.Success);
        Assert.Equal("dry-run: no changes made", result.Message);
        Assert.Equal("pnputil /add-driver C:\\pkg.inf /install", Assert.Single(result.CommandsRun));
        Assert.True(Assert.Single(backend.InstallCalls).DryRun);
    }

    [Fact]
    public void InstallDriver_Real_ReportsInstalledAndRecordsTheCall()
    {
        var backend = new MockDeviceBackend();

        var result = backend.InstallDriver(Dev(), "C:\\pkg.inf", dryRun: false);

        Assert.True(result.Success);
        Assert.Equal("installed", result.Message);
        Assert.False(Assert.Single(backend.InstallCalls).DryRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateRestorePoint_ReturnsConfiguredResultAndRecordsDescription(bool shouldSucceed)
    {
        var backend = new MockDeviceBackend { RestorePointShouldSucceed = shouldSucceed };

        var ok = backend.CreateRestorePoint("before batch");

        Assert.Equal(shouldSucceed, ok);
        Assert.Equal("before batch", Assert.Single(backend.RestorePointCalls));
    }
}
