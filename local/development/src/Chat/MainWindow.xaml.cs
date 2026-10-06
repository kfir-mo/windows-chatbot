using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Knowledge.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
namespace Chat;
public partial class MainWindow : Window
{
    private Config config = null!;
    private GatewayClient gateway = null!;
    private bool personalAllowed;
    private bool loggedIn;
    private DocumentIndex index = null!;

    private List<Conversation> conversations = [];
    private Conversation current = new();
    private CancellationTokenSource? request;
    private readonly CancellationTokenSource lifetime = new();
    private DispatcherTimer? timer;
    private bool indexing;
    private bool sourcesReady;
    private bool expanded;
    private bool initialized;
    private CancellationTokenSource? indexRequest;
    private int panelAnimationVersion;
    private Storyboard? typingAnimation;
    public MainWindow()
    {
        InitializeComponent();
        SendButton.IsEnabled = false;
        Question.TextChanged += (_, _) => UpdateSendState();
        SendButton.IsEnabledChanged += (_, _) => { if (SendButton.IsEnabled && string.IsNullOrWhiteSpace(Question.Text)) SendButton.IsEnabled = false; };
        HistoryView.ConversationSelected += async selected => { if (request != null || indexing || switchingType) return; if (selected.ChatTypeId != activeTypeId) await ActivateTypeAsync(selected.ChatTypeId, false); current = selected; Question.Clear(); ShowPage("chat"); await RenderAsync(); };
        HistoryView.DeleteRequested = DeleteConversation;
        HistoryView.LookFor = conversation => { var type = chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId); var look = LookOf(type); return (LookBrush(look), look.Glyph); };
        Loaded += (_, _) => Anchor();
        // The status line is short; its tooltip carries the full text plus any indexing warnings.
        Status.ToolTipOpening += (_, _) => Status.ToolTip = statusDetail == null ? Status.Text : Status.Text + "\n\n" + statusDetail;
        Status.ToolTip = "";
        // Idle memory: give unused pages back to Windows once the user has been inactive for a while or closes the panel.
        PreviewMouseDown += (_, _) => MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds);
        PreviewKeyDown += (_, _) => MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) MemoryTrim.Schedule(Dispatcher, 1); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemParameters.StaticPropertyChanged += WorkAreaChanged;
        Closed += (_, _) => { SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemParameters.StaticPropertyChanged -= WorkAreaChanged; };
        // The app lives in the notification area (next to the clock) instead of the taskbar.
        SourceInitialized += (_, _) => tray ??= new TrayIcon(BuildMode.Local ? "Chaty" : "Chat", ToggleFromTray,
            ("פתיחת הצ׳אט", RestoreFromTaskbar), ("הסתרה", () => HideApp(this, new())), ("-", () => { }), ("יציאה", () => Application.Current.Shutdown()));
    }
    private TrayIcon? tray;
    private void ToggleFromTray()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !expanded) RestoreFromTaskbar();
        else CollapsePanel(this, new());
    }
    private void DisplayChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(Anchor);
    private void WorkAreaChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Anchor);
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (initialized)
            return;
        initialized = true;
        try
        {
            if (BuildMode.Local) { await InitializeLocalAsync(); return; }
            config = Config.Load();
            gateway = new(config.Gateway);
            ExpandPanel(this,new());
            LoginOverlay.Visibility=Visibility.Visible;
        }
        catch (Exception ex) { Status.Text = ex.Message; SendButton.IsEnabled = false; ExpandPanel(this, new()); }
    }
    protected override void OnClosed(EventArgs e)
    {
        lifetime.Cancel();
        request?.Cancel();
        timer?.Stop();
        gateway?.Dispose();
        tray?.Dispose(); tray = null;
        base.OnClosed(e);
    }
    private const double IdleTrimSeconds = 20;
    private string? statusDetail;
    private void Anchor()
    {
        var a = SystemParameters.WorkArea;
        // The launcher keeps the same screen position whether the panel is open or closed.
        Height = Math.Min(expanded ? 680 : 100, a.Height - 8);
        Width = Math.Min(expanded ? 400 : 96, a.Width - 8);
        Left = a.Right - Width - 4;
        Top = a.Bottom - Height - 4;
        if (expanded && a.Width < 430) { Width = a.Width; Height = a.Height; Left = a.Left; Top = a.Top; }
    }
    private void ExpandPanel(object s, RoutedEventArgs e)
    {
        panelAnimationVersion++;
        expanded = true;
        MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds);
        Anchor();
        Panel.Visibility = Visibility.Visible;
        SetLauncherGlyph(true);
        AnimatePanel(true);
        Question.Focus();
    }
    private void SetLauncherGlyph(bool open)
    {
        ChatGlyph.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        CloseGlyph.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Bubble.ToolTip = open ? "סגירת הצ׳אט" : "פתיחת הצ׳אט";
        if (SystemParameters.ClientAreaAnimation)
            GlyphTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, new DoubleAnimation(open ? -90 : 90, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void WindowKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && expanded && SettingsOverlay.Visibility != Visibility.Visible) { CollapsePanel(s, e); e.Handled = true; }
    }
    private void CollapsePanel(object s, RoutedEventArgs e)
    {
        if (!expanded) return;
        var version = ++panelAnimationVersion;
        expanded = false;
        SetLauncherGlyph(false);
        AnimatePanel(false, () =>
        {
            if (version != panelAnimationVersion) return;
            Panel.Visibility = Visibility.Collapsed;
            Anchor();
            MemoryTrim.Schedule(Dispatcher, 1.5);
        });
    }
    private void AnimatePanel(bool opening, Action? completed = null)
    {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 180 : 1);
        DoubleAnimation Motion(double from, double to) => new(from, to, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var opacity = Motion(opening ? 0 : Panel.Opacity, opening ? 1 : 0);
        opacity.Completed += (_, _) => completed?.Invoke();
        Panel.BeginAnimation(OpacityProperty, opacity);
        PanelSlide.BeginAnimation(TranslateTransform.YProperty, Motion(opening ? 22 : PanelSlide.Y, opening ? 0 : 18));
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(opening ? .96 : PanelScale.ScaleX, opening ? 1 : .96));
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(opening ? .96 : PanelScale.ScaleY, opening ? 1 : .96));
    }
    private void TogglePanel(object s, RoutedEventArgs e)
    {
        if (expanded) CollapsePanel(s, e); else ExpandPanel(s, e);
    }
    private void ShowPage(string page)
    {
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        HistoryTab.Background = page == "history" ? new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)) : Brushes.Transparent;
        HistoryTab.ToolTip = page == "history" ? "חזרה לצ׳אט" : "היסטוריית שיחות";
    }
    private void UseSuggestion(object s, RoutedEventArgs e)
    {
        if (s is Button button) Question.Text = button.Tag as string ?? "";
        Question.Focus();
        Question.CaretIndex = Question.Text.Length;
    }
    private void ShowOptions(object s, RoutedEventArgs e)
    {
        var menu = CreateOptionsMenu(Reindex, HideApp);
        if (BuildMode.Local)
        {
            var settings = MenuEntry("הגדרות AI", "", async (_, _) => await OpenLocalSettingsAsync());
            menu.Items.Insert(0, settings); menu.Items.Insert(1, new Separator());
        }
        var refresh = menu.Items.OfType<MenuItem>().FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetName(i) == "עדכון מאגר המסמכים");
        if (refresh?.Tag is (TextBlock title, TextBlock detail))
        {
            var hasSources = activeConfig?.GeneralChat == false || chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged);
            title.Text = indexing ? "מעדכן מסמכים…" : "עדכון מאגר המסמכים";
            var minutes = lastSourceRefresh.HasValue ? (int)(DateTimeOffset.Now - lastSourceRefresh.Value).TotalMinutes : -1;
            detail.Text = !hasSources ? "לעוזר הזה אין תיקיות מסמכים" : minutes < 0 ? "המקורות מתעדכנים בתחילת שיחה" : minutes < 1 ? "עודכן הרגע" : minutes == 1 ? "עודכן לפני דקה" : $"עודכן לפני {minutes} דקות";
            detail.Visibility = Visibility.Visible;
            refresh.IsEnabled = hasSources && !indexing && request == null;
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("יציאה מהאפליקציה", "", (_, _) => Application.Current.Shutdown()));
        menu.PlacementTarget = s as UIElement;
        // The button sits at the panel's left edge: a left-to-right popup aligns with it and opens into the panel. Items stay right-to-left.
        menu.FlowDirection = FlowDirection.LeftToRight;
        OpenStyledMenu(menu);
    }
    public static MenuItem MenuEntry(string title, string glyph, RoutedEventHandler action, Brush? color = null)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 15, Foreground = color ?? Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleBlock = new TextBlock { Text = title, Foreground = color ?? Theme.Brush("Ink") };
        var detailBlock = new TextBlock { FontSize = 11.5, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
        text.Children.Add(titleBlock); text.Children.Add(detailBlock);
        Grid.SetColumn(text, 1); header.Children.Add(text);
        var item = new MenuItem { Header = header, Tag = (titleBlock, detailBlock), FlowDirection = FlowDirection.RightToLeft };
        System.Windows.Automation.AutomationProperties.SetName(item, title);
        item.Click += action;
        return item;
    }
    public static ContextMenu CreateOptionsMenu(RoutedEventHandler refreshAction, RoutedEventHandler hideAction)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("ChatMenu"), Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("עדכון מאגר המסמכים", "", refreshAction));
        menu.Items.Add(MenuEntry("הסתרה למגש המערכת", "", hideAction));
        return menu;
    }
    private void HideApp(object s, RoutedEventArgs e)
    {
        // Everything (panel and corner button) hides; the tray icon next to the clock brings it back.
        if (expanded) { expanded = false; panelAnimationVersion++; SetLauncherGlyph(false); Panel.Visibility = Visibility.Collapsed; Anchor(); }
        Hide();
        MemoryTrim.Schedule(Dispatcher, 1);
    }
    internal void RestoreFromTaskbar()
    {
        Show();
        WindowState = WindowState.Normal;
        Topmost = false; Topmost = true;
        ExpandPanel(this, new());
        Activate();
    }
    private bool Save()
    {
        try
        {
            Storage.Save("conversations.bin", conversations.Where(c => c.HasUserMessages).ToList());
            return true;
        }
        catch { Status.Text = "לא ניתן לשמור שיחה במחשב זה. בדקו מקום פנוי והרשאות."; return false; }
    }
    private async void NewChat(object s, RoutedEventArgs e)
    {
        if (!loggedIn || request != null || indexing || switchingType)
            return;
        current = new() { ChatTypeId = activeTypeId };
        Save();
        Question.Clear();
        ShowPage("chat");
        await RenderAsync();
        await IndexAsync(false);
    }
    private void History(object s, RoutedEventArgs e)
    {
        if (!loggedIn || request != null)
            return;
        if (HistoryView.Visibility == Visibility.Visible) { ShowPage("chat"); Question.Focus(); return; }
        foreach (var conversation in conversations) conversation.ChatTypeName = chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId)?.Name ?? conversation.ChatTypeName;
        HistoryView.Refresh(conversations.Where(c => chatTypes.Any(t => t.Id == c.ChatTypeId)));
        ShowPage("history");
    }
    private bool DeleteConversation(Conversation conversation)
    {
        var position = conversations.IndexOf(conversation);
        if (position < 0 || request != null) return false;
        conversations.RemoveAt(position);
        if (!Save()) { conversations.Insert(position, conversation); return false; }
        if (current.Id == conversation.Id) { current = new() { ChatTypeId = activeTypeId }; Question.Clear(); Messages.Children.Clear(); Welcome.Visibility = Visibility.Visible; }
        RefreshSuggestions();
        return true;
    }
    private async void Reindex(object s, RoutedEventArgs e) => await IndexAsync(true);
    private async Task IndexAsync(bool force)
    {
        if (!chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged) && activeConfig?.GeneralChat == true) { sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now; Status.Text = "שיחה חופשית עם AI"; return; }
        if (indexing || index == null)
            return;
        // Refreshed a moment ago in this session (or by the background preload): no need to rescan the folders.
        var local = !chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged);
        if (!force && local && activeSession?.Refreshed is { } refreshed && DateTimeOffset.Now - refreshed < SessionRefreshInterval)
        {
            sourcesReady = true; lastSourceRefresh = refreshed; indexedFiles = index.Statistics.Files; ApplyTypeLook();
            Status.Text = index.Summary.Length > 0 ? index.Summary : "מוכן";
            statusDetail = WarningsText();
            return;
        }
        indexing = true; sourcesReady = false; statusDetail = null;
        CancelButton.Visibility = Visibility.Visible;
        indexRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            SendButton.IsEnabled = NewChatButton.IsEnabled = false;
            if (chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged))
            {
                Status.Text = "מעדכן מקורות בשרת…";
                await gateway.RefreshAsync(activeTypeId, indexRequest.Token);
                Status.Text = "המקורות בשרת עודכנו";
            }
            else
            {
                var session = activeSession;
                await index.RefreshAsync(force, new Progress<string>(x => Status.Text = x), indexRequest.Token); indexedFiles = index.Statistics.Files; ApplyTypeLook();
                if (session != null) session.Refreshed = DateTimeOffset.Now;
                // Hovering the status shows which files were skipped and why.
                statusDetail = WarningsText();
            }
            sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) { Status.Text = "עדכון המאגר בוטל."; }
        catch { Status.Text = "לא ניתן לעדכן את המאגר. בדקו נתיב והרשאות."; }
        finally { indexing = false; SendButton.IsEnabled = NewChatButton.IsEnabled = request == null; indexRequest.Dispose(); indexRequest = null; if (request == null) CancelButton.Visibility = Visibility.Collapsed; MemoryTrim.Schedule(Dispatcher, expanded ? IdleTrimSeconds : 1.5); }
    }
    private string? WarningsText() => index.Warnings.Count == 0 ? null : "קבצים שלא נכללו:\n" + string.Join("\n", index.Warnings.Take(12)) + (index.Warnings.Count > 12 ? $"\nועוד {index.Warnings.Count - 12}…" : "");
    private async void Send(object s, RoutedEventArgs e)
    {
        if (!loggedIn || request != null || switchingType || indexing || (!BuildMode.Local && gateway == null) || string.IsNullOrWhiteSpace(Question.Text))
            return;
        if (!sourcesReady) { Status.Text = "יש לעדכן את המקורות לפני שליחת הודעה. לחצו על עדכון מאגר המסמכים."; return; }
        var q = Question.Text.Trim();
        if (q.Length > config.History.MessageCharacters)
        {
            Status.Text = $"השאלה ארוכה מדי (עד {config.History.MessageCharacters} תווים).";
            return;
        }
        request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(config.Ai.TimeoutSeconds * 4 + 30));
        SendButton.IsEnabled = false;
        HistoryTab.IsEnabled = NewChatButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        Question.Clear();
        current.ChatTypeName = chatTypes.First(t => t.Id == activeTypeId).Name;
        current.Messages.Add(new("user", q, DateTimeOffset.Now, []));
        if (!conversations.Any(c => c.Id == current.Id)) conversations.Add(current);
        try
        {
            Save();
            await RenderAsync();
            Typing.Visibility = Visibility.Visible;
            Scroll.UpdateLayout();
            Scroll.ScrollToEnd();
            if (SystemParameters.ClientAreaAnimation)
            {
                typingAnimation = ((Storyboard)Typing.Resources["TypingMotion"]).Clone();
                typingAnimation.Begin(this, true);
            }
            Status.Text = "מבין את הבקשה…";
            IProgress<string> progress = new Progress<string>(x => Status.Text = x);
            ChatMessage answer;
            if (BuildMode.Local)
            {
                using var ai = new AiClient(activeConfig);
                ai.WaitingForRateLimit = delay => progress.Report($"ממתין למכסת ה־API · כ־{Math.Ceiling(delay.TotalSeconds)} שניות…");
                answer = await new ChatEngine(activeConfig, ai, index).AskAsync(q, current, progress, request.Token);
            }
            else answer = await AskServerAsync(q, request.Token);
            Typing.Visibility = Visibility.Collapsed;
            current.Messages.Add(answer);
            Save();
            await RenderAsync();
            if (Status.Text.StartsWith("ממתין למכסת ה־API") || Status.Text is "מבין את הבקשה…" or "מכין תשובה…" or "מחפש מידע במסמכים…" or "בודק שהתשובה מתאימה לבקשה…")
                Status.Text = activeConfig.GeneralChat ? "שיחה חופשית עם AI" : "מוכן";
        }
        catch (OperationCanceledException) { Status.Text = "הבקשה בוטלה או עברה את הזמן המותר."; }
        catch (Exception ex) { Status.Text = "הבקשה נכשלה: " + ex.Message; Storage.Log(config, "chat_failed"); }
        finally { typingAnimation?.Remove(this); typingAnimation = null; Typing.Visibility = Visibility.Collapsed; request.Dispose(); request = null; SendButton.IsEnabled = true; HistoryTab.IsEnabled = NewChatButton.IsEnabled = true; if (!indexing) CancelButton.Visibility = Visibility.Collapsed; MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds); }
    }
    private void Cancel(object s, RoutedEventArgs e)
    {
        request?.Cancel();
        indexRequest?.Cancel();
    }
    public static StackPanel CreateSourceTags(IEnumerable<Source> sources, Action<string> report)
    {
        var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        block.Children.Add(new TextBlock { Text = "מקורות", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 0, 0, 6) });
        var tags = new WrapPanel();
        foreach (var source in sources.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(source.Path);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(FileBadge.For(source.Path));
            label.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var tip = new StackPanel();
            tip.Children.Add(new TextBlock { Text = "פתיחת המסמך" + (string.IsNullOrWhiteSpace(source.Location) ? "" : " · " + source.Location), FontWeight = FontWeights.SemiBold });
            tip.Children.Add(new TextBlock { Text = source.Path, FlowDirection = FlowDirection.LeftToRight, MaxWidth = 320, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var b = new Button { Style = Ui.Style("ChipButton"), Content = label, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetName(b, "פתיחת " + name);
            b.Click += async (_, _) => { if (!Access.SafeDocument(source.Path) || !await Task.Run(() => Access.CanRead(source.Path))) { report("המקור אינו נגיש או מסוג שאינו נתמך."); return; } try { Process.Start(new ProcessStartInfo(source.Path) { UseShellExecute = true }); } catch { report("לא ניתן לפתוח את המסמך."); } };
            tags.Children.Add(b);
        }
        block.Children.Add(tags);
        return block;
    }
    public static TextBox CreateMessageText(string text, bool isUser)
    {
        var direction = MessageView.DirectionOf(text);
        return new()
        {
            Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 14,
            // WPF mirrors Left alignment inside right-to-left text: it is always the reading start edge.
            TextAlignment = TextAlignment.Left, FlowDirection = direction,
            Foreground = isUser ? Brushes.White : Theme.Brush("Ink"), SelectionBrush = isUser ? Brushes.White : Theme.Brush("Accent")
        };
    }
    private async Task RenderAsync()
    {
        if (activeConfig != null && !chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged)) ChatType.RedactOutsideFolders(current, activeConfig.Documents);
        await Task.Run(() => ChatEngine.RedactInaccessible(current));
        Save();
        Messages.Children.Clear();
        Welcome.Visibility = current.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSuggestions();
        foreach (var m in current.Messages)
        {
            var message = m.Role == "user" ? MessageView.User(m) : MessageView.Assistant(m, text => Status.Text = text);
            Messages.Children.Add(message);
            if (m == current.Messages.Last() && SystemParameters.ClientAreaAnimation)
            {
                message.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var movement = new TranslateTransform();
                message.RenderTransform = movement;
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        Scroll.UpdateLayout();
        Scroll.ScrollToEnd();
    }
    private void QuestionKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send(s, new());
        }
    }
    private void RefreshSuggestions()
    {
        var type = chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
        var documents = type != null && !type.IsGeneral && (type.IsManaged || type.Paths.Count > 0);
        ApplyTypeLook();
        Suggestions.Children.Clear();
        if (current.Messages.Count == 0)
            foreach (var question in SuggestedQuestions(documents))
            {
                var button = new Button { Style = (Style)FindResource("Suggestion"), Tag = question, ToolTip = question,
                    Content = new TextBlock { Text = question, MaxWidth = 290, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = MessageView.DirectionOf(question) } };
                System.Windows.Automation.AutomationProperties.SetName(button, question);
                button.Click += UseSuggestion;
                Suggestions.Children.Add(button);
            }
        Suggestions.Visibility = Suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Each chat type has its own color, icon and landing page so users can tell at a glance where they are.</summary>
    public sealed record TypeLook(string Color, string Glyph);
    private static readonly (string Key, string Name, string Light, string Dark)[] Palette =
    {
        ("blue", "כחול", "#3B82FF", "#0B4FD8"), ("green", "ירוק", "#22C55E", "#15803D"), ("purple", "סגול", "#8B5CF6", "#6D28D9"),
        ("orange", "כתום", "#F59E0B", "#C2410C"), ("pink", "ורוד", "#EC4899", "#BE185D"), ("teal", "טורקיז", "#14B8A6", "#0F766E")
    };
    private static readonly (string Glyph, string Name)[] Glyphs =
    {
        ("\uE8BD", "צ׳אט"), ("\uE8A5", "מסמך"), ("\uE99A", "רובוט"), ("\uE821", "עבודה"), ("\uE7BE", "לימוד"), ("\uE734", "כוכב"),
        ("", "ספרייה"), ("", "תיקייה"), ("", "דואר"), ("", "טלפון"), ("", "יומן"), ("", "אנשים"),
        ("", "איש קשר"), ("", "בית"), ("", "עולם"), ("", "מידע"), ("", "עזרה"), ("", "הגדרות"),
        ("", "תיקונים"), ("", "נעילה"), ("", "ניהול"), ("", "קוד"), ("", "מסך"), ("", "רשת"),
        ("", "ענן"), ("", "חבילה"), ("", "ברק"), ("", "תגית"), ("", "דגל"), ("", "כספים"),
        ("", "קניות"), ("", "רכב"), ("", "טיסה"), ("", "לב"), ("", "בדיקה"), ("", "מצלמה")
    };
    private Dictionary<string, TypeLook>? looks;
    private Dictionary<string, TypeLook> Looks => looks ??= Storage.Load<Dictionary<string, TypeLook>>("chat-looks.bin") ?? new();
    private int? indexedFiles;
    private TypeLook LookOf(ChatType? type)
    {
        if (type != null && Looks.TryGetValue(type.Id, out var saved) && Palette.Any(c => c.Key == saved.Color)) return saved;
        var general = type == null || type.IsGeneral;
        var documents = !general && (type!.IsManaged || type.Paths.Count > 0);
        // A stable hash (string.GetHashCode changes between runs); blue stays reserved for general chat.
        var color = general ? "blue" : Palette[1 + type!.Id.Aggregate(0, (h, c) => (h * 31 + c) & 0x7FFFFFFF) % (Palette.Length - 1)].Key;
        return new(color, general ? "\uE8BD" : documents ? "\uE8A5" : "\uE99A");
    }
    private static Brush LookBrush(TypeLook look)
    {
        var (_, _, light, dark) = Palette.First(c => c.Key == look.Color);
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(light), (Color)ColorConverter.ConvertFromString(dark), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }
    private void ApplyTypeLook()
    {
        var type = chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
        var general = type == null || type.IsGeneral;
        var documents = !general && (type!.IsManaged || type.Paths.Count > 0);
        var look = LookOf(type);
        var (_, _, _, dark) = Palette.First(c => c.Key == look.Color);
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        var brush = LookBrush(look);
        HeaderBar.Background = WelcomeIconCircle.Background = brush;
        WelcomeGlow.Color = C(dark);
        HeaderGlyph.Text = WelcomeIcon.Text = look.Glyph;
        WelcomeTitle.Text = general ? "Chaty" : type!.Name;
        WelcomeKindBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, C(dark).R, C(dark).G, C(dark).B));
        WelcomeKind.Foreground = new SolidColorBrush(C(dark));
        WelcomeKind.Text = general ? "צ׳אט כללי · שיחה חופשית עם AI"
            : type!.IsManaged ? "ידע ארגוני"
            : documents ? "עוזר מסמכים · " + (indexedFiles switch { null => "סופר קבצים…", 0 => "אין קבצים נתמכים", 1 => "קובץ אחד", var n => $"{n} קבצים" })
            : "עוזר אישי · ללא מסמכים";
        WelcomeText.Text = general ? "שאלו כל שאלה, בקשו ניסוח, תרגום או סיכום. התשובות לא מבוססות על מסמכים."
            : documents ? "אני עונה רק מתוך המסמכים בתיקיות האלה, ומציג מקור לכל תשובה."
            : "לעוזר הזה עדיין אין תיקיות מסמכים. אפשר להוסיף אותן בעריכת העוזר.";
        WelcomeSources.Children.Clear();
        if (!general && !type!.IsManaged)
        {
            Border Chip(string glyph, string text, object tip)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                chip.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = new SolidColorBrush(C(dark)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                chip.Children.Add(new TextBlock { Text = text, FontSize = 12, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                return new Border { Child = chip, Background = Theme.Brush("Surface"), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3), ToolTip = tip };
            }
            // Folder chips open the folder (or select the file) in Explorer.
            Button Folder(string path)
            {
                var chip = Chip("\uE8B7", FolderName(path), new TextBlock { Text = "\u05E4\u05EA\u05D9\u05D7\u05D4 \u05D1\u05E1\u05D9\u05D9\u05E8 \u05D4\u05E7\u05D1\u05E6\u05D9\u05DD\n" + path, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right });
                var content = chip.Child; chip.Child = null;
                var button = new Button { Content = content, Style = Ui.Style("ChipButton"), Background = Theme.Brush("Surface"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = chip.ToolTip };
                Ui.SetHoverBrush(button, new SolidColorBrush(Color.FromArgb(0x1A, C(dark).R, C(dark).G, C(dark).B)));
                System.Windows.Automation.AutomationProperties.SetName(button, "\u05E4\u05EA\u05D9\u05D7\u05EA \u05D4\u05EA\u05D9\u05E7\u05D9\u05D9\u05D4 " + FolderName(path));
                button.Click += async (_, _) =>
                {
                    var (folder, file) = await Task.Run(() => (System.IO.Directory.Exists(path), System.IO.File.Exists(path)));
                    try
                    {
                        if (folder) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
                        else if (file) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                        else Status.Text = "\u05D4\u05EA\u05D9\u05E7\u05D9\u05D9\u05D4 \u05DC\u05D0 \u05E0\u05DE\u05E6\u05D0\u05D4: " + FolderName(path);
                    }
                    catch { Status.Text = "\u05DC\u05D0 \u05E0\u05D9\u05EA\u05DF \u05DC\u05E4\u05EA\u05D5\u05D7 \u05D0\u05EA \u05D4\u05EA\u05D9\u05E7\u05D9\u05D9\u05D4."; }
                };
                return button;
            }
            foreach (var path in type.Paths.Take(4)) WelcomeSources.Children.Add(Folder(path));
            if (type.Paths.Count > 4)
            {
                // The rest open from a small menu so every folder stays reachable.
                var more = new Button { Content = $"+{type.Paths.Count - 4}", Style = Ui.Style("ChipButton"), Background = Theme.Brush("Surface"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(3), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "\u05E2\u05D5\u05D3 \u05EA\u05D9\u05E7\u05D9\u05D5\u05EA" };
                more.Click += (_, _) =>
                {
                    var panel = new StackPanel { Margin = new Thickness(4) };
                    foreach (var path in type.Paths.Skip(4)) { var b = Folder(path); b.HorizontalAlignment = HorizontalAlignment.Stretch; panel.Children.Add(b); }
                    var menu = new ContextMenu { Style = Ui.Style("ChatMenu"), PlacementTarget = more, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, FlowDirection = FlowDirection.RightToLeft };
                    menu.Items.Add(new MenuItem { Header = new ScrollViewer { Content = panel, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Style = Ui.Style("PlainMenuRow") });
                    foreach (Button b in panel.Children) b.Click += (_, _) => menu.IsOpen = false;
                    menu.IsOpen = true;
                };
                WelcomeSources.Children.Add(more);
            }
            if (!string.IsNullOrWhiteSpace(type.Prompt)) WelcomeSources.Children.Add(Chip("\uE70F", "הנחיות מותאמות", type.Prompt.Length > 300 ? type.Prompt[..300] + "…" : type.Prompt));
        }
        WelcomeSources.Visibility = WelcomeSources.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PickLook(object sender, RoutedEventArgs e)
    {
        var type = chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
        if (type == null) return;
        var menu = new ContextMenu { Style = (Style)FindResource("ChatMenu"), PlacementTarget = HeaderBar, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, Width = HeaderBar.ActualWidth, VerticalOffset = -6 };
        var colors = new WrapPanel();
        // The icon set wraps into rows inside the menu (which is as wide as the header).
        // Two rows show at first; "הצגת עוד" reveals the rest. A chosen icon beyond them opens the full set.
        // Each icon takes 40px (36 + margin); the panel is an exact multiple so rows are always full.
        var perRow = Math.Max(4, (int)((HeaderBar.ActualWidth - 60) / 40));
        var glyphs = new WrapPanel { Width = perRow * 40 };
        var showAll = Array.FindIndex(Glyphs, g => g.Glyph == LookOf(type).Glyph) >= perRow * 2;
        var more = new Button { Style = Ui.Style("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2, 4, 2), MinHeight = 0, FontSize = 12.5 };
        more.Click += (_, _) => { showAll = !showAll; Fill(); };
        void Save(TypeLook look)
        {
            Looks[type.Id] = look;
            try { Storage.Save("chat-looks.bin", Looks); } catch { Status.Text = "לא ניתן לשמור את הבחירה במחשב זה."; }
            ApplyTypeLook(); Fill();
        }
        void Fill()
        {
            var look = LookOf(type);
            colors.Children.Clear(); glyphs.Children.Clear();
            foreach (var (key, name, light, dark) in Palette)
            {
                var swatch = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(light), (Color)ColorConverter.ConvertFromString(dark), new Point(0, 0), new Point(1, 1));
                var b = new Button { Style = (Style)FindResource("IconButton"), Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0), Background = swatch, Foreground = Brushes.White, FontSize = 12, ToolTip = name,
                    Content = key == look.Color ? "\uE73E" : "" };
                Ui.SetHoverBrush(b, new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)));
                System.Windows.Automation.AutomationProperties.SetName(b, "צבע " + name);
                b.Click += (_, _) => Save(LookOf(type) with { Color = key });
                colors.Children.Add(b);
            }
            more.Content = showAll ? "הצגת פחות" : $"הצגת עוד ({Glyphs.Length - perRow * 2})";
            more.Visibility = Glyphs.Length > perRow * 2 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (glyph, name) in showAll ? Glyphs : Glyphs.Take(perRow * 2))
            {
                var selected = glyph == look.Glyph;
                var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, FontSize = 16, Margin = new Thickness(0, 0, 4, 4), ToolTip = name,
                    Background = selected ? Theme.Brush("Selected") : Brushes.Transparent, Foreground = selected ? Theme.Brush("Accent") : Theme.Brush("Ink") };
                System.Windows.Automation.AutomationProperties.SetName(b, "סמל " + name);
                b.Click += (_, _) => Save(LookOf(type) with { Glyph = glyph });
                glyphs.Children.Add(b);
            }
        }
        Fill();
        MenuItem Label(string text) => new() { Style = (Style)FindResource("PlainMenuRow"), Padding = new Thickness(12, 8, 12, 6), Header = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted") } };
        menu.Items.Add(MenuTitle("צבע · " + (type.IsGeneral ? "צ׳אט כללי" : type.Name), menu));
        menu.Items.Add(new MenuItem { Style = (Style)FindResource("PlainMenuRow"), Padding = new Thickness(12, 0, 12, 6), Header = colors });
        menu.Items.Add(Label("סמל"));
        menu.Items.Add(new MenuItem { Style = (Style)FindResource("PlainMenuRow"), Padding = new Thickness(10, 0, 10, 6), Header = glyphs });
        menu.Items.Add(new MenuItem { Style = (Style)FindResource("PlainMenuRow"), Padding = new Thickness(10, 0, 10, 4), Header = more });
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("חזרה לברירת המחדל", "\uE72C", (_, _) => { Looks.Remove(type.Id); try { Storage.Save("chat-looks.bin", Looks); } catch { } ApplyTypeLook(); }));
        OpenStyledMenu(menu);
    }

    /// <summary>The user's most-asked questions for this assistant, then their latest ones, then generic examples.</summary>
    private List<string> SuggestedQuestions(bool documents)
    {
        static string Clean(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        static string Key(string text) => System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[\p{P}\p{S}\s]", "");
        var history = conversations.Where(c => c.ChatTypeId == activeTypeId).ToList();
        var recent = history.SelectMany(c => c.Messages).Where(m => m.Role == "user").OrderByDescending(m => m.Time).Select(m => Clean(m.Text))
            .Where(t => t.Length is >= 4 and <= 160 && RequestPlanner.LocalPlan(t, 40) == null);
        var examples = documents ? new[] { "מה יש במאגר?" } : new[] { "נסחו לי מייל קצר לבקשת יום חופש", "הסבירו לי בפשטות מה זה VPN" };
        return FrequentQuestions.Select(history).Concat(recent).Concat(examples).DistinctBy(Key).Take(3).ToList();
    }
}
