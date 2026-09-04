// Local cache round trips, hash verification, and manifest wire-format parity
// with the Python implementation.

using System.Text.Json;
using Waypoint.Core;

namespace Waypoint.Sources.Tests;

public sealed class LocalCacheSourceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "waypoint-localcache-tests",
        Guid.NewGuid().ToString("n"));

    public LocalCacheSourceTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void AddPackageThenSearchRoundTrips()
    {
        var source = NewSource();
        var driverFile = StageFile("nv.inf", "; fake inf for test purposes");

        var added = source.AddPackage(
            driverFile,
            hwid: "HWID1",
            classGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
            version: "31.0.15.3623",
            driverDate: new DateOnly(2024, 3, 19),
            publisher: "Acme",
            signatureType: SignatureType.Whql);

        var found = Assert.Single(source.Search(["HWID1"]));
        Assert.Equal(added, found);
        Assert.Equal("HWID1", found.Hwid);
        Assert.Equal(new DateOnly(2024, 3, 19), found.DriverDate);
        Assert.Equal(SignatureType.Whql, found.SignatureType);
        Assert.Equal(LocalCacheSource.Id, found.SourceId);
        Assert.Equal(driverFile, found.SourceUrl);
        Assert.Equal(Hashing.Sha256File(driverFile), found.Sha256);
        Assert.Equal(new FileInfo(driverFile).Length, found.SizeBytes);
        Assert.True(File.Exists(found.DownloadUri));
    }

    [Fact]
    public void SearchIgnoresNonMatchingHwids()
    {
        var source = NewSource();
        AddSimplePackage(source, "wifi.inf", "; wifi", "HWID_WIFI");
        AddSimplePackage(source, "gpu.inf", "; gpu", "HWID_GPU");

        Assert.Empty(source.Search(["HWID_NOT_HERE"]));

        var hit = Assert.Single(source.Search(["HWID_GPU", "HWID_NOT_HERE"]));
        Assert.Equal("HWID_GPU", hit.Hwid);
    }

    [Fact]
    public void AddingSameContentTwiceReusesOneBlob()
    {
        var source = NewSource();
        var first = StageFile("a/net.inf", "; identical bytes");
        var second = StageFile("b/net.inf", "; identical bytes");

        var one = AddSimplePackage(source, first, "HWID_A");
        var two = AddSimplePackage(source, second, "HWID_B");

        Assert.Equal(one.Sha256, two.Sha256);
        Assert.Equal(one.DownloadUri, two.DownloadUri);
        Assert.Single(Directory.GetDirectories(Path.Combine(source.CacheRoot, "blobs")));
        Assert.Equal(2, source.Search(["HWID_A", "HWID_B"]).Count);
    }

    [Fact]
    public async Task FetchVerifiesHashAndCopiesOut()
    {
        var source = NewSource();
        var candidate = AddSimplePackage(source, "chipset.inf", "; chipset payload", "HWID1");
        var destDir = Path.Combine(_tempRoot, "staging", "nested");

        var fetched = await source.FetchAsync(candidate, destDir);

        Assert.Equal(Path.Combine(destDir, "chipset.inf"), fetched);
        Assert.Equal("; chipset payload", File.ReadAllText(fetched));
        Assert.Equal(candidate.Sha256, Hashing.Sha256File(fetched));
    }

    [Fact]
    public async Task FetchThrowsWhenBlobIsCorrupt()
    {
        var source = NewSource();
        var candidate = AddSimplePackage(source, "audio.inf", "; original payload", "HWID1");
        File.WriteAllText(candidate.DownloadUri, "; tampered payload");
        var destDir = Path.Combine(_tempRoot, "staging");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(candidate, destDir));

        Assert.Contains("cache is corrupt or candidate metadata is stale", error.Message);
        Assert.False(File.Exists(Path.Combine(destDir, "audio.inf")));
    }

    [Fact]
    public void SearchRejectsUnknownSignatureTier()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(cacheRoot, "manifest.json"), ManifestJson("platinum_signed"));
        var source = new LocalCacheSource(cacheRoot);

        var error = Assert.Throws<JsonException>(() =>
        {
            source.Search(["HWID1"]);
        });

        Assert.Contains("platinum_signed", error.Message);
    }

    [Fact]
    public void UnknownSignatureTierInAnUnmatchedRowDoesNotBlockOtherWork()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(cacheRoot, "manifest.json"), ManifestJson("platinum_signed"));
        var source = new LocalCacheSource(cacheRoot);

        Assert.Empty(source.Search(["HWID_ELSEWHERE"]));

        var added = AddSimplePackage(source, "lan.inf", "; lan", "HWID_ELSEWHERE");
        Assert.Equal("HWID_ELSEWHERE", Assert.Single(source.Search(["HWID_ELSEWHERE"])).Hwid);
        Assert.Equal(SignatureType.Whql, added.SignatureType);
    }

    [Fact]
    public void ReadsAManifestWrittenByThePythonImplementation()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(Path.Combine(cacheRoot, "manifest.json"), ManifestJson("whql"));
        var source = new LocalCacheSource(cacheRoot);

        var results = source.Search(["HWID1", "HWID2"]);

        Assert.Equal(2, results.Count);
        Assert.Equal("HWID1", results[0].Hwid);
        Assert.Equal(new DateOnly(2024, 3, 19), results[0].DriverDate);
        Assert.Equal(SignatureType.Whql, results[0].SignatureType);
        Assert.Equal(27L, results[0].SizeBytes);
        Assert.Equal(@"C:\stage\nv.inf", results[0].SourceUrl);
        Assert.Null(results[1].DriverDate);
        Assert.Equal(SignatureType.Attestation, results[1].SignatureType);
    }

    [Fact]
    public void WritesTheManifestInThePythonWireFormat()
    {
        var source = NewSource();
        AddSimplePackage(source, "nv.inf", "; nv", "HWID1");
        source.AddPackage(
            StageFile("dated.inf", "; dated"),
            hwid: "HWID2",
            classGuid: "{class}",
            version: "2.0",
            driverDate: new DateOnly(2026, 1, 1),
            publisher: "Acme",
            signatureType: SignatureType.Attestation);

        var text = File.ReadAllText(Path.Combine(source.CacheRoot, "manifest.json"));

        // snake_case keys, in Python dataclass field order.
        Assert.Contains("\"class_guid\":", text);
        Assert.Contains("\"driver_date\": null", text);
        Assert.Contains("\"driver_date\": \"2026-01-01\"", text);
        Assert.Contains("\"signature_type\": \"whql\"", text);
        Assert.Contains("\"signature_type\": \"attestation\"", text);
        Assert.Contains("\"size_bytes\":", text);
        Assert.Contains("\"download_uri\":", text);
        Assert.DoesNotContain("driverDate", text);

        // 2-space indent, as json.dumps(..., indent=2) emits.
        Assert.Contains("\n  {", text);

        using var parsed = JsonDocument.Parse(text);
        var keys = parsed.RootElement[0]
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "hwid", "class_guid", "version", "driver_date", "publisher", "signature_type",
                "sha256", "size_bytes", "source_id", "source_url", "download_uri",
            },
            keys);
    }

    [Fact]
    public void ConstructorCreatesTheLayoutAndKeepsAnExistingManifest()
    {
        var cacheRoot = Path.Combine(_tempRoot, "cache");
        var source = new LocalCacheSource(cacheRoot);

        Assert.True(Directory.Exists(Path.Combine(cacheRoot, "blobs")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(cacheRoot, "manifest.json")));

        AddSimplePackage(source, "usb.inf", "; usb", "HWID1");
        var reopened = new LocalCacheSource(cacheRoot);
        Assert.Single(reopened.Search(["HWID1"]));
    }

    private LocalCacheSource NewSource() => new(Path.Combine(_tempRoot, "cache"));

    private string StageFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempRoot, "stage", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private DriverCandidate AddSimplePackage(LocalCacheSource source, string fileName, string content, string hwid)
        => AddSimplePackage(source, StageFile(fileName, content), hwid);

    private static DriverCandidate AddSimplePackage(LocalCacheSource source, string stagedPath, string hwid)
        => source.AddPackage(
            stagedPath,
            hwid: hwid,
            classGuid: "{class}",
            version: "1.0",
            driverDate: null,
            publisher: "Acme",
            signatureType: SignatureType.Whql);

    // Exactly what Python's json.dumps(entries, indent=2, default=str) emits.
    private static string ManifestJson(string firstSignatureType) =>
        $$"""
        [
          {
            "hwid": "HWID1",
            "class_guid": "{4d36e968-e325-11ce-bfc1-08002be10318}",
            "version": "31.0.15.3623",
            "driver_date": "2024-03-19",
            "publisher": "NVIDIA",
            "signature_type": "{{firstSignatureType}}",
            "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            "size_bytes": 27,
            "source_id": "local_cache",
            "source_url": "C:\\stage\\nv.inf",
            "download_uri": "C:\\cache\\blobs\\9f86\\nv.inf"
          },
          {
            "hwid": "HWID2",
            "class_guid": "{class}",
            "version": "2.0",
            "driver_date": null,
            "publisher": "Acme",
            "signature_type": "attestation",
            "sha256": "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752",
            "size_bytes": 12,
            "source_id": "local_cache",
            "source_url": "C:\\stage\\net.inf",
            "download_uri": "C:\\cache\\blobs\\6030\\net.inf"
          }
        ]
        """;
}
