using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Chat;
internal static class Theme
{
    public static Brush Brush(string token) => (Brush)Application.Current.FindResource(token);
    public static void Apply()
    {
        // Keep the product's original light palette, independent of Windows theme.
        var values = new (string Key, string Color)[] {
            ("Surface", "#FFFFFF"), ("Canvas", "#EEF2F7"), ("Subtle", "#EEF2F8"),
            ("Ink", "#0F2240"), ("Muted", "#5D6F86"),
            ("Line", "#E1E8F2"), ("Accent", "#1F6FFF"), ("AccentDeep", "#0B4FD8"),
            ("Selected", "#EAF2FF"), ("Danger", "#C2364A"), ("Success", "#15803D"), ("Link", "#1462E8")
        };
        foreach (var (key, color) in values) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
