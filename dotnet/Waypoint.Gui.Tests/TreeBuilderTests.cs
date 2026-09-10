// The triage rules in Architecture.md 3.1 are the GUI's contract, so they get
// tests rather than being eyeballed in a screenshot once.

using Waypoint.Core;
using Waypoint.Gui;

namespace Waypoint.Gui.Tests;

public class TreeBuilderTests
{
    private static DeviceAssessment Assess(
        string name,
        string className,
        DeviceStatus status,
        bool ambiguous = false,
        params DriverCandidate[] candidates)
        => new()
        {
            Device = new Device(
                Hwids: [$"PCI\\VEN_TEST&DEV_{name.GetHashCode():X4}"],
                ClassGuid: "{00000000-0000-0000-0000-000000000000}",
                ClassName: className,
                FriendlyName: name,
                InstanceId: $"TEST\\{name}"),
            Status = status,
            Ambiguous = ambiguous,
            Candidates = [.. candidates],
        };

    private static List<TreeNode> Devices(TreeNode tier)
        => tier.Children.SelectMany(c => c.Children).ToList();

    private static TreeNode Tier(List<TreeNode> nodes, string label)
        => nodes.Single(n => n.Title.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void UpToDateDevicesAreHiddenUnlessAskedFor()
    {
        var assessments = new[] { Assess("Fine", "Net", DeviceStatus.UpToDate) };

        var hidden = TreeBuilder.Build(assessments, includeUpToDate: false);
        var shown = TreeBuilder.Build(assessments, includeUpToDate: true);

        Assert.DoesNotContain(hidden, n => n.Title.StartsWith("Up to date", StringComparison.Ordinal));
        Assert.Single(Devices(Tier(shown, "Up to date")));
    }

    [Fact]
    public void MissingAndProblemOpenButUpgradeStaysCollapsed()
    {
        // 3.1: Missing is always shown expanded, Upgrade available is
        // collapsed by default and never auto-checked.
        var nodes = TreeBuilder.Build(
            [
                Assess("Gone", "Net", DeviceStatus.Missing),
                Assess("Broken", "Net", DeviceStatus.Problem),
                Assess("Older", "Display", DeviceStatus.UpgradeAvailable),
            ],
            includeUpToDate: false);

        Assert.True(Tier(nodes, "Missing").IsExpanded);
        Assert.True(Tier(nodes, "Problem").IsExpanded);
        Assert.False(Tier(nodes, "Upgrade available").IsExpanded);
    }

    [Fact]
    public void EmptyTierDoesNotOpenOnAHealthyMachine()
    {
        var nodes = TreeBuilder.Build([Assess("Fine", "Net", DeviceStatus.UpToDate)], includeUpToDate: false);

        Assert.All(nodes, n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void DevicesGroupBySetupClassWithinATier()
    {
        // 3.1 wants the taxonomy Device Manager uses, not driverpack names.
        var nodes = TreeBuilder.Build(
            [
                Assess("NIC", "Net", DeviceStatus.Missing),
                Assess("GPU", "Display", DeviceStatus.Missing),
                Assess("NIC 2", "Net", DeviceStatus.Missing),
            ],
            includeUpToDate: false);

        var classes = Tier(nodes, "Missing").Children;

        Assert.Equal(["Display", "Net"], classes.Select(c => c.Title));
        Assert.Equal(2, classes.Single(c => c.Title == "Net").Children.Count);
    }

    [Fact]
    public void AmbiguousDevicesAreFlaggedInTheList()
    {
        // Never auto-resolved, so they cannot be indistinguishable from the rest.
        var nodes = TreeBuilder.Build(
            [
                Assess("Core 0", "Processor", DeviceStatus.UpToDate, ambiguous: true),
                Assess("NIC", "Net", DeviceStatus.UpToDate),
            ],
            includeUpToDate: true);

        var devices = Devices(Tier(nodes, "Up to date"));

        Assert.True(devices.Single(d => d.Title == "Core 0").IsFlagged);
        Assert.False(devices.Single(d => d.Title == "NIC").IsFlagged);
    }

    [Fact]
    public void TierCountsReportEveryDeviceInTheTier()
    {
        var nodes = TreeBuilder.Build(
            [
                Assess("A", "Net", DeviceStatus.Missing),
                Assess("B", "Display", DeviceStatus.Missing),
                Assess("C", "Net", DeviceStatus.Problem),
            ],
            includeUpToDate: false);

        Assert.Equal("Missing (2)", Tier(nodes, "Missing").Title);
        Assert.Equal("Problem (1)", Tier(nodes, "Problem").Title);
        Assert.Equal("Upgrade available (0)", Tier(nodes, "Upgrade available").Title);
    }

    [Fact]
    public void AmbiguityFilterKeepsOnlyTheFlaggedDevices()
    {
        // Without this, reviewing them means hunting badges across every class
        // group; on the test machine that is 103 devices among 233.
        var nodes = TreeBuilder.Build(
            [
                Assess("Core 0", "Processor", DeviceStatus.UpToDate, ambiguous: true),
                Assess("Core 1", "Processor", DeviceStatus.UpToDate, ambiguous: true),
                Assess("NIC", "Net", DeviceStatus.UpToDate),
                Assess("GPU", "Display", DeviceStatus.Missing),
            ],
            includeUpToDate: true,
            onlyAmbiguous: true);

        var devices = nodes.SelectMany(t => t.Children).SelectMany(c => c.Children).ToList();

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.True(d.IsFlagged));
    }

    [Fact]
    public void AmbiguityFilterOverridesTheUpToDateToggle()
    {
        // A shared hardware ID is worth reviewing whatever the driver's state.
        // Intersecting the two filters would hide what was just asked for.
        var nodes = TreeBuilder.Build(
            [Assess("Core 0", "Processor", DeviceStatus.UpToDate, ambiguous: true)],
            includeUpToDate: false,
            onlyAmbiguous: true);

        Assert.Single(Devices(Tier(nodes, "Up to date")));
    }

    [Fact]
    public void AmbiguityFilterOnACleanMachineShowsNothing()
    {
        var nodes = TreeBuilder.Build(
            [Assess("NIC", "Net", DeviceStatus.UpToDate)],
            includeUpToDate: true,
            onlyAmbiguous: true);

        Assert.Empty(nodes.SelectMany(t => t.Children).SelectMany(c => c.Children));
    }

    [Fact]
    public void DevicesWithNoClassNameStillAppear()
    {
        var nodes = TreeBuilder.Build(
            [Assess("Unknown thing", string.Empty, DeviceStatus.Problem)],
            includeUpToDate: false);

        Assert.Equal("Unclassified", Tier(nodes, "Problem").Children.Single().Title);
    }
}
