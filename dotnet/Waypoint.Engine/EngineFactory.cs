// Single place deciding which backend and sources a real session uses, so the
// CLI and GUI cannot drift apart (docs/Architecture.md 3.4).
// Ported from src/waypoint/engine/factory.py.

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

    // Deliberately not in BuildDefaultSources: RefreshAsync pulls a ~57MB
    // catalog, which must not fire on every scan. Callers opt in, and must
    // RefreshAsync (or point cacheDir at a previous refresh) before Search.
    //
    // Dell only, because DellCatalogSource is the sole OEM source shaped like
    // IDriverSource. Lenovo's and Dell's driver-pack catalogs are model-keyed
    // (IModelDriverPackSource) and HP's is platform-lookup only.
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
