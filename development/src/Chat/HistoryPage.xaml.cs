using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
public partial class HistoryPage : UserControl
{
    /// <summary>One row: the conversation plus its assistant's color, its place in the day's card, and its state ("running":
    /// an answer is on the way; "ready": a new answer waits to be seen).</summary>
    public sealed record Item(Conversation Conversation, Brush Badge, string Glyph, Thickness Divider, CornerRadius Corners, string State = "")
    {
        public string Title => Conversation.ShortTitle;
        public string Meta => (string.IsNullOrWhiteSpace(Conversation.ChatTypeName) ? "כללי" : Conversation.ChatTypeName) + " · " + Conversation.Messages.Last().Time.ToLocalTime().ToString("HH:mm");
        /// <summary>The mode of the last question (the icon of the mode switch), or none for conversations from before modes were saved.</summary>
        private string LastMode => Conversation.Messages.LastOrDefault(m => m.Role == "user")?.Mode ?? "";
        public string ModeGlyph => LastMode switch { "Fast" => "bolt", "Normal" => "gauge", "Deep" => "search", "Maximum" => "search-plus", _ => "" };
        public string ModeName => LastMode switch { "Fast" => "מהיר", "Normal" => "רגיל", "Deep" => "מעמיק", "Maximum" => "חיפוש מקסימלי", _ => "" };
        /// <summary>Lines the icon up with the text: the speedometer is drawn in the top of its box, the others fill it.</summary>
        public double ModeShift => LastMode == "Normal" ? 0.5 : -1;
        public Visibility ModeVisibility => ModeGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string AccessibleName => Title + ", " + Meta + (ModeName.Length > 0 ? ", " + ModeName : "") + State switch { "running" => ", מכין תשובה", "ready" => ", תשובה חדשה", _ => "" };
    }
    public sealed record Group(string Label, List<Item> Items);
    private readonly ObservableCollection<Group> groups = new();
    private List<Conversation> all = [];
    private string? typeFilter;
    public event Action<Conversation>? ConversationSelected;
    public event Action? BackRequested;
    public Func<Conversation, bool>? DeleteRequested { get; set; }
    /// <summary>"ביטול" after a delete: puts the conversation back; false when it could not be saved.</summary>
    public Func<Conversation, bool>? RestoreRequested { get; set; }
    /// <summary>The badge (color, icon) of a conversation's assistant, so history matches the assistant's look.</summary>
    public Func<Conversation, (Brush Badge, string Glyph)>? LookFor { get; set; }
    public Func<Conversation, string>? StateFor { get; set; }
    public HistoryPage()
    {
        InitializeComponent();
        GroupList.ItemsSource = groups;
    }
    public void FocusStart() => Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    public void Refresh(IEnumerable<Conversation> conversations)
    {
        all = conversations.Where(c => c.HasUserMessages).OrderByDescending(c => c.Messages.Last().Time).ToList();
        if (typeFilter != null && !all.Any(c => c.ChatTypeId == typeFilter)) typeFilter = null;
        Status.Text = "";
        HideUndo();
        BuildFilters();
        Apply();
    }
    /// <summary>Shows titles that arrived after the list was built, keeping the search and filter.</summary>
    public void RefreshTitles() => Apply();
    private void Back(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void BuildFilters()
    {
        Filters.Children.Clear();
        var types = all.GroupBy(c => c.ChatTypeId).Select(g => (Id: g.Key, Name: g.First().ChatTypeName, Count: g.Count())).ToList();
        // A filter is only useful when there are conversations from more than one assistant.
        Filters.Visibility = types.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        void Chip(string? id, string name, int count)
        {
            var selected = typeFilter == id;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = count.ToString(), Margin = new Thickness(6, 0, 0, 0), Opacity = selected ? 0.85 : 1, Foreground = selected ? Brushes.White : Theme.Brush("Secondary") });
            var chip = new Button { Style = Ui.Style("PillButton"), Content = content, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 4, 12, 4), MinHeight = 36, FontSize = 13,
                Background = selected ? Theme.Brush("Ink") : Theme.Brush("Surface"), Foreground = selected ? Brushes.White : Theme.Brush("Ink"), BorderBrush = selected ? Theme.Brush("Ink") : Theme.Brush("Line") };
            System.Windows.Automation.AutomationProperties.SetName(chip, $"סינון: {name} ({count})" + (selected ? " · נבחר" : ""));
            chip.Click += (_, _) => { typeFilter = id; BuildFilters(); Apply(); };
            Filters.Children.Add(chip);
        }
        Chip(null, "הכל", all.Count);
        foreach (var (id, name, count) in types) Chip(id, string.IsNullOrWhiteSpace(name) ? "כללי" : name, count);
    }
    private void FilterChanged(object sender, TextChangedEventArgs e) { SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Apply(); }
    private static string DayLabel(DateTime day) => day == DateTime.Today ? "היום" : day == DateTime.Today.AddDays(-1) ? "אתמול" : day.ToString("dd/MM/yyyy");
    private void Apply()
    {
        if (GroupList == null) return;
        // Every word must appear somewhere in the conversation (title or any message), in any order.
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(Conversation c) => words.All(w => c.ChatTypeName.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Topic.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Messages.Any(m => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
        groups.Clear();
        var shown = all.Where(c => (typeFilter == null || c.ChatTypeId == typeFilter) && Matches(c)).ToList();
        foreach (var day in shown.GroupBy(c => c.Messages.Last().Time.ToLocalTime().Date))
            groups.Add(new(DayLabel(day.Key), Items(day.ToList())));
        UpdateEmpty(shown.Count);
    }
    private List<Item> Items(List<Conversation> list) => list.Select((conversation, i) =>
    {
        var (badge, glyph) = LookFor?.Invoke(conversation) ?? (Theme.Brush("Accent"), "chat");
        var first = i == 0; var last = i == list.Count - 1;
        return new Item(conversation, badge, glyph, new Thickness(0, first ? 0 : 1, 0, 0), new CornerRadius(first ? 14 : 0, first ? 14 : 0, last ? 14 : 0, last ? 14 : 0), StateFor?.Invoke(conversation) ?? "");
    }).ToList();
    private void OpenConversation(object sender, RoutedEventArgs e) => ConversationSelected?.Invoke((Conversation)((Button)sender).Tag);
    /// <summary>Deletes at once; "ביטול" in the bar at the bottom brings it back for a few seconds (the last one deleted).</summary>
    private void DeleteConversation(object sender, RoutedEventArgs e)
    {
        var conversation = (Conversation)((Button)sender).Tag;
        if (DeleteRequested?.Invoke(conversation) != true) { Status.Text = "לא ניתן למחוק את השיחה (ייתכן שתשובה עדיין בדרך)."; return; }
        all.Remove(conversation); Status.Text = "";
        if (typeFilter != null && !all.Any(c => c.ChatTypeId == typeFilter)) typeFilter = null;
        BuildFilters(); Apply();
        lastDeleted = conversation;
        UndoBar.Visibility = Visibility.Visible;
        undoTimer?.Stop();
        undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
        undoTimer.Tick += (_, _) => HideUndo();
        undoTimer.Start();
        SearchBox.Focus();
    }
    private Conversation? lastDeleted;
    private System.Windows.Threading.DispatcherTimer? undoTimer;
    private void HideUndo() { undoTimer?.Stop(); undoTimer = null; lastDeleted = null; UndoBar.Visibility = Visibility.Collapsed; }
    private void UndoDelete(object sender, RoutedEventArgs e)
    {
        if (lastDeleted is not { } conversation) { HideUndo(); return; }
        HideUndo();
        if (RestoreRequested?.Invoke(conversation) != true) { Status.Text = "לא ניתן לשחזר את השיחה."; return; }
        all.Add(conversation);
        all = all.OrderByDescending(c => c.Messages.Last().Time).ToList();
        BuildFilters(); Apply();
        SearchBox.Focus();
    }
    private void UpdateEmpty(int shown)
    {
        var filtered = all.Count > 0;
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = filtered ? "לא נמצאו שיחות" : "אין עדיין שיחות שמורות";
        EmptyDetail.Text = filtered ? "נסו מילה אחרת או בחרו \"הכל\"" : "השיחות שלכם יופיעו כאן";
        ConversationCount.Text = shown == all.Count ? all.Count.ToString() : $"{shown}/{all.Count}";
    }
}
