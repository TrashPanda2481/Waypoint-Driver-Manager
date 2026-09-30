// The Windows theme derives its colors from whatever accent the user picked,
// so readability has to hold for any accent, not just the ones we tried.

using System.Windows.Media;
using Waypoint.Gui;

namespace Waypoint.Gui.Tests;

public class PaletteTests
{
    public static TheoryData<uint> Accents => new()
    {
        0xFF0078D4, // default blue (RGB order)
        0xFFFFB900, // yellow
        0xFF00CC6A, // mint
        0xFFE81123, // red
        0xFF881798, // purple
        0xFF101010, // near-black
        0xFFF0F0F0, // near-white
    };

    private static Color Rgb(uint argb) => Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    [Fact]
    public void FromAbgr_ReadsDwmByteOrder()
        => Assert.Equal(Color.FromRgb(0xD4, 0x78, 0x00), Palette.FromAbgr(0xFF0078D4));

    [Theory]
    [MemberData(nameof(Accents))]
    public void WindowsPalette_AccentInkReadableOnPanel(uint accent)
    {
        foreach (var dark in new[] { true, false })
        {
            var p = Palette.FromWindows(Rgb(accent), dark);
            Assert.True(Palette.Contrast(p.AccentInk, p.Panel) >= 4.5, $"dark={dark}");
        }
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void WindowsPalette_ButtonTextReadableOnAccent(uint accent)
    {
        var p = Palette.FromWindows(Rgb(accent), dark: true);
        Assert.True(Palette.Contrast(p.OnAccent, p.Accent) >= 4.5);
    }

    [Fact]
    public void WaypointPalette_TextReadable()
    {
        var p = Palette.Waypoint;
        Assert.True(Palette.Contrast(p.Ink, p.Surface) >= 4.5);
        Assert.True(Palette.Contrast(p.Muted, p.Panel) >= 4.5);
        Assert.True(Palette.Contrast(p.OnAccent, p.Accent) >= 4.5);
        Assert.True(Palette.Contrast(p.WarnInk, p.WarnFill) >= 4.5);
    }
}
