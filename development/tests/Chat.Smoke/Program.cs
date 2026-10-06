using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Knowledge.Core;
using Chat;

internal static class Program
{
 [STAThread]
 static int Main()
 {
  var app = new App(); app.InitializeComponent();
  // As in the running app, awaits in the UI resume on the UI thread. The WPF dispatcher itself is never pumped: that would
  // run App startup (single-instance check, StartupUri, the real profile folder).
  System.Threading.SynchronizationContext.SetSynchronizationContext(Pump);
  typeof(App).Assembly.GetType("Chat.Theme")!.GetMethod("Apply")!.Invoke(null, null);
  var temp = Path.Combine(Path.GetTempPath(), "WindowsChat-UiSmoke-" + Guid.NewGuid().ToString("N"));
  try
  {
  
   foreach (var kind in Chat.Icon.Kinds) { var (geometry, _) = Chat.Icon.ShapeOf(kind); if (geometry.Bounds.IsEmpty) throw new Exception("Icon has no shape: " + kind); }
   if (Looks.Symbols.Length != 18 || Looks.Palette.Length != 6 || Looks.Symbols.Any(s => !Chat.Icon.Kinds.Contains(s.Key))) throw new Exception("The look picker needs 6 colors and 18 drawable symbols.");
   Console.WriteLine("PASS: every line icon parses; 6 colors and 18 symbols");

   int refreshed = 0;
   var menu = MainWindow.BuildOptionsMenu("מאגר מידע", false, true, () => refreshed++, () => { }, () => { }, () => { });
   Layout(menu, 300, 400);
   var labels = Names(menu);
   if (!labels.Contains("רענון המסמכים") || !labels.Contains("הגדרות AI") || !labels.Contains("אודות Chaty") || !labels.Contains("יציאה מ־Chaty")) throw new Exception("Menu items missing: " + string.Join(", ", labels));
   if (labels.Any(l => l.Contains("הסתרה") || l.Contains("עריכת"))) throw new Exception("The menu must not offer hide-to-tray or edit assistant.");
   Click(Descendants(menu).OfType<Button>().First(b => Name(b) == "רענון המסמכים"));
   if (refreshed != 1) throw new Exception("Refresh did not run.");
   var general = MainWindow.BuildOptionsMenu(null, false, true, () => { }, () => { }, () => { }, () => { });
   Layout(general, 300, 400);
   if (Names(general).Any(l => l.Contains("רענון"))) throw new Exception("General chat must not show the refresh item.");
   Console.WriteLine("PASS: ⋯ menu (refresh only with folders, no hide/edit items)");

   TypeLook? picked = null;
   var picker = new LookPicker(new TypeLook("blue", "chat")); picker.Changed += look => picked = look;
   Layout(picker, 320, 500);
   Click(Descendants(picker).OfType<Button>().First(b => Name(b).StartsWith("צבע ירוק")));
   Click(Descendants(picker).OfType<Button>().First(b => Name(b).StartsWith("סמל ספר")));
   if (picked != new TypeLook("green", "book")) throw new Exception("Look picker did not report the picks: " + picked);
   Console.WriteLine("PASS: color and symbol picker");

   var history = new HistoryPage(); Layout(history, 360, 600);
   var conversation = new Conversation { ChatTypeName = "כללי", Messages = new() { new("user", "UI regression sample", DateTimeOffset.Now, new()) } };
   var older = new Conversation { ChatTypeName = "מאגר מידע", Messages = new() { new("user", "Older question", DateTimeOffset.Now.AddDays(-1), new()) } };
   bool opened = false, deleted = false, back = false;
   history.ConversationSelected += _ => opened = true;
   history.BackRequested += () => back = true;
   history.DeleteRequested = _ => { deleted = true; return true; };
   history.Refresh(new[] { conversation, older }); Layout(history, 360, 600);
   var texts = Descendants(history).OfType<TextBlock>().Select(t => t.Text).ToList();
   if (!texts.Contains("היום") || !texts.Contains("אתמול")) throw new Exception("History is not grouped by day.");
   var historyButtons = Descendants(history).OfType<Button>().ToArray();
   Click(historyButtons.First(b => Equals(b.ToolTip, "פתיחת השיחה")));
   if (!opened) throw new Exception("History open did not invoke selection.");
   Click(historyButtons.First(b => Equals(b.ToolTip, "מחיקת השיחה")));
   if (deleted) throw new Exception("Deletion occurred before confirmation.");
   Click(historyButtons.First(b => Equals(b.Content, "ביטול")));
   if (deleted) throw new Exception("Cancel deleted history.");
   Click(historyButtons.First(b => Equals(b.ToolTip, "מחיקת השיחה")));
   Click(historyButtons.First(b => Equals(b.Content, "כן")));
   if (!deleted) throw new Exception("Confirmed deletion did not run.");
   Click(Descendants(history).OfType<Button>().First(b => Name(b) == "חזרה לשיחה"));
   if (!back) throw new Exception("Back did not leave history.");
   Console.WriteLine("PASS: history grouping, open, delete with confirmation, back");

   Storage.Root = temp;
   var one = Directory.CreateDirectory(Path.Combine(temp, "Docs")).FullName;
   File.WriteAllText(Path.Combine(one, "a.txt"), "x"); File.WriteAllText(Path.Combine(one, "b.docx"), "x"); File.WriteAllText(Path.Combine(one, "skip.exe"), "x"); File.WriteAllText(Path.Combine(one, "~$lock.docx"), "x");
   var two = Directory.CreateDirectory(Path.Combine(temp, "More")).FullName; File.WriteAllText(Path.Combine(two, "c.md"), "x");
   var editor = new ChatTypesPage(null, new TypeLook("orange", "people")); Layout(editor, 360, 640);
   var paths = (StackPanel)editor.FindName("PathRows");
   if (((FrameworkElement)editor.FindName("DangerCard")).Visibility == Visibility.Visible) throw new Exception("A new assistant must not offer delete.");
   if (((FrameworkElement)editor.FindName("LookCard")).Visibility == Visibility.Visible) throw new Exception("The color and symbol picker must stay closed until the avatar is pressed.");
   Click((Button)editor.FindName("AvatarButton"));
   if (((FrameworkElement)editor.FindName("LookCard")).Visibility != Visibility.Visible) throw new Exception("The avatar did not open the picker.");
   Click((Button)editor.FindName("CloseLookButton"));
   if (((FrameworkElement)editor.FindName("LookCard")).Visibility == Visibility.Visible) throw new Exception("The red X did not close the picker.");
   if (!Wait(editor.AddFolderAsync(one))) throw new Exception("Adding a folder failed.");
   Wait(editor.AddFolderAsync(two));
   Wait(editor.AddFolderAsync(Path.Combine(temp, "Missing")));
   var single = Path.Combine(temp, "single.pdf"); File.WriteAllText(single, "x");
   Wait(editor.AddFolderAsync(single));
   Layout(editor, 360, 640);
   var states = Descendants(editor).OfType<TextBlock>().Select(t => t.Text).ToList();
   if (paths.Children.Count != 4 || !states.Contains("נמצאו 2 מסמכים") || states.Count(t => t == "נמצא מסמך אחד") != 2 || !states.Contains("התיקייה, הקובץ או נתיב הרשת לא נמצאו.")) throw new Exception("Folder scan states: " + string.Join(" | ", states));
   if (Wait(editor.AddFolderAsync(one))) throw new Exception("A folder was added twice.");
   Click(Descendants(editor).OfType<Button>().Last(b => Name(b) == "הסרה מהרשימה: Missing"));
   if (paths.Children.Count != 3) throw new Exception("Remove folder did not remove a row.");
   ((TextBox)editor.FindName("TypeName")).Text = "Smoke assistant";
   ChatType? created = null;
   editor.SaveRequested = type => { created = type; return Task.CompletedTask; };
   Wait(editor.SaveAsync());
   if (created?.Name != "Smoke assistant" || created.Paths.Count != 3 || !created.Paths.Contains(single) || editor.Look != new TypeLook("orange", "people")) throw new Exception("Create assistant did not save name, folders and look.");
   Console.WriteLine("PASS: assistant form (folded look picker, unlimited folders and single files, real document counts, errors, duplicates, remove, create)");

   var edit = new ChatTypesPage(new ChatType { Name = "מאגר מידע", Paths = [one] }, new TypeLook("orange", "people")); Layout(edit, 360, 640);
   bool removed = false; bool? removedHistory = null; edit.ConversationCount = 2;
   edit.DeleteRequested = (_, history) => { removed = true; removedHistory = history; return Task.CompletedTask; };
   if (((FrameworkElement)edit.FindName("DangerCard")).Visibility != Visibility.Visible) throw new Exception("Edit must offer delete.");
   Click((Button)edit.FindName("DeleteButton"));
   if (removed || ((FrameworkElement)edit.FindName("ConfirmLayer")).Visibility != Visibility.Visible) throw new Exception("Delete must ask for confirmation first.");
   var keep = (CheckBox)edit.FindName("DeleteHistory");
   if (keep.Visibility != Visibility.Visible || keep.IsChecked != true) throw new Exception("Delete must offer, checked, to remove the assistant's conversations.");
   Click((Button)edit.FindName("CancelDeleteButton"));
   if (removed) throw new Exception("Cancel deleted the assistant.");
   Click((Button)edit.FindName("DeleteButton")); keep.IsChecked = false; Click((Button)edit.FindName("ConfirmDeleteButton"));
   Wait(Task.Delay(50));
   if (!removed || removedHistory != false) throw new Exception("Confirmed delete did not run, or removed the conversations without being asked.");
   Console.WriteLine("PASS: edit form delete needs confirmation; conversations removed by default, kept when unticked");

   var config = LocalAiCatalog.Load().Apply(new Config(), null);
   AiCredentials.Save(config.Ai, "ui-smoke-dummy-key-1234");
   var settings = new LocalSettingsPanel(config); Layout(settings, 360, 640);
   if (!Descendants(settings).OfType<TextBlock>().Any(t => t.Text == "•••• 1234")) throw new Exception("Saved key is not shown masked.");
   var replace = Descendants(settings).OfType<Button>().Single(b => Equals(b.Content, "החלפת מפתח"));
   Click(replace);
   if (!Equals(replace.Content, "ביטול החלפה")) throw new Exception("Key replace did not open.");
   Click(replace);
   if (!Equals(replace.Content, "החלפת מפתח")) throw new Exception("Key replace did not cancel.");
   var dropdown = Descendants(settings).OfType<ComboBox>().Single(); dropdown.IsDropDownOpen = true; dropdown.UpdateLayout(); dropdown.IsDropDownOpen = false;
   bool cancelled = false; settings.Completed += value => cancelled = value == null;
   Click(Descendants(settings).OfType<Button>().Single(b => Equals(b.Content, "ביטול")));
   if (!cancelled) throw new Exception("Settings cancel did not complete.");
   if (AiCredentials.Resolve(config.Ai) != "ui-smoke-dummy-key-1234") throw new Exception("Cancel changed the saved key.");
   Console.WriteLine("PASS: AI settings (masked key, replace/cancel, model list, cancel keeps key; dummy credential, no AI request)");

   if (((SolidColorBrush)app.FindResource("Surface")).Color != Colors.White) throw new Exception("Panel must remain white regardless of Windows theme.");
   Console.WriteLine("PASS: fixed white surface");

   // The answer-mode switch: never shown, so no tray icon or startup; switching must not throw (it once animated from infinity).
   var main = new MainWindow();
   foreach (var name in new[] { "FastMode", "DeepMode", "NormalMode", "FastMode" }) ((RadioButton)main.FindName(name)).IsChecked = true;
   if (((TextBlock)main.FindName("NormalLabel")).Margin.Left != 0 || ((TextBlock)main.FindName("FastLabel")).Margin.Left == 0) throw new Exception("Only the chosen answer mode shows its name.");
   main.Close();
   Console.WriteLine("PASS: answer mode switch (fast, normal, deep)");

   return 0;
  }
  catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
  finally { try { Directory.Delete(temp, true); } catch { } app.Shutdown(); }
 }
 static void Layout(FrameworkElement element, double width, double height) { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
 static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 static string Name(DependencyObject o) => System.Windows.Automation.AutomationProperties.GetName(o);
 static List<string> Names(DependencyObject root) => Descendants(root).OfType<Button>().Select(Name).ToList();
 static readonly MainThreadQueue Pump = new();
 /// <summary>Runs queued UI continuations on this thread until the task finishes (the form awaits background scans).</summary>
 static T Wait<T>(Task<T> task) { Wait((Task)task); return task.Result; }
 static void Wait(Task task) { Pump.RunUntil(task); task.GetAwaiter().GetResult(); }
 sealed class MainThreadQueue : System.Threading.SynchronizationContext
 {
  private readonly System.Collections.Concurrent.BlockingCollection<(System.Threading.SendOrPostCallback Callback, object? State)> queue = new();
  public override void Post(System.Threading.SendOrPostCallback d, object? state) => queue.Add((d, state));
  public void RunUntil(Task task)
  {
   var deadline = DateTime.UtcNow.AddSeconds(60);
   while (!task.IsCompleted || queue.Count > 0)
   {
    if (DateTime.UtcNow > deadline) throw new TimeoutException("UI task did not finish.");
    if (queue.TryTake(out var item, 10)) item.Callback(item.State);
   }
  }
 }
 static IEnumerable<DependencyObject> Descendants(DependencyObject root)
 {
  for (int i=0; i<VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root,i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
 }
}
