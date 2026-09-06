// One place for backend + source selection, so CLI and GUI can't drift
// (Architecture.md 3.4). Ported from engine/factory.py.

using Waypoint.Core;
using Waypoint.Sources;
using Waypoint.Sources.Oem;

namespace Waypoint.Engine;

public static class EngineFactory
{
    public static IDeviceBackend BuildDefaultBackend()
        => throw new PlatformNotSupportedException(
            "The Windows device backend (SetupAPI/WMI) is ADR-0001 migration step 3 and does not " +
            "exist yet. Construct WaypointEngine with an explicit backend until then — failing " +
            "loudly rather than handing back a mock that would report fabricated devices.");

    public static List<IDriverSource> BuildDefaultSources(string? cacheDir = null)
    {
        var sources = new List<IDriverSource> { new LocalCacheSource(cacheDir ?? WaypointPaths.DefaultCacheDir) };
        if (OperatingSystem.IsWindows())
        {
            sources.Add(new WindowsUpdateCatalogSource());
        }

        return sources;
    }

    // Opt-in: RefreshAsync pulls a ~57MB catalog, too costly for every scan.
    // Call RefreshAsync (or reuse a cacheDir that has) before Search.
    // Dell only — the sole OEM source shaped like IDriverSource.
    public static List<IDriverSource> BuildOemSources(string? cacheDir = null)
        => [new DellCatalogSource(cacheDir ?? WaypointPaths.DefaultCacheDir)];

    public static WaypointEngine BuildDefaultEngine(
        IDeviceBackend backend,
        string? cacheDir = null,
        string? auditLogPath = null,
        SignatureType minSignature = SignatureType.Attestation)
        => new(
            backend,
            BuildDefaultSources(cacheDir),
            new AuditLog(auditLogPath ?? WaypointPaths.DefaultAuditLogPath),
            minSignature);
}
