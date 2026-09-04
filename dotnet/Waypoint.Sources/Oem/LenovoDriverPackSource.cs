// Lenovo's per-model driver pack catalog, keyed by 4-character machine type.
// Ported from src/waypoint/sources/oem/lenovo_driverpack.py.

using System.Globalization;
using System.Xml.Linq;
using Waypoint.Core;

namespace Waypoint.Sources.Oem;

public sealed class LenovoDriverPackSource : IModelDriverPackSource
{
    public const string DefaultCatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";

    private readonly string _catalogUrl;
    private readonly DownloadFileAsync _downloader;
    private readonly string _catalogXmlPath;
    private Dictionary<string, List<DriverPack>>? _index;

    public LenovoDriverPackSource(
        string cacheDir,
        string catalogUrl = DefaultCatalogUrl,
        DownloadFileAsync? downloader = null)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _catalogUrl = catalogUrl;
        _downloader = downloader ?? HttpDownloader.DownloadAsync;
        _catalogXmlPath = Path.Combine(cacheDir, "catalogv2.xml");
    }

    public string SourceId => "lenovo_driverpack";

    public string CacheDir { get; }

    // Plain XML, not a .cab — no extraction step, unlike Dell.
    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force || !File.Exists(_catalogXmlPath))
        {
            await _downloader(_catalogUrl, _catalogXmlPath, cancellationToken).ConfigureAwait(false);
        }

        _index = null;
        LoadFromXml(_catalogXmlPath);
    }

    public void LoadFromXml(string xmlPath)
    {
        var root = XDocument.Load(xmlPath).Root
            ?? throw new InvalidOperationException($"No root element in {xmlPath}");

        var index = new Dictionary<string, List<DriverPack>>(StringComparer.Ordinal);
        foreach (var model in root.Elements("Model"))
        {
            var modelName = (string?)model.Attribute("name") ?? "unknown";
            var types = model.Elements("Types")
                .SelectMany(group => group.Elements("Type"))
                .Where(type => !string.IsNullOrEmpty(type.Value))
                .Select(type => type.Value.Trim())
                .ToList();
            if (types.Count == 0)
            {
                continue;
            }

            // <BIOS> siblings are deliberately not exposed: a bad flash bricks a machine in a
            // way no restore point or driver rollback undoes. Firmware needs its own workflow.
            foreach (var sccm in model.Elements("SCCM"))
            {
                var url = sccm.Value.Trim();
                if (url.Length == 0)
                {
                    continue;
                }

                var crc = (string?)sccm.Attribute("crc") ?? "";
                var md5 = (string?)sccm.Attribute("md5") ?? "";
                // Vendor misnomer: "crc" carries a 64-hex-char SHA-256, not a CRC32. Reported honestly.
                var (hashAlgorithm, hashValue) = crc.Length == 64 ? ("sha256", crc) : ("md5", md5);

                var packId = url[(url.LastIndexOf('/') + 1)..];
                var osLabel = (string?)sccm.Attribute("os") ?? "unknown";
                var version = (string?)sccm.Attribute("version") ?? "unknown";
                var releaseDate = ParseReleaseDate((string?)sccm.Attribute("date") ?? "");

                foreach (var typeCode in types)
                {
                    var key = typeCode.ToUpperInvariant();
                    if (!index.TryGetValue(key, out var packs))
                    {
                        packs = [];
                        index[key] = packs;
                    }
                    packs.Add(new DriverPack(
                        PackId: packId,
                        ModelName: modelName,
                        ModelKey: key,
                        OsLabel: osLabel,
                        Version: version,
                        ReleaseDate: releaseDate,
                        Url: url,
                        HashAlgorithm: hashAlgorithm,
                        HashValue: hashValue,
                        SizeBytes: 0, // not published by this catalog
                        SourceId: SourceId));
                }
            }
        }

        _index = index;
    }

    public IReadOnlyList<DriverPack> PacksForModel(string modelKey)
    {
        if (_index is null)
        {
            LoadFromXml(_catalogXmlPath);
        }

        // A real system's SMBIOS product name is longer than the machine type it starts with
        // (e.g. "10M4S00100"), so match on the first 4 characters only.
        var normalized = modelKey.Trim().ToUpperInvariant();
        var lookupKey = normalized.Length > 4 ? normalized[..4] : normalized;
        return _index!.TryGetValue(lookupKey, out var packs) ? packs.ToList() : [];
    }

    private static DateOnly? ParseReleaseDate(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }
        // Date-only field, no timezone published.
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
