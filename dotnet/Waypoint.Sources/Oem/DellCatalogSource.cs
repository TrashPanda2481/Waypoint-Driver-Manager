// Dell's official per-device driver catalog — CatalogPC.cab, a UTF-16 XML
// manifest (~57MB unpacked) listing PCI vendor/device IDs per driver package.
// Ported from src/waypoint/sources/oem/dell_catalog.py.
//
// Two honest limits, verified against the real catalog (2026-08-30):
// Dell publishes MD5 only, never SHA-256, so Search reports sha256="" and
// FetchAsync earns the real hash after verifying MD5; and every SupportedDevices
// entry observed was PCIInfo, so ACPI/HID/USB entries are ignored, not guessed at.

using System.Globalization;
using System.Xml.Linq;
using Waypoint.Core;

namespace Waypoint.Sources.Oem;

public sealed class DellCatalogSource : IDriverSource
{
    public const string DefaultCatalogUrl = "https://downloads.dell.com/catalog/CatalogPC.cab";

    // No signature-tier metadata is published here at all, unlike Windows Update.
    // Unsigned-until-proven, so the engine's signature gate (docs/Architecture.md
    // 3.3) holds these back unless a lower min_signature is chosen explicitly.
    private const SignatureType AssumedSignature = SignatureType.Unsigned;

    private readonly string _catalogUrl;
    private readonly DownloadFileAsync _downloader;
    private readonly string _catalogXmlPath;
    private readonly Dictionary<string, string> _md5ByUri = [];

    private Dictionary<string, List<DriverCandidate>>? _index;

    public DellCatalogSource(
        string cacheDir,
        string catalogUrl = DefaultCatalogUrl,
        DownloadFileAsync? downloader = null)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _catalogUrl = catalogUrl;
        _downloader = downloader ?? HttpDownloader.DownloadAsync;
        _catalogXmlPath = Path.Combine(cacheDir, "CatalogPC.xml");
    }

    public string SourceId => "dell_catalog";

    public string CacheDir { get; }

    // Explicit, opt-in-cost operation a caller schedules (daily, say) — never
    // driven off Search, which must stay I/O-free.
    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force || !File.Exists(_catalogXmlPath))
        {
            var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tmp);
            try
            {
                var cabPath = Path.Combine(tmp, "CatalogPC.cab");
                await _downloader(_catalogUrl, cabPath, cancellationToken).ConfigureAwait(false);
                var extracted = CabExtractor.Extract(cabPath, tmp);
                var xmlFile = extracted.FirstOrDefault(
                    p => string.Equals(Path.GetExtension(p), ".xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No .xml payload found inside {_catalogUrl}");
                File.Move(xmlFile, _catalogXmlPath, overwrite: true);
            }
            finally
            {
                try
                {
                    Directory.Delete(tmp, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        _index = null; // force re-parse
        LoadFromXml(_catalogXmlPath);
    }

    // Also the seam tests use to point at a small real-data fixture.
    public void LoadFromXml(string xmlPath)
    {
        // Load, not a fixed-encoding reader: the real catalog is UTF-16 and the
        // XML declaration is what says so.
        var root = XDocument.Load(xmlPath).Root
            ?? throw new InvalidOperationException($"{xmlPath} has no root element");
        var baseLocation = (string?)root.Attribute("baseLocation") ?? "downloads.dell.com";

        var index = new Dictionary<string, List<DriverCandidate>>(StringComparer.Ordinal);
        foreach (var component in root.Elements("SoftwareComponent"))
        {
            var componentType = component.Element("ComponentType");
            if (componentType is null || (string?)componentType.Attribute("value") != "DRVR")
            {
                continue;
            }

            var supportedDevices = component.Element("SupportedDevices");
            if (supportedDevices is null)
            {
                continue;
            }

            var hwids = HwidsForComponent(supportedDevices);
            if (hwids.Count == 0)
            {
                continue;
            }

            var packageId = (string?)component.Attribute("packageID") ?? "";
            var path = (string?)component.Attribute("path") ?? "";
            var downloadUri = $"https://{baseLocation}/{path}";
            _md5ByUri[downloadUri] = (string?)component.Attribute("hashMD5") ?? "";

            var driverDate = ParseDellReleaseDate((string?)component.Attribute("releaseDate") ?? "");

            foreach (var hwid in hwids)
            {
                var candidate = new DriverCandidate(
                    Hwid: hwid,
                    ClassGuid: "", // Dell's catalog publishes no PnP setup class GUID
                    Version: (string?)component.Attribute("vendorVersion") ?? "unknown",
                    DriverDate: driverDate,
                    Publisher: "Dell",
                    SignatureType: AssumedSignature,
                    Sha256: "", // unknown until FetchAsync downloads and verifies against MD5
                    SizeBytes: ParseSize((string?)component.Attribute("size")),
                    SourceId: SourceId,
                    SourceUrl: "https://www.dell.com/support/home/en-us/drivers/driversdetails"
                        + $"?driverid={packageId}",
                    DownloadUri: downloadUri);

                if (!index.TryGetValue(hwid, out var forHwid))
                {
                    forHwid = [];
                    index[hwid] = forHwid;
                }

                forHwid.Add(candidate);
            }
        }

        _index = index;
    }

    public IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids)
    {
        if (_index is null)
        {
            LoadFromXml(_catalogXmlPath);
        }

        var index = _index!;
        var results = new List<DriverCandidate>();
        var seen = new HashSet<(string Hwid, string DownloadUri)>();
        foreach (var hwid in hwids)
        {
            if (!index.TryGetValue(hwid.ToUpperInvariant(), out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (seen.Add((candidate.Hwid, candidate.DownloadUri)))
                {
                    results.Add(candidate);
                }
            }
        }

        return results;
    }

    public async Task<string> FetchAsync(
        DriverCandidate candidate,
        string destDir,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destDir);
        var filename = candidate.DownloadUri[(candidate.DownloadUri.LastIndexOf('/') + 1)..];
        var destPath = Path.Combine(destDir, filename);
        await _downloader(candidate.DownloadUri, destPath, cancellationToken).ConfigureAwait(false);

        if (_md5ByUri.TryGetValue(candidate.DownloadUri, out var expectedMd5) && expectedMd5.Length > 0)
        {
            var actualMd5 = Hashing.Md5File(destPath);
            if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MD5 mismatch for {filename}: catalog says {expectedMd5}, "
                    + $"downloaded file hashes to {actualMd5}. Refusing to use "
                    + "this download — Dell's catalog only publishes MD5, so this "
                    + "check (not SHA-256) is the only integrity signal available "
                    + "at this stage.");
            }
        }

        // The real hash of the verified bytes, for local re-verification later.
        Hashing.Sha256File(destPath);
        return destPath;
    }

    private static List<string> HwidsForComponent(XElement supportedDevices)
    {
        var hwids = new List<string>();
        foreach (var device in supportedDevices.Elements("Device"))
        {
            foreach (var pci in device.Elements("PCIInfo"))
            {
                var vendorId = ((string?)pci.Attribute("vendorID") ?? "").Trim();
                var deviceId = ((string?)pci.Attribute("deviceID") ?? "").Trim();
                if (vendorId.Length == 0 || deviceId.Length == 0)
                {
                    continue;
                }

                var subDeviceId = ((string?)pci.Attribute("subDeviceID") ?? "").Trim();
                var subVendorId = ((string?)pci.Attribute("subVendorID") ?? "").Trim();
                var generic = $"PCI\\VEN_{vendorId.ToUpperInvariant()}&DEV_{deviceId.ToUpperInvariant()}";
                hwids.Add(generic);
                if (subDeviceId.Length > 0 && subVendorId.Length > 0)
                {
                    // SUBSYS is subDeviceID then subVendorID, in that order —
                    // reversing it matches nothing, or the wrong device.
                    hwids.Add(
                        $"{generic}&SUBSYS_{subDeviceId.ToUpperInvariant()}{subVendorId.ToUpperInvariant()}");
                }
            }
        }

        return hwids;
    }

    // Dell's "March 04, 2021". Invariant culture: the CLI publishes with
    // InvariantGlobalization, so ambient culture is not available to rely on.
    private static DateOnly? ParseDellReleaseDate(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        string[] formats = ["MMMM d, yyyy", "MMMM dd, yyyy"];
        return DateTime.TryParseExact(
            value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private static long ParseSize(string? value) =>
        string.IsNullOrEmpty(value) ? 0L : long.Parse(value, CultureInfo.InvariantCulture);
}
