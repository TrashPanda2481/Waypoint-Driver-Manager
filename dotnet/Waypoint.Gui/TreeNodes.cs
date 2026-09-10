// Tier -> setup class -> device, the shape Architecture.md 3.1 asks for:
// three-tier triage, and the same PnP class taxonomy Device Manager uses
// rather than opaque driverpack names.

using Waypoint.Core;

namespace Waypoint.Gui;

public sealed class TreeNode
{
    public required string Title { get; init; }

    public string Detail { get; init; } = string.Empty;

    // Set on device rows only; the detail pane keys off it being non-null.
    public DeviceAssessment? Assessment { get; init; }

    public bool IsExpanded { get; set; }

    public bool IsFlagged { get; init; }

    public List<TreeNode> Children { get; } = [];
}

internal static class TreeBuilder
{
    private static readonly (DeviceStatus Status, string Label, bool Expanded)[] Tiers =
    [
        (DeviceStatus.Missing, "Missing", true),
        (DeviceStatus.Problem, "Problem", true),
        // Collapsed by default and never auto-checked, per 3.1.
        (DeviceStatus.UpgradeAvailable, "Upgrade available", false),
        // Off unless asked for, per 3.1. On a healthy machine this is every
        // device, and it is the only way to reach the ambiguous ones.
        (DeviceStatus.UpToDate, "Up to date", false),
    ];

    public static List<TreeNode> Build(
        IReadOnlyList<DeviceAssessment> assessments,
        bool includeUpToDate,
        bool onlyAmbiguous = false)
    {
        if (onlyAmbiguous)
        {
            assessments = [.. assessments.Where(a => a.Ambiguous)];
            // A shared hardware ID is worth reviewing whatever the driver's
            // state, so this filter overrides the up-to-date one rather than
            // intersecting with it and hiding most of what was asked for.
            includeUpToDate = true;
        }

        var nodes = new List<TreeNode>();

        foreach (var (status, label, expanded) in Tiers)
        {
            if (status == DeviceStatus.UpToDate && !includeUpToDate)
            {
                continue;
            }

            var inTier = assessments.Where(a => a.Status == status).ToList();
            var tier = new TreeNode
            {
                Title = $"{label} ({inTier.Count})",
                IsExpanded = expanded && inTier.Count > 0,
            };

            foreach (var group in inTier.GroupBy(a => ClassOf(a)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var classNode = new TreeNode
                {
                    Title = group.Key,
                    Detail = $"{group.Count()} device(s)",
                    IsExpanded = expanded,
                };

                foreach (var assessment in group.OrderBy(a => a.Device.FriendlyName, StringComparer.OrdinalIgnoreCase))
                {
                    classNode.Children.Add(DeviceRow(assessment));
                }

                tier.Children.Add(classNode);
            }

            nodes.Add(tier);
        }

        return nodes;
    }

    private static TreeNode DeviceRow(DeviceAssessment assessment)
    {
        var best = assessment.Candidates.Count > 0 ? assessment.Candidates[0] : null;
        var detail = best is null
            ? "no candidate"
            : $"{best.Version} from {best.SourceId}";

        return new TreeNode
        {
            Title = assessment.Device.FriendlyName,
            // Ambiguity is not spelled out here; the confirm badge says it.
            Detail = detail,
            // Ambiguous matches need explicit per-device confirmation and are
            // never auto-resolved, so they have to be visible in the list.
            IsFlagged = assessment.Ambiguous,
            Assessment = assessment,
        };
    }

    private static string ClassOf(DeviceAssessment assessment)
        => assessment.Device.ClassName.Length > 0 ? assessment.Device.ClassName : "Unclassified";
}
