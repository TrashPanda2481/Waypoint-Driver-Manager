// Ported from gui/app.py. Same engine construction path the CLI uses, so the
// GUI can't silently disagree with `waypoint scan`.

using System.Windows;
using System.Windows.Controls;
using Waypoint.Core;
using Waypoint.Engine;

namespace Waypoint.Gui;

public partial class MainWindow : Window
{
    private readonly WaypointEngine? _engine;
    private bool _scanning;

    // Kept so the up-to-date toggle re-renders instead of re-scanning.
    private ScanOutcome? _lastScan;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = DetailCard.Empty;

        try
        {
            _engine = EngineFactory.BuildDefaultEngine(EngineFactory.BuildDefaultBackend());
            BackendLabel.Text = $"Backend: {_engine.Backend.GetType().Name}";
            ShowEmptyState("No scan yet", "Click Scan to read this machine's device tree.");
        }
        catch (Exception ex)
        {
            // Off-platform or no backend: say so once, here, instead of
            // letting every Scan click fail with the same message.
            BackendLabel.Text = "Backend: unavailable";
            StatusLabel.Text = ScanRunner.Describe(ex);
            ScanButton.IsEnabled = false;
            ShowEmptyState("No device backend", ScanRunner.Describe(ex));
        }
    }

    // The tree and the message are alternatives, never both. Sharing the cell
    // drew them on top of each other, because a tier row renders at count 0.
    private void ShowEmptyState(string title, string hint)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
        DeviceTree.Visibility = Visibility.Collapsed;
    }

    private void ShowTree()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        DeviceTree.Visibility = Visibility.Visible;
    }

    private void OnOemToggled(object sender, RoutedEventArgs e)
    {
        // Can't be left checked in a state where it would silently do nothing,
        // which is the CLI's "no effect without --oem" warning as a UI rule.
        var oem = OemCheckBox.IsChecked == true;
        ForceRefreshCheckBox.IsEnabled = oem;
        if (!oem)
        {
            ForceRefreshCheckBox.IsChecked = false;
        }
    }

    private void OnFilterToggled(object sender, RoutedEventArgs e)
    {
        // The ambiguity filter spans every tier, so the up-to-date toggle has
        // nothing left to decide while it is on.
        ShowUpToDateCheckBox.IsEnabled = !_scanning && OnlyAmbiguousCheckBox.IsChecked != true;

        if (_lastScan is not null)
        {
            RenderTree(_lastScan);
        }
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanning || _engine is null)
        {
            return;
        }

        var includeOem = OemCheckBox.IsChecked == true;
        var forceRefresh = ForceRefreshCheckBox.IsChecked == true;

        _scanning = true;
        SetBusy(true);
        SourceFailureLabel.Visibility = Visibility.Collapsed;
        StatusLabel.Text = (includeOem, forceRefresh) switch
        {
            (true, true) => "Scanning… re-downloading OEM catalogs regardless of cache, this may take a moment.",
            (true, false) => "Scanning… including OEM catalogs, first use downloads a real catalog.",
            _ => "Scanning…",
        };

        // A re-scan keeps the previous results on screen; only a first scan has
        // an empty pane to fill.
        if (_lastScan is null)
        {
            ShowEmptyState("Scanning…", "Reading the device tree and checking each driver.");
        }

        try
        {
            var outcome = await ScanRunner.RunAsync(_engine, includeOem, forceRefresh);
            Render(outcome);
        }
        catch (Exception ex)
        {
            // Status bar, not a modal. Waypoint is meant to slot into
            // unattended workflows too, and a blocking popup fights that.
            StatusLabel.Text = $"Scan failed: {ScanRunner.Describe(ex)}";
            if (_lastScan is null)
            {
                ShowEmptyState("Scan failed", ScanRunner.Describe(ex));
            }
        }
        finally
        {
            _scanning = false;
            SetBusy(false);
        }
    }

    // Toggling a source option mid-scan would describe a run that never happened.
    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ScanButton.Content = busy ? "Scanning…" : "Scan";
        OemCheckBox.IsEnabled = !busy;
        OnlyAmbiguousCheckBox.IsEnabled = !busy;
        ShowUpToDateCheckBox.IsEnabled = !busy && OnlyAmbiguousCheckBox.IsChecked != true;
        ForceRefreshCheckBox.IsEnabled = !busy && OemCheckBox.IsChecked == true;
    }

    private void RenderTree(ScanOutcome outcome)
    {
        var onlyAmbiguous = OnlyAmbiguousCheckBox.IsChecked == true;
        var nodes = TreeBuilder.Build(
            outcome.Assessments, ShowUpToDateCheckBox.IsChecked == true, onlyAmbiguous);
        DeviceTree.ItemsSource = nodes;
        DataContext = DetailCard.Empty;

        var shown = nodes.Sum(tier => tier.Children.Sum(cls => cls.Children.Count));
        if (shown > 0)
        {
            ShowTree();
            return;
        }

        var total = outcome.Assessments.Count;
        var upToDate = outcome.Assessments.Count(a => a.Status == DeviceStatus.UpToDate);
        var ambiguous = outcome.Assessments.Count(a => a.Ambiguous);

        if (total == 0)
        {
            ShowEmptyState("No devices returned", "The scan completed but reported nothing. That is unusual.");
        }
        else if (onlyAmbiguous)
        {
            ShowEmptyState("No shared hardware IDs", $"Every one of the {total} devices matched on its own ID.");
        }
        else if (upToDate == total && ambiguous > 0)
        {
            // "Nothing needs attention" would be a lie while this many devices
            // are gated behind manual confirmation.
            ShowEmptyState(
                "No driver problems found",
                $"All {total} devices have a current driver, but {ambiguous} share a hardware ID "
                + "with another device and would need confirming one by one before any install.");
        }
        else if (upToDate == total)
        {
            ShowEmptyState(
                "Nothing needs attention",
                $"All {total} devices have a current driver. Tick Show up-to-date devices to inspect them anyway.");
        }
        else
        {
            ShowEmptyState("Nothing to show", "The current filters hide every device this scan returned.");
        }
    }

    private void Render(ScanOutcome outcome)
    {
        _lastScan = outcome;
        RenderTree(outcome);

        var actionable = outcome.Assessments.Count(
            a => a.Status is DeviceStatus.Missing or DeviceStatus.Problem);
        var ambiguous = outcome.Assessments.Count(a => a.Ambiguous);

        var summary = $"{outcome.Assessments.Count} device(s) assessed, {actionable} need attention";
        if (ambiguous > 0)
        {
            summary += $", {ambiguous} ambiguous";
        }

        StatusLabel.Text = $"Scan complete — {summary}.";

        if (outcome.Failures.Count > 0)
        {
            // An incomplete scan must not read as a complete one.
            SourceFailureLabel.Text =
                "Results may be incomplete — "
                + string.Join("; ", outcome.Failures.Select(f => $"{f.SourceId}: {f.Message}"));
            SourceFailureLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnDeviceSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        DataContext = e.NewValue is TreeNode { Assessment: { } assessment }
            ? DetailCard.From(assessment)
            : DetailCard.Empty;
    }
}
