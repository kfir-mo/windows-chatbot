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
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
        var card = new Border { Background = Theme.Brush("Surface"), CornerRadius = new CornerRadius(18), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Content = card;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "הגדרות AI", FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Style = Ui.Style("IconButton"), Content = "", FontSize = 13, ToolTip = "סגירת ההגדרות" };
        System.Windows.Automation.AutomationProperties.SetName(close, "סגירת ההגדרות");
        Grid.SetColumn(close, 1); heading.Children.Add(close); close.Click += (_, _) => Completed?.Invoke(null); panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = "בחרו מודל. אם המודל דורש מפתח, הזינו את מפתח ה־API האישי שלכם.", Foreground = Theme.Brush("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,16) });
        TextBlock LabelText(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,12,0,6) };
        void Label(string text) => panel.Children.Add(LabelText(text));
        var catalog = LocalAiCatalog.Load();
        Label("מודל");
        var model = new ComboBox { Style = (Style)Application.Current.FindResource("ModelPicker"), ItemsSource = catalog.Models.Select(m => m.Model).ToArray(), SelectedItem = current.Ai.Model, FlowDirection = FlowDirection.LeftToRight };
        if (model.SelectedIndex < 0) model.SelectedIndex = 0;
        panel.Children.Add(model);
        // Internal servers without a key (Authentication "none" or "windows" in ai.config.json) hide the whole key section.
        var keySection = new StackPanel(); panel.Children.Add(keySection);
        keySection.Children.Add(LabelText("מפתח API אישי"));
        var key = new PasswordBox { Padding = new Thickness(10), BorderThickness = new Thickness(0), Background = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight };
        var display = new TextBox { IsReadOnly = true, Padding = new Thickness(10), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Theme.Brush("Muted"), FlowDirection = FlowDirection.LeftToRight };
        var field = new Grid(); field.Children.Add(display); field.Children.Add(key);
        var keyBorder = new Border { Child = field, CornerRadius = new CornerRadius(12), Background = Theme.Brush("Subtle"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1.5), MinHeight = 46 };
        var keyRow = new Grid(); keyRow.ColumnDefinitions.Add(new ColumnDefinition()); keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.Children.Add(keyBorder);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6,0,0,0) }; Grid.SetColumn(actions, 1); keyRow.Children.Add(actions); keySection.Children.Add(keyRow);
        var keyState = new TextBlock { FontSize = 12, Margin = new Thickness(0,8,0,0), Foreground = Theme.Brush("Muted") }; keySection.Children.Add(keyState);
        keySection.Children.Add(new TextBlock { Text = "המפתח מוצפן בחשבון Windows שלכם ונשלח לשירות ה־AI המוגדר לצורך החיבור.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,8,0,0), Foreground = Theme.Brush("Muted") });
        Button ActionButton(string icon, string label)
        {
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Style = Ui.Style("IconButton"), Content = text, ToolTip = label, Margin = new Thickness(1,0,1,0) };
            System.Windows.Automation.AutomationProperties.SetName(button, label); actions.Children.Add(button); return button;
        }
        var edit = ActionButton("\uE70F", "עריכה"); var remove = ActionButton("","מחיקת המפתח");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,12,0,0) };
        var progress = new ProgressBar { Height = 3, IsIndeterminate = SystemParameters.ClientAreaAnimation, Minimum = 0, Maximum = 1, Value = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0,8,0,0) }; panel.Children.Add(progress);
        string savedKey = ""; bool editing = false;
        var keyless = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,12,0,0), Foreground = Theme.Brush("Muted") }; panel.Children.Add(keyless);
        bool NeedsKey() => catalog.Apply(current, (string)model.SelectedItem).Ai.Authentication is "bearer" or "header";
        void LockKey()
        {
            var needsKey = NeedsKey();
            keySection.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
            keyless.Visibility = needsKey ? Visibility.Collapsed : Visibility.Visible;
            keyless.Text = catalog.Apply(current, (string)model.SelectedItem).Ai.Authentication == "windows" ? "המודל הזה מתחבר עם חשבון Windows שלכם. אין צורך במפתח." : "המודל הזה לא דורש מפתח API.";
            savedKey = AiCredentials.Resolve(catalog.Apply(current, (string)model.SelectedItem).Ai) ?? "";
            keyState.Text = savedKey.Length > 0 ? "מפתח שמור · מסתיים ב־" + savedKey[^Math.Min(4, savedKey.Length)..] : "לא נשמר מפתח";
            editing = savedKey.Length == 0; key.Clear();
            key.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            display.Visibility = editing ? Visibility.Collapsed : Visibility.Visible; display.Text = "************";
            keyBorder.Background = editing ? Theme.Brush("Surface") : Theme.Brush("Subtle");
            edit.IsEnabled = remove.IsEnabled = !editing;
            edit.Background = Brushes.Transparent; edit.ToolTip = "עריכה";
            System.Windows.Automation.AutomationProperties.SetName(edit, "עריכה");
        }
        edit.Click += (_, _) => {
            if (editing) { LockKey(); status.Text = "השינוי בוטל. המפתח השמור לא השתנה."; return; }
            editing = true; display.Text = "************"; display.Visibility = Visibility.Collapsed;
            key.Password = savedKey; key.Visibility = Visibility.Visible; keyBorder.Background = Theme.Brush("Surface");
            remove.IsEnabled = false;
            edit.Background = Theme.Brush("Selected"); edit.ToolTip = "ביטול עריכה";
            System.Windows.Automation.AutomationProperties.SetName(edit, "ביטול עריכה");
            status.Text = ""; key.Focus(); key.SelectAll();
        };
        // Deleting is the same as editing to an empty field: nothing changes until "שמירת הגדרות".
        remove.Click += (_, _) => { edit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); key.Clear(); status.Foreground = Theme.Brush("Muted"); status.Text = "המפתח יימחק בלחיצה על \"שמירת הגדרות\"."; };
        model.SelectionChanged += (_, _) => LockKey(); LockKey();
        Loaded += (_, _) => model.Focus();
        var test = new Button { Style = Ui.Style("SecondaryButton"), Content = "בדיקת חיבור", ToolTip = "שולחת בקשת AI קצרה", Margin = new Thickness(0,20,0,0) }; panel.Children.Add(test);
        var footer = new Grid { Margin = new Thickness(0,10,0,0) }; footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var save = new Button { Style = Ui.Style("PrimaryButton"), Content = "שמירת הגדרות" }; footer.Children.Add(save);
        var cancel = new Button { Style = Ui.Style("GhostButton"), Content = "ביטול", MinHeight = 44, Margin = new Thickness(8,0,0,0) }; Grid.SetColumn(cancel, 1); cancel.Click += (_, _) => Completed?.Invoke(null); footer.Children.Add(cancel);
        panel.Children.Add(footer); panel.Children.Add(status);
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
            try { using var ai = new AiClient(Read()); ai.WaitingForRateLimit = delay => status.Text = $"ממתין למכסת ה־API · כ־{Math.Ceiling(delay.TotalSeconds)} שניות…"; await ai.TestConnectionAsync(string.IsNullOrWhiteSpace(key.Password) ? null : key.Password.Trim(), cancellation.Token); status.Text = "החיבור תקין"; status.Foreground = Theme.Brush("Success"); }
            catch (Exception ex) { status.Text = AiErrors.Describe(ex); status.Foreground = Theme.Brush("Danger"); }
            finally { progress.Visibility = Visibility.Collapsed; test.Content = "בדיקת חיבור"; test.IsEnabled = save.IsEnabled = true; }
        };
        save.Click += (_, _) => {
            try {
                var value = Read();
                // An emptied field removes the saved key; until a key is entered again, AI requests report that none is set.
                if (editing && !string.IsNullOrWhiteSpace(key.Password)) AiCredentials.Save(value.Ai, key.Password);
                else if (editing && savedKey.Length > 0) AiCredentials.Delete(value.Ai);
                Storage.Save("local-settings.bin", value); key.Clear(); Completed?.Invoke(value);
            } catch (Exception ex) { status.Text = ex.Message; }
        };
    }
}
