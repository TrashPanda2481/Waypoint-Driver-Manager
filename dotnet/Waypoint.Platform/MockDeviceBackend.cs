// In-memory backend for tests and for GUI/CLI development with no real
// hardware. Ported from platform/mock.py.

using Waypoint.Core;

namespace Waypoint.Platform;

public sealed class MockDeviceBackend : IDeviceBackend
{
    private readonly List<Device> _devices;

    public MockDeviceBackend(IEnumerable<Device>? devices = null)
    {
        _devices = devices is null ? [] : [.. devices];
    }

    public List<(string InstanceId, string DestDir)> BackupCalls { get; } = [];

    public List<(string InstanceId, string PackagePath, bool DryRun)> InstallCalls { get; } = [];

    public List<string> RestorePointCalls { get; } = [];

    // Flip to false to exercise the abort path.
    public bool RestorePointShouldSucceed { get; set; } = true;

    public IReadOnlyList<Device> EnumerateDevices() => [.. _devices];

    public string ExportDriverBackup(Device device, string destDir)
    {
        BackupCalls.Add((device.InstanceId, destDir));
        return $"{destDir}/{device.InstanceId}.backup";
    }

    public InstallResult InstallDriver(Device device, string packagePath, bool dryRun)
    {
        InstallCalls.Add((device.InstanceId, packagePath, dryRun));
        var command = $"pnputil /add-driver {packagePath} /install";
        return dryRun
            ? new InstallResult(true, [command], "dry-run: no changes made")
            : new InstallResult(true, [command], "installed");
    }

    public bool CreateRestorePoint(string description)
    {
        RestorePointCalls.Add(description);
        return RestorePointShouldSucceed;
    }
}
