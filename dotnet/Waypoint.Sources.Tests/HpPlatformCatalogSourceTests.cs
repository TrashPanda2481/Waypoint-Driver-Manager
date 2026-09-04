// Mirrors the HP cases in tests/test_oem_sources.py, against the same trimmed
// platformList fixture taken from HP's real feed.

using System.Diagnostics;
using Waypoint.Sources.Oem;

namespace Waypoint.Sources.Tests;

public sealed class HpPlatformCatalogSourceTests : IDisposable
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "oem", "hp_platformlist_sample.xml");

    private readonly string _cacheDir =
        Directory.CreateTempSubdirectory("waypoint-hp-tests-").FullName;

    public void Dispose() => Directory.Delete(_cacheDir, recursive: true);

    private HpPlatformCatalogSource LoadedSource()
    {
        var source = new HpPlatformCatalogSource(_cacheDir);
        source.LoadFromXml(FixturePath);
        return source;
    }

    [Fact]
    public void ReportsKnownSystemId()
    {
        // HP ZBook 15 Mobile Workstation in the fixture.
        var info = LoadedSource().IsSupported("1909");

        Assert.NotNull(info);
        Assert.Contains("ZBook 15", info.ProductName);
        Assert.NotEmpty(info.SupportedOsDescriptions);
    }

    [Fact]
    public void UnknownSystemIdReturnsNull()
        => Assert.Null(LoadedSource().IsSupported("ZZZZ"));

    [Fact]
    public void SystemIdLookupIsCaseAndWhitespaceInsensitive()
    {
        var source = LoadedSource();

        // The fixture stores "190a" lowercase; SMBIOS hands back either case.
        Assert.NotNull(source.IsSupported("190A"));
        Assert.NotNull(source.IsSupported("  190a  "));
    }

    [Fact]
    public void SupportedOsDescriptionsAreDedupedAndSorted()
    {
        // 1944 has three OS entries, all "Microsoft Windows 10".
        var info = LoadedSource().IsSupported("1944");

        Assert.NotNull(info);
        Assert.Equal(new[] { "Microsoft Windows 10" }, info.SupportedOsDescriptions);
    }

    [Fact]
    public void FirstProductNameWins()
    {
        // 1947 lists four ProductName siblings; ElementTree's find() takes the first.
        var info = LoadedSource().IsSupported("1947");

        Assert.NotNull(info);
        Assert.Equal("HP ProBook 470 G1 Notebook PC", info.ProductName);
    }

    // The shape HP actually ships: one XML member, which expand.exe renames
    // after the cab itself.
    [Fact]
    public async Task RefreshExtractsSingleMemberCatalogAndIndexesIt()
    {
        var source = SourceFedBy(BuildCab(("platformList.xml", FixturePath)));

        await source.RefreshAsync();

        Assert.True(File.Exists(Path.Combine(_cacheDir, "platformList.xml")));
        Assert.NotNull(source.IsSupported("1909"));
    }

    [Fact]
    public async Task RefreshPicksTheXmlOutOfAMultiMemberCab()
    {
        var source = SourceFedBy(BuildCab(
            ("readme.txt", null),
            ("platformList.xml", FixturePath)));

        await source.RefreshAsync();

        Assert.NotNull(source.IsSupported("1909"));
    }

    [Fact]
    public async Task RefreshRejectsCabWithoutXmlPayload()
    {
        var source = SourceFedBy(BuildCab(("readme.txt", null), ("notes.txt", null)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.RefreshAsync());

        Assert.Contains("No .xml payload", ex.Message);
    }

    [Fact]
    public async Task RefreshSkipsDownloadWhenCatalogIsAlreadyCached()
    {
        File.Copy(FixturePath, Path.Combine(_cacheDir, "platformList.xml"));
        var source = new HpPlatformCatalogSource(
            _cacheDir,
            downloader: (_, _, _) => throw new InvalidOperationException("must not download"));

        await source.RefreshAsync();

        Assert.NotNull(source.IsSupported("1909"));
    }

    // Stands in for the network: the downloader just hands back a local cab.
    private HpPlatformCatalogSource SourceFedBy(string cabPath)
        => new(_cacheDir, downloader: (_, destPath, _) =>
        {
            File.Copy(cabPath, destPath, overwrite: true);
            return Task.CompletedTask;
        });

    // makecab.exe ships with Windows, same as the expand.exe CabExtractor uses.
    // A null source path means "make up a small placeholder member".
    private string BuildCab(params (string Name, string? SourcePath)[] members)
    {
        var stagingDir = Path.Combine(_cacheDir, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        var lines = new List<string>
        {
            ".OPTION EXPLICIT",
            ".Set CabinetNameTemplate=payload.cab",
            $".Set DiskDirectory1={stagingDir}",
            ".Set Cabinet=on",
            ".Set Compress=on",
            ".Set MaxDiskSize=0",
        };

        foreach (var (name, sourcePath) in members)
        {
            var staged = Path.Combine(stagingDir, name);
            if (sourcePath is null)
            {
                File.WriteAllText(staged, "placeholder");
            }
            else
            {
                File.Copy(sourcePath, staged, overwrite: true);
            }

            lines.Add($"\"{staged}\"");
        }

        var ddfPath = Path.Combine(stagingDir, "payload.ddf");
        File.WriteAllLines(ddfPath, lines);

        var startInfo = new ProcessStartInfo("makecab.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // makecab drops setup.inf/setup.rpt in cwd; keep them out of the test bin.
            WorkingDirectory = stagingDir,
        };
        startInfo.ArgumentList.Add("/F");
        startInfo.ArgumentList.Add(ddfPath);

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"makecab failed: {stdErr}");

        return Path.Combine(stagingDir, "payload.cab");
    }
}
