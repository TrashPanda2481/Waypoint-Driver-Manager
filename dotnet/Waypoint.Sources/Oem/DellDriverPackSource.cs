// Dell's per-model driver pack catalog, keyed by SMBIOS systemID.
// Ported from src/waypoint/sources/oem/dell_driverpack.py.

using System.Globalization;
using System.Xml.Linq;
using Waypoint.Core;

namespace Waypoint.Sources.Oem;

public sealed class DellDriverPackSource : IModelDriverPackSource
{
    public const string DefaultCatalogUrl = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";

    // Strongest first — older packages in this catalog only carry weaker hashes.
    private static readonly string[] HashPreference = ["SHA256", "SHA1", "MD5"];

    private static readonly string[] ReleaseDateFormats = ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd"];

    private readonly string _catalogUrl;
    private readonly DownloadFileAsync _downloader;
    private readonly string _catalogXmlPath;
    private Dictionary<string, List<DriverPack>>? _index;

    public DellDriverPackSource(
        string cacheDir,
        string catalogUrl = DefaultCatalogUrl,
        DownloadFileAsync? downloader = null)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _catalogUrl = catalogUrl;
        _downloader = downloader ?? HttpDownloader.DownloadAsync;
        _catalogXmlPath = Path.Combine(cacheDir, "DriverPackCatalog.xml");
    }

    public string SourceId => "dell_driverpack";

    public string CacheDir { get; }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force || !File.Exists(_catalogXmlPath))
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                var cabPath = Path.Combine(tempDir, "DriverPackCatalog.cab");
                await _downloader(_catalogUrl, cabPath, cancellationToken).ConfigureAwait(false);

                var xmlFile = CabExtractor.Extract(cabPath, tempDir)
                    .FirstOrDefault(p => string.Equals(Path.GetExtension(p), ".xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No .xml payload found inside {_catalogUrl}");

                File.Move(xmlFile, _catalogXmlPath, overwrite: true);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        _index = null;
        LoadFromXml(_catalogXmlPath);
    }

    public void LoadFromXml(string xmlPath)
    {
        // DriverPackCatalog.xml declares xmlns="openmanage/cm/dm" on its root; CatalogPC.xml
        // declares none. Local-name matching parses either.
        var root = XDocument.Load(xmlPath).Root
            ?? throw new InvalidOperationException($"No root element in {xmlPath}");
        var baseLocation = (string?)root.Attribute("baseLocation") ?? "downloads.dell.com";

        var index = new Dictionary<string, List<DriverPack>>(StringComparer.Ordinal);
        foreach (var package in Children(root, "DriverPackage"))
        {
            var models = Children(package, "SupportedSystems")
                .SelectMany(brands => Children(brands, "Brand"))
                .SelectMany(brand => Children(brand, "Model"))
                .ToList();
            if (models.Count == 0)
            {
                continue;
            }

            var osNames = Children(package, "SupportedOperatingSystems")
                .SelectMany(oses => Children(oses, "OperatingSystem"))
                .Select(os => Children(os, "Display").FirstOrDefault())
                .Where(display => display is not null)
                .Select(display => display!.Value.Trim())
                .Where(name => name.Length > 0);
            var osLabel = string.Join(", ", osNames);
            if (osLabel.Length == 0)
            {
                osLabel = "unknown";
            }

            var (hashAlgorithm, hashValue) = BestHash(package);
            var path = (string?)package.Attribute("path") ?? "";
            var url = $"https://{baseLocation}/{path}";
            var releaseDate = ParseReleaseDate((string?)package.Attribute("dateTime") ?? "");
            var sizeText = (string?)package.Attribute("size") ?? "";
            var sizeBytes = sizeText.Length == 0 ? 0L : long.Parse(sizeText, CultureInfo.InvariantCulture);

            foreach (var model in models)
            {
                var systemId = (string?)model.Attribute("systemID") ?? "";
                if (systemId.Length == 0)
                {
                    continue;
                }

                var pack = new DriverPack(
                    PackId: (string?)package.Attribute("releaseID") ?? "",
                    ModelName: (string?)model.Attribute("name") ?? "unknown",
                    ModelKey: systemId,
                    OsLabel: osLabel,
                    Version: (string?)package.Attribute("dellVersion") ?? "unknown",
                    ReleaseDate: releaseDate,
                    Url: url,
                    HashAlgorithm: hashAlgorithm,
                    HashValue: hashValue,
                    SizeBytes: sizeBytes,
                    SourceId: SourceId);

                var key = systemId.ToUpperInvariant();
                if (!index.TryGetValue(key, out var packs))
                {
                    packs = [];
                    index[key] = packs;
                }
                packs.Add(pack);
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
        return _index!.TryGetValue(modelKey.ToUpperInvariant(), out var packs) ? packs.ToList() : [];
    }

    // Equivalent of ElementTree's "{*}tag" wildcard: match regardless of namespace.
    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(e => e.Name.LocalName == localName);

    private static (string Algorithm, string Value) BestHash(XElement package)
    {
        var crypto = Children(package, "Cryptography").FirstOrDefault();
        if (crypto is null)
        {
            return ("md5", (string?)package.Attribute("hashMD5") ?? "");
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hash in Children(crypto, "Hash"))
        {
            hashes[((string?)hash.Attribute("algorithm") ?? "").ToUpperInvariant()] = hash.Value.Trim();
        }

        foreach (var algorithm in HashPreference)
        {
            if (hashes.TryGetValue(algorithm, out var value) && value.Length > 0)
            {
                return (algorithm.ToLowerInvariant(), value);
            }
        }

        return ("md5", (string?)package.Attribute("hashMD5") ?? "");
    }

    private static DateOnly? ParseReleaseDate(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        // Dell publishes no timezone on this field, so it is read as a plain date.
        var head = value.Length > 19 ? value[..19] : value;
        foreach (var format in ReleaseDateFormats)
        {
            if (DateTime.TryParseExact(head, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return DateOnly.FromDateTime(parsed);
            }
        }
        return null;
    }
}
