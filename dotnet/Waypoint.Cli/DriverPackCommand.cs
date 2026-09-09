// `waypoint driverpack` — model-keyed driver packs for this machine.
//
// A different question from `scan`: not "what fits this hardware ID" but
// "what bundle does the vendor publish for this system model", which is how
// MDT/SCCM driver injection actually works. Recovered from the abandoned
// rust-rewrite branch, where this path was built and the .NET tree left the
// three model-keyed sources implemented but unreachable.

using System.Text.Json;
using Waypoint.Core;
using Waypoint.Engine;
using Waypoint.Platform;
using Waypoint.Sources.Oem;

namespace Waypoint.Cli;

internal static class DriverPackCommand
{
    public static int Run(Options options)
    {
        if (options.HasModelOverride)
        {
            var supplied = options.OverriddenModel;
            if (supplied.IsEmpty)
            {
                Console.Error.WriteLine(
                    "error: the --model-* values are all empty or firmware placeholders, "
                    + "so there is nothing to look up");
                return Commands.ExitError;
            }

            // Loud on stderr, every run. A pack listed for a machine you are
            // only pretending to be must never be mistaken for a real result.
            Console.Error.WriteLine("note: --model-* given; reporting for the supplied model, not this machine");
            return Run(options, supplied);
        }

        return Run(options, OperatingSystem.IsWindows() ? SystemModelDetector.Detect() : SystemModel.Empty);
    }

    // Seam: the model is injected so the lookup can be tested without
    // depending on whatever hardware the test happens to run on.
    public static int Run(Options options, SystemModel model)
    {
        if (model.IsEmpty)
        {
            Console.Error.WriteLine(
                "error: this machine reports no usable system model in SMBIOS, so there is "
                + "nothing to look up. Firmware often ships placeholders like 'Default string'.");
            return Commands.ExitError;
        }

        var cacheDir = options.CacheDir ?? WaypointPaths.DefaultCacheDir;
        var sources = EngineFactory.BuildOemModelPackSources(cacheDir);
        var hp = new HpPlatformCatalogSource(cacheDir);

        var found = new List<(string SourceId, DriverPack Pack)>();
        var failed = new List<string>();

        foreach (var source in sources)
        {
            try
            {
                Refresh(source, options.ForceOemRefresh);
            }
            catch (Exception ex)
            {
                // One vendor being unreachable must not lose the others; the
                // same posture Scan takes with LastSourceFailures.
                failed.Add(source.SourceId);
                Console.Error.WriteLine($"warning: {source.SourceId} refresh failed, skipping it: {ex.Message}");
                continue;
            }

            foreach (var key in LookupKeys(source.SourceId, model))
            {
                var packs = source.PacksForModel(key);
                if (packs.Count > 0)
                {
                    found.AddRange(packs.Select(p => (source.SourceId, p)));
                    break; // first key that hits wins; SKU is more specific than name
                }
            }
        }

        HpPlatformInfo? platform = null;
        try
        {
            hp.RefreshAsync(options.ForceOemRefresh).GetAwaiter().GetResult();
            platform = hp.IsSupported(model.BaseboardProduct);
        }
        catch (Exception ex)
        {
            failed.Add(HpPlatformCatalogSource.SourceId);
            Console.Error.WriteLine($"warning: hp_platform refresh failed, skipping it: {ex.Message}");
        }

        if (options.Json)
        {
            var payload = new DriverPackReport(
                model.Manufacturer,
                model.ProductName,
                model.Sku,
                model.BaseboardProduct,
                found.Select(f => new DriverPackRow(
                    f.SourceId, f.Pack.ModelName, f.Pack.ModelKey, f.Pack.OsLabel,
                    f.Pack.Version, f.Pack.ReleaseDate?.ToString("yyyy-MM-dd"),
                    f.Pack.Url, f.Pack.HashAlgorithm, f.Pack.HashValue, f.Pack.SizeBytes)).ToList(),
                platform?.ProductName,
                failed);
            Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.DriverPackReport));
        }
        else
        {
            Console.WriteLine($"system: {Describe(model)}");
            if (platform is not null)
            {
                Console.WriteLine($"  HP platform: {platform.ProductName}");
            }

            if (found.Count == 0)
            {
                // platformList.xml identifies the machine but carries no
                // downloads; HP ships those through Image Assistant.
                Console.WriteLine(platform is not null
                    ? "  identified, but HP publishes no packs in this catalog"
                    : "  no driver packs published for this model");
            }

            foreach (var (sourceId, pack) in found)
            {
                var size = pack.SizeBytes > 0 ? $"{pack.SizeBytes / 1024 / 1024} MB" : "unknown size";
                Console.WriteLine($"  [{sourceId}] {pack.ModelName} / {pack.OsLabel}");
                Console.WriteLine($"      {pack.Version}  {size}  {pack.HashAlgorithm}");
                Console.WriteLine($"      {pack.Url}");
            }
        }

        // Nothing found is "no action needed", not an error. A source that
        // failed is, since the answer may be incomplete.
        if (failed.Count > 0)
        {
            return Commands.ExitActionNeeded;
        }

        return found.Count > 0 || platform is not null ? Commands.ExitClean : Commands.ExitActionNeeded;
    }

    // Dell keys on SMBIOS systemID or model name; Lenovo on the 4-character
    // machine-type prefix of the product name. SKU first where we have one,
    // because it is the more specific of the two.
    private static IEnumerable<string> LookupKeys(string sourceId, SystemModel model)
    {
        if (sourceId == "lenovo_driverpack")
        {
            if (model.ProductName.Length > 0)
            {
                yield return model.ProductName;
            }

            yield break;
        }

        if (model.Sku.Length > 0)
        {
            yield return model.Sku;
        }

        if (model.ProductName.Length > 0)
        {
            yield return model.ProductName;
        }

        if (model.BaseboardProduct.Length > 0 && model.BaseboardProduct != model.ProductName)
        {
            yield return model.BaseboardProduct;
        }
    }

    private static void Refresh(IModelDriverPackSource source, bool force)
    {
        switch (source)
        {
            case DellDriverPackSource dell:
                dell.RefreshAsync(force).GetAwaiter().GetResult();
                break;
            case LenovoDriverPackSource lenovo:
                lenovo.RefreshAsync(force).GetAwaiter().GetResult();
                break;
        }
    }

    private static string Describe(SystemModel model)
    {
        var parts = new List<string>();
        if (model.Manufacturer.Length > 0) parts.Add(model.Manufacturer);
        if (model.ProductName.Length > 0) parts.Add(model.ProductName);
        if (model.Sku.Length > 0) parts.Add($"SKU {model.Sku}");
        return parts.Count > 0 ? string.Join(" ", parts) : "unidentified";
    }
}
