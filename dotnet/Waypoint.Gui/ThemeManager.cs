// Theme switching, persistence, and Windows color tracking.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Waypoint.Gui;

internal enum AppTheme { Waypoint, Windows }

internal static class ThemeManager
{
    // Per-user UI preference; ProgramData (WaypointPaths) is machine-wide.
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Waypoint", "gui-theme.txt");

    private static ResourceDictionary? _active;

    public static AppTheme Current { get; private set; } = AppTheme.Waypoint;
    public static Palette Palette { get; private set; } = Palette.Waypoint;

    public static void Initialize()
    {
        Apply(Load(), save: false);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        Current = theme;
        Palette = theme == AppTheme.Windows ? ReadWindowsPalette() : Palette.Waypoint;

        var merged = Application.Current.Resources.MergedDictionaries;
        var next = Palette.ToResources();
        if (_active is not null) merged.Remove(_active);
        merged.Add(next);
        _active = next;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        if (save) Save(theme);
    }

    // Win10 1809+; ignored elsewhere.
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Palette.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private static Palette ReadWindowsPalette()
    {
        var light = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int l ? l != 0 : true;

        // Default Windows blue when the accent is unset.
        var accent = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int a
            ? Palette.FromAbgr(unchecked((uint)a))
            : Color.FromRgb(0x00, 0x78, 0xD4);

        return Palette.FromWindows(accent, dark: !light);
    }

    // Fires for accent and light/dark changes; cheap enough to rebuild on any.
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Current != AppTheme.Windows) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.Windows, save: false));
    }

    private static AppTheme Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                && Enum.TryParse<AppTheme>(File.ReadAllText(SettingsPath).Trim(), out var t)
                ? t : AppTheme.Waypoint;
        }
        catch (IOException) { return AppTheme.Waypoint; }
        catch (UnauthorizedAccessException) { return AppTheme.Waypoint; }
    }

    // Best effort: a lost preference is not worth an error dialog.
    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, theme.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
