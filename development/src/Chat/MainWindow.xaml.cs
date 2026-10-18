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
    private DocumentIndex index = null!;

    private List<Conversation> conversations = [];
    private Conversation current = new();
    private readonly CancellationTokenSource lifetime = new();
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
        SendButton.IsEnabledChanged += (_, _) => { if (SendButton.IsEnabled && string.IsNullOrWhiteSpace(Question.Text) && pendingAttachment == null) SendButton.IsEnabled = false; };
        HistoryView.ConversationSelected += async selected => await OpenConversationAsync(selected);
        HistoryView.BackRequested += () => { ShowPage("chat"); Question.Focus(); };
        HistoryView.DeleteRequested = DeleteConversation;
        HistoryView.RestoreRequested = RestoreConversation;
        HistoryView.StateFor = StateOf;
        HistoryView.LookFor = conversation => { var look = LookOf(chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId)); return (Looks.Brush(look.Color), look.Glyph); };
        // The panel's children are square; one rounded clip keeps every screen inside the panel's corners.
        PanelRoot.SizeChanged += (_, e) => PanelRoot.Clip = new RectangleGeometry(new Rect(e.NewSize), 18, 18);
        // Messages are read-only text boxes, and a text box swallows the mouse wheel even when it has nothing to scroll:
        // the conversation itself scrolls wherever the pointer is (a wheel notch is three 16 px lines, as elsewhere in Windows).
        Scroll.PreviewMouseWheel += (_, e) => { Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset - e.Delta * 0.4); e.Handled = true; };
        // Every page's empty background takes the double-click (the header, chat, history, the assistant form, settings),
        // also when something inside has already handled the click.
        PanelRoot.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(BackgroundDoubleClick), true);
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
        SourceInitialized += (_, _) => RegisterOpenKey();
        SourceInitialized += (_, _) => tray ??= new TrayIcon("Chaty", ToggleFromTray,
            ("פתיחת הצ׳אט · " + HotKeyName, RestoreFromTaskbar), ("הסתרה", () => HideApp(this, new())), ("-", () => { }), ("יציאה", () => Application.Current.Shutdown()));
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
        try { await InitializeLocalAsync(); }
        catch (Exception ex) { Notice(ex.Message); SendButton.IsEnabled = false; ExpandPanel(this, new()); }
    }
    protected override void OnClosed(EventArgs e)
    {
        lifetime.Cancel();
        foreach (var token in running.Values) token.Cancel();
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
    /// <summary>A double-click on an empty part of any page restores the base size. Text, buttons and fields keep their own double-click.</summary>
    private void BackgroundDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || !expanded || tourLayer?.Visibility == Visibility.Visible) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != s; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ScrollBar or PasswordBox
                or System.Windows.Controls.Primitives.Selector or System.Windows.Controls.Primitives.RangeBase or System.Windows.Controls.Primitives.Thumb or System.Windows.Documents.Hyperlink) return;
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
        MarkSeen();
        SetLauncherGlyph(true);
        AnimatePanel(true);
        Question.Focus();
    }
    private void SetLauncherGlyph(bool open)
    {
        // While an answer is on its way the moving dots stay instead of the glyph.
        ChatGlyph.Visibility = open || thinking ? Visibility.Collapsed : Visibility.Visible;
        CloseGlyph.Visibility = open && !thinking ? Visibility.Visible : Visibility.Collapsed;
        Bubble.ToolTip = (open ? "סגירת הצ׳אט" : "פתיחת הצ׳אט") + " (" + HotKeyName + ")";
        if (SystemParameters.ClientAreaAnimation)
            GlyphTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, new DoubleAnimation(open ? -90 : 90, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void WindowKeyDown(object s, KeyEventArgs e)
    {
        // Ctrl+Shift+C copies the last answer of the conversation on screen.
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && expanded && ScreenLayer.Visibility != Visibility.Visible && HistoryView.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            if (current.Messages.LastOrDefault(m => m.Role == "assistant" && m.Text.Length > 0) is not { } last) { Notice("אין עדיין תשובה להעתקה.", false); return; }
            try { Clipboard.SetText(MessageView.PlainText(last.Text)); Notice("התשובה האחרונה הועתקה.", false); } catch { Notice("לא ניתן להעתיק את התשובה."); }
            return;
        }
        if (e.Key != Key.Escape || !expanded) return;
        // Escape closes the innermost layer first: a popover, an edit of the last question, then history; the forms handle their own Escape.
        if (TourOpen) EndTour();
        else if (SourcesLayer.Visibility == Visibility.Visible) CloseSources();
        else if (overlayKind != null) CloseOverlay();
        else if (editing != null && ScreenLayer.Visibility != Visibility.Visible && HistoryView.Visibility != Visibility.Visible) StopEditing(true);
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
        CloseOverlay(false); CloseSources(); EndTour();
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
        CloseOverlay(false); CloseSources();
        ScreenHost.Content = null;
        ScreenLayer.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderState();
        MarkSeen();
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
        catch { Notice("לא ניתן לשמור שיחה במחשב זה. בדקו מקום פנוי והרשאות."); return false; }
    }
    private async void NewChat(object s, RoutedEventArgs e)
    {
        CloseOverlay(false);
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
        if (HistoryView.Visibility == Visibility.Visible) { ShowPage("chat"); Question.Focus(); return; }
        foreach (var conversation in conversations) conversation.ChatTypeName = chatTypes.FirstOrDefault(t => t.Id == conversation.ChatTypeId)?.Name ?? conversation.ChatTypeName;
        HistoryView.Refresh(conversations);
        ShowPage("history");
        _ = TitleOldConversationsAsync();
        HistoryView.FocusStart();
    }
    private async Task OpenConversationAsync(Conversation selected)
    {
        var owner = chatTypes.Any(t => t.Id == selected.ChatTypeId) ? selected.ChatTypeId : ChatType.GeneralId;
        if (owner != activeTypeId) await ActivateTypeAsync(owner, false);
        current = selected; Question.Clear(); ShowPage("chat"); await RenderAsync(); Question.Focus();
        // A conversation from before titles existed gets its short title now.
        if (selected.Topic.Length == 0 && selected.Messages.FirstOrDefault(m => m.Role == "user") is { } first) _ = NameConversationAsync(selected, first.Text, first.File?.Name);
    }
    private bool DeleteConversation(Conversation conversation)
    {
        var position = conversations.IndexOf(conversation);
        if (position < 0 || running.ContainsKey(conversation)) return false;
        unseen.Remove(conversation); UpdateIndicators();
        conversations.RemoveAt(position);
        if (!Save()) { conversations.Insert(position, conversation); return false; }
        deleted = (conversation, position);
        if (current.Id == conversation.Id) { current = new() { ChatTypeId = activeTypeId }; StopEditing(false); Question.Clear(); Messages.Children.Clear(); Welcome.Visibility = Visibility.Visible; }
        RefreshWelcome();
        return true;
    }
    /// <summary>The last deleted conversation and its place, for "ביטול" in the history.</summary>
    private (Conversation Conversation, int Position)? deleted;
    private bool RestoreConversation(Conversation conversation)
    {
        if (deleted is not { } last || last.Conversation != conversation || conversations.Contains(conversation)) return false;
        conversations.Insert(Math.Min(last.Position, conversations.Count), conversation);
        if (!Save()) { conversations.Remove(conversation); return false; }
        deleted = null;
        RefreshWelcome();
        return true;
    }
    private async void Reindex(object s, RoutedEventArgs e) => await IndexAsync(true);
    private Task? indexTask;
    // Set while switching assistants stops the previous one's refresh: that is not a cancellation to report.
    private bool quietCancel;
    // A question sent while the documents were loading goes out as soon as they are ready.
    private bool sendWhenReady;
    private async Task IndexAsync(bool force)
    {
        if (switchingType || indexing) return;
        var task = indexTask = IndexCoreAsync(force);
        try { await task; }
        finally { if (indexTask == task) indexTask = null; }
        // The landing page orders folders by how many documents they hold: now it knows.
        if (Welcome.Visibility == Visibility.Visible && !switchingType) RefreshWelcome();
        if (sendWhenReady && sourcesReady && !Busy && !switchingType) { sendWhenReady = false; Send(this, new()); }
    }
    private async Task IndexCoreAsync(bool force)
    {
        if (activeConfig?.GeneralChat == true) { sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now; Status.Text = IdleStatus(); return; }
        if (indexing || index == null)
            return;
        // Refreshed a moment ago in this session (or by the background preload): no need to rescan the folders.
        if (!force && activeSession?.Refreshed is { } refreshed && DateTimeOffset.Now - refreshed < SessionRefreshInterval)
        {
            sourcesReady = true; lastSourceRefresh = refreshed; indexedFiles = index.Statistics.Files; RememberCount(index.Statistics.Files);
            Status.Text = IdleStatus();
            statusDetail = WarningsText();
            return;
        }
        indexing = true; sourcesReady = false; statusDetail = null;
        // A refresh the user asked for can be stopped from the send button; the automatic one runs quietly in the
        // background (its progress is in the header) and stops by itself when another assistant is chosen.
        if (force) CancelButton.Visibility = Visibility.Visible;
        indexRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            UpdateSendState();
            var session = activeSession;
            await index.RefreshAsync(force, new Progress<string>(x => Status.Text = x), indexRequest.Token); indexedFiles = index.Statistics.Files; RememberCount(index.Statistics.Files);
            if (session != null) session.Refreshed = DateTimeOffset.Now;
            // Progress reports were posted before this continuation, so they have already run: the idle line is last.
            Status.Text = IdleStatus();
            // Hovering the status shows which files were skipped and why.
            statusDetail = WarningsText();
            sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) { if (!quietCancel) { Status.Text = IdleStatus(); Notice("עדכון המאגר בוטל. אפשר לעדכן שוב מתפריט ⋯ · רענון המסמכים.", false); } }
        catch { Status.Text = IdleStatus(); Notice("לא ניתן לעדכן את המאגר. בדקו שהתיקיות קיימות ושיש הרשאת קריאה."); }
        finally { indexing = false; UpdateSendState(); indexRequest.Dispose(); indexRequest = null; if (!Busy) CancelButton.Visibility = Visibility.Collapsed; MemoryTrim.Schedule(Dispatcher, expanded ? IdleTrimSeconds : 1.5); }
    }
    private string? WarningsText() => index.Warnings.Count == 0 ? null : "קבצים שלא נכללו:\n" + string.Join("\n", index.Warnings.Take(12)) + (index.Warnings.Count > 12 ? $"\nועוד {index.Warnings.Count - 12}…" : "");
    public static string DocumentCount(int count) => count == 1 ? "מסמך אחד" : $"{count} מסמכים";
    /// <summary>The header subtitle when nothing is running: what kind of chat this is and how many documents it answers from.</summary>
    private string IdleStatus()
    {
        var type = chatTypes.FirstOrDefault(t => t.Id == activeTypeId);
        // Once the conversation has a topic, the topic is the title and this line names the kind of chat under it.
        if (current?.HasUserMessages == true)
            return type == null || type.IsGeneral ? "" : type.Name + (HasDocuments(type) && indexedFiles is int count ? " · " + DocumentCount(count) : "");
        if (type == null || type.IsGeneral) return "שיחה חופשית עם AI";
        if (type.Paths.Count == 0) return "מוכן · ללא תיקיות מסמכים";
        return indexedFiles is int files ? "מוכן · " + DocumentCount(files) : "מוכן";
    }
    /// <summary>"רגיל" uses the configured limits; "מעמיק" and "חיפוש מקסימלי" allow twice the evidence (more passages, more text), "מהיר" half of it.
    /// The steps themselves differ too: see <see cref="AnswerMode"/>. The model can differ as well: see <see cref="LocalAiCatalog.ModelFor"/>.</summary>
    private Config AnswerConfig()
    {
        var mode = CurrentMode();
        var deep = mode is AnswerMode.Deep or AnswerMode.Maximum;
        if (mode == AnswerMode.Normal) return activeConfig;
        var ai = activeConfig.Ai;
        var catalog = LocalAiCatalog.Load();
        // Maximum search keeps the chosen model: its extra calls (rewording, choosing passages) need no thinking model.
        var model = catalog.ModelFor(ai.Model, mode == AnswerMode.Maximum ? AnswerMode.Normal : mode);
        if (model != ai.Model) ai = catalog.Apply(activeConfig, model).Ai;
        var d = activeConfig.Documents;
        return new()
        {
            GeneralChat = activeConfig.GeneralChat, AssistantPrompt = activeConfig.AssistantPrompt, Ai = ai, History = activeConfig.History,
            Ui = activeConfig.Ui, Logging = activeConfig.Logging, AnswerPrompt = activeConfig.AnswerPrompt,
            Documents = new() { Root = d.Root, Roots = d.Roots, SearchSubfolders = d.SearchSubfolders, IntervalMinutes = d.IntervalMinutes, ChunkCharacters = d.ChunkCharacters, MaxFileMegabytes = d.MaxFileMegabytes,
                MaxPassages = deep ? Math.Min(30, d.MaxPassages * 2) : Math.Min(d.MaxPassages, Math.Max(3, d.MaxPassages / 2)),
                EvidenceCharacters = deep ? Math.Min(30000, d.EvidenceCharacters * 2) : Math.Min(d.EvidenceCharacters, Math.Max(1500, d.EvidenceCharacters / 2)), ExcerptCharacters = d.ExcerptCharacters }
        };
    }
    private async void Send(object s, RoutedEventArgs e)
    {
        if (Busy || string.IsNullOrWhiteSpace(Question.Text) && pendingAttachment == null || Orphaned)
            return;
        if (switchingType || indexing) { sendWhenReady = true; Notice("המסמכים עדיין נטענים. ההודעה תישלח מיד כשהטעינה תסתיים.", false); return; }
        if (!sourcesReady) { Notice("יש לרענן את המסמכים לפני שליחת הודעה (תפריט ⋯ · רענון המסמכים)."); return; }
        if (LocalAiCatalog.Load().Models.Count == 0) { Notice("אין חיבור AI: קובץ ההגדרות חסר או לא תקין. הפרטים בתפריט ⋯ · הגדרות AI."); return; }
        var file = pendingAttachment;
        var q = Question.Text.Trim();
        if (q.Length == 0) q = "סכמו לי את המסמך המצורף.";
        if (q.Length > config.History.MessageCharacters)
        {
            Notice($"ההודעה ארוכה מדי: עד {config.History.MessageCharacters:N0} תווים. אפשר לקצר, או לצרף את הטקסט כקובץ.");
            return;
        }
        Question.Clear();
        CloseNotice(this, new());
        // An edited question replaces the old one and everything after it (its answer).
        if (editing != null && current.Messages.IndexOf(editing) is var at and >= 0)
        {
            current.Messages.RemoveRange(at, current.Messages.Count - at);
            if (current.SummarizedCount > at) { current.Summary = ""; current.SummarizedCount = 0; }
        }
        StopEditing(false);
        current.ChatTypeName = chatTypes.First(t => t.Id == activeTypeId).Name;
        current.Messages.Add(new("user", q, DateTimeOffset.Now, []) { File = file, Mode = CurrentMode().ToString() });
        ClearAttachment();
        if (!conversations.Any(c => c.Id == current.Id)) conversations.Add(current);
        await AnswerAsync(q, file);
    }
    /// <summary>Answers being prepared, one per conversation: while one works, another chat can be opened and asked.</summary>
    private readonly Dictionary<Conversation, CancellationTokenSource> running = [];
    /// <summary>The step each running conversation is at, for the header when it is on screen.</summary>
    private readonly Dictionary<Conversation, string> steps = [];
    /// <summary>The conversation on screen is waiting for its answer (its send button is the stop button).</summary>
    private bool Busy => running.ContainsKey(current);
    /// <summary>Answers the conversation's last message, <paramref name="q"/>, in the background of whatever is on screen:
    /// the result goes to that conversation, and is marked new if it is not the one being looked at.</summary>
    private async Task AnswerAsync(string q, Attachment? file)
    {
        var conversation = current;
        var mode = CurrentMode();
        var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        token.CancelAfter(TimeSpan.FromSeconds(config.Ai.TimeoutSeconds * (mode == AnswerMode.Maximum ? 6 : 5) + 30));
        running[conversation] = token; steps[conversation] = "מבין את הבקשה…";
        bool OnScreen() => conversation == current;
        UpdateIndicators(); ShowBusy();
        try
        {
            Save();
            await RenderAsync();
            Scroll.UpdateLayout();
            Scroll.ScrollToEnd();
            // Steps ("…") are short and stay in the header; anything else the engine reports (a fallback, an AI error) is a notice.
            // Reports arrive posted, possibly after the answer: a finished conversation ignores a late step.
            IProgress<string> progress = new Progress<string>(x => { if (!running.ContainsKey(conversation)) return; if (x.EndsWith('…')) { steps[conversation] = x; if (OnScreen()) Status.Text = x; } else if (OnScreen()) Notice(x); });
            var answerConfig = AnswerConfig();
            using var ai = new AiClient(answerConfig);
            // The final answer is written on screen as it arrives (in every mode).
            IProgress<string> partial = new Progress<string>(text => ShowPartial(conversation, text));
            var engine = new ChatEngine(answerConfig, ai, index) { Mode = mode, Partial = partial.Report };
            var answer = await engine.AskAsync(q, conversation, progress, token.Token);
            // Maximum search is for one hard search: after its answer the next question goes back to normal.
            if (mode == AnswerMode.Maximum && OnScreen()) SetMaxSearch(false);
            conversation.Messages.Add(answer);
            Save();
            MarkArrived(conversation);
            // The first answer is in: a few words that sum up the request become the conversation's title (in the background).
            if (conversation.Topic.Length == 0 && conversation.Messages.Count(m => m.Role == "user") == 1) _ = NameConversationAsync(conversation, q, file?.Name);
        }
        catch (OperationCanceledException) { if (OnScreen()) Notice("הבקשה בוטלה או עברה את הזמן המותר.", false); }
        // The server's request limit is the only one: say so in the conversation, with when to send again.
        catch (RateLimitException ex) { conversation.Messages.Add(new("assistant", ex.Message, DateTimeOffset.Now, [])); Save(); MarkArrived(conversation); }
        catch (Exception ex)
        {
            Storage.Log(config, "chat_failed");
            // On screen a notice; a conversation in the background keeps the failure as its answer, so it is not lost.
            // No key yet: the notice links to the AI settings, where it is typed.
            if (OnScreen()) Notice("הבקשה נכשלה: " + AiErrors.Describe(ex), true, ex is MissingKeyException ? ("לחץ כאן להגדרה", () => _ = OpenLocalSettingsAsync()) : null);
            else { conversation.Messages.Add(new("assistant", "הבקשה נכשלה: " + AiErrors.Describe(ex), DateTimeOffset.Now, [])); Save(); MarkArrived(conversation); }
        }
        finally
        {
            running.Remove(conversation); steps.Remove(conversation); token.Dispose();
            var streamed = partials.Remove(conversation);
            UpdateIndicators();
            // A streamed answer is already on screen: it is not faded in again.
            if (OnScreen()) await RenderAsync(!streamed); // also clears the step from the header (ShowBusy)
            MemoryTrim.Schedule(Dispatcher, IdleTrimSeconds);
        }
    }
    /// <summary>The waiting state of the conversation on screen: typing dots, the stop button instead of send, its step.</summary>
    private void ShowBusy()
    {
        var busy = Busy;
        // Once the answer is being written, the text itself replaces the typing dots.
        var dots = busy && !partials.ContainsKey(current);
        Typing.Visibility = dots ? Visibility.Visible : Visibility.Collapsed;
        typingAnimation?.Remove(this); typingAnimation = null;
        if (dots && SystemParameters.ClientAreaAnimation) { typingAnimation = ((Storyboard)Typing.Resources["TypingMotion"]).Clone(); typingAnimation.Begin(this, true); }
        CancelButton.Visibility = busy || indexing && indexRequest != null && CancelButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (busy) Status.Text = steps[current];
        else if (Status.Text.EndsWith('…') && !indexing && !switchingType) Status.Text = IdleStatus();
        UpdateSendState();
    }

    /// <summary>Conversations with an answer nobody has looked at yet: a count on the corner button and on
    /// the history button, a dot on their history row. Looking at the conversation clears it.</summary>
    private readonly HashSet<Conversation> unseen = [];
    private Storyboard? thinkingMotion;
    private bool thinking;
    /// <summary>The corner button shows three moving dots while an answer is on its way, with the chat open or closed.</summary>
    private void SetThinking(bool on)
    {
        if (thinking == on) return;
        thinking = on;
        ThinkingDots.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ChatGlyph.Visibility = !on && !expanded ? Visibility.Visible : Visibility.Collapsed;
        CloseGlyph.Visibility = !on && expanded ? Visibility.Visible : Visibility.Collapsed;
        thinkingMotion?.Remove(this); thinkingMotion = null;
        if (on && SystemParameters.ClientAreaAnimation) { thinkingMotion = ((Storyboard)((FrameworkElement)ThinkingDots.Parent).Resources["ThinkingMotion"]).Clone(); thinkingMotion.Begin(this, true); }
        System.Windows.Automation.AutomationProperties.SetName(Bubble, on ? "Chaty מכין תשובה. פתיחה או סגירה של הצ׳אט" : "פתיחה או סגירה של הצ׳אט");
    }
    private bool LookingAt(Conversation conversation) => expanded && conversation == current && HistoryView.Visibility != Visibility.Visible && ScreenLayer.Visibility != Visibility.Visible;
    /// <summary>An answer arrived: shown at once when its conversation is on screen, otherwise marked new.</summary>
    private void MarkArrived(Conversation conversation)
    {
        if (!LookingAt(conversation)) unseen.Add(conversation);
        UpdateIndicators();
    }
    private void MarkSeen() { if (LookingAt(current) && unseen.Remove(current)) UpdateIndicators(); }
    private void UpdateIndicators()
    {
        SetThinking(running.Count > 0);
        var count = unseen.Count; var label = count > 9 ? "9+" : count.ToString();
        UnseenCount.Text = HistoryUnseenCount.Text = label;
        UnseenBadge.Visibility = HistoryUnseen.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var help = count == 0 ? "" : count == 1 ? "תשובה חדשה מחכה" : $"{count} שיחות עם תשובה חדשה";
        System.Windows.Automation.AutomationProperties.SetHelpText(Bubble, help); System.Windows.Automation.AutomationProperties.SetHelpText(HistoryTab, help);
        if (HistoryView.Visibility == Visibility.Visible) HistoryView.RefreshTitles();
    }
    /// <summary>For a history row: "running" while its answer is on the way, "ready" when one waits to be seen.</summary>
    private string StateOf(Conversation conversation) => running.ContainsKey(conversation) ? "running" : unseen.Contains(conversation) ? "ready" : "";    private System.Windows.Threading.DispatcherTimer? noticeTimer;
    /// <summary>A message in full above the composer: errors and guidance stay until closed or the next message is sent,
    /// information closes by itself. The header subtitle only ever holds the short summary or the current step.</summary>
    /// <summary><paramref name="action"/> adds a blue link at the end of the text, in the same font and size.</summary>
    private void Notice(string text, bool error = true, (string Label, Action Run)? action = null)
    {
        NoticeText.Inlines.Clear();
        NoticeText.Inlines.Add(new System.Windows.Documents.Run(text));
        if (action is { } link)
        {
            var hyperlink = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(link.Label)) { Foreground = Theme.Brush("Link"), TextDecorations = null, Cursor = Cursors.Hand };
            System.Windows.Automation.AutomationProperties.SetName(hyperlink, link.Label);
            hyperlink.Click += (_, _) => { CloseNotice(this, new()); link.Run(); };
            NoticeText.Inlines.Add(new System.Windows.Documents.Run(" "));
            NoticeText.Inlines.Add(hyperlink);
        }
        NoticeIcon.Kind = error ? "warning" : "info";
        NoticeIcon.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Theme.Brush(error ? "Danger" : "Secondary"));
        NoticeBar.Visibility = Visibility.Visible;
        noticeTimer?.Stop();
        if (!error)
        {
            noticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
            noticeTimer.Tick += (_, _) => { noticeTimer?.Stop(); NoticeBar.Visibility = Visibility.Collapsed; };
            noticeTimer.Start();
        }
    }
    private void CloseNotice(object s, RoutedEventArgs e) { noticeTimer?.Stop(); NoticeBar.Visibility = Visibility.Collapsed; }
    private Attachment? pendingAttachment;
    private async void AttachDocument(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "צירוף מסמך", Filter = "מסמכים|" + string.Join(";", DocumentIndex.Extensions.Select(x => "*" + x)) };
        if (dialog.ShowDialog(this) == true) await AttachAsync(dialog.FileName);
    }
    private async Task AttachAsync(string path)
    {
        if (Busy || AttachButton.Visibility != Visibility.Visible || !AttachButton.IsEnabled) return;
        AttachButton.IsEnabled = false; Status.Text = "קורא את הקובץ…";
        try
        {
            var file = await Task.Run(() => Attachments.Read(path, config.Documents.MaxFileMegabytes, lifetime.Token));
            pendingAttachment = file;
            AttachmentName.Text = file.Name; AttachmentBadge.Content = FileBadge.For(file.Name); AttachmentChip.ToolTip = path;
            AttachmentChip.Visibility = Visibility.Visible; UpdateSendState();
            var budget = Attachments.Budget((activeConfig ?? config).Ai);
            Status.Text = IdleStatus();
            if (file.Text.Length > budget) Notice($"הקובץ ארוך: ה־AI יקרא כ־{Math.Max(1, budget * 100L / file.Text.Length)}% הראשונים שלו.", false);
            Question.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = IdleStatus(); Notice(ex is InvalidOperationException ? ex.Message : "לא ניתן לקרוא את הקובץ. ודאו שהוא לא פתוח לעריכה או פגום."); }
        finally { AttachButton.IsEnabled = true; }
    }
    private void ClearAttachment() { pendingAttachment = null; AttachmentChip.Visibility = Visibility.Collapsed; AttachmentBadge.Content = null; UpdateSendState(); }
    private void RemoveAttachment(object s, RoutedEventArgs e) { ClearAttachment(); Question.Focus(); }
    private void ComposerDragOver(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = AttachButton.Visibility == Visibility.Visible ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }
    private async void ComposerDrop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        await AttachAsync(files[0]);
    }
    /// <summary>The stop button: the answer of the conversation on screen, otherwise a documents refresh.</summary>
    private void Cancel(object s, RoutedEventArgs e)
    {
        if (running.TryGetValue(current, out var token)) token.Cancel();
        else indexRequest?.Cancel();
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
    private async Task RenderAsync(bool animate = true)
    {
        // An edit belongs to the conversation it started in.
        if (editing != null && !current.Messages.Contains(editing)) StopEditing(false);
        // The conversation on screen brings its own state: its answer may still be on the way.
        ShowBusy(); MarkSeen();
        // The folder check compares sources with the active assistant's folders; a kept conversation of a deleted assistant
        // has none to compare with. Access to every source is still checked below.
        if (activeConfig != null && !Orphaned) ChatType.RedactOutsideFolders(current, activeConfig.Documents);
        await Task.Run(() => ChatEngine.RedactInaccessible(current));
        Save();
        Messages.Children.Clear(); liveMessage = null;
        Welcome.Visibility = current.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshWelcome();
        if (Orphaned)
        {
            Question.IsEnabled = false;
            QuestionHint.Text = "העוזר של השיחה נמחק · השיחה לקריאה בלבד";
            System.Windows.Automation.AutomationProperties.SetName(Question, QuestionHint.Text);
        }
        else Question.IsEnabled = true;
        // The last question can be edited (the pencil under it) while nothing is on its way.
        var editable = Busy || Orphaned ? null : current.Messages.LastOrDefault(m => m.Role == "user");
        foreach (var m in current.Messages)
        {
            var message = m.Role == "user" ? MessageView.User(m, m == editable ? () => BeginEdit(m) : null) : MessageView.Assistant(m, text => Notice(text));
            Messages.Children.Add(message);
            if (m == current.Messages.Last() && animate && SystemParameters.ClientAreaAnimation)
            {
                message.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var movement = new TranslateTransform();
                message.RenderTransform = movement;
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        // Back in a conversation whose answer is being written: the text so far.
        if (Busy && partials.ContainsKey(current)) RenderLive(false);
        Scroll.UpdateLayout();
        // A new conversation reads from the top of its landing page; an ongoing one shows the latest message.
        if (current.Messages.Count == 0) Scroll.ScrollToTop(); else Scroll.ScrollToEnd();
    }
    /// <summary>Answers being written, per conversation: the text so far, shown in place of the typing dots.</summary>
    private readonly Dictionary<Conversation, string> partials = [];
    private FrameworkElement? liveMessage;
    private DispatcherTimer? liveTimer;
    private void ShowPartial(Conversation conversation, string text)
    {
        // A report posted after the answer arrived is late: the answer itself is on screen.
        if (!running.ContainsKey(conversation)) return;
        if (text.Length == 0) partials.Remove(conversation); else partials[conversation] = text;
        if (conversation != current) return;
        // At most ~16 redraws a second, fewer for a long text: the markdown is laid out again each time.
        if (liveTimer == null) { liveTimer = new(); liveTimer.Tick += (_, _) => { liveTimer.Stop(); if (Busy) RenderLive(true); }; }
        // Changing the interval of a running timer restarts it: a fast stream would then never be drawn.
        if (!liveTimer.IsEnabled) { liveTimer.Interval = TimeSpan.FromMilliseconds(text.Length > 3000 ? 250 : 60); liveTimer.Start(); }
    }
    /// <summary>The answer being written, as the last message (without its time or copy button until it is done). The view
    /// follows the text only when it was already at the bottom, so reading further up is not interrupted.</summary>
    private void RenderLive(bool follow)
    {
        var atEnd = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24;
        if (liveMessage != null) { Messages.Children.Remove(liveMessage); liveMessage = null; }
        if (partials.TryGetValue(current, out var text))
        {
            liveMessage = MessageView.Assistant(new("assistant", text, DateTimeOffset.Now, []), _ => { }, true);
            Messages.Children.Add(liveMessage);
        }
        ShowBusy();
        if (follow && atEnd) { Scroll.UpdateLayout(); Scroll.ScrollToEnd(); }
    }
    /// <summary>The last question being edited in the composer; sending replaces it and what came after it.</summary>
    private ChatMessage? editing;
    private void BeginEdit(ChatMessage message)
    {
        if (Busy || !current.Messages.Contains(message)) return;
        editing = message;
        Question.Text = message.Text; Question.CaretIndex = Question.Text.Length;
        // Its document goes again with it (general chat); in an assistant there is no attach button and no document.
        if (message.File is { } file && AttachButton.Visibility == Visibility.Visible)
        {
            pendingAttachment = file; AttachmentName.Text = file.Name; AttachmentBadge.Content = FileBadge.For(file.Name); AttachmentChip.ToolTip = file.Name; AttachmentChip.Visibility = Visibility.Visible;
        }
        EditBar.Visibility = Visibility.Visible;
        UpdateSendState();
        Question.Focus();
    }
    private void CancelEdit(object s, RoutedEventArgs e) => StopEditing(true);
    /// <summary>Ends an edit. <paramref name="clear"/> (the X or Escape) also takes its text and document out of the composer;
    /// sending or leaving the conversation does not.</summary>
    private void StopEditing(bool clear)
    {
        if (editing is not { } was) return;
        editing = null;
        EditBar.Visibility = Visibility.Collapsed;
        if (!clear) return;
        if (Question.Text == was.Text) Question.Clear();
        if (was.File != null && pendingAttachment == was.File) ClearAttachment();
        Question.Focus();
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
        if (Busy) return;
        Question.Text = question;
        Send(this, new());
    }
    private void CreateAssistant(object s, RoutedEventArgs e) => OpenChatTypeDialog(null);
    /// <summary>The chosen answer mode (or maximum search) shows its name next to its icon; the name slides open and the
    /// others fold to icons, so the row stays short.</summary>
    private void ModeChanged(object s, RoutedEventArgs e)
    {
        if (NormalLabel == null || FastLabel == null || DeepLabel == null || MaxSearchButton == null || MaxLabel == null) return;
        // Choosing fast, normal or deep ends maximum search.
        if (s is RadioButton { IsChecked: true } && MaxSearchButton.IsChecked == true) { MaxSearchButton.IsChecked = false; MaxSearchNotice.Visibility = Visibility.Collapsed; }
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 200 : 1);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (mode, label) in new (System.Windows.Controls.Primitives.ToggleButton, TextBlock)[] { (NormalMode, NormalLabel), (FastMode, FastLabel), (DeepMode, DeepLabel), (MaxSearchButton, MaxLabel) })
        {
            var open = mode.IsChecked == true;
            // Every label starts with a finite MaxWidth (70 or 0): WPF cannot animate from the default, infinity.
            label.Margin = new Thickness(open ? 6 : 0, 0, open ? 2 : 0, 0);
            label.BeginAnimation(MaxWidthProperty, new DoubleAnimation(open ? 70 : 0, duration) { EasingFunction = ease });
            label.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration));
        }
    }
    private AnswerMode CurrentMode() => ModeSwitch.Visibility != Visibility.Visible ? AnswerMode.Normal
        : MaxSearchButton.Visibility == Visibility.Visible && MaxSearchButton.IsChecked == true ? AnswerMode.Maximum
        : FastMode.IsChecked == true ? AnswerMode.Fast : DeepMode.IsChecked == true ? AnswerMode.Deep : AnswerMode.Normal;
    private void MaxSearchClicked(object s, RoutedEventArgs e) => SetMaxSearch(MaxSearchButton.IsChecked == true);
    private void LeaveMaxSearch(object s, RoutedEventArgs e) { SetMaxSearch(false); Question.Focus(); }
    private void HideMaxSearchNotice(object s, RoutedEventArgs e) { MaxSearchNotice.Visibility = Visibility.Collapsed; Question.Focus(); }
    /// <summary>Maximum search instead of fast / normal / deep: on, none of them is chosen and the orange cost note shows;
    /// off, "רגיל" is chosen again (unless another mode was just picked).</summary>
    private void SetMaxSearch(bool on)
    {
        MaxSearchButton.IsChecked = on;
        MaxSearchNotice.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) FastMode.IsChecked = NormalMode.IsChecked = DeepMode.IsChecked = false;
        else if (FastMode.IsChecked != true && DeepMode.IsChecked != true) NormalMode.IsChecked = true;
        ModeChanged(this, new());
    }

    private Dictionary<string, TypeLook>? looks;
    private Dictionary<string, TypeLook> SavedLooks => looks ??= LoadLooks();
    private static Dictionary<string, TypeLook> LoadLooks()
    {
        var saved = Storage.Load<Dictionary<string, TypeLook>>("chat-looks.bin") ?? new();
        // Symbols saved as icon-font glyphs by earlier versions are translated (or dropped back to the default).
        return saved.Where(x => Looks.IsColor(x.Value.Color)).Select(x => (x.Key, Glyph: Looks.Migrate(x.Value.Glyph), x.Value.Color))
            .Where(x => x.Glyph != null).ToDictionary(x => x.Key, x => new TypeLook(x.Color, x.Glyph!));
    }
    private void SaveLooks() { try { Storage.Save("chat-looks.bin", SavedLooks); } catch { Notice("לא ניתן לשמור את הבחירה במחשב זה."); } }
    private int? indexedFiles;
    private TypeLook LookOf(ChatType? type)
    {
        if (type != null && SavedLooks.TryGetValue(type.Id, out var saved)) return saved;
        var general = type == null || type.IsGeneral;
        var documents = !general && type!.Paths.Count > 0;
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
    private static bool HasDocuments(ChatType? type) => type != null && !type.IsGeneral && type.Paths.Count > 0;
    /// <summary>The header, composer and landing page follow the active assistant's color and symbol.</summary>
    private void ApplyTypeLook()
    {
        var type = ActiveType;
        var look = LookOf(type);
        Application.Current.Resources["AssistantBrush"] = Looks.Brush(look.Color);
        Application.Current.Resources["AssistantSoftBrush"] = Looks.Tint(look.Color, 0.14);
        HeaderIcon.Kind = look.Glyph;
        ShowHeaderTitle();
        // Fast / normal / deep: for assistants with folders, and in the general chat (short answer at once, or a
        // thorough one, with a thinking model when the configuration has one).
        var modes = type == null || type.IsGeneral || HasDocuments(type);
        ModeSwitch.Visibility = modes ? Visibility.Visible : Visibility.Collapsed;
        // Maximum search looks through documents, so only assistants with their own folders offer it.
        var maximum = HasDocuments(type);
        MaxSearchButton.Visibility = maximum ? Visibility.Visible : Visibility.Collapsed;
        if (!maximum && MaxSearchButton.IsChecked == true) SetMaxSearch(false);
        Grid.SetColumnSpan(QuestionArea, modes ? 3 : 1);
        // Attaching a document is part of the general chat only; assistants answer from their own folders.
        var attach = type == null || type.IsGeneral;
        AttachButton.Visibility = attach ? Visibility.Visible : Visibility.Collapsed;
        if (!attach) ClearAttachment();
        Grid.SetRow(SendArea, modes ? 1 : 0); Grid.SetRow(AttachButton, modes ? 1 : 0);
        QuestionHint.Text = HasDocuments(type) ? "שאלו על המסמכים…" : "כתבו הודעה…";
        System.Windows.Automation.AutomationProperties.SetName(Question, QuestionHint.Text.TrimEnd('…'));
        Disclaimer.Text = HasDocuments(type) ? "Chaty עלול לטעות. כדאי לבדוק מידע חשוב במקור." : "Chaty עלול לטעות. כדאי לבדוק מידע חשוב.";
    }
    /// <summary>The header title: the assistant's name on a new chat, the conversation's topic (its first question) once it
    /// has one. The subtitle then names the assistant, unless it shows a step in progress.</summary>
    private void ShowHeaderTitle()
    {
        var type = ActiveType;
        var topic = current?.HasUserMessages == true ? current.ShortTitle : null;
        ChatTypeName.Text = topic ?? type?.Name ?? "כללי";
        // The whole first request, for whoever wants to read it.
        ChatTypeName.ToolTip = topic == null ? null : string.Join(' ', current!.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!Status.Text.EndsWith('…') && !indexing) Status.Text = IdleStatus();
    }
    /// <summary>One small call, with the model "מהיר" would use: the title is a few words, not worth a thinking model.
    /// If it fails (no connection, the server's limit) the first words of the question stay the title.</summary>
    private async Task<bool> NameConversationAsync(Conversation conversation, string question, string? attachment)
    {
        try
        {
            var catalog = LocalAiCatalog.Load();
            var options = catalog.Apply(activeConfig, catalog.ModelFor(activeConfig.Ai.Model, AnswerMode.Fast));
            using var ai = new AiClient(options);
            var topic = await ConversationTopic.SummarizeAsync(ai, question, attachment, lifetime.Token);
            if (topic == null || conversation.Topic.Length > 0) return topic != null;
            conversation.Topic = topic; Save();
            if (conversation == current) ShowHeaderTitle();
            return true;
        }
        catch { return false; }
    }
    private bool titling;
    /// <summary>Conversations from before titles existed get theirs when the history opens: the 10 newest, one call each,
    /// one after another, stopping at the first failure (no connection, the server's limit). Titles are saved, so once only.</summary>
    private async Task TitleOldConversationsAsync()
    {
        if (titling) return;
        titling = true;
        try
        {
            foreach (var conversation in conversations.Where(c => c.HasUserMessages && c.Topic.Length == 0).OrderByDescending(c => c.Messages.Last().Time).Take(10).ToList())
            {
                var first = conversation.Messages.First(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Text));
                if (!await NameConversationAsync(conversation, first.Text, first.File?.Name)) break;
                if (HistoryView.Visibility == Visibility.Visible) HistoryView.RefreshTitles();
            }
        }
        finally { titling = false; }
    }
    private void RefreshWelcome()
    {
        ApplyTypeLook();
        var type = ActiveType;
        var general = type == null || type.IsGeneral;
        var documents = HasDocuments(type);
        tileWanted = general;
        WelcomeTitle.Text = general ? "במה אפשר לעזור?" : "שלום שוב, במה אפשר לעזור?";
        WelcomeText.Text = general ? "שאלות, כתיבה, תרגום וסיכומים. בצ'אט הזה התשובות לא מבוססות על המסמכים שלכם."
            : documents ? "התשובות מבוססות על התיקיות המחוברות בלבד, וכל תשובה כוללת הפניה למסמך המקור."
            : "לעוזר הזה אין עדיין תיקיות מסמכים, לכן הוא עונה כמו שיחה חופשית, לפי ההנחיות שלו.";
        // Connected folders: read-only tags that open the folder in Explorer. All of them wrap; nothing scrolls sideways.
        // Up to 2 sources: their tags side by side. More: the first one (the folder with the most documents, when there is a
        // folder), then one tag with the counts of the rest ("3 📁 4 📄"); a click opens all of them in a short list that
        // floats over the chat (it pushes nothing down) and closes on a click outside.
        CloseSources();
        WelcomeSources.Children.Clear();
        if (!general)
        {
            var sources = OrderedSources(type!);
            if (sources.Count <= 2) foreach (var source in sources) WelcomeSources.Children.Add(FolderTag(source.Path, source.Document));
            else
            {
                WelcomeSources.Children.Add(FolderTag(sources[0].Path, sources[0].Document));
                var rest = sources.Skip(1).ToList();
                int folders = rest.Count(s => !s.Document), files = rest.Count - folders;
                var counts = new StackPanel { Orientation = Orientation.Horizontal };
                void Count(int count, string glyph)
                {
                    if (count == 0) return;
                    counts.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(counts.Children.Count == 0 ? 0 : 12, 0, 5, 0) });
                    counts.Children.Add(new Icon(glyph, 16, Looks.Brush("orange")) { VerticalAlignment = VerticalAlignment.Center });
                }
                Count(folders, "folder"); Count(files, "file");
                var chevron = new Icon("chevron-down", 14, Theme.Brush("Muted"), 2.2) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                counts.Children.Add(chevron);
                var summary = (folders > 0 ? folders == 1 ? "תיקייה אחת" : $"{folders} תיקיות" : "") + (folders > 0 && files > 0 ? " ו־" : "") + (files > 0 ? files == 1 ? "מסמך אחד" : $"{files} מסמכים" : "");
                var toggle = new Button { Style = Ui.Style("PillButton"), Content = counts, Padding = new Thickness(14, 6, 12, 6), Margin = new Thickness(3), ToolTip = "עוד " + summary };
                System.Windows.Automation.AutomationProperties.SetName(toggle, "הצגת כל המקורות: עוד " + summary);
                toggle.Click += (_, _) => OpenSources(toggle, chevron, sources);
                WelcomeSources.Children.Add(toggle);
            }
        }
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
        promoWanted = general && !chatTypes.Any(t => !t.IsGeneral);
        FitWelcome(); Dispatcher.BeginInvoke(FitWelcome, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private bool tileWanted, promoWanted;
    private Icon? sourcesChevron;
    /// <summary>Opens the list of all sources under the count tag, inside the chat, as wide as fits (at most 300).</summary>
    private void OpenSources(Button toggle, Icon chevron, List<(string Path, bool Document)> sources)
    {
        SourcesRows.Children.Clear();
        foreach (var source in sources) { var row = FolderTag(source.Path, source.Document, true); row.Click += (_, _) => CloseSources(); SourcesRows.Children.Add(row); }
        var below = toggle.TranslatePoint(new Point(0, toggle.ActualHeight), ChatPage);
        SourcesList.Width = Math.Min(300, ChatPage.ActualWidth - 32);
        SourcesList.Margin = new Thickness(16, below.Y + 6, 16, 0);
        SourcesScroll.MaxHeight = Math.Max(120, Math.Min(220, ChatPage.ActualHeight - below.Y - 24));
        SourcesScroll.ScrollToTop();
        SourcesLayer.Visibility = Visibility.Visible;
        sourcesChevron = chevron; chevron.Kind = "chevron-up";
        Dispatcher.BeginInvoke(() => SourcesRows.Children.OfType<Button>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void CloseSources()
    {
        if (SourcesLayer.Visibility != Visibility.Visible) return;
        SourcesLayer.Visibility = Visibility.Collapsed;
        if (sourcesChevron != null) sourcesChevron.Kind = "chevron-down";
        sourcesChevron = null;
    }
    /// <summary>A click beside the list (also on the tag that opened it) closes it.</summary>
    private void SourcesLayerClicked(object s, MouseButtonEventArgs e) { if (e.OriginalSource == SourcesLayer) { CloseSources(); e.Handled = true; } }
    /// <summary>An assistant's sources for the landing page: folders first, the one with the most documents (in the loaded index)
    /// first among them, then single documents.</summary>
    private List<(string Path, bool Document)> OrderedSources(ChatType type)
    {
        var files = index != null && type.Id == activeTypeId ? index.Files : [];
        int Count(string folder) { var prefix = folder.TrimEnd('\\') + "\\"; return files.Count(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); }
        return type.Paths.Select(p => (Path: p, Document: IsSingleDocument(p))).OrderBy(s => s.Document).ThenByDescending(s => s.Document ? 0 : Count(s.Path)).ToList();
    }
    private void ScrollSizeChanged(object sender, SizeChangedEventArgs e) { CloseSources(); FitWelcome(); }
    /// <summary>The landing page never scrolls: when it is taller than the chat area, the least needed parts are hidden in
    /// this order (the top icon, extra suggestions, the assistant invitation, extra recent questions) and return when there is room.</summary>
    private void FitWelcome()
    {
        WelcomeTile.Visibility = tileWanted ? Visibility.Visible : Visibility.Collapsed;
        PromoCard.Visibility = promoWanted ? Visibility.Visible : Visibility.Collapsed;
        foreach (UIElement item in Suggestions.Children) item.Visibility = Visibility.Visible;
        foreach (UIElement item in RecentList.Children) item.Visibility = Visibility.Visible;
        Welcome.MinHeight = 0;
        if (Welcome.Visibility != Visibility.Visible || Scroll.ViewportHeight <= 0 || Scroll.ViewportWidth <= 0) return;
        var room = Scroll.ViewportHeight - Welcome.Margin.Top - Welcome.Margin.Bottom;
        List<UIElement> optional = [];
        if (tileWanted) optional.Add(WelcomeTile);
        optional.AddRange(Suggestions.Children.Cast<UIElement>().Skip(1).Reverse());
        if (promoWanted) optional.Add(PromoCard);
        optional.AddRange(RecentList.Children.Cast<UIElement>().Skip(1).Reverse());
        foreach (var item in optional)
        {
            Welcome.UpdateLayout(); // a hidden child only re-measures its own panel; a full pass gives the real height
            if (Welcome.DesiredSize.Height <= Scroll.ViewportHeight) break;
            item.Visibility = Visibility.Collapsed;
        }
        Welcome.MinHeight = Math.Max(0, Math.Floor(room));
    }
    /// <summary>A source that is one document (not a folder): shown with a document icon. Decided by its extension only, never
    /// by asking the disk: an unreachable network share would freeze the window for many seconds.</summary>
    private static bool IsSingleDocument(string path) => Access.SafeDocument(path);
    /// <summary>A source as a pill tag, or (<paramref name="row"/>) as a row of the open list of all sources.</summary>
    private Button FolderTag(string path, bool single, bool row = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon(single ? "file" : "folder", 16, Looks.Brush("orange")) { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = FolderName(path), FontSize = 13, FontWeight = FontWeights.Medium, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var tip = new StackPanel();
        tip.Children.Add(new TextBlock { Text = "פתיחה בסייר הקבצים", FontWeight = FontWeights.SemiBold });
        tip.Children.Add(new TextBlock { Text = path, FlowDirection = FlowDirection.LeftToRight, FontFamily = (FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 2, 0, 0) });
        var button = row ? new Button { Style = Ui.Style("RowButton"), Content = content, Padding = new Thickness(10, 7, 10, 7), MinHeight = 36, HorizontalContentAlignment = HorizontalAlignment.Left, ToolTip = tip }
            : new Button { Style = Ui.Style("PillButton"), Content = content, Padding = new Thickness(12, 6, 14, 6), Margin = new Thickness(3), ToolTip = tip };
        if (row) Ui.SetCornerRadius(button, new CornerRadius(10));
        System.Windows.Automation.AutomationProperties.SetName(button, "פתיחת התיקייה " + FolderName(path));
        button.Click += async (_, _) =>
        {
            var (folder, file) = await Task.Run(() => (System.IO.Directory.Exists(path), System.IO.File.Exists(path)));
            try
            {
                if (folder) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
                else if (file) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                else Notice("התיקייה לא נמצאה: " + FolderName(path));
            }
            catch { Notice("לא ניתן לפתוח את התיקייה."); }
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
        var title = new TextBlock { Text = conversation.ShortTitle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var when = new TextBlock { Text = time.Date == DateTime.Today ? time.ToString("HH:mm") : time.Date == DateTime.Today.AddDays(-1) ? "אתמול" : time.ToString("dd/MM"), FontSize = 12.5, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(when, 2); grid.Children.Add(when);
        var row = new Button { Style = Ui.Style("RowButton"), Content = grid, MinHeight = 46, BorderBrush = Theme.Brush("Divider"), BorderThickness = new Thickness(0, position == 0 ? 0 : 1, 0, 0), ToolTip = OneLine(conversation.Title) };
        Ui.SetCornerRadius(row, new CornerRadius(position == 0 ? 14 : 0, position == 0 ? 14 : 0, position == count - 1 ? 14 : 0, position == count - 1 ? 14 : 0));
        System.Windows.Automation.AutomationProperties.SetName(row, "פתיחת השיחה " + conversation.ShortTitle);
        row.Click += async (_, _) => await OpenConversationAsync(conversation);
        return row;
    }
    private static string OneLine(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");

    // Clicking an example sends it at once, and general chat has no documents or attachments: each one must stand on its own.
    private static readonly string[] GeneralExamples =
    [
        "נסחו לי מייל קצר לבקשת יום חופש", "מה זה VPN, בפשטות?", "תרגמו לאנגלית: אשמח לקבוע פגישה מחר", "איך כותבים נוסחת VLOOKUP באקסל?",
        "איך מנהלים פגישה יעילה בחצי שעה?", "תנו לי רעיונות לשם לפרויקט חדש", "איך כותבים קוד פשוט בפייתון?", "מה ההבדל בין RAM לאחסון?",
        "עזרו לי לנסח הודעת תודה לצוות", "איך מגדירים חתימה במייל ב-Outlook?"
    ];
    private static readonly string[] DocumentExamples = ["מה יש במאגר?", "סכמו לי את המסמך העיקרי", "מה הנוהל כשמשהו לא עובד?", "למי פונים בבעיה?"];
    /// <summary>Example questions: a few random ones for general chat, document-oriented ones for an assistant (never the user's own past questions).</summary>
    private static List<string> SuggestedQuestions(bool documents)
        => documents ? DocumentExamples.Take(3).ToList() : GeneralPick;
    /// <summary>Picked once per run, so the examples don't reshuffle while the user looks at them.</summary>
    private static readonly List<string> GeneralPick = GeneralExamples.OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
}
