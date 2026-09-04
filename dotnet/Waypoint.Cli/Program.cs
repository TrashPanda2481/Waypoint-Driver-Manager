// AOT toolchain smoke test — mock data through Matching, not the real CLI yet.

using Waypoint.Core;

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

var candidate = new DriverCandidate(
    Hwid: "PCI\\VEN_10DE&DEV_2504",
    ClassGuid: "{4d36e968-e325-11ce-bfc1-08002be10318}",
    Version: "32.0.15.6094",
    DriverDate: new DateOnly(2026, 6, 1),
    Publisher: "NVIDIA",
    SignatureType: SignatureType.Whql,
    Sha256: "0000000000000000000000000000000000000000000000000000000000000",
    SizeBytes: 812_345_678,
    SourceId: "local_cache",
    SourceUrl: "https://example.test",
    DownloadUri: "/cache/blobs/000.../driver.inf");

var assessments = Matching.AssessAll(
    [device],
    new Dictionary<string, List<DriverCandidate>> { [candidate.Hwid] = [candidate] });

foreach (var assessment in assessments)
{
    Console.WriteLine($"{assessment.Device.FriendlyName} [{assessment.Device.ClassName}] -> {assessment.Status.ToWireString()}");
    foreach (var c in assessment.Candidates)
    {
        Console.WriteLine($"  candidate: {c.Version} ({c.SignatureType.ToWireString()}, {c.DriverDate})");
    }

    foreach (var note in assessment.Notes)
    {
        Console.WriteLine($"  note: {note}");
    }
}
