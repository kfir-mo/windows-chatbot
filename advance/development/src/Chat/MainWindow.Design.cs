using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Chat;
public partial class MainWindow
{
    private DateTimeOffset? lastSourceRefresh;
    private void UpdateSendState() => SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text) && loggedIn && request == null && !indexing && !switchingType;

    private void OpenStyledMenu(ContextMenu menu)
    {
        MenuScrim.Visibility = Visibility.Visible;
        menu.MaxHeight = Math.Max(160, Panel.ActualHeight - 120);
        menu.Closed += (_, _) => { MenuScrim.Visibility = Visibility.Collapsed; (menu.PlacementTarget as FrameworkElement)?.Focus(); };
        menu.Opened += (_, _) => {
            if (SystemParameters.ClientAreaAnimation) menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(160)));
        };
        try { menu.IsOpen = true; }
        catch { MenuScrim.Visibility = Visibility.Collapsed; throw; }
    }
}
