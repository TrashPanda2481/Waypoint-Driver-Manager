// Ported from the driver-pack cases in tests/test_oem_sources.py.
// Fixtures are real vendor catalogs, trimmed — no network access needed.

using Waypoint.Sources.Oem;

namespace Waypoint.Sources.Tests;

public sealed class ModelDriverPackTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "waypoint-tests", Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", "oem", name);

    [Fact]
    public void DellDriverPackMatchesSystemId()
    {
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        // 092F is the OptiPlex 5070 in the fixture.
        var packs = source.PacksForModel("092F");

        Assert.Single(packs);
        Assert.Equal("OptiPlex 5070", packs[0].ModelName);
        Assert.Equal("dell_driverpack", packs[0].SourceId);
        Assert.Equal("sha256", packs[0].HashAlgorithm);
        Assert.Equal(64, packs[0].HashValue.Length);
    }

    [Fact]
    public void DellDriverPackUnknownModelReturnsEmpty()
    {
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        Assert.Empty(source.PacksForModel("ZZZZ"));
    }

    [Fact]
    public void LenovoDriverPackMatchesMachineType()
    {
        var source = new LenovoDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("lenovo_catalog_sample.xml"));

        // 10M4 is the ThinkCentre M715Q in the fixture.
        var packs = source.PacksForModel("10M4");

        Assert.NotEmpty(packs);
        Assert.Equal("ThinkCentre M715Q", packs[0].ModelName);
        Assert.Equal("lenovo_driverpack", packs[0].SourceId);
        // Lenovo's "crc" attribute is really a SHA-256; reported as such, not as "crc".
        Assert.Equal("sha256", packs[0].HashAlgorithm);
        Assert.Equal(64, packs[0].HashValue.Length);
    }

    [Fact]
    public void LenovoDriverPackMatchesCaseInsensitivelyAndFullSerial()
    {
        var source = new LenovoDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("lenovo_catalog_sample.xml"));

        // A real SMBIOS product name is longer than the 4-char type code.
        var packs = source.PacksForModel("10m4s00100");

        Assert.NotEmpty(packs);
        Assert.Equal("ThinkCentre M715Q", packs[0].ModelName);
        Assert.Equal(source.PacksForModel("10M4"), packs);
    }

    [Fact]
    public async Task LenovoRefreshDownloadsPlainXmlAndIndexesIt()
    {
        var source = new LenovoDriverPackSource(
            _cacheDir,
            downloader: (url, dest, _) =>
            {
                File.Copy(Fixture("lenovo_catalog_sample.xml"), dest, overwrite: true);
                return Task.CompletedTask;
            });

        await source.RefreshAsync();

        Assert.True(File.Exists(Path.Combine(_cacheDir, "catalogv2.xml")));
        Assert.NotEmpty(source.PacksForModel("10M4"));
    }

    // PacksForModel loads the cached catalog lazily when refresh hasn't run this session.
    [Fact]
    public void DellPacksForModelLoadsCachedCatalogLazily()
    {
        Directory.CreateDirectory(_cacheDir);
        File.Copy(Fixture("dell_driverpack_sample.xml"), Path.Combine(_cacheDir, "DriverPackCatalog.xml"));

        var source = new DellDriverPackSource(_cacheDir);

        Assert.Single(source.PacksForModel("092F"));
    }

    [Fact]
    public void DellDriverPackReportsPublishedUrlAndReleaseDate()
    {
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        var pack = source.PacksForModel("092F")[0];

        Assert.StartsWith("https://downloads.dell.com/", pack.Url, StringComparison.Ordinal);
        Assert.NotNull(pack.ReleaseDate);
        Assert.True(pack.SizeBytes > 0);
    }
    [Fact]
    public void DellDriverPackAlsoMatchesOnModelName()
    {
        // Dell's KB says systemID is not readily available via WMI and
        // recommends name-matching on Windows; SMBIOS SKU is also commonly an
        // unset placeholder, so name is the key that actually works there.
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        var packs = source.PacksForModel("OptiPlex 5070");

        Assert.NotEmpty(packs);
        Assert.All(packs, p => Assert.Equal("OptiPlex 5070", p.ModelName));
    }

    [Fact]
    public void ModelNameSpansEverySystemIdSharingIt()
    {
        // "OptiPlex 5070" is published under both 092F and 0932, so the name is
        // the broader key: it covers every release either systemID reaches.
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        var byName = source.PacksForModel("OptiPlex 5070");
        var by092F = source.PacksForModel("092F");
        var by0932 = source.PacksForModel("0932");

        var covered = byName.Select(p => p.PackId).ToHashSet();
        Assert.Superset(by092F.Select(p => p.PackId).ToHashSet(), covered);
        Assert.Superset(by0932.Select(p => p.PackId).ToHashSet(), covered);
    }

    [Fact]
    public void OneReleaseSpanningTwoSystemIdsIsListedOnce()
    {
        // Release PPPRC supports the 5070 under both 092F and 0932. Indexing
        // walks each <Model>, so without dedup the name key gets it twice --
        // the live catalog listed every 5070 pack twice.
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        var ids = source.PacksForModel("OptiPlex 5070").Select(p => p.PackId).ToList();

        Assert.Contains("PPPRC", ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    [Fact]
    public void ModelNameLookupIsCaseAndSpaceInsensitiveOnTheEdges()
    {
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        Assert.NotEmpty(source.PacksForModel("optiplex 5070"));
        Assert.NotEmpty(source.PacksForModel("  OPTIPLEX 5070  "));
    }

    [Fact]
    public void UnknownModelNameReturnsNothing()
    {
        var source = new DellDriverPackSource(_cacheDir);
        source.LoadFromXml(Fixture("dell_driverpack_sample.xml"));

        Assert.Empty(source.PacksForModel("Gigabyte B760M GAMING PLUS"));
    }
}
