using System.Windows;

namespace Waypoint.Gui;

public partial class App : Application
{
    // Before StartupUri builds MainWindow, so its DynamicResources resolve.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
    }
}
