// One place for backend + source selection, so CLI and GUI can't drift
// (Architecture.md 3.4). Ported from engine/factory.py.

using Waypoint.Core;
using Waypoint.Platform;
using Waypoint.Sources;
using Waypoint.Sources.Oem;

namespace Waypoint.Engine;

public static class EngineFactory
{
    public static IDeviceBackend BuildDefaultBackend()
        => OperatingSystem.IsWindows()
            ? new WindowsDeviceBackend()
            : throw new PlatformNotSupportedException(
                "Waypoint is Windows-only (ADR-0001). No device backend exists for this platform.");

    // WindowsUpdateCatalogSource is deliberately not here. PublishAot sets
    // System.Runtime.InteropServices.BuiltInComInterop.IsSupported=false, so
    // its [ComImport] activation throws "Built-in COM has been disabled" in
    // the configuration Waypoint actually ships -- every scan would report a
    // failed source. Reaching Windows Update needs a ComWrappers rewrite
    // ([GeneratedComInterface]); tracked in docs/TODO.md.
    public static List<IDriverSource> BuildDefaultSources(string? cacheDir = null)
        => [new LocalCacheSource(cacheDir ?? WaypointPaths.DefaultCacheDir)];

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
