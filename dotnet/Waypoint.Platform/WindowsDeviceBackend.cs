// Windows device backend. Ported from platform/windows.py, which read the
// tree through WMI; this uses CfgMgr32 instead so the CLI stays AOT-clean.
// Install and backup remain pnputil, as ADR-0001 specifies.

using System.Diagnostics;
using System.Runtime.Versioning;
using Waypoint.Core;
using Waypoint.Platform.Windows;

namespace Waypoint.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceBackend : IDeviceBackend
{
    private Dictionary<string, string>? _signersByInf;

    public IReadOnlyList<Device> EnumerateDevices()
    {
        var signers = SignersByInf();
        var devices = new List<Device>();

        foreach (var node in DeviceTree.Enumerate())
        {
            devices.Add(new Device(
                Hwids: node.Hwids,
                ClassGuid: node.ClassGuid,
                ClassName: node.ClassName,
                FriendlyName: node.FriendlyName,
                InstanceId: node.InstanceId,
                ProblemCode: node.ProblemCode,
                Installed: InstalledDriverFor(node, signers)));
        }

        return devices;
    }

    private static InstalledDriver? InstalledDriverFor(DeviceNode node, Dictionary<string, string> signers)
    {
        // No INF means nothing is bound; the Python equivalently found no
        // Win32_PnPSignedDriver row.
        if (string.IsNullOrEmpty(node.DriverInfPath))
        {
            return null;
        }

        return new InstalledDriver(
            Version: node.DriverVersion ?? "unknown",
            DriverDate: node.DriverDate,
            Publisher: node.DriverProvider ?? "unknown",
            SignatureType: DriverSignature.Resolve(node.DriverInfPath, signers),
            InfPath: node.DriverInfPath);
    }

    // One pnputil call per scan; third-party packages only, see
    // DriverSignature.Resolve for why that is not a gap.
    private Dictionary<string, string> SignersByInf()
    {
        if (_signersByInf is not null)
        {
            return _signersByInf;
        }

        try
        {
            _signersByInf = DriverSignature.ParseSigners(RunCapture("pnputil", ["/enum-drivers"]).StdOut);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No pnputil means no third-party signer data; inbox drivers still
            // resolve, and oemNNN packages fall through to Unsigned.
            _signersByInf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return _signersByInf;
    }

    public string ExportDriverBackup(Device device, string destDir)
    {
        if (device.Installed?.InfPath is not { Length: > 0 } inf)
        {
            throw new InvalidOperationException(
                $"No installed driver INF known for {device.InstanceId}, cannot back up.");
        }

        var dest = Path.Combine(destDir, device.InstanceId.Replace('\\', '_'));
        Directory.CreateDirectory(dest);

        var result = RunCapture("pnputil", ["/export-driver", inf, dest]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pnputil /export-driver failed for {inf} (exit {result.ExitCode}): "
                + $"{result.StdErr.Trim()}{result.StdOut.Trim()}");
        }

        return dest;
    }

    public InstallResult InstallDriver(Device device, string packagePath, bool dryRun)
    {
        var command = $"pnputil /add-driver \"{packagePath}\" /install";
        if (dryRun)
        {
            return new InstallResult(true, [command], "dry-run: no changes made");
        }

        var result = RunCapture("pnputil", ["/add-driver", packagePath, "/install"]);
        return new InstallResult(
            result.ExitCode == 0,
            [command],
            result.StdOut.Length > 0 ? result.StdOut : result.StdErr);
    }

    // SRSetRestorePointW rather than the WMI SystemRestore class the Python
    // used: same API underneath, no System.Management dependency.
    // Any failure returns false, which blocks the batch (Architecture.md 3.3).
    public bool CreateRestorePoint(string description)
    {
        try
        {
            var info = new RestorePoint.RESTOREPOINTINFO
            {
                dwEventType = RestorePoint.BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = RestorePoint.APPLICATION_INSTALL,
                llSequenceNumber = 0,
                szDescription = description,
            };

            return RestorePoint.SRSetRestorePointW(ref info, out var status) && status.nStatus == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCapture(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }
}
