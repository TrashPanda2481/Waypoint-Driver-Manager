// Keeps the engine off the UI thread. Ported from gui/workers.py, where this
// was a QObject moved onto a QThread; await does the same job here, so the
// worker/thread lifetime bookkeeping that file warns about has no equivalent.

using Waypoint.Core;
using Waypoint.Engine;
using Waypoint.Sources;
using Waypoint.Sources.Oem;

namespace Waypoint.Gui;

internal sealed record ScanOutcome(
    IReadOnlyList<DeviceAssessment> Assessments,
    IReadOnlyList<SourceFailure> Failures);

internal static class ScanRunner
{
    // Device enumeration plus a first --oem refresh (a ~57MB download) is far
    // too slow to run on the dispatcher.
    public static Task<ScanOutcome> RunAsync(
        WaypointEngine engine,
        bool includeOem,
        bool forceOemRefresh,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                var scanning = includeOem
                    ? WithOemSources(engine, forceOemRefresh, cancellationToken)
                    : engine;

                var assessments = scanning.Scan();
                return new ScanOutcome(assessments, scanning.LastSourceFailures);
            },
            cancellationToken);

    // A second engine rather than a mutated source list. The Python added the
    // OEM sources to the live engine and stripped them again in a finally,
    // which duplicated them if that unwind was ever missed; the CLI already
    // builds a new engine instead (Commands.Build), and so does this.
    private static WaypointEngine WithOemSources(
        WaypointEngine engine,
        bool forceOemRefresh,
        CancellationToken cancellationToken)
    {
        var oemSources = EngineFactory.BuildOemSources(CacheDirOf(engine));
        foreach (var source in oemSources.OfType<DellCatalogSource>())
        {
            source.RefreshAsync(forceOemRefresh, cancellationToken).GetAwaiter().GetResult();
        }

        var sources = new List<IDriverSource>(engine.Sources);
        sources.AddRange(oemSources);
        return new WaypointEngine(engine.Backend, sources, engine.Audit, engine.MinSignature);
    }

    // Catalogs land next to the local cache, not in a second default location.
    private static string CacheDirOf(WaypointEngine engine)
        => engine.Sources.OfType<LocalCacheSource>().FirstOrDefault()?.CacheRoot
            ?? WaypointPaths.DefaultCacheDir;

    // A technician reads this in the status bar, so it has to name the fix.
    public static string Describe(Exception exception) => exception switch
    {
        PlatformNotSupportedException => "Waypoint runs on Windows only; no device backend exists for this platform.",
        UnauthorizedAccessException => $"Access denied: {exception.Message}. Try running as administrator.",
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };
}
