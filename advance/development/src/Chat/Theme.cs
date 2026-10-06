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
            ("Surface", "#FFFFFF"), ("Subtle", "#F0F3F7"),
            ("Ink", "#142D4E"), ("Muted", "#586B83"),
            ("Line", "#DBE5F2"), ("Accent", "#1673FF"),
            ("Selected", "#E7F0FF"), ("Danger", "#AD3646"), ("Link", "#1269ED")
        };
        foreach (var (key, color) in values) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
