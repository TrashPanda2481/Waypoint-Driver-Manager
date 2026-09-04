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
}
