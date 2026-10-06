using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
public partial class ChatTypesPage : Window
{
    private readonly string editingId;
    private readonly List<PathRow> rows = [];
    private bool busy;
    private bool closed;
    public Func<ChatType, Task>? SaveRequested { get; set; }
    public Func<string, Task>? DeleteRequested { get; set; }
    private sealed record PathRow(Border Frame, TextBox Input, TextBlock Status, TextBlock Detail);
    public ChatTypesPage(ChatType? type = null)
    {
        InitializeComponent();
        editingId = type?.Id ?? Guid.NewGuid().ToString("N");
        Heading.Text = type == null ? "עוזר אישי חדש" : "עריכת עוזר אישי";
        CheckButton.Content = type == null ? "יצירת עוזר" : "שמירת שינויים";
        TypeName.Text = type?.Name ?? "";
        CustomPrompt.Text = type?.Prompt ?? "";
        DeleteButton.Visibility = type == null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in type?.Paths ?? []) AddRow(path);
        if (rows.Count == 0) AddRow("");
        Closed += (_, _) => closed = true;
        Loaded += (_, _) => TypeName.Focus();
    }
    private void Dismiss(object sender, RoutedEventArgs e) => Close();
    private void ResizePrompt(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e) => CustomPrompt.Height = Math.Clamp(CustomPrompt.ActualHeight + e.VerticalChange, 76, 220);
    private void DialogKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void NameChanged(object sender, TextChangedEventArgs e) { if (Feedback != null) Feedback.Text = ""; }
    private void AddPath(object sender, RoutedEventArgs e) { AddRow(""); rows[^1].Input.Focus(); Feedback.Text = ""; }
    private void AddRow(string path)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var input = new TextBox { Text = path, Height = 44, FlowDirection = FlowDirection.LeftToRight, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(input, "נתיב תיקיית תיעוד — אופציונלי");
        var status = new TextBlock { Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Theme.Brush("Muted") };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        Grid.SetColumn(status, 1);
        var test = new Button { Style = Ui.Style("IconButton"), Content = "\uE73E", Foreground = Theme.Brush("Accent"), ToolTip = "בדיקת גישה" };
        System.Windows.Automation.AutomationProperties.SetName(test, "בדיקת גישה לנתיב");
        Grid.SetColumn(test, 2);
        var remove = new Button { Style = Ui.Style("IconButton"), Content = "\uE74D", ToolTip = "הסרת נתיב" };
        Grid.SetColumn(remove, 3);
        System.Windows.Automation.AutomationProperties.SetName(remove, "הסרת נתיב");
        var detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 4), Foreground = Theme.Brush("Muted"), Visibility = Visibility.Collapsed };
        Grid.SetRow(detail, 1); Grid.SetColumnSpan(detail, 4);
        var inputArea = new Grid(); inputArea.Children.Add(input);
        var placeholder = new TextBlock { Text = @"C:\Docs או \\server\share", FlowDirection = FlowDirection.LeftToRight, IsHitTestVisible = false, Foreground = Theme.Brush("Muted"), Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center }; inputArea.Children.Add(placeholder);
        void Hint() => placeholder.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Hint(); Hint();
        grid.Children.Add(inputArea); grid.Children.Add(status); grid.Children.Add(test); grid.Children.Add(remove); grid.Children.Add(detail);
        var browse = new Button { Style = Ui.Style("LinkButton"), Content = "בחירת תיקייה…", FontSize = 12, MinHeight = 30, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "בחירת תיקיית מסמכים" };
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(this) == true) input.Text = picker.FolderName; };
        var rowPanel = new StackPanel(); rowPanel.Children.Add(grid); rowPanel.Children.Add(browse);
        var frame = new Border { Child = rowPanel, Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), Padding = new Thickness(6), Background = Brush("#F4F7FB") };
        var row = new PathRow(frame, input, status, detail);
        input.TextChanged += (_, _) => { SetState(row, "", "#586B83", "#F4F7FB"); detail.Visibility = Visibility.Collapsed; Feedback.Text = ""; UpdatePathHint(); };
        test.Click += async (_, _) => await CheckPathAsync(rows.IndexOf(row));
        remove.Click += (_, _) => { rows.Remove(row); PathRows.Children.Remove(frame); if (rows.Count == 0) AddRow(""); UpdatePathHint(); Feedback.Text = ""; };
        rows.Add(row); PathRows.Children.Add(frame); UpdatePathHint();
    }
    private void UpdatePathHint() => PathHint.Visibility = rows.All(r => string.IsNullOrWhiteSpace(r.Input.Text)) ? Visibility.Visible : Visibility.Collapsed;
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static void SetState(PathRow row, string text, string color, string background)
    { row.Status.Text = text; row.Status.Foreground = Brush(color); row.Frame.Background = Brush(background); }
    public async Task CheckPathAsync(int rowIndex)
    {
        if (busy || rowIndex < 0 || rowIndex >= rows.Count) return;
        var row = rows[rowIndex];
        if (string.IsNullOrWhiteSpace(row.Input.Text))
        { row.Detail.Text = "הזינו נתיב כדי לבדוק גישה. אפשר ליצור צ׳אט גם ללא נתיב."; row.Detail.Visibility = Visibility.Visible; return; }
        busy = true; Fields.IsEnabled = CheckButton.IsEnabled = false;
        var identity = WindowsIdentity.GetCurrent().Name;
        SetState(row, "בודק…", "#1673FF", "#EDF4FF");
        row.Detail.Visibility = Visibility.Collapsed;
        try
        {
            var path = ChatType.NormalizePaths([row.Input.Text]).Single();
            await Task.Run(() => { using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); entries.MoveNext(); }).WaitAsync(TimeSpan.FromSeconds(15));
            if (closed) return;
            SetState(row, "✓ עבר", "#16804A", "#EDF9F1");
            row.Detail.Text = "הבדיקה בוצעה עם המשתמש " + identity + ". ניתן לגשת לתיקייה.";
        }
        catch (Exception ex)
        {
            if (closed) return;
            var reason = ex switch
            {
                TimeoutException => "השרת לא השיב בזמן. בדקו חיבור רשת וזמינות של השרת.",
                UnauthorizedAccessException => "למשתמש אין הרשאת גישה לתיקייה.",
                DirectoryNotFoundException => "התיקייה או נתיב הרשת לא נמצאו.",
                InvalidDataException => ex.Message,
                IOException when (ex.HResult & 0xffff) == 53 => "נתיב הרשת לא נמצא. בדקו את שם השרת ואת חיבור הרשת.",
                IOException when (ex.HResult & 0xffff) == 67 => "שם השיתוף ברשת לא נמצא.",
                IOException when (ex.HResult & 0xffff) == 1326 => "Windows דחה את ההזדהות מול שרת הקבצים.",
                IOException => "לא ניתן לגשת לתיקייה. בדקו נתיב, חיבור רשת והרשאות (קוד " + (ex.HResult & 0xffff) + ").",
                ArgumentException or NotSupportedException => "מבנה הנתיב אינו תקין.",
                _ => "לא ניתן להשלים את הבדיקה. בדקו את הנתיב ואת הרשאות הגישה."
            };
            SetState(row, "✕ נכשל", "#C63B4A", "#FFF0F1");
            row.Detail.Text = "הבדיקה בוצעה עם המשתמש " + identity + ". " + reason;
        }
        finally { busy = false; Fields.IsEnabled = CheckButton.IsEnabled = true; if (!closed) row.Detail.Visibility = Visibility.Visible; }
    }
    private async void Save(object sender, RoutedEventArgs e) => await SaveAsync();
    public async Task SaveAsync()
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(TypeName.Text)) { Feedback.Text = "שם העוזר הוא שדה חובה."; TypeName.Focus(); return; }
        busy = true; Fields.IsEnabled = CheckButton.IsEnabled = false;
        try
        {
            var paths = ChatType.NormalizePaths(rows.Select(r => r.Input.Text));
            if (SaveRequested != null) await SaveRequested(new ChatType { Id = editingId, Name = TypeName.Text.Trim(), Paths = paths, Prompt = CustomPrompt.Text.Trim() });
            if (!closed) Close();
        }
        catch (Exception ex) { Feedback.Text = ex.Message; }
        finally { busy = false; Fields.IsEnabled = CheckButton.IsEnabled = true; }
    }
    private async void DeleteType(object sender, RoutedEventArgs e)
    {
        if (busy || DeleteRequested == null) return;
        busy = true; Fields.IsEnabled = CheckButton.IsEnabled = false;
        try { await DeleteRequested(editingId); Close(); }
        catch (Exception ex) { Feedback.Text = ex.Message; }
        finally { busy = false; Fields.IsEnabled = CheckButton.IsEnabled = true; }
    }
}
