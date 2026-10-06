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
    /// <summary>One row: the conversation plus its assistant's color, and its place in the day's card.</summary>
    public sealed record Item(Conversation Conversation, Brush Badge, string Glyph, Thickness Divider, CornerRadius Corners)
    {
        public string Title => System.Text.RegularExpressions.Regex.Replace(Conversation.Title.Trim(), @"\s+", " ");
        public string Meta => (string.IsNullOrWhiteSpace(Conversation.ChatTypeName) ? "כללי" : Conversation.ChatTypeName) + " · " + Conversation.Messages.Last().Time.ToLocalTime().ToString("HH:mm");
        public string AccessibleName => Title + ", " + Meta;
    }
    public sealed record Group(string Label, List<Item> Items);
    private Border? pendingConfirmation;
    private readonly ObservableCollection<Group> groups = new();
    private List<Conversation> all = [];
    private string? typeFilter;
    public event Action<Conversation>? ConversationSelected;
    public event Action? BackRequested;
    public Func<Conversation, bool>? DeleteRequested { get; set; }
    /// <summary>The badge (color, icon) of a conversation's assistant, so history matches the assistant's look.</summary>
    public Func<Conversation, (Brush Badge, string Glyph)>? LookFor { get; set; }
    public HistoryPage()
    {
        InitializeComponent();
        GroupList.ItemsSource = groups;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && pendingConfirmation != null) { CancelDelete(this, new()); e.Handled = true; } };
    }
    public void FocusStart() => Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    public void Refresh(IEnumerable<Conversation> conversations)
    {
        all = conversations.Where(c => c.HasUserMessages).OrderByDescending(c => c.Messages.Last().Time).ToList();
        if (typeFilter != null && !all.Any(c => c.ChatTypeId == typeFilter)) typeFilter = null;
        Status.Text = "";
        BuildFilters();
        Apply();
    }
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
        pendingConfirmation = null;
        // Every word must appear somewhere in the conversation (title or any message), in any order.
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(Conversation c) => words.All(w => c.ChatTypeName.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Messages.Any(m => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
        groups.Clear();
        var shown = 0;
        foreach (var day in all.Where(c => (typeFilter == null || c.ChatTypeId == typeFilter) && Matches(c)).GroupBy(c => c.Messages.Last().Time.ToLocalTime().Date))
        {
            var list = day.ToList();
            var items = list.Select((conversation, i) =>
            {
                var (badge, glyph) = LookFor?.Invoke(conversation) ?? (Theme.Brush("Accent"), "chat");
                var first = i == 0; var last = i == list.Count - 1;
                return new Item(conversation, badge, glyph, new Thickness(0, first ? 0 : 1, 0, 0), new CornerRadius(first ? 14 : 0, first ? 14 : 0, last ? 14 : 0, last ? 14 : 0));
            }).ToList();
            groups.Add(new(DayLabel(day.Key), items));
            shown += items.Count;
        }
        UpdateEmpty(shown);
    }
    private void OpenConversation(object sender, RoutedEventArgs e) => ConversationSelected?.Invoke((Conversation)((Button)sender).Tag);
    private void DeleteConversation(object sender, RoutedEventArgs e)
    {
        if (pendingConfirmation != null) pendingConfirmation.Visibility = Visibility.Collapsed;
        pendingConfirmation = ((Grid)((Button)sender).Parent).Children.OfType<Border>().First(b => Equals(b.Tag, "confirmation"));
        pendingConfirmation.Visibility = Visibility.Visible;
        ((Panel)pendingConfirmation.Child).Children.OfType<Button>().Last().Focus();
    }
    private void CancelDelete(object sender, RoutedEventArgs e)
    {
        if (pendingConfirmation != null) { pendingConfirmation.Visibility = Visibility.Collapsed; ((Grid)pendingConfirmation.Parent).Children.OfType<Button>().Last().Focus(); }
        pendingConfirmation = null;
    }
    private void ConfirmDelete(object sender, RoutedEventArgs e)
    {
        var conversation = (Conversation)((Button)sender).Tag;
        if (DeleteRequested?.Invoke(conversation) != true) { Status.Text = "לא ניתן לשמור את המחיקה. השיחה לא נמחקה."; return; }
        pendingConfirmation = null;
        all.Remove(conversation); Status.Text = "";
        if (typeFilter != null && !all.Any(c => c.ChatTypeId == typeFilter)) typeFilter = null;
        BuildFilters(); Apply();
        BackButton.Focus();
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
