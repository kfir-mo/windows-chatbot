using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Knowledge.Core;

namespace Chat;
public partial class HistoryPage : UserControl
{
    private Border? pendingConfirmation;
    private readonly ObservableCollection<Conversation> items = new();
    public event Action<Conversation>? ConversationSelected;
    public Func<Conversation, bool>? DeleteRequested { get; set; }
    public HistoryPage() { InitializeComponent(); ConversationList.ItemsSource = items; PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && pendingConfirmation != null) { CancelDelete(this, new()); e.Handled = true; } }; }
    public void Refresh(IEnumerable<Conversation> conversations)
    {
        items.Clear();
        foreach (var conversation in conversations.Where(c => c.HasUserMessages).OrderByDescending(c => c.Messages.Last().Time)) items.Add(conversation);
        UpdateEmpty();
    }
    private void OpenConversation(object sender, RoutedEventArgs e) => ConversationSelected?.Invoke((Conversation)((Button)sender).Tag);
    private void DeleteConversation(object sender, RoutedEventArgs e)
    {
        if (pendingConfirmation != null) pendingConfirmation.Visibility = Visibility.Collapsed;
        pendingConfirmation = ((Grid)((Button)sender).Parent).Children.OfType<Border>().First(b => Equals(b.Tag, "confirmation"));
        pendingConfirmation.Visibility = Visibility.Visible;
        ((StackPanel)pendingConfirmation.Child).Children.OfType<Button>().Last().Focus();
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
        items.Remove(conversation); Status.Text = ""; UpdateEmpty();
    }
    private void UpdateEmpty() { Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ConversationCount.Text = items.Count.ToString(); }
}
