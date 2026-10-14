using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Knowledge.Core;

namespace Chat;
/// <summary>Popovers that open from the header over the chat: the assistant switcher, the icon and color picker, the ⋯ menu and About.</summary>
public partial class MainWindow
{
    private DateTimeOffset? lastSourceRefresh;
    // An attached document can be sent on its own ("summarize the attached document"); while documents load, sending
    // waits for them (see Send), so the button stays available.
    private void UpdateSendState() => SendButton.IsEnabled = (!string.IsNullOrWhiteSpace(Question.Text) || pendingAttachment != null) && loggedIn && !Busy;

    private string? overlayKind;
    private IInputElement? overlayReturnFocus;
    private Action? overlayClosed;
    /// <summary>Shows one popover. <paramref name="dim"/> darkens the chat under it; clicking outside or Escape closes it.</summary>
    private void ShowOverlay(string kind, FrameworkElement content, bool dim, IInputElement? returnFocus, Action? closed = null)
    {
        CloseOverlay(false);
        overlayKind = kind; overlayClosed = closed; overlayReturnFocus = returnFocus;
        Scrim.Background = dim ? Theme.Brush("Scrim") : Brushes.Transparent;
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Cycle);
        OverlayHost.Content = content;
        Overlay.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(150);
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            var slide = new TranslateTransform(); content.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (dim) Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        }
        UpdateHeaderState();
        Dispatcher.BeginInvoke(() => content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void CloseOverlay(bool restoreFocus = true)
    {
        if (overlayKind == null) return;
        var closed = overlayClosed; var focus = overlayReturnFocus;
        overlayKind = null; overlayClosed = null; overlayReturnFocus = null;
        Overlay.Visibility = Visibility.Collapsed;
        OverlayHost.Content = null;
        closed?.Invoke();
        UpdateHeaderState();
        if (restoreFocus) focus?.Focus();
    }
    private void ScrimClicked(object sender, MouseButtonEventArgs e) { CloseOverlay(); e.Handled = true; }
    /// <summary>The header button whose popover is open gets a light background; the chevron points up while the switcher is open.</summary>
    private void UpdateHeaderState()
    {
        Brush Active(string kind) => overlayKind == kind ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : Brushes.Transparent;
        MenuButton.Background = Active("menu");
        ChatTypePicker.Background = Active("switcher");
        HistoryTab.Background = HistoryView.Visibility == Visibility.Visible ? new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)) : Brushes.Transparent;
        // The symbol has no tile of its own; it gets one on hover (button style) and while its icon and color popover is open.
        LookButton.Background = overlayKind == "look" ? new SolidColorBrush(Color.FromArgb(0x4D, 255, 255, 255)) : Brushes.Transparent;
        PickerChevron.Kind = overlayKind == "switcher" ? "chevron-up" : "chevron-down";
    }
    private static Border Card(UIElement child, double radius) => new()
    {
        Background = Theme.Brush("Surface"), CornerRadius = new CornerRadius(radius), Child = child,
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.18, Color = (Color)ColorConverter.ConvertFromString("#1B1F27") }
    };
    private static Border Divider(double margin = 6) => new() { Height = 1, Background = Theme.Brush("Divider"), Margin = new Thickness(margin, 6, margin, 6) };
    private static TextBlock GroupLabel(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted"), Margin = new Thickness(12, 6, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    private static Button CloseButton(string name, Action close)
    {
        var button = new Button { Style = Ui.Style("IconButton"), Content = new Icon("close", 18, null, 2), ToolTip = "סגירה" };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => close();
        return button;
    }

    // ---- Assistant switcher (from the name in the header) ----
    private void PickChatType(object sender, RoutedEventArgs e)
    {
        if (overlayKind == "switcher") { CloseOverlay(); return; }
        if (!loggedIn || running.Count > 0) { Notice("המתינו שהתשובות שבדרך יסתיימו.", false); return; }
        var list = new StackPanel { Margin = new Thickness(6) };
        foreach (var type in chatTypes.Where(t => t.IsGeneral)) list.Children.Add(SwitcherRow(type));
        var personal = chatTypes.Where(t => !t.IsGeneral && !t.IsManaged).ToList();
        if (personal.Count > 0 || personalAllowed) { list.Children.Add(Divider()); list.Children.Add(GroupLabel("העוזרים שלי")); }
        foreach (var type in personal) list.Children.Add(SwitcherRow(type));
        foreach (var group in chatTypes.Where(t => t.IsManaged).GroupBy(t => string.Join(" / ", new[] { "ידע ארגוני", t.Project, t.Category }.Where(x => !string.IsNullOrWhiteSpace(x)))))
        {
            list.Children.Add(Divider()); list.Children.Add(GroupLabel(group.Key));
            foreach (var type in group) list.Children.Add(SwitcherRow(type));
        }
        if (personalAllowed)
        {
            if (personal.Count > 0) list.Children.Add(Divider());
            var tile = new Grid { Width = 36, Height = 36 };
            tile.Children.Add(new Rectangle { RadiusX = 10, RadiusY = 10, Stroke = Theme.Brush("FieldLine"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 } });
            tile.Children.Add(new Icon("plus", 18, Theme.Brush("Link"), 2.2) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(tile);
            content.Children.Add(new TextBlock { Text = "עוזר אישי חדש", FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Link"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var create = new Button { Style = Ui.Style("RowButton"), Content = content, MinHeight = 52, Padding = new Thickness(8, 6, 8, 6) };
            Ui.SetCornerRadius(create, new CornerRadius(12));
            System.Windows.Automation.AutomationProperties.SetName(create, "עוזר אישי חדש");
            create.Click += (_, _) => { CloseOverlay(false); OpenChatTypeDialog(null); };
            list.Children.Add(create);
        }
        var card = Card(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(200, ChatPage.ActualHeight - 24) }, 16);
        card.Margin = new Thickness(8, 8, 8, 0); card.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(card, "בחירת עוזר");
        ShowOverlay("switcher", card, true, ChatTypePicker);
    }
    /// <summary>"2 תיקיות · 40 מסמכים"; the document count is known once the assistant's index was refreshed this session.</summary>
    /// <summary>The last document count of each assistant (a few bytes in the profile), so the switcher shows it for
    /// assistants whose index is not loaded.</summary>
    private System.Collections.Generic.Dictionary<string, int> documentCounts = Storage.Load<System.Collections.Generic.Dictionary<string, int>>("document-counts.bin") ?? [];
    private void RememberCount(int files)
    {
        if (documentCounts.TryGetValue(activeTypeId, out var known) && known == files) return;
        documentCounts[activeTypeId] = files;
        foreach (var gone in documentCounts.Keys.Where(id => !chatTypes.Any(t => t.Id == id)).ToList()) documentCounts.Remove(gone);
        try { Storage.Save("document-counts.bin", documentCounts); } catch { }
    }
    private string AssistantMeta(ChatType type)
    {
        if (type.IsGeneral) return "שיחה חופשית עם AI";
        if (type.IsManaged) return "ידע ארגוני";
        if (type.Paths.Count == 0) return "ללא תיקיות מסמכים";
        // Sources are folders and single documents (a path with a document extension).
        var singles = type.Paths.Count(p => DocumentIndex.Extensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()));
        var folderCount = type.Paths.Count - singles;
        var single = singles == 1 ? "מסמך אחד" : $"{singles} מסמכים";
        if (folderCount == 0) return single;
        var folders = (folderCount == 1 ? "תיקייה אחת" : $"{folderCount} תיקיות") + (singles > 0 ? " + " + single : "");
        int? files = type.Id == activeTypeId ? indexedFiles
            : documentCounts.TryGetValue(type.Id, out var known) ? known : null;
        return files is int n ? folders + " · " + DocumentCount(n) : folders;
    }
    private Grid SwitcherRow(ChatType type)
    {
        var look = LookOf(type);
        var active = type.Id == activeTypeId;
        var editable = !type.IsGeneral && !type.IsManaged && personalAllowed;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(Looks.Avatar(look, 36, 10));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, editable ? 44 : 4, 0) };
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = type.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
        if (active) name.Children.Add(new Icon("check", 15, Looks.Brush(look.Color), 2.6) { Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = AssistantMeta(type), FontSize = 12.5, Foreground = Theme.Brush("Secondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1); content.Children.Add(text);
        // The whole box (symbol, name and description) is one row and one highlight.
        var row = new Button { Style = Ui.Style("RowButton"), Content = content, MinHeight = 52, Padding = new Thickness(8, 6, 8, 6), Background = active ? Looks.Tint(look.Color, 0.08) : Brushes.Transparent };
        Ui.SetCornerRadius(row, new CornerRadius(12));
        System.Windows.Automation.AutomationProperties.SetName(row, type.Name + (active ? " (פעיל)" : "") + " · " + AssistantMeta(type));
        row.Click += async (_, _) => { CloseOverlay(false); try { if (type.Id != activeTypeId) await ActivateTypeAsync(type.Id, false); Question.Focus(); } catch (Exception ex) { Notice(ex.Message); } };
        var cell = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        cell.Children.Add(row);
        // Editing an assistant is offered only here, next to its row.
        if (editable)
        {
            var edit = new Button { Style = Ui.Style("IconButton"), Content = new Icon("pencil", 18), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "עריכת העוזר" };
            System.Windows.Automation.AutomationProperties.SetName(edit, "עריכת העוזר " + type.Name);
            edit.Click += (_, _) => { CloseOverlay(false); OpenChatTypeDialog(type); };
            cell.Children.Add(edit);
        }
        return cell;
    }

    // ---- Icon & color (from the avatar in the header) ----
    private void PickLook(object sender, RoutedEventArgs e)
    {
        if (overlayKind == "look") { CloseOverlay(); return; }
        var type = ActiveType;
        if (type == null) return;
        var saved = SavedLooks.TryGetValue(type.Id, out var existing) ? existing : null;
        var committed = false;
        var picker = new LookPicker(LookOf(type));
        var done = new Button { Style = Ui.Style("AssistantButton"), Content = "סיום", MinHeight = 40, Padding = new Thickness(20, 6, 20, 6) };
        // Every pick previews at once in the header; "סיום" keeps it, closing any other way restores the previous look.
        picker.Changed += look => { SavedLooks[type.Id] = look; ApplyTypeLook(); };
        var reset = new Button { Style = Ui.Style("GhostButton"), MinHeight = 40, Padding = new Thickness(8, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
        var resetContent = new StackPanel { Orientation = Orientation.Horizontal };
        resetContent.Children.Add(new Icon("reset", 16, null, 2) { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        resetContent.Children.Add(new TextBlock { Text = "ברירת מחדל", VerticalAlignment = VerticalAlignment.Center });
        reset.Content = resetContent;
        System.Windows.Automation.AutomationProperties.SetName(reset, "חזרה לצבע ולסמל של ברירת המחדל");
        reset.Click += (_, _) => { SavedLooks.Remove(type.Id); picker.Set(LookOf(type)); ApplyTypeLook(); };
        done.Click += (_, _) => { committed = true; if (SavedLooks.ContainsKey(type.Id) || saved != null) SaveLooks(); CloseOverlay(); };

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock { Text = "מראה העוזר", FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var close = CloseButton("סגירה", () => CloseOverlay()); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, -6, -8, -6);
        heading.Children.Add(close);
        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(reset);
        done.HorizontalAlignment = HorizontalAlignment.Right; footer.Children.Add(done);
        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        body.Children.Add(heading); body.Children.Add(picker); body.Children.Add(new Border { Height = 1, Background = Theme.Brush("Divider"), Margin = new Thickness(0, 4, 0, 10) }); body.Children.Add(footer);
        var card = Card(body, 18);
        System.Windows.Automation.AutomationProperties.SetName(card, "מראה העוזר");
        // A small caret points at the avatar it opened from.
        var caret = new Border { Width = 14, Height = 14, Background = Theme.Brush("Surface"), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(23, -7, 0, 0) };
        var container = new Grid { Margin = new Thickness(8, 10, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        container.Children.Add(card); container.Children.Add(caret);
        ShowOverlay("look", new ScrollViewer { Content = container, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, true, LookButton, () =>
        {
            if (committed) return;
            if (saved == null) SavedLooks.Remove(type.Id); else SavedLooks[type.Id] = saved;
            ApplyTypeLook();
        });
    }

    // ---- ⋯ menu ----
    /// <summary>The menu under ⋯. The refresh group appears only for an assistant with document folders (never in general chat).</summary>
    public static Border BuildOptionsMenu(string? sourcesName, bool refreshing, bool refreshEnabled, Action refresh, Action? settings, Action about, Action exit)
    {
        var list = new StackPanel { Margin = new Thickness(6) };
        Button Item(string icon, string title, string? detail, Action action, Brush? color = null, UIElement? trailing = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Icon(icon, 20, color ?? Theme.Brush("Secondary")) { VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Foreground = color ?? Theme.Brush("Ink"), FontWeight = FontWeights.Medium });
            if (detail != null) text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            if (trailing != null) { Grid.SetColumn(trailing, 2); grid.Children.Add(trailing); }
            var button = new Button { Style = Ui.Style("RowButton"), Content = grid, Padding = new Thickness(10, 8, 10, 8), Foreground = color ?? Theme.Brush("Ink") };
            Ui.SetCornerRadius(button, new CornerRadius(10));
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            button.Click += (_, _) => action();
            list.Children.Add(button);
            return button;
        }
        if (sourcesName != null)
        {
            list.Children.Add(GroupLabel(sourcesName));
            Item("refresh", refreshing ? "מעדכן מסמכים…" : "רענון המסמכים", "סורק מחדש את התיקיות המחוברות", refresh).IsEnabled = refreshEnabled;
            list.Children.Add(Divider(4));
        }
        if (settings != null) Item("sliders", "הגדרות AI", null, settings);
        var beta = new Border { Background = Theme.Solid("#FEF3C7"), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "BETA", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Theme.Solid("#92400E"), FlowDirection = FlowDirection.LeftToRight } };
        Item("info", "אודות Chaty", null, about, null, beta);
        list.Children.Add(Divider(4));
        Item("power", "יציאה מ־Chaty", null, exit, Theme.Brush("Danger"));
        var card = Card(list, 14);
        card.Width = 264; card.HorizontalAlignment = HorizontalAlignment.Right; card.VerticalAlignment = VerticalAlignment.Top; card.Margin = new Thickness(8, 6, 8, 0);
        System.Windows.Automation.AutomationProperties.SetName(card, "תפריט");
        return card;
    }
    private void ShowOptions(object s, RoutedEventArgs e)
    {
        if (overlayKind == "menu") { CloseOverlay(); return; }
        var type = ActiveType;
        var menu = BuildOptionsMenu(HasDocuments(type) ? type!.Name : null, indexing, !indexing && running.Count == 0 && !switchingType,
            () => { CloseOverlay(); Reindex(this, new()); },
            BuildMode.Local ? async () => { CloseOverlay(false); await OpenLocalSettingsAsync(); } : null,
            ShowAbout,
            () => Application.Current.Shutdown());
        ShowOverlay("menu", menu, false, MenuButton);
    }

    // ---- About ----
    private void ShowAbout()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };
        var top = new Grid();
        var close = CloseButton("סגירת חלון האודות", () => CloseOverlay()); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 0, -10, 0);
        top.Children.Add(close);
        top.Children.Add(new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(16), Background = Theme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
            Child = new Icon("chat", 30, Brushes.White) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        body.Children.Add(top);
        var name = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        name.Children.Add(new TextBlock { Text = "Chaty", FontSize = 22, FontWeight = FontWeights.ExtraBold, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new Border { Background = Theme.Solid("#F59E0B"), CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "BETA", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Ink"), FlowDirection = FlowDirection.LeftToRight } });
        body.Children.Add(name);
        body.Children.Add(new TextBlock { Text = "v" + version, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, Foreground = Theme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(new TextBlock { Text = "עוזר AI אישי שעונה מתוך המסמכים שלכם, ישירות מהמחשב.", FontSize = 13.5, Foreground = Theme.Brush("Secondary"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 260, Margin = new Thickness(0, 10, 0, 14), LineHeight = 20 });
        var warning = new Grid();
        warning.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        warning.ColumnDefinitions.Add(new ColumnDefinition());
        warning.Children.Add(new Icon("warning", 18, Theme.Solid("#B45309"), 2) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
        var warningText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Theme.Solid("#78350F"), Margin = new Thickness(10, 0, 0, 0), LineHeight = 19 };
        warningText.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run("גרסת בטא.")));
        warningText.Inlines.Add(new System.Windows.Documents.Run(" ייתכנו תקלות ושינויים. כדאי לבדוק מידע חשוב במקור."));
        Grid.SetColumn(warningText, 1); warning.Children.Add(warningText);
        body.Children.Add(new Border { Background = Theme.Solid("#FFFBEB"), BorderBrush = Theme.Solid("#FCD34D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Child = warning });
        var details = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Detail(string label, string value, bool ltr = false)
        {
            var row = new Grid { MinHeight = 38 };
            if (details.Children.Count > 0) row.Children.Add(new Border { Height = 1, Background = Theme.Brush("Divider"), VerticalAlignment = VerticalAlignment.Top });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FlowDirection = ltr ? FlowDirection.LeftToRight : FlowDirection.RightToLeft, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value });
            System.Windows.Automation.AutomationProperties.SetName(row, label + ": " + value);
            details.Children.Add(row);
        }
        Detail("נוצר על ידי", "כפיר מנטין");
        Detail("גרסה", version + " (Beta)", true);
        Detail("מודל פעיל", config?.Ai.Model ?? "", true);
        body.Children.Add(details);
        body.Children.Add(new TextBlock { Text = "© 2026 כפיר מנטין. כל הזכויות שמורות.", FontSize = 11.5, Foreground = Theme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) });
        var card = Card(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 20);
        card.Margin = new Thickness(18, 12, 18, 12); card.VerticalAlignment = VerticalAlignment.Center; card.MaxWidth = 340;
        System.Windows.Automation.AutomationProperties.SetName(card, "אודות Chaty");
        ShowOverlay("about", card, true, MenuButton);
    }
}
