// Self-test, not a scan. Exercises matching, the local cache, JSON round-trip
// and the audit log against fixed sample data so Native AOT analyses the real
// types. Deliberately uses an impossible HWID and an obviously fake device
// name: realistic-looking sample output gets mistaken for a real detection.

using Waypoint.Core;
using Waypoint.Engine;
using Waypoint.Sources;

const string SampleHwid = @"PCI\VEN_FFFF&DEV_FFFF";

Console.WriteLine("Waypoint Driver Manager 0.1.0 — self-test");
Console.WriteLine();
Console.WriteLine("  THIS IS NOT A DRIVER SCAN.");
Console.WriteLine("  This build cannot read your hardware. The Windows device backend");
Console.WriteLine("  is not implemented yet (ADR-0001 step 3), so nothing below reflects");
Console.WriteLine("  this machine — it is fixed sample data, identical everywhere.");
Console.WriteLine();

var scratch = Path.Combine(Path.GetTempPath(), "waypoint-selftest", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    var cache = new LocalCacheSource(Path.Combine(scratch, "cache"));

    var driverFile = Path.Combine(scratch, "sample.inf");
    File.WriteAllText(driverFile, "; sample inf, not a real driver");
    cache.AddPackage(
        driverFile,
        hwid: SampleHwid,
        classGuid: "{00000000-0000-0000-0000-000000000000}",
        version: "2.0.0",
        driverDate: new DateOnly(2026, 6, 1),
        publisher: "Sample Publisher",
        signatureType: SignatureType.Whql);

    var device = new Device(
        Hwids: [SampleHwid],
        ClassGuid: "{00000000-0000-0000-0000-000000000000}",
        ClassName: "Display",
        FriendlyName: "SAMPLE DEVICE (not your hardware)",
        InstanceId: @"PCI\VEN_FFFF&DEV_FFFF\0000",
        Installed: new InstalledDriver(
            Version: "1.0.0",
            DriverDate: new DateOnly(2024, 1, 1),
            Publisher: "Sample Publisher",
            SignatureType: SignatureType.Whql));

    var candidates = cache.Search([SampleHwid]).ToList();
    var assessments = Matching.AssessAll(
        [device],
        new Dictionary<string, List<DriverCandidate>> { [SampleHwid] = candidates });

    var audit = new AuditLog(Path.Combine(scratch, "audit.jsonl"));
    audit.Record("self_test", ("devices", assessments.Count), ("candidates", candidates.Count));

    foreach (var assessment in assessments)
    {
        Console.WriteLine(
            $"  {assessment.Device.FriendlyName} [{assessment.Device.ClassName}] -> {assessment.Status.ToWireString()}");
        foreach (var candidate in assessment.Candidates)
        {
            Console.WriteLine(
                $"    candidate: {candidate.Version} ({candidate.SignatureType.ToWireString()}, {candidate.DriverDate:yyyy-MM-dd}) from {candidate.SourceId}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  OK — matching, local cache, JSON round-trip, audit log ({audit.ReadAll().Count} record).");
    Console.WriteLine("  Real device enumeration arrives with the Windows backend.");
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
