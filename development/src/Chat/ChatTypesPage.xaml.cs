using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Knowledge.Core;

namespace Chat;
/// <summary>The "new assistant" and "edit assistant" screen: one form, pre-filled when editing, with a delete section only then.</summary>
public partial class ChatTypesPage : UserControl
{
    private readonly string editingId;
    private readonly bool editing;
    private readonly bool searchSubfolders;
    private readonly List<FolderRow> rows = [];
    private readonly LookPicker picker;
    private bool busy;
    /// <summary>How many saved conversations belong to the assistant being edited; the delete confirmation offers to remove them too.</summary>
    public int ConversationCount { get; set; }
    private bool closed;
    public Func<ChatType, Task>? SaveRequested { get; set; }
    /// <summary>Deletes the assistant; the flag says whether its conversations leave the history too.</summary>
    public Func<string, bool, Task>? DeleteRequested { get; set; }
    /// <summary>Raised once when the form is done (saved, deleted or dismissed).</summary>
    public event Action? Closed;
    public TypeLook Look => picker.Look;
    private sealed record FolderRow(string Path, Border Frame, TextBlock State, Icon Spinner, CancellationTokenSource Scan);
    public ChatTypesPage(ChatType? type = null, TypeLook? look = null, bool searchSubfolders = true)
    {
        InitializeComponent();
        editing = type != null;
        this.searchSubfolders = searchSubfolders;
        editingId = type?.Id ?? Guid.NewGuid().ToString("N");
        Heading.Text = editing ? "עריכת עוזר" : "עוזר אישי חדש";
        CheckButton.Content = editing ? "שמירת שינויים" : "יצירת עוזר";
        TypeName.Text = type?.Name ?? "";
        CustomPrompt.Text = type?.Prompt ?? "";
        DangerCard.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (type != null) ConfirmTitle.Text = $"למחוק את \"{type.Name}\"?";
        picker = new LookPicker(look ?? new TypeLook("green", "folder"));
        picker.Changed += ShowLook;
        // The inline picker stays folded until the avatar is pressed.
        LookHost.Content = picker;
        ShowLook(picker.Look);
        foreach (var path in type?.Paths ?? []) AddRow(path);
        UpdateFolders();
        Unloaded += (_, _) => { closed = true; foreach (var row in rows) row.Scan.Cancel(); };
        Loaded += (_, _) => TypeName.Focus();
    }
    private void ShowLook(TypeLook look)
    {
        var brush = Looks.Brush(look.Color);
        AvatarFace.Background = CheckButton.Background = AddButton.Background = brush;
        AvatarIcon.Kind = look.Glyph;
    }
    private void ToggleLook(object sender, RoutedEventArgs e) => LookCard.Visibility = LookCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private void CloseLook(object sender, RoutedEventArgs e) { LookCard.Visibility = Visibility.Collapsed; AvatarButton.Focus(); }
    private void Close()
    {
        if (closed) return;
        closed = true;
        foreach (var row in rows) row.Scan.Cancel();
        Closed?.Invoke();
    }
    private void Dismiss(object sender, RoutedEventArgs e) => Close();
    private void DialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (ConfirmLayer.Visibility == Visibility.Visible) CancelDelete(this, e);
        else Close();
    }
    private void NameChanged(object sender, TextChangedEventArgs e) { NameHint.Visibility = TypeName.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ShowFeedback(""); }
    private void PromptChanged(object sender, TextChangedEventArgs e) => PromptHint.Visibility = CustomPrompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void PathTyped(object sender, TextChangedEventArgs e)
    {
        FirstPathHint.Visibility = FirstPath.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPathFeedback("");
    }
    private void ShowFeedback(string text) { if (Feedback == null) return; Feedback.Text = text; Feedback.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void ShowPathFeedback(string text) { if (PathFeedback == null) return; PathFeedback.Text = text; PathFeedback.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }

    // ---- Folders ----
    /// <summary>"עיון" picks a folder; the document button picks one specific file (a path can also be typed or pasted).</summary>
    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "בחירת תיקיית מסמכים" };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }
    private string? PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "בחירת מסמך", Filter = "מסמכים|" + string.Join(";", DocumentIndex.Extensions.Select(x => "*" + x)) };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
    private void BrowseFileFirst(object sender, RoutedEventArgs e) { if (PickFile() is { } path) _ = AddFolderAsync(path); }
    /// <summary>Files dragged from Explorer: the overlay shows while they are over the sources. Preview events, so the path
    /// fields (which would take the drop as text) never see it.</summary>
    private void SourcesDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy; e.Handled = true;
        DropOverlay.Visibility = Visibility.Visible;
    }
    private void SourcesDragLeave(object sender, DragEventArgs e)
    {
        // Moving between the controls inside raises a leave too: only leaving the whole area hides the overlay.
        var at = e.GetPosition(SourcesArea);
        if (at.X > 0 && at.Y > 0 && at.X < SourcesArea.ActualWidth && at.Y < SourcesArea.ActualHeight) return;
        DropOverlay.Visibility = Visibility.Collapsed;
    }
    /// <summary>Each dropped folder or document is added like a typed path (checked, counted, duplicates refused).</summary>
    private async void DropSources(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        foreach (var path in paths) await AddFolderAsync(path);
    }
    // Choosing from the computer adds at once; only a typed path waits for "הוספה".
    private void BrowseFirst(object sender, RoutedEventArgs e) { if (PickFolder() is { } path) _ = AddFolderAsync(path); }
    private static bool LooksLikeFile(string path) => DocumentIndex.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    private void AddFirst(object sender, RoutedEventArgs e) => _ = AddTypedAsync(FirstPath);
    private void FirstPathKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; _ = AddTypedAsync(FirstPath); } }
    private async Task AddTypedAsync(TextBox input)
    {
        if (string.IsNullOrWhiteSpace(input.Text)) { ShowPathFeedback("הזינו נתיב לתיקייה או לקובץ, או בחרו מהמחשב."); input.Focus(); return; }
        if (await AddFolderAsync(input.Text))
            input.Clear();
        input.Focus();
    }
    /// <summary>Adds a folder or a single document (any number of them) and scans it for supported documents. False when the path is invalid or already listed.</summary>
    public async Task<bool> AddFolderAsync(string path)
    {
        string normalized;
        try { normalized = ChatType.NormalizePaths([path]).Single(); }
        catch (Exception ex) { ShowPathFeedback(ex is InvalidDataException ? ex.Message : "מבנה הנתיב אינו תקין."); return false; }
        if (rows.Any(r => r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))) { ShowPathFeedback("הנתיב כבר ברשימה."); return false; }
        ShowPathFeedback(""); ShowFeedback("");
        var row = AddRow(normalized);
        UpdateFolders();
        await row;
        return true;
    }
    private Task AddRow(string path)
    {
        var grid = new Grid { Margin = new Thickness(12, 10, 4, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = Theme.Brush("Canvas"), VerticalAlignment = VerticalAlignment.Top,
            Child = new Icon(LooksLikeFile(path) ? "file" : "folder", 18, Theme.Brush("Secondary")) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var text = new StackPanel { Margin = new Thickness(12, 0, 6, 0) };
        text.Children.Add(new TextBlock { Text = FolderName(path), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = ShortPath(path), FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, Foreground = Theme.Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2), ToolTip = path });
        var spinner = new Icon("spinner", 13, Theme.Brush("Muted"), 2.6) { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        var state = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(state, System.Windows.Automation.AutomationLiveSetting.Polite);
        var status = new StackPanel { Orientation = Orientation.Horizontal };
        status.Children.Add(spinner); status.Children.Add(state);
        text.Children.Add(status);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var remove = new Button { Style = Ui.Style("IconButton"), Content = new Icon("close", 18, null, 2), VerticalAlignment = VerticalAlignment.Center, ToolTip = "הסרה מהרשימה" };
        System.Windows.Automation.AutomationProperties.SetName(remove, "הסרה מהרשימה: " + FolderName(path));
        Grid.SetColumn(remove, 2); grid.Children.Add(remove);
        var frame = new Border { Child = grid, BorderBrush = Theme.Brush("Divider") };
        var row = new FolderRow(path, frame, state, spinner, CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None));
        remove.Click += (_, _) =>
        {
            row.Scan.Cancel();
            var position = rows.IndexOf(row);
            rows.Remove(row); PathRows.Children.Remove(frame); UpdateFolders(); ShowFeedback("");
            // Keep the keyboard in the list: the next row's remove button, or the path field.
            var next = rows.Count == 0 ? null : rows[Math.Min(position, rows.Count - 1)];
            if (next != null) ((Grid)next.Frame.Child).Children.OfType<Button>().Last().Focus(); else if (rows.Count == 0) FirstPath.Focus();
        };
        rows.Add(row); PathRows.Children.Add(frame);
        return ScanAsync(row);
    }
    private void UpdateFolders()
    {
        for (int i = 0; i < rows.Count; i++) rows[i].Frame.BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0);
        FolderCard.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>Long paths keep their start and their last folders: "D:\…\samples\HR" (the full path is in the tooltip).</summary>
    private static string ShortPath(string path)
    {
        if (path.Length <= 34) return path;
        var parts = path.Split(Path.DirectorySeparatorChar);
        var root = path.StartsWith(@"\\") && parts.Length > 3 ? @"\\" + parts[2] : parts[0];
        var tail = string.Join(Path.DirectorySeparatorChar, parts.TakeLast(2));
        return root.Length + tail.Length > 30 ? "…" + Path.DirectorySeparatorChar + parts[^1] : root + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + tail;
    }
    private static string FolderName(string path) { var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)); return string.IsNullOrEmpty(name) ? path : name; }
    /// <summary>Counts the files the index will read: supported types, skipping hidden/system files, Office lock files and links.</summary>
    public static int CountDocuments(string root, bool subfolders)
    {
        // A single document counts as one, when the index can read its type.
        if (File.Exists(root)) return DocumentIndex.Extensions.Contains(Path.GetExtension(root).ToLowerInvariant()) ? 1 : throw new NotSupportedException();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        using (var probe = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) probe.MoveNext();
        var options = new EnumerationOptions { RecurseSubdirectories = subfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(root, "*", options).Count(file =>
        {
            var name = Path.GetFileName(file);
            return !name.StartsWith("~$") && !name.StartsWith(".~lock") && DocumentIndex.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant());
        });
    }
    private async Task ScanAsync(FolderRow row)
    {
        row.Spinner.Visibility = Visibility.Visible;
        row.State.Text = "סורק…"; row.State.Foreground = Theme.Brush("Muted");
        if (SystemParameters.ClientAreaAnimation) ((RotateTransform)row.Spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        try
        {
            var count = await Task.Run(() => CountDocuments(row.Path, searchSubfolders), row.Scan.Token).WaitAsync(TimeSpan.FromSeconds(30), row.Scan.Token);
            if (row.Scan.IsCancellationRequested) return;
            row.State.Text = count == 0 ? "לא נמצאו מסמכים נתמכים" : count == 1 ? "נמצא מסמך אחד" : $"נמצאו {count} מסמכים";
            row.State.Foreground = count == 0 ? Theme.Brush("Secondary") : Theme.Brush("Success");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (row.Scan.IsCancellationRequested) return;
            row.State.Text = ex switch
            {
                TimeoutException => "התיקייה לא השיבה בזמן. בדקו חיבור רשת וזמינות השרת.",
                UnauthorizedAccessException => "אין הרשאת גישה לתיקייה עבור " + WindowsIdentity.GetCurrent().Name + ".",
                NotSupportedException => "סוג הקובץ אינו נתמך.",
                DirectoryNotFoundException => "התיקייה, הקובץ או נתיב הרשת לא נמצאו.",
                IOException when (ex.HResult & 0xffff) == 53 => "נתיב הרשת לא נמצא. בדקו את שם השרת ואת חיבור הרשת.",
                IOException when (ex.HResult & 0xffff) == 67 => "שם השיתוף ברשת לא נמצא.",
                IOException when (ex.HResult & 0xffff) == 1326 => "Windows דחה את ההזדהות מול שרת הקבצים.",
                IOException => "לא ניתן לגשת לתיקייה (קוד " + (ex.HResult & 0xffff) + ").",
                _ => "לא ניתן לסרוק את התיקייה. בדקו נתיב והרשאות."
            };
            row.State.Foreground = Theme.Brush("Danger");
        }
        finally
        {
            // Also when cancelled (the row removed, the form closed): a forever animation would keep the app busy.
            ((RotateTransform)row.Spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
            if (!row.Scan.IsCancellationRequested) { row.Spinner.Visibility = Visibility.Collapsed; }
        }
    }

    // ---- Save / delete ----
    private async void Save(object sender, RoutedEventArgs e) => await SaveAsync();
    public async Task SaveAsync()
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(TypeName.Text)) { ShowFeedback("שם העוזר הוא שדה חובה."); TypeName.Focus(); return; }
        busy = true; Fields.IsEnabled = CheckButton.IsEnabled = false;
        try
        {
            // A path typed in a box but not yet added counts too.
            var typed = new[] { FirstPath.Text }.Where(p => !string.IsNullOrWhiteSpace(p));
            var paths = ChatType.NormalizePaths(rows.Select(r => r.Path).Concat(typed));
            if (SaveRequested != null) await SaveRequested(new ChatType { Id = editingId, Name = TypeName.Text.Trim(), Paths = paths, Prompt = CustomPrompt.Text.Trim() });
            Close();
        }
        catch (Exception ex) { ShowFeedback(ex.Message); }
        finally { busy = false; Fields.IsEnabled = CheckButton.IsEnabled = true; }
    }
    private void DeleteType(object sender, RoutedEventArgs e)
    {
        if (busy || DeleteRequested == null) return;
        DeleteHistory.IsChecked = true;
        DeleteHistory.Visibility = ConversationCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteHistoryText.Text = "למחוק גם את השיחות עם עוזר זה מהיסטוריית השיחות" + (ConversationCount > 1 ? $" ({ConversationCount})" : "");
        ConfirmLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => CancelDeleteButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
    private void CancelDelete(object sender, RoutedEventArgs e) { ConfirmLayer.Visibility = Visibility.Collapsed; DeleteButton.Focus(); }
    private void ConfirmScrimClicked(object sender, MouseButtonEventArgs e) { CancelDelete(sender, e); e.Handled = true; }
    private async void ConfirmDelete(object sender, RoutedEventArgs e) => await DeleteAsync();
    public async Task DeleteAsync()
    {
        if (busy || DeleteRequested == null) return;
        busy = true; ConfirmLayer.Visibility = Visibility.Collapsed; Fields.IsEnabled = CheckButton.IsEnabled = false;
        try { await DeleteRequested(editingId, DeleteHistory.IsChecked == true); Close(); }
        catch (Exception ex) { ShowFeedback(ex.Message); }
        finally { busy = false; Fields.IsEnabled = CheckButton.IsEnabled = true; }
    }
}
