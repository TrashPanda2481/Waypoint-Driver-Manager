// CAB extraction via expand.exe, which ships with Windows. Ported from
// oem/cab.py; the cabextract fallback is dropped as Windows-only (ADR-0001).

using System.Diagnostics;

namespace Waypoint.Sources.Oem;

public sealed class CabExtractionException(string message) : Exception(message);

public static class CabExtractor
{
    private static ReadOnlySpan<byte> CabMagic => "MSCF"u8;

    // Two expand.exe quirks force this shape: it refuses to "expand a file
    // onto itself" yet still exits 0, so extracting into the cab's own
    // directory silently returns the cab; and for a single-member cab it
    // ignores -F: and names the output after the CAB, so the payload can't be
    // found by extension. Hence: stage privately, fall back to the only file.
    public static string ExtractXmlPayload(string cabPath, string destPath)
    {
        RequireCabMagic(cabPath);

        var parent = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var staging = Directory.CreateTempSubdirectory("waypoint-cab-");
        try
        {
            var output = RunExpand(cabPath, staging.FullName);
            var extracted = Directory.GetFiles(staging.FullName);

            // expand.exe reports success on things that plainly failed, so
            // trust the artifacts rather than the exit code.
            if (extracted.Length == 0)
            {
                throw new CabExtractionException(
                    $"expand.exe produced no output for {cabPath}. The download may be truncated or corrupt. {output}");
            }

            var payload = extracted.Length == 1
                ? extracted[0]
                : Array.Find(extracted, p => string.Equals(Path.GetExtension(p), ".xml", StringComparison.OrdinalIgnoreCase))
                  ?? throw new CabExtractionException($"No .xml payload found inside {cabPath}");

            if (new FileInfo(payload).Length == 0)
            {
                throw new CabExtractionException($"The payload extracted from {cabPath} is empty.");
            }

            File.Move(payload, destPath, overwrite: true);
            return destPath;
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // A non-cab (an HTML error page, a truncated download) is copied through
    // verbatim by expand.exe with exit code 0 — catch it here instead.
    private static void RequireCabMagic(string cabPath)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(cabPath);
        if (stream.Read(header) != header.Length || !header.SequenceEqual(CabMagic))
        {
            throw new CabExtractionException(
                $"{cabPath} is not a cabinet file (no MSCF header). The download probably " +
                "failed or returned an error page rather than the catalog.");
        }
    }

    private static string RunExpand(string cabPath, string destDir)
    {
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

        // Concurrent drain: reading one pipe to the end first deadlocks the
        // moment the child fills the other one.
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return $"{stdOut.GetAwaiter().GetResult().Trim()} {stdErr.GetAwaiter().GetResult().Trim()}".Trim();
    }
}
