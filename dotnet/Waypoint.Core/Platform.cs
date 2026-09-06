// Platform backend contract. Ported from platform/base.py.

namespace Waypoint.Core;

public interface IDeviceBackend
{
    IReadOnlyList<Device> EnumerateDevices();

    // Must succeed before the engine installs over a driver.
    string ExportDriverBackup(Device device, string destDir);

    // dryRun must not mutate state — return the commands that would run.
    InstallResult InstallDriver(Device device, string packagePath, bool dryRun);

    // True only on verified success; false must block the batch (SDI ticket #108).
    bool CreateRestorePoint(string description);
}

public sealed record InstallResult(
    bool Success,
    IReadOnlyList<string> CommandsRun,
    string Message = "");
