// Guards the expand.exe quirks that made the first cut of CabExtractor
// silently return the cab instead of its payload.

using Waypoint.Sources.Oem;

namespace Waypoint.Sources.Tests;

public sealed class CabExtractorTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "oem");

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "waypoint-cab-tests", Guid.NewGuid().ToString("N"));

    public CabExtractorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ExtractXmlPayload_RecoversThePayload_NotTheCabItself()
    {
        var cab = Path.Combine(FixtureDir, "hp_platformlist_sample.cab");
        var dest = Path.Combine(_tempDir, "platformList.xml");

        CabExtractor.ExtractXmlPayload(cab, dest);

        var extracted = File.ReadAllText(dest);
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "hp_platformlist_sample.xml"));
        Assert.Equal(expected, extracted);
        Assert.StartsWith("<ImagePal>", extracted.TrimStart());
    }

    [Fact]
    public void ExtractXmlPayload_RejectsAFileThatIsNotACab()
    {
        // expand.exe copies a non-cab through verbatim and still exits 0.
        var notACab = Path.Combine(_tempDir, "error-page.cab");
        File.WriteAllText(notACab, "<html><body>404 Not Found</body></html>");

        var ex = Assert.Throws<CabExtractionException>(
            () => CabExtractor.ExtractXmlPayload(notACab, Path.Combine(_tempDir, "out.xml")));
        Assert.Contains("not a cabinet file", ex.Message);
    }

    [Fact]
    public void ExtractXmlPayload_WorksWhenTheDestinationSitsBesideTheCab()
    {
        // The shape that silently failed before: expand.exe refuses to write
        // into the cab's own directory and still exits 0.
        var cab = Path.Combine(_tempDir, "platformList.cab");
        File.Copy(Path.Combine(FixtureDir, "hp_platformlist_sample.cab"), cab);

        CabExtractor.ExtractXmlPayload(cab, Path.Combine(_tempDir, "platformList.xml"));

        var extracted = File.ReadAllText(Path.Combine(_tempDir, "platformList.xml"));
        Assert.StartsWith("<ImagePal>", extracted.TrimStart());
        Assert.Contains("1909", extracted);
    }
}
