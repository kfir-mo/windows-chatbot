using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        Directory.CreateDirectory("validation");
        var app = new Chat.App();
        app.InitializeComponent();

        System.Threading.SynchronizationContext.SetSynchronizationContext(new SmokeContext());
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var chat = new Chat.MainWindow();
        ((Border)chat.FindName("Panel")).Visibility = Visibility.Visible;
        ((System.Windows.Shapes.Path)chat.FindName("ChatGlyph")).Visibility = Visibility.Collapsed;
        ((System.Windows.Shapes.Path)chat.FindName("CloseGlyph")).Visibility = Visibility.Visible;
        ((TextBlock)chat.FindName("Status")).Text = "המאגר מוכן · 500 מסמכים";
        Render(chat,"login.png",428,780);
        ((Border)chat.FindName("LoginOverlay")).Visibility=Visibility.Collapsed;
        typeof(Chat.MainWindow).GetField("loggedIn",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(chat,true);
        Render(chat,"chat.png",428,780);
        var types = new Chat.ChatTypesPage(new Knowledge.Core.ChatType { Id = "demo", Name = "מערכת משאבי אנוש", Paths = [@"\\server\docs\HR", @"\\server\guides\Employees"] });
        Render(types,"chat-types.png",480,610);
        types.Close();
        // Check real local access and failure/retry with fake persistence only.
        var probe = new Chat.ChatTypesPage(new Knowledge.Core.ChatType { Name = "בדיקת מקורות", Paths = [Environment.CurrentDirectory, Path.Combine(Environment.CurrentDirectory, Guid.NewGuid().ToString("N"))] });
        var saved = 0;
        probe.SaveRequested = type => { saved++; if (type.Paths.Count != 1) throw new Exception("Duplicate paths must be merged."); return Task.CompletedTask; };
        Pump(probe.CheckPathAsync(0));
        Pump(probe.CheckPathAsync(1));
        var probeRows = (StackPanel)probe.FindName("PathRows");
        var firstRow = (Grid)((Border)probeRows.Children[0]).Child;
        var secondRow = (Grid)((Border)probeRows.Children[1]).Child;
        if (saved != 0 || ((TextBlock)firstRow.Children[1]).Text != "✓ עבר" || ((TextBlock)secondRow.Children[1]).Text != "✕ נכשל") throw new Exception("Each path needs its own result; failure must prevent saving.");
        Render(probe,"chat-types-validation.png",480,640);
        ((TextBox)secondRow.Children[0]).Text = Environment.CurrentDirectory;
        Pump(probe.SaveAsync());
        if (saved != 1) throw new Exception("Successful retry should save once.");
        var typeDeleted = false;
        var deleteProbe = new Chat.ChatTypesPage(new Knowledge.Core.ChatType { Name = "delete" });
        deleteProbe.DeleteRequested = _ => { typeDeleted = true; return Task.CompletedTask; };
        ((Button)deleteProbe.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!typeDeleted) throw new Exception("Type deletion should invoke callback directly.");
        var optional = new Chat.ChatTypesPage();
        Knowledge.Core.ChatType? created = null;
        optional.SaveRequested = type => { created = type; return Task.CompletedTask; };
        Pump(optional.SaveAsync());
        if (created != null) throw new Exception("Name must be required.");
        ((TextBox)optional.FindName("TypeName")).Text = "עוזר כתיבה";
        ((TextBox)optional.FindName("CustomPrompt")).Text = "ענה בקצרה";
        Render(optional,"personal-assistant.png",480,580);
        Pump(optional.SaveAsync());
        if (created == null || created.Paths.Count != 0 || created.Prompt != "ענה בקצרה" || !created.Apply(new()).GeneralChat) throw new Exception("Prompt-only assistants must work without paths.");
        var messages = (StackPanel)chat.FindName("Messages");
        var sample = new StackPanel();
        sample.Children.Add(Chat.MainWindow.CreateMessageText("כדי לאפס סיסמה, פנו למוקד התמיכה בשלוחה 4321. לאחר אימות הזהות תקבלו סיסמה זמנית למשך 60 דקות.", false));
        var selectable = (TextBox)sample.Children[0];
        selectable.SelectAll();
        if (selectable.SelectedText != selectable.Text || !selectable.IsReadOnly) throw new Exception("Messages must allow selecting text without editing it.");
        selectable.Select(0, 0);
        var tags = Chat.MainWindow.CreateSourceTags([
            new("D:/example/תמיכה טכנית.docx", "איפוס סיסמה"),
            new("D:/example/תמיכה טכנית.docx", "מוקד התמיכה")], _ => { });
        if (tags.Children.Count != 1 || ((Button)tags.Children[0]).ToolTip != null) throw new Exception("Sources must be deduplicated without path overlays.");
        sample.Children.Add(tags);
        messages.Children.Add(new Border { Child = sample, Padding = new Thickness(14), Background = new SolidColorBrush(Color.FromRgb(240,243,247)), CornerRadius = new CornerRadius(16), Margin = new Thickness(0,12,18,0) });
        ((FrameworkElement)chat.FindName("Welcome")).Visibility = Visibility.Visible;
        ((FrameworkElement)chat.FindName("Suggestions")).Visibility = Visibility.Collapsed;
        Render(chat,"source-tags.png",428,780);
        var typing = (Border)chat.FindName("Typing");
        messages.Children.Add(new Border { Child = Chat.MainWindow.CreateMessageText("וכמה זמן הסיסמה הזמנית תקפה?", true), Background = new SolidColorBrush(Color.FromRgb(22,115,255)), CornerRadius = new CornerRadius(16,16,16,4), Padding = new Thickness(14,11,14,11), Margin = new Thickness(26,12,0,10) });
        typing.Visibility = Visibility.Visible;
        if (typing.Parent != messages.Parent) throw new Exception("Loading reply must follow messages inside the conversation scroll area.");
        Render(chat,"reply-loading.png",428,780);
        var typingMotion = ((System.Windows.Media.Animation.Storyboard)typing.Resources["TypingMotion"]).Clone();
        typingMotion.Begin(chat, true);
        typingMotion.Remove(chat);
        typing.Visibility = Visibility.Collapsed;
        ((Border)chat.FindName("Panel")).Visibility = Visibility.Collapsed;
        ((System.Windows.Shapes.Path)chat.FindName("ChatGlyph")).Visibility = Visibility.Visible;
        ((System.Windows.Shapes.Path)chat.FindName("CloseGlyph")).Visibility = Visibility.Collapsed;
        Render(chat,"chat-launcher.png",100,108);
        var historySamples = new[]
        {
            new Knowledge.Core.Conversation { ChatTypeName = "כללי", Messages = [new("user", "איך מבקשים גישה למערכת הארגונית?", DateTimeOffset.Now, [])] },
            new Knowledge.Core.Conversation { ChatTypeName = "משאבי אנוש", Messages = [new("user", "מה התהליך לבקשת חופשה ואיך מעדכנים את המנהל הישיר?", DateTimeOffset.Now.AddDays(-1), [])] },
            new Knowledge.Core.Conversation()
        };
        ((Border)chat.FindName("Panel")).Visibility = Visibility.Visible;
        typeof(Chat.MainWindow).GetField("chatTypes",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(chat, new List<Knowledge.Core.ChatType> { new() { Id = "default", Name = "כללי" } });
        ((Button)chat.FindName("HistoryTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var history = (Chat.HistoryPage)chat.FindName("HistoryView");
        history.Refresh(historySamples);
        Render(chat,"history.png",428,780);
        if (((ItemsControl)history.FindName("ConversationList")).Items.Count != 2) throw new Exception("Empty conversation is visible.");
        // Exercise direct deletion using synthetic data only; never touch saved user chats.
        var deleted = false;
        history.DeleteRequested = _ => { deleted = true; return true; };
        typeof(Chat.HistoryPage).GetMethod("DeleteConversation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(history, [new Button { Tag = historySamples[0] }, new RoutedEventArgs()]);
        if (!deleted || ((ItemsControl)history.FindName("ConversationList")).Items.Count != 1) throw new Exception("Direct deletion failed.");
        if (((Grid)chat.FindName("ChatPage")).Visibility != Visibility.Collapsed) throw new Exception("Chat page must hide behind history.");
        ((Button)chat.FindName("ChatTab")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (history.Visibility != Visibility.Collapsed) throw new Exception("History must hide when returning to chat.");
        chat.Close();
        var menu = Chat.MainWindow.CreateOptionsMenu((_, _) => { }, (_, _) => { });
        menu.ApplyTemplate();
        RenderVisual(menu, "options-menu.png", 250, 106);
        Console.WriteLine("Both WPF windows instantiated and rendered successfully.");
    }

    private sealed class SmokeContext : System.Threading.SynchronizationContext
    {
        public readonly System.Collections.Concurrent.BlockingCollection<Action> Work = new();
        public override void Post(System.Threading.SendOrPostCallback callback, object? state) => Work.Add(() => callback(state));
    }
    private static void Pump(Task task)
    {
        var context = (SmokeContext)System.Threading.SynchronizationContext.Current!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI check did not finish.");
            if (context.Work.TryTake(out var work, 100)) work();
        }
        task.GetAwaiter().GetResult();
    }
    private static void Render(Window window,string name,int width,int height)
    {
        var visual=(FrameworkElement)window.Content;
        if (visual is Grid grid) grid.Background=window.Background;
        RenderVisual(visual,name,width,height);
    }
    private static void RenderVisual(FrameworkElement visual,string name,int width,int height)
    {
        visual.Measure(new Size(width,height));
        visual.Arrange(new Rect(0,0,width,height));
        visual.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine("validation",name));encoder.Save(file);
    }
}




