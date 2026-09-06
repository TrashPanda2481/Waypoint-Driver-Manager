// HP platform lookup — deliberately NOT a driver source.
// Ported from oem/hp_platform.py.
//
// HP's per-update feed is a WSUS SDP: applicability is arbitrary WQL against
// Win32_ComputerSystem/BaseBoard, so per-device matching would need a WMI
// query interpreter, not a catalog parser. Faking it would present guesses as
// fact. Answers only what HP publishes declaratively: is this SystemID known,
// and for which OS versions. Do not extend into IDriverSource.

using System.Xml.Linq;

namespace Waypoint.Sources.Oem;

public sealed record HpPlatformInfo(
    string SystemId,
    string ProductName,
    IReadOnlyList<string> SupportedOsDescriptions);

public sealed class HpPlatformCatalogSource
{
    public const string SourceId = "hp_platform";

    // The list HP Image Assistant itself uses; SystemID is Win32_BaseBoard.Product.
    public const string DefaultPlatformListUrl = "https://hpia.hpcloud.hp.com/ref/platformList.cab";

    private readonly string _catalogUrl;
    private readonly DownloadFileAsync _downloader;
    private readonly string _catalogXmlPath;
    private Dictionary<string, HpPlatformInfo>? _index;

    public HpPlatformCatalogSource(
        string cacheDir,
        string catalogUrl = DefaultPlatformListUrl,
        DownloadFileAsync? downloader = null)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(CacheDir);
        _catalogUrl = catalogUrl;
        _downloader = downloader ?? HttpDownloader.DownloadAsync;
        _catalogXmlPath = Path.Combine(CacheDir, "platformList.xml");
    }

    public string CacheDir { get; }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force || !File.Exists(_catalogXmlPath))
        {
            var tmp = Directory.CreateTempSubdirectory("waypoint-hp-").FullName;
            try
            {
                var cabPath = Path.Combine(tmp, "platformList.cab");
                await _downloader(_catalogUrl, cabPath, cancellationToken).ConfigureAwait(false);

                CabExtractor.ExtractXmlPayload(cabPath, _catalogXmlPath);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }

        _index = null;
        LoadFromXml(_catalogXmlPath);
    }

    public void LoadFromXml(string xmlPath)
    {
        // PreserveWhitespace keeps LeadingText matching ElementTree's .text exactly.
        var root = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace).Root;
        var index = new Dictionary<string, HpPlatformInfo>(StringComparer.Ordinal);

        foreach (var platform in root?.Elements("Platform") ?? [])
        {
            var systemIdText = LeadingText(platform.Element("SystemID"));
            if (systemIdText is null)
            {
                continue;
            }

            var systemId = systemIdText.Trim().ToUpperInvariant();

            var osDescriptions = platform.Elements("OS")
                .Select(os => LeadingText(os.Element("OSDescription")))
                .Where(text => !string.IsNullOrEmpty(text))
                .Select(text => text!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToArray();

            // A platform can carry several ProductName siblings; the first wins.
            var productNameText = LeadingText(platform.Element("ProductName"));

            index[systemId] = new HpPlatformInfo(
                systemId,
                string.IsNullOrEmpty(productNameText) ? "unknown" : productNameText.Trim(),
                osDescriptions);
        }

        _index = index;
    }

    // Confirms HP knows the platform. It cannot say which drivers apply to a
    // device on it — that needs the WQL evaluator this source deliberately omits.
    public HpPlatformInfo? IsSupported(string systemId)
    {
        if (_index is null)
        {
            LoadFromXml(_catalogXmlPath);
        }

        return _index!.GetValueOrDefault(systemId.Trim().ToUpperInvariant());
    }

    // ElementTree's .text: the text before the first child element, else null.
    private static string? LeadingText(XElement? element)
        => element?.FirstNode is XText text ? text.Value : null;
}
