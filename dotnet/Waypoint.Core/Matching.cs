// Matching/ranking logic: Device + candidates -> DeviceAssessment. No I/O.
// Ported from core/matching.py.

namespace Waypoint.Core;

public static class Matching
{
    // Code 28 = no driver. Code 1 = driver present but broken (Missing vs Problem split).
    private static readonly HashSet<int> NoDriverCodes = [28];

    private static readonly DateOnly MinDate = new(1970, 1, 1);

    // Trust tier first, then recency. Below-policy signatures are filtered out.
    // Deliberate divergence: matching.py sorts the date ascending, handing
    // build_plan the OLDEST candidate in the best tier. Undated sort last.
    public static List<DriverCandidate> RankCandidates(
        IEnumerable<DriverCandidate> candidates,
        SignatureType minSignature = SignatureType.Attestation)
    {
        return candidates
            .Where(c => c.SignatureType.TrustRank() <= minSignature.TrustRank())
            .OrderBy(c => c.SignatureType.TrustRank())
            .ThenByDescending(c => c.DriverDate ?? MinDate)
            .ToList();
    }

    // Single-device triage. candidatesByHwid is pre-merged by the caller (engine).
    public static DeviceAssessment AssessDevice(
        Device device,
        IReadOnlyDictionary<string, List<DriverCandidate>> candidatesByHwid,
        SignatureType minSignature = SignatureType.Attestation)
    {
        var notes = new List<string>();

        var allCandidates = new List<DriverCandidate>();
        foreach (var hwid in device.Hwids)
        {
            if (candidatesByHwid.TryGetValue(hwid, out var forHwid))
            {
                allCandidates.AddRange(forHwid);
            }
        }

        var ranked = RankCandidates(allCandidates, minSignature);

        DeviceStatus status;
        if (device.Installed is null || (device.ProblemCode is { } code && NoDriverCodes.Contains(code)))
        {
            status = DeviceStatus.Missing;
        }
        else if (device.ProblemCode is not null)
        {
            status = DeviceStatus.Problem;
            notes.Add($"Device reports problem code {device.ProblemCode}.");
        }
        else if (device.Installed.SignatureType.TrustRank() > minSignature.TrustRank())
        {
            status = DeviceStatus.Problem;
            notes.Add(
                $"Installed driver signature tier '{device.Installed.SignatureType.ToWireString()}' " +
                $"fails current policy (minimum '{minSignature.ToWireString()}').");
        }
        else
        {
            var newer = ranked.Where(c => c.IsNewerThan(device.Installed)).ToList();
            if (newer.Count > 0)
            {
                status = DeviceStatus.UpgradeAvailable;
                ranked = newer;
            }
            else
            {
                status = DeviceStatus.UpToDate;
                ranked = [];
            }
        }

        return new DeviceAssessment
        {
            Device = device,
            Status = status,
            Candidates = ranked,
            Notes = notes,
        };
    }

    // HWIDs shared by more than one device this scan — never auto-resolved.
    public static HashSet<string> FindAmbiguousHwids(IEnumerable<Device> devices)
    {
        var counts = new Dictionary<string, int>();
        foreach (var device in devices)
        {
            foreach (var hwid in device.Hwids)
            {
                counts[hwid] = counts.GetValueOrDefault(hwid) + 1;
            }
        }

        return counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();
    }

    // Assess every device and flag ambiguous HWIDs.
    public static List<DeviceAssessment> AssessAll(
        IReadOnlyList<Device> devices,
        IReadOnlyDictionary<string, List<DriverCandidate>> candidatesByHwid,
        SignatureType minSignature = SignatureType.Attestation)
    {
        var ambiguousHwids = FindAmbiguousHwids(devices);
        var results = new List<DeviceAssessment>();
        foreach (var device in devices)
        {
            var assessment = AssessDevice(device, candidatesByHwid, minSignature);
            if (device.Hwids.Any(ambiguousHwids.Contains))
            {
                assessment.Ambiguous = true;
                assessment.Notes.Add(
                    "Hardware ID shared with another detected device — requires " +
                    "manual per-device confirmation before install.");
            }

            results.Add(assessment);
        }

        return results;
    }
}
