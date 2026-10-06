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
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
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
        trayIcon = CreateTrayIcon();
        tray = new()
        {
            Icon = trayIcon,
            Text = "עוזר הידע",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("הצג", null, (_, _) => Dispatcher.Invoke(RestoreFromTaskbar));
        menu.Items.Add("יציאה", null, (_, _) => Dispatcher.Invoke(() => { tray.Dispose(); Application.Current.Shutdown(); }));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTaskbar);
        Loaded += (_, _) => Anchor();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemParameters.StaticPropertyChanged += WorkAreaChanged;
        Closed += (_, _) => { SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemParameters.StaticPropertyChanged -= WorkAreaChanged; tray.Dispose(); trayIcon.Dispose(); };
    }
    private System.Drawing.Icon CreateTrayIcon()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawImage(Icon, new Rect(0, 0, 32, 32));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var drawing = new System.Drawing.Bitmap(stream);
        var handle = drawing.GetHicon();
        try { using var borrowed = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
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
        base.OnClosed(e);
    }
    private void Anchor()
    {
        var a = SystemParameters.WorkArea;
        Height = Math.Min(expanded ? 780 : 108, a.Height - 16);
        Width = Math.Min(expanded ? 428 : 100, a.Width - 16);
        Left = a.Right - Width - 8;
        Top = a.Bottom - Height - 8;
        if (expanded && a.Width < 480) { Width = a.Width; Height = a.Height; Left = a.Left; Top = a.Top; }
    }
    private void ExpandPanel(object s, RoutedEventArgs e)
    {
        panelAnimationVersion++;
        expanded = true;
        Anchor();
        Panel.Visibility = Visibility.Visible;
        Bubble.Visibility = Visibility.Collapsed; LauncherRow.Height = new GridLength(0);
        ChatGlyph.Visibility = Visibility.Collapsed;
        CloseGlyph.Visibility = Visibility.Visible;
        Bubble.ToolTip = "סגירת הצ׳אט לבועה";
        AnimatePanel(true);
        Question.Focus();
    }
    private void CollapsePanel(object s, RoutedEventArgs e)
    {
        if (!expanded) return;
        var version = ++panelAnimationVersion;
        expanded = false;
        ChatGlyph.Visibility = Visibility.Visible;
        CloseGlyph.Visibility = Visibility.Collapsed;
        Bubble.ToolTip = "פתיחת הצ׳אט";
        AnimatePanel(false, () =>
        {
            if (version != panelAnimationVersion) return;
            Panel.Visibility = Visibility.Collapsed;
            Bubble.Visibility = Visibility.Visible; LauncherRow.Height = new GridLength(72);
            Anchor();
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
    private void FocusChat(object s, RoutedEventArgs e) { ShowPage("chat"); Question.Focus(); }
    private void ShowPage(string page)
    {
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;

        var selected = Theme.Brush("Accent");
        var normal = Theme.Brush("Ink");
        ChatTab.Background = page == "chat" ? selected : Brushes.Transparent;
        ChatTab.Foreground = page == "chat" ? Brushes.White : normal;
        TabChatGlyph.Fill = ChatTab.Foreground;
        HistoryTab.Background = page == "history" ? selected : Brushes.Transparent;
        HistoryTab.Foreground = page == "history" ? Brushes.White : normal;
    }
    private void UseSuggestion(object s, RoutedEventArgs e)
    {
        if (s is Button button) Question.Text = button.Tag as string ?? button.Content?.ToString() ?? "";
        Question.Focus();
        Question.CaretIndex = Question.Text.Length;
    }
    private void ShowOptions(object s, RoutedEventArgs e)
    {
        var menu = CreateOptionsMenu(Reindex, HideApp);
        if (BuildMode.Local)
        {
            var settings = new MenuItem { Header = "⚙  הגדרות AI" };
            settings.Click += async (_, _) => await OpenLocalSettingsAsync();
            menu.Items.Insert(0, settings); menu.Items.Insert(1, new Separator());
        }
        var refresh = menu.Items.OfType<MenuItem>().FirstOrDefault(i => System.Windows.Automation.AutomationProperties.GetName(i) == "עדכון מאגר המסמכים");
        if (refresh != null) {
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = indexing ? "מעדכן מסמכים…" : "עדכון מאגר המסמכים" });
            label.Children.Add(new TextBlock { Text = lastSourceRefresh.HasValue ? "עודכן לפני " + Math.Max(0,(int)(DateTimeOffset.Now - lastSourceRefresh.Value).TotalMinutes) + " דקות" : "המקורות מתעדכנים בתחילת שיחה", FontSize = 12, Foreground = Theme.Brush("Muted") }); refresh.Header = label; refresh.IsEnabled = !indexing && request == null;
        }
        menu.PlacementTarget = s as UIElement;
        OpenStyledMenu(menu);
    }
    public static ContextMenu CreateOptionsMenu(RoutedEventHandler refreshAction, RoutedEventHandler hideAction)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("ChatMenu"), Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string title, string geometry, Brush color, RoutedEventHandler action)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.RightToLeft };
            header.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = color, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 18, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = title, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header, Foreground = color };
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            item.Click += action;
            return item;
        }
        menu.Items.Add(Item("עדכון מאגר המסמכים", "M 19,8 A 8,8 0 1 0 20,15 M 19,2 L 19,8 L 13,8", Brushes.SlateGray, refreshAction));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("הסתר לשורת המשימות", "M 3,21 L 3,3 L 12,1 L 12,23 L 3,21 M 12,3 L 19,3 L 19,9 M 19,19 L 19,21 L 12,21 M 8,12 L 8,14 M 15,14 L 24,14 M 21,11 L 24,14 L 21,17", Brushes.SlateGray, hideAction));
        return menu;
    }
    private void HideApp(object s, RoutedEventArgs e)
    {
        // Keep a visible taskbar button instead of relying on the tray overflow menu.
        WindowState = WindowState.Minimized;
    }
    private void RestoreFromTaskbar()
    {
        Show();
        WindowState = WindowState.Normal;
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
        if (!chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged) && activeConfig?.GeneralChat == true) { sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now; Status.Text = "AI · ללא מקורות מסמכים"; return; }
        if (indexing || index == null)
            return;
        indexing = true; sourcesReady = false;
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
            else await index.RefreshAsync(force, new Progress<string>(x => Status.Text = x), indexRequest.Token);
            sourcesReady = true; lastSourceRefresh = DateTimeOffset.Now;
        }
        catch (OperationCanceledException) { Status.Text = "עדכון המאגר בוטל."; }
        catch { Status.Text = "לא ניתן לעדכן את המאגר. בדקו נתיב והרשאות."; }
        finally { indexing = false; SendButton.IsEnabled = NewChatButton.IsEnabled = request == null; indexRequest.Dispose(); indexRequest = null; if (request == null) CancelButton.Visibility = Visibility.Collapsed; }
    }
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
            if (Status.Text.StartsWith("ממתין למכסת ה־API") || Status.Text is "מבין את הבקשה…" or "מחפש מידע במסמכים…" or "בודק שהתשובה מתאימה לבקשה…")
                Status.Text = "מוכן";
        }
        catch (OperationCanceledException) { Status.Text = "הבקשה בוטלה או עברה את הזמן המותר."; }
        catch (Exception ex) { Status.Text = "הבקשה נכשלה: " + ex.Message; Storage.Log(config, "chat_failed"); }
        finally { typingAnimation?.Remove(this); typingAnimation = null; Typing.Visibility = Visibility.Collapsed; request.Dispose(); request = null; SendButton.IsEnabled = true; HistoryTab.IsEnabled = NewChatButton.IsEnabled = true; if (!indexing) CancelButton.Visibility = Visibility.Collapsed; }
    }
    private void Cancel(object s, RoutedEventArgs e)
    {
        request?.Cancel();
        indexRequest?.Cancel();
    }
    public static WrapPanel CreateSourceTags(IEnumerable<Source> sources, Action<string> report)
    {
            var tags = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var source in sources.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
            {
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = "▤  " + System.IO.Path.GetFileName(source.Path), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 12 });
                label.Children.Add(new TextBlock { Text = source.Path, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 3, 0, 0) });
                var b = new Button { Content = label, MaxWidth = 280, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 5, 5), Background = Theme.Brush("Surface"), BorderBrush = new SolidColorBrush(Color.FromRgb(211, 225, 244)), BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Right, Cursor = System.Windows.Input.Cursors.Hand };
                System.Windows.Automation.AutomationProperties.SetName(b, "פתיחת " + System.IO.Path.GetFileName(source.Path));
                b.Click += async (_, _) => { if (!Access.SafeDocument(source.Path) || !await Task.Run(() => Access.CanRead(source.Path))) { report("המקור אינו נגיש או מסוג שאינו נתמך."); return; } try { Process.Start(new ProcessStartInfo(source.Path) { UseShellExecute = true }); } catch { report("לא ניתן לפתוח את המסמך."); } };
                tags.Children.Add(b);
            }
            return tags;
    }
    public static TextBox CreateMessageText(string text, bool isUser) => new()
    {
        Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        // WPF mirrors this alignment in the RTL text editor; visually this is the right edge.
        TextAlignment = TextAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch, FlowDirection = FlowDirection.RightToLeft,
        Foreground = isUser ? Brushes.White : Theme.Brush("Ink")
    };
    private async Task RenderAsync()
    {
        if (activeConfig != null && !chatTypes.Any(t => t.Id == activeTypeId && t.IsManaged)) ChatType.RedactOutsideFolders(current, activeConfig.Documents);
        await Task.Run(() => ChatEngine.RedactInaccessible(current));
        Save();
        Messages.Children.Clear();
        Welcome.Visibility = Visibility.Visible;
        RefreshSuggestions();
        foreach (var m in current.Messages)
        {
            var isUser = m.Role == "user";
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = (isUser ? "אתם" : "עוזר הידע") + " · " + m.Time.ToLocalTime().ToString("HH:mm"), Foreground = isUser ? new SolidColorBrush(Color.FromRgb(218, 233, 255)) : Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
            stack.Children.Add(CreateMessageText(m.Text, isUser));
            if (m.Sources.Count > 0) stack.Children.Add(CreateSourceTags(m.Sources, text => Status.Text = text));
            var message = new Border { Child = stack, Background = isUser ? Theme.Brush("Accent") : Theme.Brush("Subtle"), CornerRadius = isUser ? new CornerRadius(16, 16, 16, 4) : new CornerRadius(16, 16, 4, 16), Padding = new Thickness(14, 11, 14, 11), Margin = isUser ? new Thickness(26, 4, 0, 10) : new Thickness(0, 4, 18, 10) };
            Messages.Children.Add(message);
            if (m == current.Messages.Last() && SystemParameters.ClientAreaAnimation)
            {
                message.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var movement = new TranslateTransform();
                message.RenderTransform = movement;
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
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
        Suggestions.Children.Clear();
        if (current.Messages.Count == 0)
            foreach (var question in FrequentQuestions.Select(conversations.Where(c => c.ChatTypeId == activeTypeId)).Concat(new[] { "מה מגבלת ההעלאה?", "מה שם רשת המעבדה?", "כמה מקום פנוי נדרש להורדת אפליקציה?" }).Distinct().Take(3))
            {
                var button = new Button { Style = (Style)FindResource("Suggestion"), Tag = question, MaxWidth = 340,
                    Content = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right } };
                System.Windows.Automation.AutomationProperties.SetName(button, question);
                button.Click += UseSuggestion;
                Suggestions.Children.Add(button);
            }
        Suggestions.Visibility = Suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}



