// Pure matching/ranking logic: Device + candidates -> DeviceAssessment.
//
// Zero I/O. Every method here is deterministic and takes its inputs as plain
// data, which is what lets Waypoint.Core.Tests cover the "never guess on
// ambiguous matches, never silently recommend a downgrade" rules without
// touching real hardware, WMI, or udev.
//
// Ported from src/waypoint/core/matching.py (see ADR-0001).

namespace Waypoint.Core;

public static class Matching
{
    // Device Manager problem codes that mean "no driver at all" vs "driver
    // present but broken." Code 28 = drivers not installed. Code 1 = device
    // not configured correctly (often *some* driver present). This
    // distinction drives the Missing vs Problem triage split described in
    // docs/Architecture.md.
    private static readonly HashSet<int> NoDriverCodes = [28];

    private static readonly DateOnly MinDate = new(1970, 1, 1);

    /// <summary>
    /// Sort candidates by trust tier first, then recency, filtering out
    /// anything below the minimum signature policy. Never silently allows an
    /// unsigned/test-signed driver through — that requires an explicit,
    /// logged override at the engine layer, not a ranking default.
    /// </summary>
    public static List<DriverCandidate> RankCandidates(
        IEnumerable<DriverCandidate> candidates,
        SignatureType minSignature = SignatureType.Attestation)
    {
        return candidates
            .Where(c => c.SignatureType.TrustRank() <= minSignature.TrustRank())
            .OrderBy(c => c.SignatureType.TrustRank())
            .ThenBy(c => c.DriverDate ?? MinDate)
            .ToList();
    }

    /// <summary>
    /// Build the triage assessment for a single device.
    /// <paramref name="candidatesByHwid"/> maps a hardware ID to every
    /// candidate any source returned for it — the engine is responsible for
    /// merging sources before calling this; matching logic itself stays
    /// source-agnostic.
    /// </summary>
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

    /// <summary>
    /// Return every HWID that appears on more than one installed device in
    /// this scan. SDI's "installed touchpad drivers on a desktop" class of
    /// bug comes from resolving this kind of overlap automatically;
    /// Waypoint's rule is: never do that silently. See
    /// docs/Architecture.md section 3.1.
    /// </summary>
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

    /// <summary>Convenience wrapper: assess every device and flag ambiguous HWIDs.</summary>
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
