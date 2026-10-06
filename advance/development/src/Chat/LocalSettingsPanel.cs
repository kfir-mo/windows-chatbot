using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
public sealed class LocalSettingsPanel : UserControl
{
    public event Action<Config?>? Completed;
    public LocalSettingsPanel(Config current)
    {
        FlowDirection = FlowDirection.RightToLeft; FontFamily = new FontFamily("Segoe UI");
        Foreground = Theme.Brush("Ink");
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(this, System.Windows.Input.KeyboardNavigationMode.Cycle);
        var panel = new StackPanel { Margin = new Thickness(20) };
        var card = new Border { Background = Theme.Brush("Surface"), CornerRadius = new CornerRadius(18), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Content = card;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "הגדרות AI", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Width = 44, Height = 44, FontSize = 24, Padding = new Thickness(7,0,7,0), Background = Brushes.Transparent, Foreground = Theme.Brush("Muted"), ToolTip = "סגירת ההגדרות" };
        System.Windows.Automation.AutomationProperties.SetName(close, "סגירת ההגדרות");
        Grid.SetColumn(close, 1); heading.Children.Add(close); close.Click += (_, _) => Completed?.Invoke(null); panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = "בחרו מודל והזינו את מפתח ה־API האישי שלכם.", Foreground = Theme.Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,16) });
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0,10,0,5) });
        var catalog = LocalAiCatalog.Load();
        Label("מודל");
        var model = new ComboBox { Style = (Style)Application.Current.FindResource("ModelPicker"), ItemsSource = catalog.Models.Select(m => m.Model).ToArray(), SelectedItem = current.Ai.Model, FlowDirection = FlowDirection.LeftToRight };
        if (model.SelectedIndex < 0) model.SelectedIndex = 0;
        panel.Children.Add(model);
        Label("מפתח API אישי");
        var key = new PasswordBox { Padding = new Thickness(10), BorderThickness = new Thickness(0), Background = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight };
        var display = new TextBox { IsReadOnly = true, Padding = new Thickness(10), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Theme.Brush("Muted"), FlowDirection = FlowDirection.LeftToRight };
        var field = new Grid(); field.Children.Add(display); field.Children.Add(key);
        var keyBorder = new Border { Child = field, CornerRadius = new CornerRadius(10), Background = Theme.Brush("Subtle"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1) };
        var keyRow = new Grid(); keyRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); keyRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        keyRow.Children.Add(keyBorder);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(actions, 1); keyRow.Children.Add(actions); panel.Children.Add(keyRow);
        var keyState = new TextBlock { FontSize = 12, Margin = new Thickness(0,8,0,0), Foreground = Theme.Brush("Muted") }; panel.Children.Add(keyState);
        panel.Children.Add(new TextBlock { Text = "המפתח מוצפן בחשבון Windows שלכם ונשלח לשירות ה־AI המוגדר לצורך החיבור.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,8,0,0), Foreground = Theme.Brush("Muted") });
        Button ActionButton(string icon, string label)
        {
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,5,0) });
            var button = new Button { Content = text, ToolTip = label, Width = 44, Height = 44, Padding = new Thickness(5), Margin = new Thickness(1), Background = Brushes.Transparent };
            System.Windows.Automation.AutomationProperties.SetName(button, label); actions.Children.Add(button); return button;
        }
        var reveal = ActionButton("\uE890", "צפייה"); var copy = ActionButton("\uE8C8", "העתקה"); var edit = ActionButton("\uE70F", "עריכה");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,12,0,0) };
        var progress = new ProgressBar { Height = 3, IsIndeterminate = SystemParameters.ClientAreaAnimation, Minimum = 0, Maximum = 1, Value = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0,8,0,0) }; panel.Children.Add(progress);
        string savedKey = ""; bool editing = false, revealed = false;
        void LockKey()
        {
            savedKey = AiCredentials.Resolve(catalog.Apply(current, (string)model.SelectedItem).Ai) ?? "";
            keyState.Text = savedKey.Length > 0 ? "מפתח שמור · מסתיים ב־" + savedKey[^Math.Min(4, savedKey.Length)..] : "לא נשמר מפתח";
            editing = savedKey.Length == 0; revealed = false; key.Clear();
            key.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            display.Visibility = editing ? Visibility.Collapsed : Visibility.Visible; display.Text = "************";
            keyBorder.Background = editing ? Theme.Brush("Surface") : Theme.Brush("Subtle");
            reveal.IsEnabled = copy.IsEnabled = edit.IsEnabled = !editing;
            edit.Background = Brushes.Transparent; edit.ToolTip = "עריכה";
            System.Windows.Automation.AutomationProperties.SetName(edit, "עריכה");
            reveal.ToolTip = "צפייה";
        }
        reveal.Click += (_, _) => { revealed = !revealed; display.Text = revealed ? savedKey : "************"; reveal.ToolTip = revealed ? "הסתרה" : "צפייה"; };
        copy.Click += (_, _) => { try { Clipboard.SetText(savedKey); status.Text = "המפתח הועתק"; } catch { status.Text = "לא ניתן להעתיק כרגע. נסו שוב."; } };
        edit.Click += (_, _) => {
            if (editing) { LockKey(); status.Text = "השינוי בוטל. המפתח השמור לא השתנה."; return; }
            editing = true; revealed = false; display.Text = "************"; display.Visibility = Visibility.Collapsed;
            key.Password = savedKey; key.Visibility = Visibility.Visible; keyBorder.Background = Theme.Brush("Surface");
            reveal.IsEnabled = copy.IsEnabled = false;
            edit.Background = Theme.Brush("Selected"); edit.ToolTip = "ביטול עריכה";
            System.Windows.Automation.AutomationProperties.SetName(edit, "ביטול עריכה");
            status.Text = ""; key.Focus(); key.SelectAll();
        };
        model.SelectionChanged += (_, _) => LockKey(); LockKey();
        Loaded += (_, _) => model.Focus();
        var test = new Button { Content = "בדיקת חיבור", ToolTip = "שולחת בקשת AI קצרה", Background = Brushes.Transparent, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0,18,0,0), Padding = new Thickness(10) }; panel.Children.Add(test);
        var save = new Button { Content = "שמירת הגדרות", Background = Theme.Brush("Accent"), Foreground = Brushes.White, Padding = new Thickness(10), Margin = new Thickness(0,8,0,0) }; panel.Children.Add(save); var cancel = new Button { Content = "ביטול", Background = Brushes.Transparent }; cancel.Click += (_, _) => Completed?.Invoke(null); panel.Children.Add(cancel); panel.Children.Add(status);
        Config Read()
        {
            var value = catalog.Apply(current, (string)model.SelectedItem);
            value.Validate();
            return value;
        }
        var cancellation = new CancellationTokenSource(); Unloaded += (_, _) => { cancellation.Cancel(); key.Clear(); display.Clear(); savedKey = ""; };
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; Completed?.Invoke(null); } };
        test.Click += async (_, _) => {
            test.IsEnabled = save.IsEnabled = false; progress.Visibility = Visibility.Visible; status.Text = "בודק חיבור…"; test.Content = "בודק חיבור…";
            try { using var ai = new AiClient(Read()); ai.WaitingForRateLimit = delay => status.Text = $"ממתין למכסת ה־API · כ־{Math.Ceiling(delay.TotalSeconds)} שניות…"; await ai.TestConnectionAsync(string.IsNullOrWhiteSpace(key.Password) ? null : key.Password.Trim(), cancellation.Token); status.Text = "החיבור תקין"; status.Foreground = new SolidColorBrush(Color.FromRgb(22,112,65)); }
            catch (Exception ex) { status.Text = AiErrors.Describe(ex); status.Foreground = new SolidColorBrush(Color.FromRgb(173,54,70)); }
            finally { progress.Visibility = Visibility.Collapsed; test.Content = "בדיקת חיבור"; test.IsEnabled = save.IsEnabled = true; }
        };
        save.Click += (_, _) => {
            try {
                var value = Read();
                if (editing && !string.IsNullOrWhiteSpace(key.Password)) AiCredentials.Save(value.Ai, key.Password);
                if (string.IsNullOrWhiteSpace(AiCredentials.Resolve(value.Ai))) throw new InvalidOperationException("הזינו מפתח API עבור החיבור הזה.");
                Storage.Save("local-settings.bin", value); key.Clear(); Completed?.Invoke(value);
            } catch (Exception ex) { status.Text = ex.Message; }
        };
    }
}
