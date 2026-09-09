// Dell's per-model driver pack catalog, keyed by SMBIOS systemID.
// Ported from sources/oem/dell_driverpack.py.

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

                CabExtractor.ExtractXmlPayload(cabPath, _catalogXmlPath);
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
                var modelName = (string?)model.Attribute("name") ?? "";
                if (systemId.Length == 0 && modelName.Length == 0)
                {
                    continue;
                }

                var pack = new DriverPack(
                    PackId: (string?)package.Attribute("releaseID") ?? "",
                    ModelName: modelName.Length == 0 ? "unknown" : modelName,
                    ModelKey: systemId.Length == 0 ? modelName : systemId,
                    OsLabel: osLabel,
                    Version: (string?)package.Attribute("dellVersion") ?? "unknown",
                    ReleaseDate: releaseDate,
                    Url: url,
                    HashAlgorithm: hashAlgorithm,
                    HashValue: hashValue,
                    SizeBytes: sizeBytes,
                    SourceId: SourceId);

                // Indexed by BOTH systemID and model name. Dell's own driver-pack
                // KB says systemID "is not readily accessible [via a] WMI query"
                // and recommends matching on name for Windows, and SMBIOS SKU is
                // frequently an unset placeholder on real machines.
                // https://www.dell.com/support/kbdoc/en-us/000122176/driver-pack-catalog
                foreach (var key in Keys(systemId, modelName))
                {
                    if (!index.TryGetValue(key, out var packs))
                    {
                        packs = [];
                        index[key] = packs;
                    }

                    packs.Add(pack);
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
        // Trimmed to match how Keys() normalizes the index. The Python did not
        // trim here, but it did not index on names either.
        return _index!.TryGetValue(modelKey.Trim().ToUpperInvariant(), out var packs) ? packs.ToList() : [];
    }

    // systemID and name, uppercased, skipping blanks and the case where the
    // two are identical so a pack is never indexed twice under one key.
    private static IEnumerable<string> Keys(string systemId, string modelName)
    {
        var id = systemId.Trim().ToUpperInvariant();
        var name = modelName.Trim().ToUpperInvariant();

        if (id.Length > 0)
        {
            yield return id;
        }

        if (name.Length > 0 && name != id)
        {
            yield return name;
        }
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
