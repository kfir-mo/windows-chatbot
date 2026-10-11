using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Chat;
internal static class Theme
{
    public static Brush Brush(string token) => (Brush)Application.Current.FindResource(token);
    public static SolidColorBrush Solid(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    public static void Apply()
    {
        // A fixed light palette, independent of the Windows theme. Text tokens keep ≥ 4.5:1 on Canvas and Surface.
        var values = new (string Key, string Color)[] {
            ("Surface", "#FFFFFF"), ("Canvas", "#F5F6F8"), ("Subtle", "#F0F1F4"),
            ("Ink", "#1B1F27"), ("Secondary", "#4A5160"), ("Muted", "#5B6270"),
            ("Line", "#E4E6EB"), ("Divider", "#EEF0F3"), ("FieldLine", "#D5D8DE"),
            ("Accent", "#2563EB"), ("AccentDeep", "#1D4ED8"), ("Selected", "#EFF4FF"), ("Link", "#2563EB"),
            ("Danger", "#B91C1C"), ("DangerLine", "#F3C4C4"), ("DangerSoft", "#FEF2F2"),
            ("Success", "#166534"), ("SuccessSoft", "#DCFCE7"), ("SuccessDot", "#16A34A"),
            ("Disabled", "#E9EBEF"), ("DisabledInk", "#8A909C"), ("Scrim", "#5214161C")
        };
        foreach (var (key, color) in values) Application.Current.Resources[key] = Solid(color);
        // The active assistant's color; MainWindow replaces it when the assistant changes.
        Application.Current.Resources["AssistantBrush"] = Solid("#2563EB");
        Application.Current.Resources["AssistantSoftBrush"] = Solid("#E0E9FC");
    }
}
