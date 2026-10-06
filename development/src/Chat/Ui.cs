using System.Windows;
using System.Windows.Media;

namespace Chat;
/// <summary>Attached settings consumed by the shared button template in App.xaml.</summary>
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(12)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);

    private static readonly Brush DefaultHover = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0x0F, 0x22, 0x40)));
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached("HoverBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(DefaultHover));
    public static Brush GetHoverBrush(DependencyObject o) => (Brush)o.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject o, Brush value) => o.SetValue(HoverBrushProperty, value);

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
}
