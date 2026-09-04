// CAB extraction via expand.exe, which ships with Windows.
// Ported from src/waypoint/sources/oem/cab.py (the cabextract fallback is
// dropped — the .NET port is Windows-only, see ADR-0001).

using System.Diagnostics;

namespace Waypoint.Sources.Oem;

public sealed class CabExtractionException(string message) : Exception(message);

public static class CabExtractor
{
    // Extracts every file in cabPath into destDir, flat. Dell/HP/Lenovo cabs
    // are a single XML payload, so no subdirectory handling is needed.
    public static IReadOnlyList<string> Extract(string cabPath, string destDir)
    {
        Directory.CreateDirectory(destDir);

        var startInfo = new ProcessStartInfo("expand.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-F:*");
        startInfo.ArgumentList.Add(cabPath);
        startInfo.ArgumentList.Add(destDir);

        using var process = Process.Start(startInfo)
            ?? throw new CabExtractionException(
                $"Could not start expand.exe to unpack {cabPath}. On Windows this should never " +
                "happen — expand.exe ships with the OS, so something is wrong with PATH.");

        var stdErr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CabExtractionException(
                $"expand.exe failed on {cabPath} with exit code {process.ExitCode}. " +
                $"The download may be truncated or corrupt. {stdErr.Trim()}");
        }

        return Directory.GetFiles(destDir);
    }
}
