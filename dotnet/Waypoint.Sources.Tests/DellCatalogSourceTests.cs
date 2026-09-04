// Ported from the Dell cases in tests/test_oem_sources.py, against the same
// trimmed real-catalog fixture.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Waypoint.Core;
using Waypoint.Sources.Oem;

namespace Waypoint.Sources.Tests;

public sealed class DellCatalogSourceTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "oem", "dell_catalog_sample.xml");

    [Fact]
    public void MatchesGenericPciHwid()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        // Intel Integrated Sensor Solution Driver lists PCI\VEN_8086&DEV_7745
        var results = source.Search(["PCI\\VEN_8086&DEV_7745"]);

        var candidate = Assert.Single(results);
        Assert.Equal("dell_catalog", candidate.SourceId);
        Assert.Equal("3.11.100.7733", candidate.Version);
        Assert.Equal("", candidate.Sha256); // honestly unknown until FetchAsync verifies
        Assert.StartsWith("https://downloads.dell.com/", candidate.DownloadUri, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchesSpecificSubsysHwid()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        // AMD Radeon component: vendorID=1002 deviceID=6900 subDeviceID=079D subVendorID=1028
        var generic = source.Search(["PCI\\VEN_1002&DEV_6900"]);
        var specific = source.Search(["PCI\\VEN_1002&DEV_6900&SUBSYS_079D1028"]);

        Assert.Equal("16.400.2701", Assert.Single(generic).Version);
        Assert.Equal("16.400.2701", Assert.Single(specific).Version);
    }

    // The whole source hangs off this: SUBSYS is subDeviceID then subVendorID.
    [Fact]
    public void SubsysConcatenatesSubDeviceIdBeforeSubVendorId()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        Assert.Single(source.Search(["PCI\\VEN_1002&DEV_6900&SUBSYS_079D1028"]));
        Assert.Empty(source.Search(["PCI\\VEN_1002&DEV_6900&SUBSYS_1028079D"]));

        // Realtek audio, a second component with different sub-IDs.
        Assert.Single(source.Search(["PCI\\VEN_10EC&DEV_0299&SUBSYS_08AC1028"]));
        Assert.Empty(source.Search(["PCI\\VEN_10EC&DEV_0299&SUBSYS_102808AC"]));
    }

    [Fact]
    public void UnmatchedHwidReturnsNothing()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        Assert.Empty(source.Search(["PCI\\VEN_FFFF&DEV_FFFF"]));
    }

    [Fact]
    public async Task FetchThrowsOnMd5Mismatch()
    {
        using var tmp = new TempDir();
        var payload = "pretend driver package bytes"u8.ToArray();
        var source = new DellCatalogSource(
            tmp.Root,
            downloader: (_, destPath, _) =>
            {
                File.WriteAllBytes(destPath, payload);
                return Task.CompletedTask;
            });
        source.LoadFromXml(FixturePath);
        var candidate = source.Search(["PCI\\VEN_8086&DEV_7745"])[0];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(candidate, Path.Combine(tmp.Root, "out")));

        Assert.Contains("MD5 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchUpperCasesIncomingHwid()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        Assert.Single(source.Search(["pci\\ven_8086&dev_7745"]));
    }

    [Fact]
    public void CandidateIsAssumedUnsignedAndCarriesParsedReleaseDate()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        var candidate = Assert.Single(source.Search(["PCI\\VEN_1002&DEV_6900"]));

        Assert.Equal(SignatureType.Unsigned, candidate.SignatureType);
        Assert.Equal(new DateOnly(2020, 12, 4), candidate.DriverDate); // "December 04, 2020"
        Assert.Equal("Dell", candidate.Publisher);
        Assert.Equal("", candidate.ClassGuid);
        Assert.Equal(539119632L, candidate.SizeBytes);
    }

    [Fact]
    public void DeduplicatesRepeatedPciEntriesWithinOneComponent()
    {
        using var tmp = new TempDir();
        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(FixturePath);

        // DEV_7745 appears three times under one Device in the real catalog.
        Assert.Single(source.Search(["PCI\\VEN_8086&DEV_7745", "PCI\\VEN_8086&DEV_7745"]));
    }

    // The fixture is UTF-8; Dell ships the real 57MB catalog as UTF-16.
    [Fact]
    public void ReadsUtf16CatalogFromItsXmlDeclaration()
    {
        using var tmp = new TempDir();
        var utf16Path = Path.Combine(tmp.Root, "CatalogPC-utf16.xml");
        using (var writer = XmlWriter.Create(utf16Path, new XmlWriterSettings { Encoding = Encoding.Unicode }))
        {
            XDocument.Load(FixturePath).Save(writer);
        }

        var source = new DellCatalogSource(tmp.Root);
        source.LoadFromXml(utf16Path);

        Assert.Equal("3.11.100.7733", Assert.Single(source.Search(["PCI\\VEN_8086&DEV_7745"])).Version);
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "waypoint-dell-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
