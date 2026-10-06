using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Media;
using Knowledge.Core;
using Chat;

internal static class Program
{
 [STAThread]
 static int Main()
 {
  var app = new App(); app.InitializeComponent();
  typeof(App).Assembly.GetType("Chat.Theme")!.GetMethod("Apply")!.Invoke(null, null);
  try
  {
   var menu = MainWindow.CreateOptionsMenu((_, _) => { }, (_, _) => { });
   menu.ApplyTemplate(); menu.Measure(new Size(360, 600)); menu.Arrange(new Rect(0,0,360,600)); menu.UpdateLayout();
   menu.IsOpen = true; menu.UpdateLayout(); menu.IsOpen = false;
   Console.WriteLine("PASS: options menu including separators creates, opens and closes");
   var assistantMenu = new ContextMenu { Style = (Style)app.FindResource("ChatMenu") };
   assistantMenu.Items.Add(new MenuItem { Header = "General" }); assistantMenu.Items.Add(new Separator()); assistantMenu.Items.Add(new MenuItem { Header = "New assistant" });
   assistantMenu.ApplyTemplate(); assistantMenu.Measure(new Size(360,600)); assistantMenu.Arrange(new Rect(0,0,360,600)); assistantMenu.UpdateLayout();
   assistantMenu.IsOpen = true; assistantMenu.UpdateLayout(); assistantMenu.IsOpen = false;
   Console.WriteLine("PASS: assistant menu including separators creates, opens and closes");
   var history = new HistoryPage(); history.Measure(new Size(360,600)); history.Arrange(new Rect(0,0,360,600));
   Console.WriteLine("PASS: history layout");
   var conversation = new Conversation { Messages = new() { new("user", "UI regression sample", DateTimeOffset.Now, new()) } };
   bool opened = false, deleted = false;
   history.ConversationSelected += _ => opened = true;
   history.DeleteRequested = _ => { deleted = true; return true; };
   history.Refresh(new[] { conversation }); history.UpdateLayout();
   var historyButtons = Descendants(history).OfType<Button>().ToArray();
   historyButtons.First(b => Equals(b.ToolTip, "פתיחת השיחה")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (!opened) throw new Exception("History open did not invoke selection.");
   historyButtons.First(b => Equals(b.ToolTip, "מחיקת השיחה")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (deleted) throw new Exception("Deletion occurred before confirmation.");
   historyButtons.First(b => Equals(b.Content, "ביטול")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (deleted) throw new Exception("Cancel deleted history.");
   historyButtons.First(b => Equals(b.ToolTip, "מחיקת השיחה")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   historyButtons.First(b => Equals(b.Content, "כן")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (!deleted) throw new Exception("Confirmed deletion did not run.");
   Console.WriteLine("PASS: history open, delete, cancel and confirm button handlers");
   var editor = new ChatTypesPage(); editor.ApplyTemplate(); editor.Measure(new Size(360,640)); editor.Arrange(new Rect(0,0,360,640)); editor.UpdateLayout(); var editorContent = (FrameworkElement)editor.Content; editorContent.Measure(new Size(360,640)); editorContent.Arrange(new Rect(0,0,360,640)); editorContent.UpdateLayout();
   var paths = (StackPanel)editor.FindName("PathRows");
   Descendants(editorContent).OfType<Button>().Single(b => b.Content is string label && label.Contains("הוספת נתיב נוסף")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (paths.Children.Count != 2) throw new Exception("Add path did not add a row.");
   Descendants(editorContent).OfType<Button>().First(b => Equals(b.ToolTip, "הסרת נתיב")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (paths.Children.Count != 1) throw new Exception("Remove path did not remove a row.");
   ((TextBox)editor.FindName("TypeName")).Text = "Smoke assistant";
   bool created = false;
   editor.SaveRequested = type => { created = type.Name == "Smoke assistant"; return System.Threading.Tasks.Task.CompletedTask; };
   editor.SaveAsync().GetAwaiter().GetResult();
   if (!created) throw new Exception("Create assistant did not invoke save.");
   Console.WriteLine("PASS: assistant editor layout, add/remove path and create handler");
   Storage.Root = Path.Combine(Path.GetTempPath(), "WindowsChat-UiSmoke-" + Guid.NewGuid().ToString("N"));
   var config = LocalAiCatalog.Load().Apply(new Config(), null);
   AiCredentials.Save(config.Ai, "ui-smoke-dummy-key");
   var settings = new LocalSettingsPanel(config); settings.Measure(new Size(340,640)); settings.Arrange(new Rect(0,0,340,640)); settings.UpdateLayout();
   var edit = Descendants(settings).OfType<Button>().Single(b => Equals(b.ToolTip, "עריכה"));
   edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (!Equals(edit.ToolTip, "ביטול עריכה")) throw new Exception("Key edit did not open.");
   edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (!Equals(edit.ToolTip, "עריכה")) throw new Exception("Key edit did not cancel.");
   var picker = Descendants(settings).OfType<ComboBox>().Single(); picker.IsDropDownOpen = true; picker.UpdateLayout(); picker.IsDropDownOpen = false;
   bool cancelled = false; settings.Completed += value => cancelled = value == null;
   Descendants(settings).OfType<Button>().Single(b => Equals(b.Content, "ביטול")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
   if (!cancelled) throw new Exception("Settings cancel did not complete.");
   foreach (var file in Directory.GetFiles(Storage.Root)) File.Delete(file); Directory.Delete(Storage.Root);
   Console.WriteLine("PASS: settings layout, model dropdown, key edit/cancel and dialog cancel (dummy credential, no AI request)");
   if (((System.Windows.Media.SolidColorBrush)app.FindResource("Surface")).Color != System.Windows.Media.Colors.White) throw new Exception("Panel must remain white regardless of Windows theme.");
   Console.WriteLine("PASS: fixed white surface");
   return 0;
  }
  catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
  finally { app.Shutdown(); }
 }
 static IEnumerable<DependencyObject> Descendants(DependencyObject root)
 {
  for (int i=0; i<VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root,i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
 }
}
