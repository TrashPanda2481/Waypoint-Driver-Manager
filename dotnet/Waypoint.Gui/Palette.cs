// Theme colors as data. Built in code, not XAML: the Windows palette is
// derived from the live accent, so both themes share one path.

using System.Windows;
using System.Windows.Media;

namespace Waypoint.Gui;

internal sealed record Palette(
    bool IsDark,
    Color Surface, Color Panel, Color Line, Color Ink, Color Muted,
    Color Accent, Color AccentHover, Color AccentPressed, Color AccentInk, Color OnAccent,
    Color Warn, Color WarnFill, Color WarnInk, Color FlagFill,
    Color InactiveSelection)
{
    // Near-black base, slate panels, orange accent.
    public static Palette Waypoint { get; } = new(
        IsDark: true,
        Surface: Hex(0x1C1F23), Panel: Hex(0x2A3038), Line: Hex(0x3E4753),
        Ink: Hex(0xE6E9ED), Muted: Hex(0x9BA5B0),
        Accent: Hex(0xF28C38), AccentHover: Hex(0xF6A15C), AccentPressed: Hex(0xD8731F),
        AccentInk: Hex(0xF4994E), OnAccent: Hex(0x1A1C20),
        // Gold, not amber: amber reads as the orange accent.
        Warn: Hex(0xE3B341), WarnFill: Hex(0x3A3219), WarnInk: Hex(0xF2D27A), FlagFill: Hex(0x4A3F16),
        InactiveSelection: Hex(0x3E4753));

    // Windows neutrals for the current mode, around the user's accent.
    public static Palette FromWindows(Color accent, bool dark)
    {
        var (surface, panel, line, ink, muted) = dark
            ? (Hex(0x202020), Hex(0x2B2B2B), Hex(0x3D3D3D), Hex(0xF3F3F3), Hex(0xABABAB))
            : (Hex(0xFFFFFF), Hex(0xF3F3F3), Hex(0xDADADA), Hex(0x1B1B1B), Hex(0x5D5D5D));

        return new Palette(
            IsDark: dark,
            Surface: surface, Panel: panel, Line: line, Ink: ink, Muted: muted,
            Accent: accent,
            AccentHover: Mix(accent, dark ? Colors.White : Colors.Black, 0.15),
            AccentPressed: Mix(accent, Colors.Black, dark ? 0.15 : 0.28),
            AccentInk: EnsureContrast(accent, panel, 4.5),
            OnAccent: ReadableOn(accent),
            Warn: dark ? Hex(0xE3B341) : Hex(0xC9820A),
            WarnFill: dark ? Hex(0x3A3219) : Hex(0xFFF7E6),
            WarnInk: dark ? Hex(0xF2D27A) : Hex(0x6B4708),
            FlagFill: dark ? Hex(0x4A3F16) : Hex(0xFFE9C7),
            InactiveSelection: dark ? Hex(0x3D3D3D) : Hex(0xE1E1E1));
    }

    public ResourceDictionary ToResources()
    {
        var d = new ResourceDictionary();
        void Put(object key, Color c) => d[key] = Frozen(c);

        Put("Surface", Surface);
        Put("Panel", Panel);
        Put("Line", Line);
        Put("Ink", Ink);
        Put("Muted", Muted);
        Put("Accent", Accent);
        Put("AccentHover", AccentHover);
        Put("AccentPressed", AccentPressed);
        Put("AccentInk", AccentInk);
        Put("OnAccent", OnAccent);
        Put("Warn", Warn);
        Put("WarnFill", WarnFill);
        Put("WarnInk", WarnInk);
        Put("FlagFill", FlagFill);
        Put("FlagInk", WarnInk);

        // Stock control text is black; follow Ink instead.
        Put(SystemColors.ControlTextBrushKey, Ink);
        Put(SystemColors.WindowTextBrushKey, Ink);

        // Selection: accent instead of system blue. Detail text follows HighlightText.
        Put(SystemColors.HighlightBrushKey, Accent);
        Put(SystemColors.HighlightTextBrushKey, OnAccent);
        Put(SystemColors.InactiveSelectionHighlightBrushKey, InactiveSelection);
        Put(SystemColors.InactiveSelectionHighlightTextBrushKey, Ink);
        return d;
    }

    // DWM stores AccentColor as 0xAABBGGRR.
    public static Color FromAbgr(uint abgr)
        => Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));

    // WCAG 2 contrast ratio, 1..21.
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Black or white, whichever reads better on the fill.
    public static Color ReadableOn(Color fill)
        => Contrast(fill, Colors.White) >= Contrast(fill, Colors.Black) ? Colors.White : Colors.Black;

    // Pull toward black or white (away from the background) until readable.
    public static Color EnsureContrast(Color fg, Color bg, double min)
    {
        var target = Luminance(bg) > 0.5 ? Colors.Black : Colors.White;
        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
            var c = Mix(fg, target, t);
            if (Contrast(c, bg) >= min) return c;
        }
        return target;
    }

    public static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    private static double Luminance(Color c)
        => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte v)
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
