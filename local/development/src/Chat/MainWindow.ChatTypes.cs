using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Knowledge.Core;

namespace Chat;
public partial class MainWindow
{
    private List<ChatType> chatTypes = [];
    private string activeTypeId = "default";
    private bool switchingType;
    private Config activeConfig = null!;
    private async Task ActivateTypeAsync(string id, bool restoreHistory)
    {
        if (!loggedIn || request != null || indexing || switchingType) return;
        var type = chatTypes.First(t => t.Id == id);
        switchingType = true;
        ChatTypePicker.IsEnabled = SendButton.IsEnabled = HistoryTab.IsEnabled = NewChatButton.IsEnabled = false;
        try
        {
            var typeConfig = type.Apply(config);
            var session = SessionIndexFor(id, typeConfig);
            var nextIndex = await session.Loading.WaitAsync(lifetime.Token);
            activeTypeId = id; activeConfig = typeConfig; index = nextIndex; activeSession = session; indexedFiles = null;
            ChatTypeName.Text = type.Name;
            current = restoreHistory ? conversations.LastOrDefault(c => c.ChatTypeId == id) ?? new() { ChatTypeId = id } : new() { ChatTypeId = id };
            Question.Clear(); ShowPage("chat"); await RenderAsync();
            Storage.Save("active-chat-type.bin", id);
            await IndexAsync(false);
        }
        finally { switchingType = false; ChatTypePicker.IsEnabled = SendButton.IsEnabled = HistoryTab.IsEnabled = NewChatButton.IsEnabled = true; }
    }
    /// <summary>Document indexes stay loaded for the whole session (they vanish when the app closes), so switching
    /// assistants or starting a new chat doesn't decrypt, re-tokenize and rescan the folders again. The persistent,
    /// encrypted cache in %LOCALAPPDATA% still makes the first load after a restart fast.</summary>
    private sealed class SessionIndex(string identity, Task<DocumentIndex> loading)
    {
        public string Identity { get; } = identity;
        public Task<DocumentIndex> Loading { get; } = loading;
        public DateTimeOffset? Refreshed { get; set; }
    }
    private static readonly TimeSpan SessionRefreshInterval = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, SessionIndex> sessionIndexes = [];
    private SessionIndex? activeSession;
    private SessionIndex SessionIndexFor(string id, Config typeConfig)
    {
        // Folder or limit changes (an edited assistant) build a new index.
        var identity = System.Text.Json.JsonSerializer.Serialize(typeConfig.Documents);
        if (sessionIndexes.TryGetValue(id, out var cached) && cached.Identity == identity && !cached.Loading.IsFaulted && !cached.Loading.IsCanceled) return cached;
        var created = new SessionIndex(identity, Task.Run(() => new DocumentIndex(typeConfig), lifetime.Token));
        sessionIndexes[id] = created;
        return created;
    }
    /// <summary>After startup, load and refresh the other assistants in the background, one at a time.</summary>
    private async Task PreloadAssistantsAsync()
    {
        foreach (var type in chatTypes.Where(t => !t.IsGeneral && !t.IsManaged && t.Paths.Count > 0).ToList())
        {
            try
            {
                await Task.Delay(1500, lifetime.Token);
                if (type.Id == activeTypeId || !chatTypes.Contains(type)) continue;
                var session = SessionIndexFor(type.Id, type.Apply(config));
                if (session.Refreshed != null) continue;
                var loaded = await session.Loading.WaitAsync(lifetime.Token);
                await loaded.RefreshAsync(false, null, lifetime.Token);
                session.Refreshed = DateTimeOffset.Now;
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
        MemoryTrim.Schedule(Dispatcher, expanded ? IdleTrimSeconds : 1.5);
    }
    private void PickChatType(object sender, RoutedEventArgs e)
    {
        if (!loggedIn || request != null || indexing || switchingType) { Status.Text = "המתינו לסיום הפעולה הנוכחית."; return; }
        // Span the header (the template insets 10px per side), so the menu stays inside the panel.
        var menu = new ContextMenu { Style = (Style)FindResource("ChatMenu"), PlacementTarget = HeaderBar, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, Width = HeaderBar.ActualWidth, VerticalOffset = -6 };
        MenuItem Section(string title) => new() {
            IsEnabled = false, Opacity = 1, MinHeight = 0, Padding = new Thickness(12, 8, 12, 4),
            Header = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted") }
        };
        var icons = (System.Windows.Media.FontFamily)FindResource("IconFont");
        string? previousSection = null;
        foreach (var type in chatTypes.OrderBy(t => t.IsGeneral ? 0 : t.IsManaged ? 1 : 2).ThenBy(t=>t.Project).ThenBy(t=>t.Category))
        {
            var section = type.IsGeneral ? "צ׳אט כללי" : type.IsManaged ? string.Join(" / ", new[] { "ידע ארגוני", type.Project, type.Category }.Where(x => !string.IsNullOrWhiteSpace(x))) : "העוזרים שלי";
            if (section != previousSection) { if (previousSection != null) menu.Items.Add(new Separator()); menu.Items.Add(previousSection == null ? MenuTitle(section, menu) : Section(section)); previousSection = section; }
            var active = type.Id == activeTypeId;
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Each assistant shows its own color and icon, as in the header and the history.
            var look = LookOf(type);
            header.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = LookBrush(look), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = look.Glyph, FontFamily = icons, FontSize = 13, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(trailing, 2); header.Children.Add(trailing);
            if (active) trailing.Children.Add(new TextBlock { Text = "", FontFamily = icons, FontSize = 13, Foreground = Theme.Brush("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
            var detail = type.IsGeneral ? "שיחה חופשית עם AI" : type.IsManaged ? "ידע ארגוני" : type.Paths.Count == 0 ? "ללא תיקיות מסמכים" : type.Paths.Count == 1 ? FolderName(type.Paths[0]) : $"{type.Paths.Count} תיקיות מסמכים";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = type.Name, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = active ? Theme.Brush("Link") : Theme.Brush("Ink"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Theme.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1); header.Children.Add(text);
            if (!type.IsGeneral && !type.IsManaged)
            {
                var edit = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 32, Height = 32, FontSize = 13, Margin = new Thickness(6, 0, 0, 0), ToolTip = "עריכת " + type.Name };
                System.Windows.Automation.AutomationProperties.SetName(edit, "עריכת " + type.Name);
                trailing.Children.Add(edit);
                edit.Click += (_, args) => { args.Handled = true; menu.IsOpen = false; OpenChatTypeDialog(type); };
            }
            var item = new MenuItem { Header = header, Padding = new Thickness(10, 7, 6, 7), Background = active ? Theme.Brush("Selected") : System.Windows.Media.Brushes.Transparent };
            System.Windows.Automation.AutomationProperties.SetName(item, type.Name);
            item.Click += async (_, _) => { try { if (type.Id != activeTypeId) await ActivateTypeAsync(type.Id, false); } catch (Exception ex) { Status.Text = ex.Message; } };
            menu.Items.Add(item);
        }
        var manage = MenuEntry("עוזר אישי חדש", "", (_, _) => { }, Theme.Brush("Link"));
        manage.Click += (_, _) => { menu.IsOpen = false; OpenChatTypeDialog(null); };
        if (personalAllowed) { menu.Items.Add(new Separator()); menu.Items.Add(manage); } OpenStyledMenu(menu);
    }
    private static string FolderName(string path) { var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)); return string.IsNullOrEmpty(name) ? path : name; }
    private void OpenChatTypeDialog(ChatType? type)
    {
        if (!loggedIn || !personalAllowed || type?.IsGeneral == true || type?.IsManaged == true) return;
        timer?.Stop();
        try
        {
            var dialog = new ChatTypesPage(type) { Owner = this, SaveRequested = SaveChatTypeAsync, DeleteRequested = DeleteChatTypeAsync };
            dialog.MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40);
            dialog.Width = Math.Max(300, Panel.ActualWidth + 8);
            MenuScrim.Visibility = Visibility.Visible;
            dialog.ShowDialog();
        }
        finally { MenuScrim.Visibility = Visibility.Collapsed; ChatTypePicker.Focus(); timer?.Start(); }
    }
    private async Task DeleteChatTypeAsync(string id)
    {
        if (!personalAllowed || id == ChatType.GeneralId || id.StartsWith("org:")) throw new InvalidOperationException("לא ניתן למחוק את הצ׳אט הכללי.");
        var next = chatTypes.Where(t => t.Id != id).ToList();
        // Keep a usable, empty chat when the final configured type is removed.
        if (next.Count == 0) next.Add(ChatType.General());
        Storage.Save("chat-types.bin", next.Where(t=>!t.IsManaged).ToList()); chatTypes = next;
        sessionIndexes.Remove(id);
        if (activeTypeId == id) await ActivateTypeAsync(next[0].Id, false);
    }
    private async Task SaveChatTypeAsync(ChatType type)
    {
        if (!personalAllowed || type.IsGeneral || type.IsManaged) throw new InvalidOperationException("הצ׳אט הכללי אינו ניתן לעריכה.");
        if (!loggedIn || request != null || indexing || switchingType) throw new InvalidOperationException("המתינו לסיום הפעולה הנוכחית.");
        if (chatTypes.Any(t => t.Id != type.Id && t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("כבר קיים סוג צ׳אט בשם זה.");
        var next = chatTypes.Where(t => t.Id != type.Id).Append(type).ToList();
        Storage.Save("chat-types.bin", next.Where(t=>!t.IsManaged).ToList()); chatTypes = next;
        await ActivateTypeAsync(type.Id, false);
    }
}


