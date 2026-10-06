using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Chat;
public partial class MainWindow
{
    private DateTimeOffset? lastSourceRefresh;
    private void UpdateSendState() => SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text) && loggedIn && request == null && !indexing && !switchingType;

    /// <summary>A muted section title with a small close (X) button at the menu's top-left corner.</summary>
    private static MenuItem MenuTitle(string text, ContextMenu menu)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var close = new Button { Style = Ui.Style("IconButton"), Content = "", Width = 26, Height = 26, FontSize = 10, ToolTip = "סגירה", Margin = new Thickness(6, -4, -2, -4) };
        Ui.SetCornerRadius(close, new CornerRadius(13));
        System.Windows.Automation.AutomationProperties.SetName(close, "סגירה");
        close.Click += (_, _) => menu.IsOpen = false;
        Grid.SetColumn(close, 1); row.Children.Add(close);
        return new MenuItem { Style = Ui.Style("PlainMenuRow"), Padding = new Thickness(12, 8, 8, 6), Header = row };
    }
    private void OpenStyledMenu(ContextMenu menu)
    {
        // Dropdowns float over the chat without dimming it; only modal dialogs use the scrim.
        menu.MaxHeight = Math.Max(160, Panel.ActualHeight - 120);
        menu.Closed += (_, _) => { (menu.PlacementTarget as FrameworkElement)?.Focus(); };
        menu.Opened += (_, _) => {
            if (SystemParameters.ClientAreaAnimation) menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(160)));
        };
        menu.IsOpen = true;
    }
}
