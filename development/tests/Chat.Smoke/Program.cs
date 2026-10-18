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
   if (!labels.Contains("רענון המסמכים") || !labels.Contains("הגדרות AI") || !labels.Contains("הסבר על האפליקציה") || !labels.Contains("יציאה מ־Chaty")) throw new Exception("Menu items missing: " + string.Join(", ", labels));
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
   var conversation = new Conversation { ChatTypeName = "כללי", Messages = new() { new("user", "UI regression sample", DateTimeOffset.Now, new()) { Mode = "Fast" } } };
   var older = new Conversation { ChatTypeName = "מאגר מידע", Messages = new() { new("user", "Older question", DateTimeOffset.Now.AddDays(-1), new()) } };
   bool opened = false, back = false; int deletes = 0, restores = 0;
   history.ConversationSelected += _ => opened = true;
   history.BackRequested += () => back = true;
   history.DeleteRequested = _ => { deletes++; return true; };
   history.RestoreRequested = _ => { restores++; return true; };
   history.Refresh(new[] { conversation, older }); Layout(history, 360, 600);
   var texts = Descendants(history).OfType<TextBlock>().Select(t => t.Text).ToList();
   var modeIcons = Descendants(history).OfType<Chat.Icon>().Where(i => i.ToolTip is string tip && tip.Length > 0 && i.Visibility == Visibility.Visible).ToList();
   if (modeIcons.Count != 1 || modeIcons[0].Kind != "bolt" || !Equals(modeIcons[0].ToolTip, "מהיר")) throw new Exception("History must show the last question's mode icon (only where it is known).");
   if (!texts.Contains("היום") || !texts.Contains("אתמול")) throw new Exception("History is not grouped by day.");
   Button HistoryButton(Func<Button, bool> match) { Layout(history, 360, 600); return Descendants(history).OfType<Button>().First(match); }
   Click(HistoryButton(b => Equals(b.ToolTip, "פתיחת השיחה")));
   if (!opened) throw new Exception("History open did not invoke selection.");
   // Delete at once, with "ביטול" to bring it back.
   Click(HistoryButton(b => Name(b) == "מחיקת השיחה"));
   if (deletes != 1 || ((FrameworkElement)history.FindName("UndoBar")).Visibility != Visibility.Visible) throw new Exception("Delete must remove at once and offer undo.");
   Click((Button)history.FindName("UndoButton"));
   if (restores != 1 || ((FrameworkElement)history.FindName("UndoBar")).Visibility == Visibility.Visible) throw new Exception("Undo did not restore the conversation.");
   Layout(history, 360, 600);
   if (Descendants(history).OfType<Button>().Count(b => Equals(b.ToolTip, "פתיחת השיחה")) != 2) throw new Exception("The restored conversation is not back in the list.");
   Click(Descendants(history).OfType<Button>().First(b => Name(b) == "חזרה לשיחה"));
   if (!back) throw new Exception("Back did not leave history.");
   Console.WriteLine("PASS: history grouping, open, delete with undo, back");

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

   // Never the real %APPDATA%\config.json: a test file with one fictional model.
   AiConfigFile.Path = Path.Combine(temp, "config.json");
   File.WriteAllText(AiConfigFile.Path, "[ { \"name\": \"test\", \"apiKey\": \"\", \"models\": [ { \"id\": \"smoke-model\", \"url\": \"https://ai.example/v1\" } ] } ]");
   var config = LocalAiCatalog.Load().Apply(new Config(), null);
   if (config.Ai.Model != "smoke-model") throw new Exception("config.json models were not loaded.");
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
   Console.WriteLine("PASS: answer mode switch (fast, normal, deep)");

   // The tour (here as on the first start, with no API key): step by step, "הבא" to the end, ending on the AI settings,
   // which stay open for the user to fill in.
   ((FrameworkElement)main.FindName("Panel")).Visibility = Visibility.Visible; ((FrameworkElement)main.FindName("LoginOverlay")).Visibility = Visibility.Collapsed;
   Layout((FrameworkElement)main.Content, 400, 680);
   // The settings the app starts with (no API key in the temp profile), so the AI settings screen can open.
   typeof(MainWindow).GetField("config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(main, new Config());
   // Personal assistants allowed, so the tour also opens the assistant list and the new assistant form.
   foreach (var flag in new[] { "personalAllowed", "loggedIn" }) typeof(MainWindow).GetField(flag, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(main, true);
   typeof(MainWindow).GetMethod("StartTour", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, [typeof(bool)])!.Invoke(main, [true]);
   var tourLayer = ((Grid)main.FindName("PanelRoot")).Children.OfType<Grid>().Last();
   string TourStep() { Layout((FrameworkElement)main.Content, 400, 680); return Descendants(tourLayer).OfType<Border>().Select(Name).First(n => n.StartsWith("סיור באפליקציה")); }
   if (tourLayer.Visibility != Visibility.Visible || !TourStep().Contains("שלב 1 מתוך")) throw new Exception("The tour must start at its first step.");
   var steps = new List<string>();
   while (tourLayer.Visibility == Visibility.Visible)
   {
    if (steps.Count > 12) throw new Exception("The tour does not end.");
    steps.Add(TourStep());
    if (steps[^1].EndsWith(": עוזר אישי חדש") && ((ContentControl)main.FindName("OverlayHost")).Content == null) throw new Exception("The new-assistant step must show the open list.");
    Click(Descendants(tourLayer).OfType<Button>().Last(b => b.Content is "הבא" or "סיום"));
   }
   foreach (var title in new[] { ": עוזרים", ": עוזר אישי חדש", ": עוזר אישי", ": הנחיות" })
    if (!steps.Any(s => s.EndsWith(title))) throw new Exception("The tour is missing the step" + title + " in " + string.Join(" | ", steps));
   if (!steps[^1].EndsWith(": הגדרות AI")) throw new Exception("The first-start tour must end on the AI settings.");
   if (((ContentControl)main.FindName("ScreenHost")).Content is not LocalSettingsPanel keyScreen) throw new Exception("The first-start tour must leave the AI settings open.");
   keyScreen.Cancel(); Layout((FrameworkElement)main.Content, 400, 680);
   Console.WriteLine("PASS: tour of the app (first start: how to open assistants, then the AI settings left open to fill in)");
   Click((Button)main.FindName("HelpButton"));
   if (((ContentControl)main.FindName("OverlayHost")).Content is not FrameworkElement about || Name(about) != "אודות Chaty") throw new Exception("The \"i\" must open About.");
   typeof(MainWindow).GetMethod("CloseOverlay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(main, [true]);
   Console.WriteLine("PASS: the \"i\" opens About");

   // Maximum search: instead of the three modes, never with them; the orange note's X hides only the note.
   var max = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("MaxSearchButton"); var note = (FrameworkElement)main.FindName("MaxSearchNotice");
   RadioButton Mode(string name) => (RadioButton)main.FindName(name);
   void Toggle() { max.IsChecked = max.IsChecked != true; max.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
   Toggle();
   if (new[] { "FastMode", "NormalMode", "DeepMode" }.Any(n => Mode(n).IsChecked == true) || note.Visibility != Visibility.Visible) throw new Exception("Maximum search must clear the modes and show its note.");
   Click(Descendants(note).OfType<Button>().Single(b => Name(b) == "הסתרת ההודעה על חיפוש מקסימלי"));
   if (note.Visibility == Visibility.Visible || max.IsChecked != true) throw new Exception("The X must hide the note and keep maximum search.");
   Mode("DeepMode").IsChecked = true;
   if (max.IsChecked == true) throw new Exception("Choosing a mode must end maximum search.");
   Toggle(); Click(Descendants(note).OfType<Button>().Single(b => Name(b) == "חזרה למצב רגיל"));
   if (max.IsChecked == true || note.Visibility == Visibility.Visible || Mode("NormalMode").IsChecked != true) throw new Exception("\"חזרה לרגיל\" must end maximum search and choose normal.");
   Console.WriteLine("PASS: maximum search (clears the modes, note with X and back-to-normal, a mode ends it)");

   // Driven through the private members, as the window is never shown.
   const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
   object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(main, args);
   var shown = (Conversation)typeof(MainWindow).GetField("current", Private)!.GetValue(main)!;
   // The corner and history buttons: moving dots while answering, a count of conversations with an unseen answer.
   Call("SetThinking", true);
   if (((FrameworkElement)main.FindName("ThinkingDots")).Visibility != Visibility.Visible || ((FrameworkElement)main.FindName("ChatGlyph")).Visibility == Visibility.Visible) throw new Exception("Thinking must show the dots instead of the glyph.");
   Call("SetThinking", false);
   Call("MarkArrived", new Conversation()); Call("MarkArrived", shown);
   if (((TextBlock)main.FindName("UnseenCount")).Text != "2" || ((TextBlock)main.FindName("HistoryUnseenCount")).Text != "2" || ((FrameworkElement)main.FindName("HistoryUnseen")).Visibility != Visibility.Visible)
       throw new Exception("Two conversations with unseen answers must show 2 on the corner and history buttons.");
   if ((string)Call("StateOf", shown)! != "ready") throw new Exception("A conversation with an unseen answer must be \"ready\" in history.");
   main.Close();
   Console.WriteLine("PASS: corner and history buttons (thinking dots, count of unseen answers, ready state for history rows)");

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
