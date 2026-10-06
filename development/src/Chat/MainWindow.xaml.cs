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
        HistoryView.ConversationSelected += async selected => await OpenConversationAsync(selected);
        HistoryView.BackRequested += () => { ShowPage("chat"); Question.Focus(); };
        HistoryView.DeleteRequested = DeleteConversation;
        HistoryView.LookFor = conversation => { var look = LookOf(chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId)); return (Looks.Brush(look.Color), look.Glyph); };
        // The panel's children are square; one rounded clip keeps every screen inside the panel's corners.
        PanelRoot.SizeChanged += (_, e) => PanelRoot.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);
        // The chat area's empty background also takes the double-click (also when the message list has handled the click).
        ChatPage.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(BackgroundDoubleClick), true);
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
    private const double BaseWidth = 400, BaseHeight = 680, MinPanelWidth = 340, MinPanelHeight = 440;
    /// <summary>The open window's size; the user can drag the panel's top and left edges, and a double-click restores the base size.</summary>
    private double panelWidth = BaseWidth, panelHeight = BaseHeight;
    private void Anchor()
    {
        var a = SystemParameters.WorkArea;
        // The launcher keeps the same screen position whether the panel is open or closed.
        Height = Math.Min(expanded ? panelHeight : 100, a.Height - 8);
        Width = Math.Min(expanded ? panelWidth : 96, a.Width - 8);
        Left = a.Right - Width - 4;
        Top = a.Bottom - Height - 4;
        if (expanded && a.Width < 430) { Width = a.Width; Height = a.Height; Left = a.Left; Top = a.Top; }
    }
    private Point? gripStart;
    private Size gripSize;
    private Point CursorOnScreen(MouseEventArgs e)
    {
        var device = PointToScreen(e.GetPosition(this));
        return PresentationSource.FromVisual(this)?.CompositionTarget is { } target ? target.TransformFromDevice.Transform(device) : device;
    }
    private void GripDown(object s, MouseButtonEventArgs e)
    {
        if (!expanded || SystemParameters.WorkArea.Width < 430) return;
        e.Handled = true;
        if (e.ClickCount == 2) { ResetPanelSize(); return; }
        gripStart = CursorOnScreen(e); gripSize = new(Width, Height);
        ((UIElement)s).CaptureMouse();
    }
    private void GripMove(object s, MouseEventArgs e)
    {
        if (gripStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = CursorOnScreen(e);
        var a = SystemParameters.WorkArea;
        var edge = (string)((FrameworkElement)s).Tag;
        // The window grows up and to the left; its bottom-right corner (the launcher) stays put.
        if (edge != "left") panelHeight = Math.Clamp(gripSize.Height + start.Y - now.Y, MinPanelHeight, a.Height - 8);
        if (edge != "top") panelWidth = Math.Clamp(gripSize.Width + start.X - now.X, MinPanelWidth, a.Width - 8);
        Anchor();
    }
    private void GripUp(object s, MouseButtonEventArgs e) { gripStart = null; ((UIElement)s).ReleaseMouseCapture(); }
    private void ResetPanelSize() { panelWidth = BaseWidth; panelHeight = BaseHeight; Anchor(); }
    /// <summary>A double-click on an empty part of the header or the chat restores the base size. Text, buttons and fields keep their own double-click.</summary>
    private void BackgroundDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || !expanded) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != s; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.ToggleButton) return;
        if (panelWidth == BaseWidth && panelHeight == BaseHeight) return;
        e.Handled = true;
        ResetPanelSize();
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
        if (e.Key != Key.Escape || !expanded) return;
        // Escape closes the innermost layer first: a popover, then history; the forms handle their own Escape.
        if (overlayKind != null) CloseOverlay();
        else if (ScreenLayer.Visibility == Visibility.Visible) return;
        else if (HistoryView.Visibility == Visibility.Visible) { ShowPage("chat"); Question.Focus(); }
        else CollapsePanel(s, e);
        e.Handled = true;
    }
    private void CollapsePanel(object s, RoutedEventArgs e)
    {
        if (!expanded) return;
        var version = ++panelAnimationVersion;
        expanded = false;
        CloseOverlay(false);
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
    /// <summary>"chat" shows the conversation, "history" the history page under the header.</summary>
    private void ShowPage(string page)
    {
        CloseOverlay(false);
        ScreenHost.Content = null;
        ScreenLayer.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderState();
    }
    /// <summary>A full-panel screen (AI settings, assistant form) over the header and the chat.</summary>
    private void ShowScreen(FrameworkElement screen)
    {
        CloseOverlay(false);
        ScreenHost.Content = screen;
        ScreenLayer.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation) screen.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }
    private void CloseScreen(FrameworkElement screen)
    {
        if (ScreenHost.Content != screen) return;
        ScreenHost.Content = null;
        ScreenLayer.Visibility = Visibility.Collapsed;
        if (HistoryView.Visibility == Visibility.Visible) HistoryView.FocusStart(); else Question.Focus();
    }
    private void HideApp(object s, RoutedEventArgs e)
    {
        // Everything (panel and corner button) hides; the tray icon next to the clock brings it back.
        if (expanded) { expanded = false; panelAnimationVersion++; CloseOverlay(false); SetLauncherGlyph(false); Panel.Visibility = Visibility.Collapsed; Anchor(); }
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
        CloseOverlay(false);
        if (!loggedIn || request != null || indexing || switchingType)
            return;
        current = new() { ChatTypeId = activeTypeId };
        Save();
        Question.Clear();
        ShowPage("chat");
        await RenderAsync();
        Question.Focus();
        await IndexAsync(false);
    }
    private void History(object s, RoutedEventArgs e)
    {
        if (!loggedIn || request != null)
            return;
        if (HistoryView.Visibility == Visibility.Visible) { ShowPage("chat"); Question.Focus(); return; }
        foreach (var conversation in conversations) conversation.ChatTypeName = chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId)?.Name ?? conversation.ChatTypeName;
        HistoryView.Refresh(conversations);
        ShowPage("history");
        HistoryView.FocusStart();
    }
    private async Task OpenConversationAsync(Conversation selected)
    {
        if (request != null || indexing || switchingType) return;
        var owner = chatTypes.Any(t => t.Id == selected.ChatTypeId) ? selected.ChatTypeId : ChatType.GeneralId;
        if (owner != activeTypeId) await ActivateTypeAsync(owner, false);
        current = selected; Question.Clear(); ShowPage("chat"); await RenderAsync(); Question.Focus();
    }
    private bool DeleteConversation(Conversation conversation)
    {
        var position = conversations.IndexOf(conversation);
        if (position < 0 || request != null) return false;
        conversations.RemoveAt(position);
        if (!Save()) { conversations.Insert(position, conversation); return false; }
        if (current.Id == conversation.Id) { current = new() { ChatTypeId = activeTypeId }; Question.Clear(); Messages.Children.Clear(); Welcome.Visibility = Visibility.Visible; }
        RefreshWelcome();
        return true;
    }
    private async void Reindex(object s, RoutedEventArgs e) => await IndexAsync(true);
    private async Task IndexAsync(bool force)
    {
        if (!chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged) && activeConfig?.GeneralChat == true) { sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now; Status.Text = IdleStatus(); return; }
        if (indexing || index == null)
            return;
        // Refreshed a moment ago in this session (or by the background preload): no need to rescan the folders.
        var local = !chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged);
        if (!force && local && activeSession?.Refreshed is { } refreshed && DateTimeOffset.Now - refreshed < SessionRefreshInterval)
        {
            sourcesReady = true; lastSourceRefresh = refreshed; indexedFiles = index.Statistics.Files;
            Status.Text = IdleStatus();
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
                await index.RefreshAsync(force, new Progress<string>(x => Status.Text = x), indexRequest.Token); indexedFiles = index.Statistics.Files;
                if (session != null) session.Refreshed = DateTimeOffset.Now;
                // Progress reports were posted before this continuation, so they have already run: the idle line is last.
                Status.Text = IdleStatus();
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
    public static string DocumentCount(int count) => count == 1 ? "מסמך אחד" : $"{count} מסמכים";
    /// <summary>The header subtitle when nothing is running: what kind of chat this is and how many documents it answers from.</summary>
    private string IdleStatus()
    {
        var type = chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
        if (type == null || type.IsGeneral) return "שיחה חופשית עם AI";
        if (type.IsManaged) return "ידע ארגוני";
        if (type.Paths.Count == 0) return "מוכן · ללא תיקיות מסמכים";
        return indexedFiles is int files ? "מוכן · " + DocumentCount(files) : "מוכן";
    }
    /// <summary>"רגיל" uses the configured limits; "מעמיק" allows twice the evidence (more passages, more text), "מהיר" half of it.
    /// The steps themselves differ too: see <see cref="AnswerMode"/>.</summary>
    private Config AnswerConfig()
    {
        var mode = CurrentMode();
        var deep = mode == AnswerMode.Deep;
        if (mode == AnswerMode.Normal) return activeConfig;
        var d = activeConfig.Documents;
        return new()
        {
            GeneralChat = activeConfig.GeneralChat, AssistantPrompt = activeConfig.AssistantPrompt, Ai = activeConfig.Ai, Gateway = activeConfig.Gateway, History = activeConfig.History,
            Ui = activeConfig.Ui, Logging = activeConfig.Logging, AnswerPrompt = activeConfig.AnswerPrompt,
            Documents = new() { Root = d.Root, Roots = d.Roots, SearchSubfolders = d.SearchSubfolders, IntervalMinutes = d.IntervalMinutes, ChunkCharacters = d.ChunkCharacters, MaxFileMegabytes = d.MaxFileMegabytes,
                MaxPassages = deep ? Math.Min(30, d.MaxPassages * 2) : Math.Min(d.MaxPassages, Math.Max(3, d.MaxPassages / 2)),
                EvidenceCharacters = deep ? Math.Min(30000, d.EvidenceCharacters * 2) : Math.Min(d.EvidenceCharacters, Math.Max(1500, d.EvidenceCharacters / 2)), ExcerptCharacters = d.ExcerptCharacters }
        };
    }
    private async void Send(object s, RoutedEventArgs e)
    {
        if (!loggedIn || request != null || switchingType || indexing || (!BuildMode.Local && gateway == null) || string.IsNullOrWhiteSpace(Question.Text) || Orphaned)
            return;
        if (!sourcesReady) { Status.Text = "יש לרענן את המסמכים לפני שליחת הודעה (תפריט ⋯ · רענון המסמכים)."; return; }
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
                answer = await new ChatEngine(AnswerConfig(), ai, index) { Mode = CurrentMode() }.AskAsync(q, current, progress, request.Token);
            }
            else answer = await AskServerAsync(q, request.Token);
            Typing.Visibility = Visibility.Collapsed;
            current.Messages.Add(answer);
            Save();
            await RenderAsync();
            if (Status.Text.StartsWith("ממתין למכסת ה־API") || Status.Text is "מבין את הבקשה…" or "מכין תשובה…" or "מחפש מידע במסמכים…" or "בודק שהתשובה מתאימה לבקשה…" or "קורא את המסמך המתאים ביותר…")
                Status.Text = IdleStatus();
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
    /// <summary>A saved conversation whose assistant was deleted (and kept in the history): it can be read, not continued.</summary>
    private bool Orphaned => current.HasUserMessages && !chatTypes.Any(t => t.Id == current.ChatTypeId);
    private async Task RenderAsync()
    {
        // The folder check compares sources with the active assistant's folders; a kept conversation of a deleted assistant
        // has none to compare with. Access to every source is still checked below.
        if (activeConfig != null && !Orphaned && !chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged)) ChatType.RedactOutsideFolders(current, activeConfig.Documents);
        await Task.Run(() => ChatEngine.RedactInaccessible(current));
        Save();
        Messages.Children.Clear();
        Welcome.Visibility = current.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshWelcome();
        if (Orphaned)
        {
            Question.IsEnabled = false;
            QuestionHint.Text = "העוזר של השיחה נמחק · השיחה לקריאה בלבד";
            System.Windows.Automation.AutomationProperties.SetName(Question, QuestionHint.Text);
        }
        else Question.IsEnabled = true;
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
        // A new conversation reads from the top of its landing page; an ongoing one shows the latest message.
        if (current.Messages.Count == 0) Scroll.ScrollToTop(); else Scroll.ScrollToEnd();
    }
    private void QuestionKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send(s, new());
        }
    }
    private void UseSuggestion(string question)
    {
        if (request != null || indexing || switchingType) return;
        Question.Text = question;
        Send(this, new());
    }
    private void CreateAssistant(object s, RoutedEventArgs e) => OpenChatTypeDialog(null);
    /// <summary>The chosen answer mode shows its name next to its icon; the name slides open and the others fold to icons.</summary>
    private void ModeChanged(object s, RoutedEventArgs e)
    {
        if (NormalLabel == null || FastLabel == null || DeepLabel == null) return;
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 200 : 1);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (mode, label) in new[] { (NormalMode, NormalLabel), (FastMode, FastLabel), (DeepMode, DeepLabel) })
        {
            var open = mode.IsChecked == true;
            // Every label starts with a finite MaxWidth (70 or 0): WPF cannot animate from the default, infinity.
            label.Margin = new Thickness(open ? 6 : 0, 0, open ? 2 : 0, 0);
            label.BeginAnimation(MaxWidthProperty, new DoubleAnimation(open ? 70 : 0, duration) { EasingFunction = ease });
            label.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration));
        }
    }
    private AnswerMode CurrentMode() => activeConfig.GeneralChat ? AnswerMode.Normal : FastMode.IsChecked == true ? AnswerMode.Fast : DeepMode.IsChecked == true ? AnswerMode.Deep : AnswerMode.Normal;

    private Dictionary<string, TypeLook>? looks;
    private Dictionary<string, TypeLook> SavedLooks => looks ??= LoadLooks();
    private static Dictionary<string, TypeLook> LoadLooks()
    {
        var saved = Storage.Load<Dictionary<string, TypeLook>>("chat-looks.bin") ?? new();
        // Symbols saved as icon-font glyphs by earlier versions are translated (or dropped back to the default).
        return saved.Where(x => Looks.IsColor(x.Value.Color)).Select(x => (x.Key, Glyph: Looks.Migrate(x.Value.Glyph), x.Value.Color))
            .Where(x => x.Glyph != null).ToDictionary(x => x.Key, x => new TypeLook(x.Color, x.Glyph!));
    }
    private void SaveLooks() { try { Storage.Save("chat-looks.bin", SavedLooks); } catch { Status.Text = "לא ניתן לשמור את הבחירה במחשב זה."; } }
    private int? indexedFiles;
    private TypeLook LookOf(ChatType? type)
    {
        if (type != null && SavedLooks.TryGetValue(type.Id, out var saved)) return saved;
        var general = type == null || type.IsGeneral;
        var documents = !general && (type!.IsManaged || type.Paths.Count > 0);
        // A stable hash (string.GetHashCode changes between runs); blue stays reserved for general chat.
        var color = general ? "blue" : Looks.Palette[1 + type!.Id.Aggregate(0, (h, c) => (h * 31 + c) & 0x7FFFFFFF) % (Looks.Palette.Length - 1)].Key;
        return new(color, general ? "chat" : documents ? "folder" : "people");
    }
    /// <summary>A new assistant starts with a color no other assistant uses yet.</summary>
    private TypeLook NewAssistantLook()
    {
        var used = chatTypes.Where(t => !t.IsGeneral).Select(t => LookOf(t).Color).ToHashSet();
        var color = Looks.Palette.Skip(1).Select(c => c.Key).FirstOrDefault(c => !used.Contains(c)) ?? Looks.Palette[1 + chatTypes.Count % (Looks.Palette.Length - 1)].Key;
        return new(color, "folder");
    }
    private ChatType? ActiveType => chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
    private static bool HasDocuments(ChatType? type) => type != null && !type.IsGeneral && (type.IsManaged || type.Paths.Count > 0);
    /// <summary>The header, composer and landing page follow the active assistant's color and symbol.</summary>
    private void ApplyTypeLook()
    {
        var type = ActiveType;
        var look = LookOf(type);
        Application.Current.Resources["AssistantBrush"] = Looks.Brush(look.Color);
        Application.Current.Resources["AssistantSoftBrush"] = Looks.Tint(look.Color, 0.14);
        HeaderIcon.Kind = look.Glyph;
        ChatTypeName.Text = type?.Name ?? "כללי";
        var documents = HasDocuments(type) && type?.IsManaged != true;
        ModeSwitch.Visibility = documents ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(QuestionArea, documents ? 2 : 1);
        Grid.SetRow(SendArea, documents ? 1 : 0);
        QuestionHint.Text = HasDocuments(type) ? "שאלו על המסמכים…" : "כתבו הודעה…";
        System.Windows.Automation.AutomationProperties.SetName(Question, QuestionHint.Text.TrimEnd('…'));
        Disclaimer.Text = HasDocuments(type) ? "Chaty עלול לטעות. כדאי לבדוק מידע חשוב במקור." : "Chaty עלול לטעות. כדאי לבדוק מידע חשוב.";
    }
    private void RefreshWelcome()
    {
        ApplyTypeLook();
        var type = ActiveType;
        var general = type == null || type.IsGeneral;
        var documents = HasDocuments(type);
        WelcomeTile.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        WelcomeTitle.Text = general ? "במה אפשר לעזור?" : "שלום שוב, במה אפשר לעזור?";
        WelcomeText.Text = general ? "שאלות, כתיבה, תרגום וסיכומים. בצ'אט הזה התשובות לא מבוססות על המסמכים שלכם."
            : type!.IsManaged ? "התשובות מבוססות על מאגר הידע הארגוני, וכל תשובה כוללת הפניה למסמך המקור."
            : documents ? "התשובות מבוססות על התיקיות המחוברות בלבד, וכל תשובה כוללת הפניה למסמך המקור."
            : "לעוזר הזה אין עדיין תיקיות מסמכים, לכן הוא עונה כמו שיחה חופשית, לפי ההנחיות שלו.";
        // Connected folders: read-only tags that open the folder in Explorer. All of them wrap; nothing scrolls sideways.
        WelcomeSources.Children.Clear();
        if (!general && !type!.IsManaged) foreach (var path in type.Paths) WelcomeSources.Children.Add(FolderTag(path));
        WelcomeSources.Visibility = WelcomeSources.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Assistants show their last questions; general chat (and an assistant without history) suggests some.
        List<Conversation> recent = general ? [] : conversations.Where(c => c.ChatTypeId == activeTypeId && c.HasUserMessages).OrderByDescending(c => c.Messages.Last().Time).Take(3).ToList();
        RecentList.Children.Clear();
        for (int i = 0; i < recent.Count; i++) RecentList.Children.Add(RecentRow(recent[i], i, recent.Count));
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Suggestions.Children.Clear();
        if (recent.Count == 0)
            foreach (var question in SuggestedQuestions(documents))
            {
                var button = new Button { Style = Ui.Style("PillButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), ToolTip = question,
                    Content = new TextBlock { Text = question, MaxWidth = 290, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = MessageView.DirectionOf(question) } };
                System.Windows.Automation.AutomationProperties.SetName(button, "שליחת השאלה: " + question);
                button.Click += (_, _) => UseSuggestion(question);
                Suggestions.Children.Add(button);
            }
        SuggestionSection.Visibility = Suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The invitation to create an assistant is only for people who don't have one yet.
        PromoCard.Visibility = general && personalAllowed && !chatTypes.Any(t => !t.IsGeneral && !t.IsManaged) ? Visibility.Visible : Visibility.Collapsed;
    }
    private Button FolderTag(string path)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon("folder", 16, Looks.Brush("orange")) { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = FolderName(path), FontSize = 13, FontWeight = FontWeights.Medium, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var tip = new StackPanel();
        tip.Children.Add(new TextBlock { Text = "פתיחה בסייר הקבצים", FontWeight = FontWeights.SemiBold });
        tip.Children.Add(new TextBlock { Text = path, FlowDirection = FlowDirection.LeftToRight, FontFamily = (FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 0, 0) });
        var button = new Button { Style = Ui.Style("PillButton"), Content = content, Padding = new Thickness(12, 6, 14, 6), Margin = new Thickness(3), ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(button, "פתיחת התיקייה " + FolderName(path));
        button.Click += async (_, _) =>
        {
            var (folder, file) = await Task.Run(() => (System.IO.Directory.Exists(path), System.IO.File.Exists(path)));
            try
            {
                if (folder) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
                else if (file) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                else Status.Text = "התיקייה לא נמצאה: " + FolderName(path);
            }
            catch { Status.Text = "לא ניתן לפתוח את התיקייה."; }
        };
        return button;
    }
    private Button RecentRow(Conversation conversation, int position, int count)
    {
        var time = conversation.Messages.Last().Time.ToLocalTime();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Icon("clock", 18, Theme.Brush("Muted"), 1.8) { VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = OneLine(conversation.Title), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var when = new TextBlock { Text = time.Date == DateTime.Today ? time.ToString("HH:mm") : time.Date == DateTime.Today.AddDays(-1) ? "אתמול" : time.ToString("dd/MM"), FontSize = 12.5, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(when, 2); grid.Children.Add(when);
        var row = new Button { Style = Ui.Style("RowButton"), Content = grid, MinHeight = 46, BorderBrush = Theme.Brush("Divider"), BorderThickness = new Thickness(0, position == 0 ? 0 : 1, 0, 0), ToolTip = conversation.Title.Length > 60 ? conversation.Title : null };
        Ui.SetCornerRadius(row, new CornerRadius(position == 0 ? 14 : 0, position == 0 ? 14 : 0, position == count - 1 ? 14 : 0, position == count - 1 ? 14 : 0));
        System.Windows.Automation.AutomationProperties.SetName(row, "פתיחת השיחה " + OneLine(conversation.Title));
        row.Click += async (_, _) => await OpenConversationAsync(conversation);
        return row;
    }
    private static string OneLine(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");

    private static readonly string[] GeneralExamples =
    [
        "נסחו לי מייל קצר לבקשת יום חופש", "מה זה VPN, בפשטות?", "תרגמו לאנגלית: אשמח לקבוע פגישה מחר", "איך כותבים נוסחת VLOOKUP באקסל?",
        "סכמו לי טקסט ארוך בשלוש נקודות", "תנו לי רעיונות לשם לפרויקט חדש", "איך כותבים קוד פשוט בפייתון?", "מה ההבדל בין RAM לאחסון?",
        "עזרו לי לנסח הודעת תודה לצוות", "איך מגדירים חתימה במייל ב-Outlook?"
    ];
    private static readonly string[] DocumentExamples = ["מה יש במאגר?", "סכמו לי את המסמך העיקרי", "מה הנוהל כשמשהו לא עובד?", "למי פונים בבעיה?"];
    /// <summary>Example questions: a few random ones for general chat, document-oriented ones for an assistant (never the user's own past questions).</summary>
    private static List<string> SuggestedQuestions(bool documents)
        => documents ? DocumentExamples.Take(3).ToList() : GeneralPick;
    /// <summary>Picked once per run, so the examples don't reshuffle while the user looks at them.</summary>
    private static readonly List<string> GeneralPick = GeneralExamples.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
}
