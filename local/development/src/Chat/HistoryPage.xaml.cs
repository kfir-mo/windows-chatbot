using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
public partial class HistoryPage : UserControl
{
    /// <summary>One row: the conversation plus its assistant's color and icon.</summary>
    public sealed record Item(Conversation Conversation, Brush Badge, string Glyph);
    private Border? pendingConfirmation;
    private readonly ObservableCollection<Item> items = new();
    private List<Conversation> all = [];
    private string? typeFilter;
    public event Action<Conversation>? ConversationSelected;
    public Func<Conversation, bool>? DeleteRequested { get; set; }
    /// <summary>The badge (color, icon) of a conversation's assistant, so history matches the assistant's look.</summary>
    public Func<Conversation, (Brush Badge, string Glyph)>? LookFor { get; set; }
    public HistoryPage() { InitializeComponent(); ConversationList.ItemsSource = items; PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && pendingConfirmation != null) { CancelDelete(this, new()); e.Handled = true; } }; }
    public void Refresh(IEnumerable<Conversation> conversations)
    {
        all = conversations.Where(c => c.HasUserMessages).OrderByDescending(c => c.Messages.Last().Time).ToList();
        if (typeFilter != null && !all.Any(c => c.ChatTypeId == typeFilter)) typeFilter = null;
        BuildFilters();
        Apply();
    }
    private void BuildFilters()
    {
        Filters.Children.Clear();
        var types = all.GroupBy(c => c.ChatTypeId).Select(g => (Id: g.Key, Name: g.First().ChatTypeName, Count: g.Count())).ToList();
        // A filter is only useful when there are conversations from more than one assistant.
        Filters.Visibility = types.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        void Chip(string? id, string name, int count)
        {
            var selected = typeFilter == id;
            var chip = new Button { Style = Ui.Style("ChipButton"), Content = $"{name} · {count}", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4),
                Background = selected ? Theme.Brush("Selected") : Theme.Brush("Subtle"), Foreground = selected ? Theme.Brush("Link") : Theme.Brush("Ink"), FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
            System.Windows.Automation.AutomationProperties.SetName(chip, "סינון: " + name);
            chip.Click += (_, _) => { typeFilter = id; BuildFilters(); Apply(); };
            Filters.Children.Add(chip);
        }
        Chip(null, "הכל", all.Count);
        foreach (var (id, name, count) in types) Chip(id, string.IsNullOrWhiteSpace(name) ? "צ׳אט" : name, count);
    }
    private void FilterChanged(object sender, TextChangedEventArgs e) { SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Apply(); }
    private void Apply()
    {
        if (ConversationList == null) return;
        pendingConfirmation = null;
        // Every word must appear somewhere in the conversation (title or any message), in any order.
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(Conversation c) => words.All(w => c.ChatTypeName.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Messages.Any(m => m.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
        items.Clear();
        foreach (var conversation in all.Where(c => (typeFilter == null || c.ChatTypeId == typeFilter) && Matches(c)))
        {
            var (badge, glyph) = LookFor?.Invoke(conversation) ?? (Theme.Brush("Accent"), "");
            items.Add(new(conversation, badge, glyph));
        }
        UpdateEmpty();
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
    }
    private void UpdateEmpty()
    {
        var filtered = all.Count > 0;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = filtered ? "לא נמצאו שיחות" : "אין עדיין שיחות שמורות";
        EmptyDetail.Text = filtered ? "נסו מילה אחרת או בחרו \"הכל\"" : "השיחות שלכם יופיעו כאן";
        ConversationCount.Text = items.Count == all.Count ? all.Count.ToString() : $"{items.Count}/{all.Count}";
    }
}
