// scan / plan / apply. Ported from cli/main.py; apply is implemented rather
// than stubbed, see the note on Apply below.

using System.Text.Json;
using Waypoint.Core;
using Waypoint.Engine;
using Waypoint.Sources.Oem;

namespace Waypoint.Cli;

internal static class Commands
{
    public const int ExitClean = 0;
    public const int ExitActionNeeded = 1;
    public const int ExitError = 2;

    public static int Scan(Options options)
    {
        var (engine, _) = Build(options);
        var assessments = engine.Scan();
        ReportSourceFailures(engine);

        if (options.Json)
        {
            var rows = assessments.Select(a => new ScanRow(
                a.Device.InstanceId,
                a.Device.FriendlyName,
                a.Device.ClassName,
                a.Status.ToWireString(),
                a.Ambiguous,
                a.Candidates.Count,
                a.Notes)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(rows, CliJsonContext.Default.ListScanRow));
        }
        else
        {
            foreach (var a in assessments)
            {
                var flag = a.Ambiguous ? " [AMBIGUOUS]" : string.Empty;
                Console.WriteLine(
                    $"[{a.Status.ToWireString(),-17}] {a.Device.ClassName,-12} {a.Device.FriendlyName}{flag}");
            }
        }

        return assessments.Any(a => a.Status is DeviceStatus.Missing or DeviceStatus.Problem)
            ? ExitActionNeeded
            : ExitClean;
    }

    public static int Plan(Options options)
    {
        var (engine, _) = Build(options);
        var plan = engine.BuildPlan(engine.Scan());

        if (options.Json)
        {
            Console.WriteLine(plan.ToJson());
        }
        else
        {
            foreach (var entry in plan.Entries)
            {
                var marker = entry.RequiresConfirmation ? "CONFIRM" : "auto";
                var candidate = entry.ChosenCandidate is { } c
                    ? $"{c.Version} ({c.SourceId})"
                    : "none";
                Console.WriteLine($"[{marker,-7}] {entry.FriendlyName}: {entry.Status} -> {candidate}");
            }
        }

        if (options.OutPath is { Length: > 0 } outPath)
        {
            File.WriteAllText(outPath, plan.ToJson());
        }

        return ExitClean;
    }

    // The Python CLI stubs this out, returning an error and pointing at the
    // engine. The engine's Apply has been implemented and tested since, and it
    // already enforces confirmation itself, so wiring it up here is a real
    // command rather than a divergence in behaviour. Plan-file replay is still
    // out of scope: a plan on disk carries no Device objects to install onto.
    public static int Apply(Options options)
    {
        var (engine, _) = Build(options);
        var assessments = engine.Scan();
        var plan = engine.BuildPlan(assessments);

        var devicesByInstanceId = assessments.ToDictionary(
            a => a.Device.InstanceId,
            a => a.Device,
            StringComparer.OrdinalIgnoreCase);

        var confirmed = options.ConfirmAll
            ? plan.Entries.Select(e => e.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : options.Confirmed;

        var gated = plan.Entries
            .Where(e => e.ChosenCandidate is not null && e.RequiresConfirmation && !confirmed.Contains(e.InstanceId))
            .ToList();

        var results = engine.Apply(
            plan,
            devicesByInstanceId,
            dryRun: !options.Apply,
            backupDir: options.BackupDir ?? WaypointPaths.DefaultBackupDir,
            confirmedInstanceIds: confirmed);

        if (options.Json)
        {
            var rows = results
                .Select(r => new ApplyRow(r.InstanceId, r.Success, r.Commands, r.Message))
                .ToList();
            Console.WriteLine(JsonSerializer.Serialize(rows, CliJsonContext.Default.ListApplyRow));
        }
        else
        {
            if (!options.Apply)
            {
                Console.WriteLine("dry run - nothing was installed. Pass --apply to install.");
            }

            foreach (var r in results)
            {
                Console.WriteLine($"[{(r.Success ? "ok" : "FAILED"),-6}] {r.InstanceId}");
                foreach (var command in r.Commands)
                {
                    Console.WriteLine($"          {command}");
                }
            }

            foreach (var entry in gated)
            {
                Console.WriteLine(
                    $"[skipped] {entry.FriendlyName} needs confirmation: --confirm \"{entry.InstanceId}\"");
            }
        }

        if (results.Any(r => !r.Success))
        {
            return ExitError;
        }

        return gated.Count > 0 ? ExitActionNeeded : ExitClean;
    }

    // stderr, never stdout: a --json consumer must still get clean JSON.
    private static void ReportSourceFailures(WaypointEngine engine)
    {
        foreach (var failure in engine.LastSourceFailures)
        {
            Console.Error.WriteLine(
                $"warning: source '{failure.SourceId}' failed, results are incomplete: {failure.Message}");
        }
    }

    private static (WaypointEngine Engine, string CacheDir) Build(Options options)
    {
        var cacheDir = options.CacheDir ?? WaypointPaths.DefaultCacheDir;
        var engine = EngineFactory.BuildDefaultEngine(
            EngineFactory.BuildDefaultBackend(),
            cacheDir,
            options.AuditLogPath,
            options.MinSignature);

        if (!options.Oem)
        {
            return (engine, cacheDir);
        }

        // Refresh before the first Search: force=false reuses an already
        // downloaded catalog under this cache dir, so only the first --oem run
        // pays the ~57MB download.
        var oemSources = EngineFactory.BuildOemSources(cacheDir);
        foreach (var source in oemSources.OfType<DellCatalogSource>())
        {
            source.RefreshAsync(options.ForceOemRefresh).GetAwaiter().GetResult();
        }

        var withOem = new List<IDriverSource>(engine.Sources);
        withOem.AddRange(oemSources);
        return (new WaypointEngine(engine.Backend, withOem, engine.Audit, engine.MinSignature), cacheDir);
    }
}
