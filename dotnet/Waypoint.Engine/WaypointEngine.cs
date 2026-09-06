// The one path GUI and CLI both call: scan -> plan -> (confirm) -> backup ->
// install. Ported from engine/session.py. See Architecture.md 3.4.

using Waypoint.Core;

namespace Waypoint.Engine;

// One applied entry. Python returns a list of dicts with these four keys.
public sealed record ApplyResult(
    string InstanceId,
    bool Success,
    IReadOnlyList<string> Commands,
    string Message);

public sealed class WaypointEngine
{
    private const string RestorePointDescription = "Waypoint Driver Manager batch install";

    public WaypointEngine(
        IDeviceBackend backend,
        IReadOnlyList<IDriverSource> sources,
        AuditLog auditLog,
        SignatureType minSignature = SignatureType.Attestation)
    {
        Backend = backend;
        Sources = sources;
        Audit = auditLog;
        MinSignature = minSignature;
    }

    public IDeviceBackend Backend { get; }

    public IReadOnlyList<IDriverSource> Sources { get; }

    public AuditLog Audit { get; }

    public SignatureType MinSignature { get; }

    public List<DeviceAssessment> Scan()
    {
        var devices = Backend.EnumerateDevices();
        Audit.Record("scan", ("device_count", devices.Count));

        var allHwids = devices
            .SelectMany(d => d.Hwids)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hwid => hwid, StringComparer.Ordinal)
            .ToList();

        var candidatesByHwid = new Dictionary<string, List<DriverCandidate>>(StringComparer.Ordinal);
        foreach (var hwid in allHwids)
        {
            candidatesByHwid[hwid] = [];
        }

        foreach (var source in Sources)
        {
            // Search is metadata-only — no I/O here, downloads happen later per selected candidate.
            var found = source.Search(allHwids);
            Audit.Record("source_search", ("source_id", source.SourceId), ("candidates_found", found.Count));
            foreach (var candidate in found)
            {
                if (!candidatesByHwid.TryGetValue(candidate.Hwid, out var forHwid))
                {
                    forHwid = [];
                    candidatesByHwid[candidate.Hwid] = forHwid;
                }

                forHwid.Add(candidate);
            }
        }

        var assessments = Matching.AssessAll(devices, candidatesByHwid, MinSignature);
        Audit.Record(
            "assessment_complete",
            ("missing", assessments.Count(a => a.Status == DeviceStatus.Missing)),
            ("problem", assessments.Count(a => a.Status == DeviceStatus.Problem)),
            ("upgrade_available", assessments.Count(a => a.Status == DeviceStatus.UpgradeAvailable)));
        return assessments;
    }

    // Ambiguous devices and the upgrade tier are never auto-selected (3.1).
    public Plan BuildPlan(IReadOnlyList<DeviceAssessment> assessments)
    {
        var entries = new List<PlanEntry>();
        foreach (var assessment in assessments)
        {
            var chosen = assessment.Candidates.FirstOrDefault();
            var requiresConfirmation = assessment.Ambiguous || assessment.Status == DeviceStatus.UpgradeAvailable;
            entries.Add(new PlanEntry(
                InstanceId: assessment.Device.InstanceId,
                FriendlyName: assessment.Device.FriendlyName,
                Status: assessment.Status.ToWireString(),
                ChosenCandidate: chosen,
                Ambiguous: assessment.Ambiguous,
                RequiresConfirmation: requiresConfirmation));
        }

        var plan = new Plan { Entries = entries };
        Audit.Record("plan_built", ("entry_count", entries.Count));
        return plan;
    }

    // The engine enforces confirmation itself — callers are not trusted to remember.
    public List<ApplyResult> Apply(
        Plan plan,
        IReadOnlyDictionary<string, Device> devicesByInstanceId,
        bool dryRun,
        string backupDir,
        IReadOnlySet<string>? confirmedInstanceIds = null)
    {
        var confirmed = confirmedInstanceIds ?? new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ApplyResult>();

        if (!dryRun)
        {
            // Restore point first, or the whole batch stops. SDI ticket #108.
            var restoreOk = Backend.CreateRestorePoint(RestorePointDescription);
            Audit.Record("restore_point", ("success", restoreOk));
            if (!restoreOk)
            {
                Audit.Record("apply_aborted", ("reason", "restore_point_failed"));
                throw new InvalidOperationException(
                    "Restore point creation failed or could not be verified — aborting install " +
                    "batch. This is the exact failure mode SDI ticket #108 hit; Waypoint blocks " +
                    "on it instead of proceeding.");
            }
        }

        foreach (var entry in plan.Entries)
        {
            if (entry.ChosenCandidate is null)
            {
                continue;
            }

            if (entry.RequiresConfirmation && !confirmed.Contains(entry.InstanceId))
            {
                Audit.Record("skip_unconfirmed", ("instance_id", entry.InstanceId));
                continue;
            }

            var device = devicesByInstanceId[entry.InstanceId];

            // Back up whatever is bound today before replacing it.
            if (!dryRun && device.Installed is not null)
            {
                var backupPath = Backend.ExportDriverBackup(device, backupDir);
                Audit.Record("backup", ("instance_id", entry.InstanceId), ("backup_path", backupPath));
            }

            var packagePath = entry.ChosenCandidate.DownloadUri;
            var result = Backend.InstallDriver(device, packagePath, dryRun);
            Audit.Record(
                "install",
                ("instance_id", entry.InstanceId),
                ("dry_run", dryRun),
                ("success", result.Success),
                ("commands", result.CommandsRun));
            results.Add(new ApplyResult(
                InstanceId: entry.InstanceId,
                Success: result.Success,
                Commands: result.CommandsRun,
                Message: result.Message));
        }

        return results;
    }
}
