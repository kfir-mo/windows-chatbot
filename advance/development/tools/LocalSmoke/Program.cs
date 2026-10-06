using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Knowledge.Core;

internal static class Program
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    static void Main()
    {
        var original = Storage.Root;
        var root = Path.Combine(Path.GetTempPath(), "local-smoke-" + Guid.NewGuid());
        Directory.CreateDirectory(root); Storage.Root = Path.Combine(root, "state");
        var context = new SmokeContext(); SynchronizationContext.SetSynchronizationContext(context);
        var app = new Chat.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new Chat.MainWindow();
        try
        {
            if (!Chat.BuildMode.Local) throw new Exception("Expected local build.");
            Storage.Save("local-settings.bin", new Config());
            var type = new ChatType { Name = "Prototype documents", Paths = [root] };
            Storage.Save("chat-types.bin", new List<ChatType> { type });
            var file = Path.Combine(root, "guide.docx");
            WriteDoc(file, "Original instructions for alpha system.");
            Pump((Task)typeof(Chat.MainWindow).GetMethod("InitializeLocalAsync", Private)!.Invoke(window, null)!, context);
            Pump((Task)typeof(Chat.MainWindow).GetMethod("ActivateTypeAsync", Private)!.Invoke(window, [type.Id, false])!, context);
            var index = (DocumentIndex)typeof(Chat.MainWindow).GetField("index", Private)!.GetValue(window)!;
            if (index.Statistics.Files != 1) throw new Exception("Initial folder scan failed.");
            File.Delete(file); WriteDoc(file, "Updated instructions with new beta workflow and additional steps.");
            typeof(Chat.MainWindow).GetMethod("NewChat", Private)!.Invoke(window, [window, new RoutedEventArgs()]);
            Until(() => index.ReadAsync(file, default).GetAwaiter().GetResult().Any(p => p.Text.Contains("Updated instructions")), context);
            File.Delete(file);
            typeof(Chat.MainWindow).GetMethod("NewChat", Private)!.Invoke(window, [window, new RoutedEventArgs()]);
            Until(() => index.Statistics.Files == 0, context);
            AiCredentials.Save(LocalAiCatalog.Load().Apply(new Config(), null).Ai, "fictional-ui-test-key");
            var settings = new Chat.LocalSettingsPanel(new Config());
            var content = (FrameworkElement)settings.Content;
            content.Measure(new Size(460,400)); content.Arrange(new Rect(0,0,460,400)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(460,400,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            Directory.CreateDirectory("validation");
            using (var output = File.Create("validation/local-settings.png")) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output); }

            var surface = (FrameworkElement)window.Content;
            surface.Measure(new Size(428,780)); surface.Arrange(new Rect(0,0,428,780)); surface.UpdateLayout();
            var popupTask = (Task)typeof(Chat.MainWindow).GetMethod("OpenLocalSettingsAsync", Private)!.Invoke(window, null)!;
            var overlay = (Border)window.FindName("SettingsOverlay");
            if (overlay.Visibility != Visibility.Visible) throw new Exception("Settings must open inside the chat.");
            var host = (ContentControl)window.FindName("SettingsHost");
            var popup = (Chat.LocalSettingsPanel)host.Content;
            popup.BeginAnimation(UIElement.OpacityProperty, null); popup.Opacity = 1;
            var chatPanel = (Border)window.FindName("Panel"); chatPanel.BeginAnimation(UIElement.OpacityProperty, null); chatPanel.Opacity = 1;
            surface.Measure(new Size(428,780)); surface.Arrange(new Rect(0,0,428,780)); surface.UpdateLayout();
            var full = new RenderTargetBitmap(428,780,96,96,PixelFormats.Pbgra32); full.Render(surface);
            using (var output = File.Create("validation/local-settings-popup.png")) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(full)); encoder.Save(output); }
            var locked = Descendants(popup).OfType<TextBox>().Single(t => t.IsReadOnly);
            var secretInput = Descendants(popup).OfType<PasswordBox>().Single();
            if (locked.Text != "************" || secretInput.Visibility != Visibility.Collapsed) throw new Exception("Saved key must start masked and locked.");
            var view = Descendants(popup).OfType<Button>().Single(b => b.ToolTip as string == "צפייה");
            view.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (locked.Text != "fictional-ui-test-key" || !locked.IsReadOnly) throw new Exception("Reveal must stay read-only.");
            view.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var edit = Descendants(popup).OfType<Button>().Single(b => b.ToolTip as string == "עריכה");
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (secretInput.Visibility != Visibility.Visible || locked.Visibility != Visibility.Collapsed) throw new Exception("Edit must explicitly unlock the field.");
            secretInput.Password = "unsaved-replacement";
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (secretInput.Visibility != Visibility.Collapsed || secretInput.Password != "" || locked.Text != "************") throw new Exception("Second edit click must discard the draft and relock.");
            if (AiCredentials.Resolve(LocalAiCatalog.Load().Apply(new Config(), null).Ai) != "fictional-ui-test-key") throw new Exception("Cancel must preserve the saved key.");
            var close = Descendants(popup).OfType<Button>().Single(b => b.ToolTip as string == "סגירת ההגדרות");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(popupTask, context);
            if (overlay.Visibility != Visibility.Collapsed || host.Content != null) throw new Exception("Closing settings must return to the chat.");
            Console.WriteLine("Local build: startup, settings render, chat creation, changed-file refresh and deleted-file refresh passed.");
        }
        finally { window.Close(); Storage.Root = original; Directory.Delete(root, true); }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static void WriteDoc(string path, string text)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open());
        writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:body></w:document>");
    }
    static void Pump(Task task, SmokeContext context) { Until(() => task.IsCompleted, context); task.GetAwaiter().GetResult(); }
    static void Until(Func<bool> ready, SmokeContext context)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); if (context.Work.TryTake(out var action, 20)) action(); }
        while (context.Work.TryTake(out var pending)) pending();
    }
    sealed class SmokeContext : SynchronizationContext
    {
        public readonly System.Collections.Concurrent.BlockingCollection<Action> Work = new();
        public override void Post(SendOrPostCallback callback, object? state) => Work.Add(() => callback(state));
    }
}
