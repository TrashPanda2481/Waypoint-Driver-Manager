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
        }
        catch (Exception ex)
        {
            // Off-platform or no backend: say so once, here, instead of
            // letting every Scan click fail with the same message.
            BackendLabel.Text = "Backend: unavailable";
            StatusLabel.Text = ScanRunner.Describe(ex);
            ScanButton.IsEnabled = false;
        }
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

    private void OnShowUpToDateToggled(object sender, RoutedEventArgs e)
    {
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
        ScanButton.IsEnabled = false;
        SourceFailureLabel.Visibility = Visibility.Collapsed;
        StatusLabel.Text = (includeOem, forceRefresh) switch
        {
            (true, true) => "Scanning… re-downloading OEM catalogs regardless of cache, this may take a moment.",
            (true, false) => "Scanning… including OEM catalogs, first use downloads a real catalog.",
            _ => "Scanning…",
        };

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
        }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void RenderTree(ScanOutcome outcome)
    {
        var showUpToDate = ShowUpToDateCheckBox.IsChecked == true;
        var nodes = TreeBuilder.Build(outcome.Assessments, showUpToDate);
        DeviceTree.ItemsSource = nodes;
        DataContext = DetailCard.Empty;

        var shown = nodes.Sum(tier => tier.Children.Sum(cls => cls.Children.Count));
        var upToDate = outcome.Assessments.Count(a => a.Status == DeviceStatus.UpToDate);

        EmptyTreeLabel.Text = upToDate == outcome.Assessments.Count && outcome.Assessments.Count > 0
            ? $"Nothing needs attention. All {upToDate} devices are up to date — "
                + "tick “Show up-to-date devices” to inspect them anyway."
            : "No devices to show.";
        EmptyTreeLabel.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
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
