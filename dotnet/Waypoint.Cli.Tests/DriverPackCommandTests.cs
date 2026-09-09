// Exercises the model-keyed lookup end to end with no network: the cache is
// pre-seeded with the real trimmed vendor catalogs, which is exactly what a
// previous refresh would have left there, so RefreshAsync finds them and skips
// the download.

using Waypoint.Cli;
using Waypoint.Core;

namespace Waypoint.Cli.Tests;

public sealed class DriverPackCommandTests : IDisposable
{
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "waypoint-driverpack-tests", Guid.NewGuid().ToString("N"));

    public DriverPackCommandTests()
    {
        Directory.CreateDirectory(_cacheDir);

        // The Sources test project owns the fixtures; reach them by walking up
        // from this assembly rather than duplicating the files again.
        var fixtures = FindFixtures();
        Copy(fixtures, "dell_driverpack_sample.xml", "DriverPackCatalog.xml");
        Copy(fixtures, "lenovo_catalog_sample.xml", "catalogv2.xml");
        Copy(fixtures, "hp_platformlist_sample.xml", "platformList.xml");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindFixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Waypoint.Sources.Tests", "fixtures", "oem");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the shared OEM fixtures.");
    }

    private void Copy(string fixtures, string source, string cachedName)
        => File.Copy(Path.Combine(fixtures, source), Path.Combine(_cacheDir, cachedName), overwrite: true);

    private Options OptionsFor() => new() { Command = "driverpack", CacheDir = _cacheDir };

    private (int ExitCode, string Out, string Err) Run(SystemModel model)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var priorOut = Console.Out;
        var priorErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = DriverPackCommand.Run(OptionsFor(), model);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorErr);
        }
    }

    [Fact]
    public void FindsDellPacksBySystemIdSku()
    {
        var model = SystemModel.From("Dell Inc.", "OptiPlex 5070", "092F", "0A64");

        var (code, output, _) = Run(model);

        Assert.Equal(0, code);
        Assert.Contains("dell_driverpack", output);
        Assert.Contains("OptiPlex 5070", output);
    }

    [Fact]
    public void FallsBackToModelNameWhenSkuIsAPlaceholder()
    {
        // The case that makes this work on real hardware: firmware reports
        // "Default string" for SKU, so only the name is usable.
        var model = SystemModel.From("Dell Inc.", "OptiPlex 5070", "Default string", "0A64");

        Assert.Empty(model.Sku);

        var (code, output, _) = Run(model);

        Assert.Equal(0, code);
        Assert.Contains("OptiPlex 5070", output);
    }

    [Fact]
    public void FindsLenovoPackByMachineTypePrefix()
    {
        // Lenovo keys on the first 4 characters of the product name.
        var model = SystemModel.From("LENOVO", "10M4S00100", "", "3102");

        var (code, output, _) = Run(model);

        Assert.Equal(0, code);
        Assert.Contains("lenovo_driverpack", output);
    }

    [Fact]
    public void ReportsHpPlatformFromTheBaseboard()
    {
        var model = SystemModel.From("HP", "HP ZBook 15", "", "1909");

        var (code, output, _) = Run(model);

        Assert.Equal(0, code);
        Assert.Contains("ZBook 15", output);
    }

    [Fact]
    public void UnknownModelIsActionNeededNotAnError()
    {
        // A machine no vendor publishes packs for is a miss, not a failure.
        var model = SystemModel.From("Gigabyte Technology Co., Ltd.", "B760M GAMING PLUS WIFI DDR4", "", "B760M GAMING PLUS WIFI DDR4");

        var (code, output, _) = Run(model);

        Assert.Equal(1, code);
        Assert.Contains("no driver packs", output);
    }

    [Fact]
    public void RefusesWhenFirmwareReportsNothingUsable()
    {
        // Every field a placeholder: there is no question to ask a catalog.
        var model = SystemModel.From("Default string", "Default string", "Default string", "To be filled by O.E.M.");

        Assert.True(model.IsEmpty);

        var (code, _, err) = Run(model);

        Assert.Equal(2, code);
        Assert.Contains("no usable system model", err);
    }

    [Fact]
    public void JsonOutputCarriesTheModelAndPacks()
    {
        var options = OptionsFor();
        options.Json = true;

        var stdout = new StringWriter();
        var priorOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            DriverPackCommand.Run(options, SystemModel.From("Dell Inc.", "OptiPlex 5070", "092F", "0A64"));
        }
        finally
        {
            Console.SetOut(priorOut);
        }

        var json = stdout.ToString();
        Assert.Contains("\"product_name\": \"OptiPlex 5070\"", json);
        Assert.Contains("\"packs\"", json);
        Assert.Contains("\"hash_algorithm\": \"sha256\"", json);
    }
}
