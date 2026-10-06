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
            var nextIndex = await Task.Run(() => new DocumentIndex(typeConfig), lifetime.Token);
            activeTypeId = id; activeConfig = typeConfig; index = nextIndex;
            ChatTypePicker.Content = new TextBlock { Text = type.Name + "  ▾", MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis };
            current = restoreHistory ? conversations.LastOrDefault(c => c.ChatTypeId == id) ?? new() { ChatTypeId = id } : new() { ChatTypeId = id };
            Question.Clear(); ShowPage("chat"); await RenderAsync();
            Storage.Save("active-chat-type.bin", id);
            await IndexAsync(false);
        }
        finally { switchingType = false; ChatTypePicker.IsEnabled = SendButton.IsEnabled = HistoryTab.IsEnabled = NewChatButton.IsEnabled = true; }
    }
    private void PickChatType(object sender, RoutedEventArgs e)
    {
        if (!loggedIn || request != null || indexing || switchingType) { Status.Text = "המתינו לסיום הפעולה הנוכחית."; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("ChatMenu"), PlacementTarget = ChatTypePicker, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Section(string title) => new() {
            IsEnabled = false, Opacity = 1, Padding = new Thickness(0), Margin = new Thickness(2,6,2,3),
            Header = new Border { Background = System.Windows.Media.Brushes.Transparent, BorderBrush = Theme.Brush("Muted"), BorderThickness = new Thickness(0,0,0,1), Width = 235, Padding = new Thickness(10,7,10,7),
                Child = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Muted") } }
        };
        string? previousSection = null;
        foreach (var type in chatTypes.OrderBy(t => t.IsGeneral ? 0 : t.IsManaged ? 1 : 2).ThenBy(t=>t.Project).ThenBy(t=>t.Category))
        {
            var section = type.IsGeneral ? "צ׳אט כללי" : type.IsManaged ? string.Join(" / ", new[] { "ידע ארגוני", type.Project, type.Category }.Where(x => !string.IsNullOrWhiteSpace(x))) : "העוזרים שלי";
            if (section != previousSection) { menu.Items.Add(Section(section)); previousSection = section; }
            var header = new Grid { Width = 235 };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = (type.Id == activeTypeId ? "✓  " : "") + type.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var edit = new Button { Content = new TextBlock { Text = "\uE70F", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets") }, Background = System.Windows.Media.Brushes.Transparent, Width = 44, Height = 44, Padding = new Thickness(8), Margin = new Thickness(6, 0, 0, 0), ToolTip = "עריכת " + type.Name };
            System.Windows.Automation.AutomationProperties.SetName(edit, "עריכת " + type.Name);
            Grid.SetColumn(edit, 1); header.Children.Add(edit);
            if (type.IsGeneral || type.IsManaged) edit.Visibility = Visibility.Collapsed;
            edit.Click += (_, args) => { args.Handled = true; menu.IsOpen = false; OpenChatTypeDialog(type); };
            var item = new MenuItem { Header = header, Background = type.Id == activeTypeId ? Theme.Brush("Selected") : System.Windows.Media.Brushes.Transparent };
            edit.Opacity = 0; header.MouseEnter += (_, _) => edit.Opacity = 1; header.MouseLeave += (_, _) => { if (!header.IsKeyboardFocusWithin) edit.Opacity = 0; }; header.IsKeyboardFocusWithinChanged += (_, _) => edit.Opacity = header.IsKeyboardFocusWithin || header.IsMouseOver ? 1 : 0;
            item.Click += async (_, _) => { try { if (type.Id != activeTypeId) await ActivateTypeAsync(type.Id, true); } catch (Exception ex) { Status.Text = ex.Message; } };
            menu.Items.Add(item);
        }
        var manage = new MenuItem { Header = "+  עוזר אישי חדש", Foreground = Theme.Brush("Link") };
        manage.Click += (_, _) => { menu.IsOpen = false; OpenChatTypeDialog(null); };
        if (personalAllowed) { menu.Items.Add(new Separator()); menu.Items.Add(manage); } OpenStyledMenu(menu);
    }
    private void OpenChatTypeDialog(ChatType? type)
    {
        if (!loggedIn || !personalAllowed || type?.IsGeneral == true || type?.IsManaged == true) return;
        timer?.Stop();
        try
        {
            var dialog = new ChatTypesPage(type) { Owner = this, SaveRequested = SaveChatTypeAsync, DeleteRequested = DeleteChatTypeAsync };
            dialog.MaxHeight = Math.Max(200, Panel.ActualHeight - 32);
            dialog.Width = Math.Max(240, Panel.ActualWidth - 32);
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
        if (activeTypeId == id) await ActivateTypeAsync(next[0].Id, true);
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


