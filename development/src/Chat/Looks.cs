using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Chat;
/// <summary>An assistant's color (a <see cref="Looks.Palette"/> key) and symbol (an <see cref="Icon"/> kind). Saved in chat-looks.bin.</summary>
public sealed record TypeLook(string Color, string Glyph);

/// <summary>Each assistant has its own color and symbol so users can tell at a glance where they are.</summary>
public static class Looks
{
    public static readonly (string Key, string Name, string Hex)[] Palette =
    {
        ("blue", "כחול", "#2563EB"), ("green", "ירוק", "#15803D"), ("purple", "סגול", "#7C3AED"),
        ("orange", "כתום", "#C2410C"), ("pink", "ורוד", "#BE185D"), ("teal", "טורקיז", "#0F766E")
    };
    public static readonly (string Key, string Name)[] Symbols =
    {
        ("chat", "שיחה"), ("people", "אנשים"), ("folder", "תיקייה"), ("book", "ספר"), ("school", "לימודים"), ("briefcase", "עבודה"),
        ("star", "כוכב"), ("heart", "לב"), ("mail", "דואר"), ("home", "בית"), ("lock", "מנעול"), ("bolt", "ברק"),
        ("globe", "עולם"), ("code", "קוד"), ("calendar", "יומן"), ("cloud", "ענן"), ("cart", "קניות"), ("plane", "טיסה")
    };
    public static bool IsColor(string key) => Palette.Any(c => c.Key == key);
    public static bool IsSymbol(string key) => Symbols.Any(s => s.Key == key);
    /// <summary>Looks saved by earlier versions used Segoe icon-font glyphs; map the ones that have a counterpart.</summary>
    public static string? Migrate(string glyph) => IsSymbol(glyph) ? glyph : glyph switch
    {
        "" or "" => "chat", "" or "" => "folder", "" => "briefcase", "" => "school", "" => "star",
        _ => null
    };
    public static Color ColorOf(string key) => (Color)ColorConverter.ConvertFromString(Palette.FirstOrDefault(c => c.Key == key).Hex ?? Palette[0].Hex);
    public static string NameOf(string key) => Palette.FirstOrDefault(c => c.Key == key).Name ?? "";
    public static SolidColorBrush Brush(string key) { var brush = new SolidColorBrush(ColorOf(key)); brush.Freeze(); return brush; }
    /// <summary>The color mixed into white: a light tile or row background in the assistant's color.</summary>
    public static SolidColorBrush Tint(string key, double amount = 0.1)
    {
        var c = ColorOf(key);
        byte Mix(byte v) => (byte)Math.Round(255 - (255 - v) * amount);
        var brush = new SolidColorBrush(Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B))); brush.Freeze();
        return brush;
    }
    /// <summary>A rounded square with the assistant's symbol in white on its color.</summary>
    public static Border Avatar(TypeLook look, double size = 36, double radius = 10) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Brush(look.Color),
        Child = new Icon(look.Glyph, Math.Round(size * 0.5), Brushes.White) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
    };
}

/// <summary>The color circles and the symbol grid. Used by the header popover and inline in the assistant form; every
/// pick raises <see cref="Changed"/> so the caller can preview it live.</summary>
public sealed class LookPicker : StackPanel
{
    public TypeLook Look { get; private set; }
    public event Action<TypeLook>? Changed;
    private readonly UniformGrid colors = new() { Columns = 6, Margin = new Thickness(-3, 0, -3, 0) };
    private readonly UniformGrid symbols = new() { Columns = 6, Margin = new Thickness(-3, 0, -3, 0) };
    public LookPicker(TypeLook look)
    {
        Look = look;
        Children.Add(Label("צבע"));
        Children.Add(colors);
        Children.Add(Label("סמל", 14));
        Children.Add(symbols);
        Fill();
    }
    private static TextBlock Label(string text, double top = 0) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Secondary"), Margin = new Thickness(0, top, 0, 8) };
    public void Set(TypeLook look) { Look = look; Fill(); }
    private void Pick(TypeLook look)
    {
        var focused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
        var tag = focused?.Tag;
        Look = look; Fill(); Changed?.Invoke(look);
        // Rebuilding replaces the buttons; keep keyboard focus on the one that was used.
        if (tag != null) (colors.Children.Cast<FrameworkElement>().Concat(symbols.Children.Cast<FrameworkElement>()).FirstOrDefault(b => Equals(b.Tag, tag)))?.Focus();
    }
    private void Fill()
    {
        colors.Children.Clear(); symbols.Children.Clear();
        foreach (var (key, name, _) in Looks.Palette)
        {
            var selected = key == Look.Color;
            var face = new Grid { Width = 40, Height = 40 };
            if (selected) face.Children.Add(new Ellipse { Stroke = Looks.Brush(key), StrokeThickness = 2 });
            face.Children.Add(new Ellipse { Width = selected ? 32 : 36, Height = selected ? 32 : 36, Fill = Looks.Brush(key) });
            if (selected) face.Children.Add(new Icon("check", 16, Brushes.White, 3) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Style = Ui.Style("IconButton"), Width = 44, Height = 44, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, Content = face, Tag = "color:" + key, ToolTip = name };
            Ui.SetCornerRadius(button, new CornerRadius(22));
            Ui.SetHoverBrush(button, Brushes.Transparent);
            System.Windows.Automation.AutomationProperties.SetName(button, "צבע " + name + (selected ? " (נבחר)" : ""));
            button.Click += (_, _) => Pick(Look with { Color = key });
            colors.Children.Add(button);
        }
        foreach (var (key, name) in Looks.Symbols)
        {
            var selected = key == Look.Glyph;
            var button = new Button { Style = Ui.Style("IconButton"), Width = 44, Height = 44, Margin = new Thickness(3, 0, 3, 8), HorizontalAlignment = HorizontalAlignment.Center, Tag = "symbol:" + key, ToolTip = name,
                Background = selected ? Looks.Brush(Look.Color) : Theme.Brush("Canvas"), Foreground = selected ? Brushes.White : Theme.Brush("Secondary"), Content = new Icon(key, 20) };
            Ui.SetCornerRadius(button, new CornerRadius(10));
            System.Windows.Automation.AutomationProperties.SetName(button, "סמל " + name + (selected ? " (נבחר)" : ""));
            button.Click += (_, _) => Pick(Look with { Glyph = key });
            symbols.Children.Add(button);
        }
    }
}
