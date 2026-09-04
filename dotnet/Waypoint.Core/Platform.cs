// Platform backend contract. Ported from src/waypoint/platform/base.py.
// Core and the engine depend only on this, never on SetupAPI/WMI directly.

namespace Waypoint.Core;

public interface IDeviceBackend
{
    IReadOnlyList<Device> EnumerateDevices();

    // Must succeed and return a path before the engine installs over a driver.
    string ExportDriverBackup(Device device, string destDir);

    // dryRun must not mutate system state — return the commands that would run.
    InstallResult InstallDriver(Device device, string packagePath, bool dryRun);

    // True only on verified success. False must block the install batch —
    // SDI ticket #108 was a restore point that silently didn't work.
    bool CreateRestorePoint(string description);
}

public sealed record InstallResult(
    bool Success,
    IReadOnlyList<string> CommandsRun,
    string Message = "");
