using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
/// <summary>"הגדרות AI": a full-panel screen with the model (shared by all assistants) and the API key.</summary>
public sealed class LocalSettingsPanel : UserControl
{
    public event Action<Config?>? Completed;
    public LocalSettingsPanel(Config current)
    {
        FlowDirection = FlowDirection.RightToLeft; FontFamily = (FontFamily)FindResource("UiFont");
        Foreground = Theme.Brush("Ink"); Background = Theme.Brush("Canvas");
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(this, System.Windows.Input.KeyboardNavigationMode.Cycle);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        // Screen header: back arrow and title.
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var back = new Button { Style = Ui.Style("IconButton"), Foreground = Theme.Brush("Ink"), Content = new Icon("back", 20, null, 2), ToolTip = "חזרה" };
        System.Windows.Automation.AutomationProperties.SetName(back, "חזרה בלי לשמור");
        back.Click += (_, _) => Completed?.Invoke(null);
        heading.Children.Add(back);
        heading.Children.Add(new TextBlock { Text = "הגדרות AI", FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        root.Children.Add(new Border { Background = Theme.Brush("Surface"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Height = 60, Padding = new Thickness(8, 0, 12, 0), Child = heading });

        var panel = new StackPanel { Margin = new Thickness(16, 18, 16, 20) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        TextBlock Label(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        TextBlock Hint(string text) => new() { Text = text, FontSize = 12.5, Foreground = Theme.Brush("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };

        var catalog = LocalAiCatalog.Load();
        panel.Children.Add(Label("מודל"));
        var model = new ComboBox { Style = Ui.Style("ModelPicker"), ItemsSource = catalog.Models.Select(m => m.Model).ToArray(), SelectedItem = current.Ai.Model, FlowDirection = FlowDirection.LeftToRight, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 13.5 };
        System.Windows.Automation.AutomationProperties.SetName(model, "מודל");
        if (model.SelectedIndex < 0) model.SelectedIndex = 0;
        panel.Children.Add(model);
        panel.Children.Add(Hint("המודל שישמש את כל העוזרים"));

        // Internal servers without a key (Authentication "none" or "windows" in ai.config.json) hide the whole key section.
        var keySection = new StackPanel { Margin = new Thickness(0, 22, 0, 0) }; panel.Children.Add(keySection);
        keySection.Children.Add(Label("מפתח API"));
        var card = new StackPanel();
        keySection.Children.Add(new Border { Background = Theme.Brush("Surface"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 14), Child = card });
        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Theme.Brush("Canvas"), VerticalAlignment = VerticalAlignment.Center,
            Child = new Icon("key", 19, Theme.Brush("Secondary")) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var keyText = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var keyTitle = new TextBlock { FontWeight = FontWeights.Bold };
        var keyMask = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, Foreground = Theme.Brush("Muted"), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        keyText.Children.Add(keyTitle); keyText.Children.Add(keyMask);
        Grid.SetColumn(keyText, 1); summary.Children.Add(keyText);
        var pillDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var pillText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal }; pillContent.Children.Add(pillDot); pillContent.Children.Add(pillText);
        var pill = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center, Child = pillContent };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(pill, System.Windows.Automation.AutomationLiveSetting.Polite);
        Grid.SetColumn(pill, 2); summary.Children.Add(pill);
        card.Children.Add(summary);

        // Typing a new key happens inside the card; nothing is stored until "שמירה".
        var key = new PasswordBox { Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 13 };
        System.Windows.Automation.AutomationProperties.SetName(key, "מפתח API חדש");
        var keyHint = new TextBlock { Text = "הדביקו כאן את מפתח ה־API", Foreground = Theme.Brush("Muted"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        key.PasswordChanged += (_, _) => keyHint.Visibility = key.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var keyField = new Grid(); keyField.Children.Add(key); keyField.Children.Add(keyHint);
        var keyBox = new Border { Child = keyField, CornerRadius = new CornerRadius(10), Background = Theme.Brush("Surface"), BorderBrush = Theme.Brush("FieldLine"), BorderThickness = new Thickness(1.5), Height = 44, Margin = new Thickness(0, 12, 0, 0) };
        key.GotKeyboardFocus += (_, _) => keyBox.BorderBrush = Theme.Brush("Accent"); key.LostKeyboardFocus += (_, _) => keyBox.BorderBrush = Theme.Brush("FieldLine");
        card.Children.Add(keyBox);

        var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var replace = new Button { Style = Ui.Style("SecondaryButton"), Content = "החלפת מפתח", Padding = new Thickness(8, 6, 8, 6) };
        var test = new Button { Style = Ui.Style("SecondaryButton"), Content = "בדיקת חיבור", Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(8, 0, 0, 0), ToolTip = "שולחת בקשת AI קצרה" };
        var remove = new Button { Style = Ui.Style("DangerOutlineButton"), Width = 40, Height = 40, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), BorderBrush = Theme.Brush("DangerLine"), Content = new Icon("trash", 18), ToolTip = "מחיקת המפתח" };
        System.Windows.Automation.AutomationProperties.SetName(remove, "מחיקת המפתח");
        Grid.SetColumn(test, 1); Grid.SetColumn(remove, 2);
        actions.Children.Add(replace); actions.Children.Add(test); actions.Children.Add(remove);
        card.Children.Add(actions);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(result, System.Windows.Automation.AutomationLiveSetting.Polite);
        card.Children.Add(result);
        var progress = new ProgressBar { Height = 3, IsIndeterminate = SystemParameters.ClientAreaAnimation, Minimum = 0, Maximum = 1, Value = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        card.Children.Add(progress);

        var note = new Grid { Margin = new Thickness(2, 12, 0, 0) };
        note.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        note.ColumnDefinitions.Add(new ColumnDefinition());
        note.Children.Add(new Icon("padlock", 15, Theme.Brush("Secondary")) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var noteText = new TextBlock { Text = "המפתח מוצפן בחשבון Windows שלכם ונשלח רק לשירות ה־AI שבחרתם.", FontSize = 12.5, Foreground = Theme.Brush("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), LineHeight = 18 };
        Grid.SetColumn(noteText, 1); note.Children.Add(noteText);
        keySection.Children.Add(note);
        var keyless = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 22, 0, 0), Foreground = Theme.Brush("Secondary") }; panel.Children.Add(keyless);

        // Footer.
        var footer = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Theme.Brush("Danger"), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        footer.Children.Add(status);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var save = new Button { Style = Ui.Style("PrimaryButton"), Content = "שמירה" }; buttons.Children.Add(save);
        var cancel = new Button { Style = Ui.Style("GhostButton"), Content = "ביטול", MinHeight = 46, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(cancel, 1); cancel.Click += (_, _) => Completed?.Invoke(null); buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        var footerBar = new Border { Background = Theme.Brush("Surface"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12, 16, 14), Child = footer };
        Grid.SetRow(footerBar, 2); root.Children.Add(footerBar);

        string savedKey = ""; bool editing = false, deleting = false;
        void Pill(string text, string background, string foreground, string? dot)
        {
            pillText.Text = text; pillText.Foreground = Theme.Solid(foreground); pill.Background = Theme.Solid(background);
            pillDot.Visibility = dot == null ? Visibility.Collapsed : Visibility.Visible; if (dot != null) pillDot.Fill = Theme.Solid(dot);
            System.Windows.Automation.AutomationProperties.SetName(pill, "מצב החיבור: " + text);
        }
        void Result(string text, string token) { result.Text = text; result.Foreground = Theme.Brush(token); result.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        void Untested() { if (deleting || savedKey.Length == 0 && key.Password.Length == 0) Pill("חסר מפתח", "#F0F1F4", "#4A5160", null); else Pill("לא נבדק", "#F0F1F4", "#4A5160", null); }
        bool NeedsKey() => catalog.Apply(current, (string)model.SelectedItem).Ai.Authentication is "bearer" or "header";
        void ShowKey()
        {
            var showMask = savedKey.Length > 0 && !(editing && !deleting);
            keyTitle.Text = deleting ? "המפתח יימחק בשמירה" : editing ? (savedKey.Length > 0 ? "מפתח חדש" : "לא נשמר מפתח") : "מפתח שמור";
            keyMask.Text = showMask && !deleting ? "•••• " + savedKey[^Math.Min(4, savedKey.Length)..] : "";
            keyMask.Visibility = keyMask.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            keyBox.Visibility = editing && !deleting ? Visibility.Visible : Visibility.Collapsed;
            replace.Content = editing && savedKey.Length > 0 ? "ביטול החלפה" : "החלפת מפתח";
            replace.Visibility = savedKey.Length > 0 || deleting ? Visibility.Visible : Visibility.Collapsed;
            if (deleting) replace.Content = "ביטול מחיקה";
            Grid.SetColumnSpan(test, replace.Visibility == Visibility.Visible ? 1 : 2); Grid.SetColumn(test, replace.Visibility == Visibility.Visible ? 1 : 0);
            test.Margin = new Thickness(replace.Visibility == Visibility.Visible ? 8 : 0, 0, 0, 0);
            remove.IsEnabled = savedKey.Length > 0 && !deleting;
            System.Windows.Automation.AutomationProperties.SetName(replace, (string)replace.Content);
        }
        void LockKey()
        {
            var settings = catalog.Apply(current, (string)model.SelectedItem).Ai;
            var needsKey = NeedsKey();
            keySection.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
            keyless.Visibility = needsKey ? Visibility.Collapsed : Visibility.Visible;
            keyless.Text = settings.Authentication == "windows" ? "המודל הזה מתחבר עם חשבון Windows שלכם. אין צורך במפתח." : "המודל הזה לא דורש מפתח API.";
            savedKey = AiCredentials.Resolve(settings) ?? "";
            editing = savedKey.Length == 0; deleting = false; key.Clear();
            ShowKey(); Untested(); Result("", "Muted");
        }
        replace.Click += (_, _) =>
        {
            if (deleting || editing) { LockKey(); Result("השינוי בוטל. המפתח השמור לא השתנה.", "Secondary"); replace.Focus(); return; }
            editing = true; key.Clear(); ShowKey(); Untested(); Result("", "Muted");
            key.Focus();
        };
        // Deleting takes effect on "שמירה", like any other change here.
        remove.Click += (_, _) => { editing = true; deleting = true; key.Clear(); ShowKey(); Untested(); Result("המפתח יימחק בלחיצה על \"שמירה\".", "Secondary"); replace.Focus(); };
        key.PasswordChanged += (_, _) => { if (editing) Untested(); };
        model.SelectionChanged += (_, _) => LockKey(); LockKey();
        Loaded += (_, _) => model.Focus();
        Config Read()
        {
            var value = catalog.Apply(current, (string)model.SelectedItem);
            value.Validate();
            return value;
        }
        var cancellation = new CancellationTokenSource(); Unloaded += (_, _) => { cancellation.Cancel(); key.Clear(); savedKey = ""; };
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; Completed?.Invoke(null); } };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = save.IsEnabled = false; progress.Visibility = Visibility.Visible; test.Content = "בודק…";
            Pill("בודק…", "#EFF4FF", "#1D4ED8", null); Result("", "Muted");
            try
            {
                using var ai = new AiClient(Read());
                ai.WaitingForRateLimit = delay => Dispatcher.BeginInvoke(() => Result($"ממתין למכסת ה־API · כ־{Math.Ceiling(delay.TotalSeconds)} שניות…", "Secondary"));
                await ai.TestConnectionAsync(editing && !string.IsNullOrWhiteSpace(key.Password) ? key.Password.Trim() : null, cancellation.Token);
                Pill("מחובר", "#DCFCE7", "#166534", "#16A34A"); Result("החיבור תקין.", "Success");
            }
            catch (Exception ex) { Pill("שגיאה", "#FEF2F2", "#B91C1C", "#B91C1C"); Result(AiErrors.Describe(ex), "Danger"); }
            finally { progress.Visibility = Visibility.Collapsed; test.Content = "בדיקת חיבור"; test.IsEnabled = save.IsEnabled = true; }
        };
        save.Click += (_, _) =>
        {
            try
            {
                var value = Read();
                // Only the trash button removes the saved key (an empty replacement field keeps it); until a key is entered again, AI requests report that none is set.
                if (editing && !deleting && !string.IsNullOrWhiteSpace(key.Password)) AiCredentials.Save(value.Ai, key.Password);
                else if (deleting && savedKey.Length > 0) AiCredentials.Delete(value.Ai);
                Storage.Save("local-settings.bin", value); key.Clear(); Completed?.Invoke(value);
            }
            catch (Exception ex) { status.Text = ex.Message; status.Visibility = Visibility.Visible; }
        };
    }
}
