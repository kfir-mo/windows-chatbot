using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Chat;
/// <summary>A line icon drawn on a 24×24 grid with a ~1.9px round stroke (the design's SVG set). It takes its color
/// from the inherited text color (TextElement.Foreground), like a glyph. Icons never mirror in right-to-left layouts.</summary>
public sealed class Icon : Viewbox
{
    // SVG path data from the design. Circles are written as two arcs. Kinds marked "fill:" are filled shapes.
    private static readonly Dictionary<string, string> Data = new()
    {
        // Assistant symbols (the order of Looks.Symbols).
        ["chat"] = "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z",
        ["people"] = "M9 4.5a3.5 3.5 0 1 1 0 7 3.5 3.5 0 0 1 0-7zM2.5 20a6.5 6.5 0 0 1 13 0M17 6.5a2.5 2.5 0 1 1 0 5M16 14.2a5 5 0 0 1 5.5 5.8",
        ["folder"] = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
        ["book"] = "M4 19.5A2.5 2.5 0 0 1 6.5 17H20V2H6.5A2.5 2.5 0 0 0 4 4.5zM4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5",
        ["school"] = "M2 9l10-5 10 5-10 5zM6 11v5c3 2 9 2 12 0v-5",
        ["briefcase"] = "M3 8h18v12H3zM8 8V5h8v3",
        ["star"] = "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z",
        ["heart"] = "M20.8 5.6a5.5 5.5 0 0 0-7.8 0L12 6.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 22l8.8-8.6a5.5 5.5 0 0 0 0-7.8z",
        ["mail"] = "M3 5h18v14H3zM3 6l9 7 9-7",
        ["home"] = "M3 11l9-8 9 8v10H3zM9 21v-7h6v7",
        ["lock"] = "M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4",
        ["bolt"] = "M13 2 3 14h9l-1 8 10-12h-9z",
        ["globe"] = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c2.5 2.5 3.8 5.5 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.5-3.8-9S9.5 5.5 12 3",
        ["code"] = "M8 7l-5 5 5 5M16 7l5 5-5 5",
        ["calendar"] = "M4 5h16v16H4zM4 10h16M9 3v4M15 3v4",
        ["cloud"] = "M17.5 19H8a5 5 0 1 1 1-9.9A6 6 0 0 1 20.5 11a4 4 0 0 1-3 8z",
        ["cart"] = "M3 3h2l2.4 12h11l2-8H6M9 20h.01M18 20h.01",
        ["plane"] = "M2 16l20-8-8 14-2-6z",
        // Interface icons.
        ["plus"] = "M12 5v14M5 12h14",
        ["close"] = "M18 6 6 18M6 6l12 12",
        ["check"] = "M20 6 9 17l-5-5",
        ["chevron-down"] = "m6 9 6 6 6-6",
        ["chevron-up"] = "m18 15-6-6-6 6",
        ["chevron-left"] = "m15 18-6-6 6-6",
        ["chevron-right"] = "m9 18 6-6-6-6",
        ["back"] = "M5 12h14M12 5l7 7-7 7",
        ["history"] = "M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2",
        ["reset"] = "M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5",
        ["clock"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0M12 7v5l3 2",
        ["more"] = "fill:M3.2 12a1.8 1.8 0 1 0 3.6 0a1.8 1.8 0 1 0-3.6 0zM10.2 12a1.8 1.8 0 1 0 3.6 0a1.8 1.8 0 1 0-3.6 0zM17.2 12a1.8 1.8 0 1 0 3.6 0a1.8 1.8 0 1 0-3.6 0z",
        ["refresh"] = "M21 12a9 9 0 0 1-15.5 6.3L3 16M3 12a9 9 0 0 1 15.5-6.3L21 8M21 3v5h-5M3 21v-5h5",
        ["sliders"] = "M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1M13 6a2 2 0 1 0 4 0a2 2 0 1 0-4 0M7 12a2 2 0 1 0 4 0a2 2 0 1 0-4 0M15 18a2 2 0 1 0 4 0a2 2 0 1 0-4 0",
        ["info"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0M12 11v5M12 8h.01",
        ["power"] = "M12 3v9M6.4 6.4a8 8 0 1 0 11.2 0",
        ["search"] = "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0M21 21l-4.3-4.3",
        ["search-plus"] = "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0M21 21l-4.3-4.3M11 8v6M8 11h6",
        ["trash"] = "M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6",
        ["pencil"] = "M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z",
        ["pen"] = "M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z",
        // Points left: "forward" in the right-to-left composer.
        ["send"] = "M19 12H5M12 5l-7 7 7 7",
        ["copy"] = "M9 9h10a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H11a2 2 0 0 1-2-2zM5 15H4a1 1 0 0 1-1-1V5a2 2 0 0 1 2-2h9a1 1 0 0 1 1 1v1",
        ["gauge"] = "M3.5 16a9 9 0 1 1 17 0M12 13l4-4M11 13a1 1 0 1 0 2 0a1 1 0 1 0-2 0",
        ["paperclip"] = "M21 11.5 12.4 20a5.5 5.5 0 0 1-7.8-7.8l8.9-8.9a3.7 3.7 0 0 1 5.2 5.2l-8.9 8.9a1.8 1.8 0 0 1-2.6-2.6l8.1-8.1",
        ["file"] = "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5",
        ["stop"] = "fill:M7 7h10v10H7z",
        ["warning"] = "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0zM12 9v4M12 17h.01",
        ["key"] = "M4 15a4 4 0 1 0 8 0a4 4 0 1 0-8 0M10.8 12.2l9.2-9.2M17 6l3 3M15 8l2 2",
        ["padlock"] = "M6 11h12a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2zM8 11V7a4 4 0 0 1 8 0v4",
        ["spinner"] = "M12 3a9 9 0 1 0 9 9",
    };
    private static readonly Dictionary<string, (Geometry Geometry, bool Filled)> Parsed = new();
    public static IEnumerable<string> Kinds => Data.Keys;
    /// <summary>The parsed shape (WPF reads "6.1.9" as one bad number, so implicit decimal separators get a space first).</summary>
    public static (Geometry Geometry, bool Filled) ShapeOf(string kind)
    {
        if (Parsed.TryGetValue(kind, out var cached)) return cached;
        var text = Data.TryGetValue(kind, out var value) ? value : Data["chat"];
        var filled = text.StartsWith("fill:");
        if (filled) text = text[5..];
        text = Regex.Replace(text, @"(\.\d+)(?=\.)", "$1 ");
        var geometry = Geometry.Parse(text); geometry.Freeze();
        return Parsed[kind] = (geometry, filled);
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("chat", (o, _) => ((Icon)o).Update()));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.9, (o, _) => ((Icon)o).Update()));
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(20.0, (o, e) => { var icon = (Icon)o; icon.Width = icon.Height = (double)e.NewValue; }));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private readonly Path path = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    public Icon()
    {
        Width = Height = 20; Stretch = Stretch.Uniform; FlowDirection = FlowDirection.LeftToRight;
        IsHitTestVisible = false; Focusable = false; SnapsToDevicePixels = false;
        Child = new Canvas { Width = 24, Height = 24, Children = { path } };
        Update();
    }
    public Icon(string kind, double size = 20, Brush? color = null, double strokeWidth = 1.9) : this()
    {
        Kind = kind; Size = size; StrokeWidth = strokeWidth;
        if (color != null) TextElement.SetForeground(this, color);
    }
    private void Update()
    {
        var (geometry, filled) = ShapeOf(Kind);
        path.Data = geometry;
        var color = new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self };
        BindingOperations.ClearAllBindings(path);
        if (filled) { path.StrokeThickness = 0; path.SetBinding(System.Windows.Shapes.Shape.FillProperty, color); }
        else { path.Fill = null; path.StrokeThickness = StrokeWidth; path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, color); }
    }
}
