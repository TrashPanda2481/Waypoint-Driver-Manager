// AOT toolchain smoke test — exercises the real engine/source types against a
// temp cache so the trimmer actually analyses them. Not the real CLI yet.

using Waypoint.Core;
using Waypoint.Engine;
using Waypoint.Sources;

var scratch = Path.Combine(Path.GetTempPath(), "waypoint-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    var cache = new LocalCacheSource(Path.Combine(scratch, "cache"));

    var driverFile = Path.Combine(scratch, "driver.inf");
    File.WriteAllText(driverFile, "; fake inf for smoke-test purposes");
    cache.AddPackage(
        driverFile,
        hwid: "PCI\\VEN_10DE&DEV_2504",
        classGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
        version: "32.0.15.6094",
        driverDate: new DateOnly(2026, 6, 1),
        publisher: "NVIDIA",
        signatureType: SignatureType.Whql);

    var installed = new InstalledDriver(
        Version: "27.20.100.8681",
        DriverDate: new DateOnly(2023, 5, 1),
        Publisher: "NVIDIA",
        SignatureType: SignatureType.Whql);

    var device = new Device(
        Hwids: ["PCI\\VEN_10DE&DEV_2504"],
        ClassGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
        ClassName: "Display",
        FriendlyName: "NVIDIA GeForce RTX 3060",
        InstanceId: "PCI\\VEN_10DE&DEV_2504\\4&1a2b3c4d&0&0008",
        Installed: installed);

    var candidates = cache.Search([device.Hwids[0]]).ToList();
    var assessments = Matching.AssessAll(
        [device],
        new Dictionary<string, List<DriverCandidate>> { [device.Hwids[0]] = candidates });

    var audit = new AuditLog(Path.Combine(scratch, "audit.jsonl"));
    audit.Record("smoke", ("devices", assessments.Count), ("candidates", candidates.Count));

    foreach (var assessment in assessments)
    {
        Console.WriteLine(
            $"{assessment.Device.FriendlyName} [{assessment.Device.ClassName}] -> {assessment.Status.ToWireString()}");
        foreach (var candidate in assessment.Candidates)
        {
            Console.WriteLine(
                $"  candidate: {candidate.Version} ({candidate.SignatureType.ToWireString()}, {candidate.DriverDate:yyyy-MM-dd}) from {candidate.SourceId}");
        }
    }

    Console.WriteLine($"audit records: {audit.ReadAll().Count}");
}
finally
{
    try
    {
        Directory.Delete(scratch, recursive: true);
    }
    catch (IOException)
    {
    }
}
