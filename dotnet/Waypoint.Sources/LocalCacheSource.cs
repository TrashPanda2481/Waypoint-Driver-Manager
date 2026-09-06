// Local content-addressed driver cache — the one source that needs no
// network. Ported from sources/local_cache.py.

using System.Text.Json;
using System.Text.Json.Nodes;
using Waypoint.Core;

namespace Waypoint.Sources;

// On-disk layout, shared byte-for-byte with the Python implementation:
//   <cache_root>/manifest.json      list of DriverCandidate records
//   <cache_root>/blobs/<sha256>/    the package contents, keyed by content hash
public sealed class LocalCacheSource : IDriverSource
{
    public const string Id = "local_cache";

    private const string BlobsDirName = "blobs";
    private const string ManifestFileName = "manifest.json";

    private readonly string _manifestPath;

    public LocalCacheSource(string cacheRoot)
    {
        CacheRoot = cacheRoot;
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(Path.Combine(CacheRoot, BlobsDirName));
        _manifestPath = Path.Combine(CacheRoot, ManifestFileName);
        if (!File.Exists(_manifestPath))
        {
            File.WriteAllText(_manifestPath, "[]");
        }
    }

    // The GUI reads this to keep OEM catalog files next to the local cache.
    public string CacheRoot { get; }

    public string SourceId => Id;

    // Registers a technician-vetted package, keyed by content hash.
    public DriverCandidate AddPackage(
        string sourceFile,
        string hwid,
        string classGuid,
        string version,
        DateOnly? driverDate,
        string publisher,
        SignatureType signatureType)
    {
        var sha256 = Hashing.Sha256File(sourceFile);
        var blobDir = Path.Combine(CacheRoot, BlobsDirName, sha256);
        var blobPath = Path.Combine(blobDir, Path.GetFileName(sourceFile));

        // Content-addressed: the same bytes land in the same blob dir, so a
        // re-add of identical content copies nothing.
        if (!Directory.Exists(blobDir))
        {
            Directory.CreateDirectory(blobDir);
            File.Copy(sourceFile, blobPath);
        }

        var candidate = new DriverCandidate(
            Hwid: hwid,
            ClassGuid: classGuid,
            Version: version,
            DriverDate: driverDate,
            Publisher: publisher,
            SignatureType: signatureType,
            Sha256: sha256,
            SizeBytes: new FileInfo(sourceFile).Length,
            SourceId: Id,
            SourceUrl: sourceFile,
            DownloadUri: blobPath);

        var entries = LoadManifest();
        entries.Add(JsonSerializer.SerializeToNode(candidate, WaypointJsonContext.Default.DriverCandidate));
        SaveManifest(entries);
        return candidate;
    }

    public IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids)
    {
        var hwidSet = new HashSet<string>(hwids);
        var results = new List<DriverCandidate>();
        foreach (var entry in LoadManifest())
        {
            if (entry is not JsonObject record || record["hwid"]?.GetValue<string>() is not string entryHwid)
            {
                continue;
            }

            if (!hwidSet.Contains(entryHwid))
            {
                continue;
            }

            // Throws on an unrecognized signature_type rather than downgrading
            // a corrupt manifest row into a trusted tier.
            results.Add(JsonSerializer.Deserialize(record, WaypointJsonContext.Default.DriverCandidate)!);
        }

        return results;
    }

    public async Task<string> FetchAsync(
        DriverCandidate candidate,
        string destDir,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = candidate.DownloadUri;
        if (Hashing.Sha256File(source) != candidate.Sha256)
        {
            throw new InvalidOperationException(
                $"Hash mismatch for {source}: cache is corrupt or candidate metadata is stale.");
        }

        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, Path.GetFileName(source));
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(destPath))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        // shutil.copy2 parity: the mtime travels with the file.
        File.SetLastWriteTimeUtc(destPath, File.GetLastWriteTimeUtc(source));
        return destPath;
    }

    // Kept as raw nodes so one unparseable row can't lock a technician out of
    // adding drivers — matching Python, which only decodes rows that match.
    private JsonArray LoadManifest()
    {
        using var stream = File.OpenRead(_manifestPath);
        return JsonNode.Parse(stream) as JsonArray
            ?? throw new InvalidOperationException($"Manifest {_manifestPath} is not a JSON array.");
    }

    private void SaveManifest(JsonArray entries)
    {
        using var stream = File.Create(_manifestPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        entries.WriteTo(writer);
    }
}
