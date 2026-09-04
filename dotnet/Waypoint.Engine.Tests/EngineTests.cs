// Engine tests on the mock backend — no real OS or hardware. Covers the
// safety-critical behavior in docs/Architecture.md section 3.3: the batch
// aborts if the restore point fails, and unconfirmed entries never apply.

using System.Text.Json;
using Waypoint.Core;
using Waypoint.Platform;

namespace Waypoint.Engine.Tests;

public class EngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "waypoint-engine-tests",
        Guid.NewGuid().ToString("N"));

    private readonly AuditLog _audit;

    public EngineTests()
    {
        Directory.CreateDirectory(_tempDir);
        _audit = new AuditLog(Path.Combine(_tempDir, "audit.jsonl"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Apply_AbortsWhenRestorePointFails_AndInstallsNothing()
    {
        var device = MissingDevice();
        var backend = new MockDeviceBackend([device]) { RestorePointShouldSucceed = false };
        var engine = NewEngine(backend);

        var plan = engine.BuildPlan(engine.Scan());

        var error = Assert.Throws<InvalidOperationException>(() => engine.Apply(
            plan,
            new Dictionary<string, Device> { ["DEV1"] = device },
            dryRun: false,
            backupDir: BackupDir));

        Assert.Contains("Restore point", error.Message);
        Assert.Empty(backend.InstallCalls);
        Assert.Contains(_audit.ReadAll(), r => r["event"].GetString() == "apply_aborted");
    }

    [Fact]
    public void MissingDevice_InstallsWithoutConfirmationRequired()
    {
        var device = MissingDevice();
        var backend = new MockDeviceBackend([device]);
        var engine = NewEngine(backend);

        var plan = engine.BuildPlan(engine.Scan());
        Assert.False(plan.Entries[0].RequiresConfirmation);

        var results = engine.Apply(
            plan,
            new Dictionary<string, Device> { ["DEV1"] = device },
            dryRun: true,
            backupDir: BackupDir);

        Assert.Single(results);
        Assert.True(results[0].Success);
    }

    [Fact]
    public void UpgradeTier_IsSkippedWithoutExplicitConfirmation()
    {
        var device = UpgradableDevice();
        var backend = new MockDeviceBackend([device]);
        var engine = NewEngine(backend);
        var devices = new Dictionary<string, Device> { ["DEV1"] = device };

        var plan = engine.BuildPlan(engine.Scan());
        Assert.True(plan.Entries[0].RequiresConfirmation);

        Assert.Empty(engine.Apply(plan, devices, dryRun: true, backupDir: BackupDir));
        Assert.Contains(_audit.ReadAll(), r => r["event"].GetString() == "skip_unconfirmed");

        var confirmed = engine.Apply(
            plan,
            devices,
            dryRun: true,
            backupDir: BackupDir,
            confirmedInstanceIds: new HashSet<string> { "DEV1" });

        Assert.Single(confirmed);
    }

    [Fact]
    public void RestorePointIsCreated_BeforeAnyBackupOrInstall()
    {
        var device = UpgradableDevice();
        var backend = new SequenceRecordingBackend(device);
        var engine = NewEngine(backend);

        var plan = engine.BuildPlan(engine.Scan());
        engine.Apply(
            plan,
            new Dictionary<string, Device> { ["DEV1"] = device },
            dryRun: false,
            backupDir: BackupDir,
            confirmedInstanceIds: new HashSet<string> { "DEV1" });

        Assert.Equal(["restore_point", "backup:DEV1", "install:DEV1"], backend.Sequence);
    }

    [Fact]
    public void AmbiguousDevice_IsGated_EvenWhenItsStatusIsNotUpgradeAvailable()
    {
        // Two devices claiming the same HWID: both "missing", both ambiguous.
        var first = MissingDevice();
        var second = MissingDevice() with { InstanceId = "DEV2", FriendlyName = "Fake GPU 2" };
        var backend = new MockDeviceBackend([first, second]);
        var engine = NewEngine(backend);
        var devices = new Dictionary<string, Device> { ["DEV1"] = first, ["DEV2"] = second };

        var plan = engine.BuildPlan(engine.Scan());
        Assert.All(plan.Entries, e => Assert.Equal(DeviceStatus.Missing.ToWireString(), e.Status));
        Assert.All(plan.Entries, e => Assert.True(e.RequiresConfirmation));

        Assert.Empty(engine.Apply(plan, devices, dryRun: true, backupDir: BackupDir));

        var confirmed = engine.Apply(
            plan,
            devices,
            dryRun: true,
            backupDir: BackupDir,
            confirmedInstanceIds: new HashSet<string> { "DEV1" });

        Assert.Equal("DEV1", Assert.Single(confirmed).InstanceId);
    }

    [Fact]
    public void AuditLog_ReadsBackRecords_SkipsBlankLines_AndToleratesAMissingFile()
    {
        var path = Path.Combine(_tempDir, "nested", "fresh.jsonl");
        var log = new AuditLog(path);
        Assert.Empty(log.ReadAll());

        log.Record("install", ("instance_id", "PCI\\VEN_10DE&DEV_2504"), ("dry_run", true), ("commands", new[] { "pnputil" }));
        File.AppendAllText(path, "\n   \n");

        var record = Assert.Single(log.ReadAll());
        Assert.Equal("install", record["event"].GetString());
        // '&' stays literal, as Python's json.dumps leaves it.
        Assert.Equal("PCI\\VEN_10DE&DEV_2504", record["instance_id"].GetString());
        Assert.Equal(JsonValueKind.True, record["dry_run"].ValueKind);
        Assert.Equal(["pnputil"], record["commands"].EnumerateArray().Select(e => e.GetString()));
        Assert.StartsWith(DateTime.UtcNow.ToString("yyyy-MM-dd"), record["timestamp"].GetString());
    }

    [Fact]
    public void PlanJson_KeepsThePythonCliShape()
    {
        var matched = MissingDevice();
        var unmatched = new Device(["HWID_NO_CANDIDATE"], "{class}", "Net", "Fake NIC", "DEV3", ProblemCode: 28);
        var engine = NewEngine(new MockDeviceBackend([matched, unmatched]));

        var plan = engine.BuildPlan(engine.Scan());

        using var document = JsonDocument.Parse(plan.ToJson());
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(
            ["instance_id", "friendly_name", "status", "chosen_candidate", "ambiguous", "requires_confirmation"],
            entries[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("missing", entries[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("chosen_candidate").ValueKind);

        var candidate = entries[0].GetProperty("chosen_candidate");
        Assert.Equal("whql", candidate.GetProperty("signature_type").GetString());
        Assert.Equal("2026-01-01", candidate.GetProperty("driver_date").GetString());
        Assert.Equal(1024, candidate.GetProperty("size_bytes").GetInt64());
    }

    private string BackupDir => Path.Combine(_tempDir, "backups");

    private WaypointEngine NewEngine(IDeviceBackend backend) =>
        new(backend, [new FakeSource(Candidate)], _audit);

    private static readonly DriverCandidate Candidate = new(
        Hwid: "HWID1",
        ClassGuid: "{class}",
        Version: "2.0",
        DriverDate: new DateOnly(2026, 1, 1),
        Publisher: "Acme",
        SignatureType: SignatureType.Whql,
        Sha256: "abc123",
        SizeBytes: 1024,
        SourceId: "fake",
        SourceUrl: "https://example.test",
        DownloadUri: "/cache/blobs/abc123/driver.inf");

    // Problem code 28 = no driver bound.
    private static Device MissingDevice() =>
        new(["HWID1"], "{class}", "Display", "Fake GPU", "DEV1", ProblemCode: 28);

    private static Device UpgradableDevice() =>
        new(
            ["HWID1"],
            "{class}",
            "Display",
            "Fake GPU",
            "DEV1",
            Installed: new InstalledDriver("1.0", new DateOnly(2024, 1, 1), "Acme", SignatureType.Whql));

    // Stands in for LocalCacheSource, which lands in its own slice.
    private sealed class FakeSource(params DriverCandidate[] candidates) : IDriverSource
    {
        public string SourceId => "fake";

        public IReadOnlyList<DriverCandidate> Search(IReadOnlyList<string> hwids) =>
            candidates.Where(c => hwids.Contains(c.Hwid)).ToList();

        public Task<string> FetchAsync(DriverCandidate candidate, string destDir, CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate.DownloadUri);
    }

    // Records call order, which MockDeviceBackend's per-call lists cannot show.
    private sealed class SequenceRecordingBackend(params Device[] devices) : IDeviceBackend
    {
        public List<string> Sequence { get; } = [];

        public IReadOnlyList<Device> EnumerateDevices() => devices;

        public string ExportDriverBackup(Device device, string destDir)
        {
            Sequence.Add($"backup:{device.InstanceId}");
            return $"{destDir}/{device.InstanceId}.backup";
        }

        public InstallResult InstallDriver(Device device, string packagePath, bool dryRun)
        {
            Sequence.Add($"install:{device.InstanceId}");
            return new InstallResult(true, [], "installed");
        }

        public bool CreateRestorePoint(string description)
        {
            Sequence.Add("restore_point");
            return true;
        }
    }
}
