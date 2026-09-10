// WPF ships BooleanToVisibilityConverter but nothing for the inverse, and the
// detail pane needs both directions off the one HasSelection flag.

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Waypoint.Gui;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
