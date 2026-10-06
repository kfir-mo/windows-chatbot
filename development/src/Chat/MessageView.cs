using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Knowledge.Core;

namespace Chat;
/// <summary>Builds chat message visuals. The panel is right-to-left, so logical Left is the visual right: user messages sit on the right, assistant replies on the left.</summary>
internal static class MessageView
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Geometry ChatShape = Geometry.Parse("M 3,2 L 21,2 Q 24,2 24,5 L 24,16 Q 24,19 21,19 L 11,19 L 5,24 L 5,19 L 3,19 Q 0,19 0,16 L 0,5 Q 0,2 3,2 Z");

    /// <summary>Hebrew wins unless the text is overwhelmingly Latin, so a Hebrew answer that starts with "VPN" still reads right-to-left.</summary>
    public static FlowDirection DirectionOf(string text)
    {
        int hebrew = 0, latin = 0;
        foreach (var c in text)
        {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF') hebrew++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }
        return hebrew == 0 && latin > 0 || latin > hebrew * 4 ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
    }

    public static FrameworkElement User(ChatMessage message)
    {
        var bubble = new Border
        {
            Background = (Brush)Application.Current.FindResource("AssistantBrush"),
            // Right-to-left mirrors corners: logical bottom-left is the visual bottom-right tail, on the bubble's own side.
            CornerRadius = new CornerRadius(16, 16, 16, 4), Padding = new Thickness(12, 8, 12, 9), MaxWidth = 260,
            Child = MainWindow.CreateMessageText(message.Text, true)
        };
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 36, 8) };
        column.Children.Add(bubble);
        // Under the bubble: the time on its own side and, while the message is hovered or focused, a copy button at the far end.
        var footer = new Grid { Margin = new Thickness(4, 2, 0, 0) };
        footer.Children.Add(new TextBlock { Text = message.Time.ToLocalTime().ToString("HH:mm"), FontSize = 11, Foreground = Theme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Style = Ui.Style("SmallGhostButton"), Content = new Icon("copy", 13, null, 2), Padding = new Thickness(5, 3, 5, 3), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "העתקת ההודעה", Visibility = Visibility.Hidden };
        System.Windows.Automation.AutomationProperties.SetName(copy, "העתקת ההודעה");
        copy.Click += async (_, _) =>
        {
            try { Clipboard.SetText(message.Text); copy.Content = new Icon("check", 13, Theme.Brush("Success"), 2.4); copy.ToolTip = "הועתק"; }
            catch { copy.ToolTip = "לא ניתן להעתיק"; }
            await Task.Delay(1600); copy.Content = new Icon("copy", 13, null, 2); copy.ToolTip = "העתקת ההודעה";
        };
        footer.Children.Add(copy);
        column.Children.Add(footer);
        void ShowCopy() => copy.Visibility = column.IsMouseOver || column.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
        column.Background = Brushes.Transparent;
        column.MouseEnter += (_, _) => ShowCopy(); column.MouseLeave += (_, _) => ShowCopy();
        column.IsKeyboardFocusWithinChanged += (_, _) => ShowCopy();
        return column;
    }

    public static FrameworkElement Assistant(ChatMessage message, Action<string> report)
    {
        var grid = new Grid { Margin = new Thickness(14, 4, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var avatar = Avatar(); Grid.SetColumn(avatar, 1); grid.Children.Add(avatar);
        var content = new StackPanel();
        content.Children.Add(Markdown(message.Text));
        if (message.Sources.Count > 0) content.Children.Add(MainWindow.CreateSourceTags(message.Sources, report));
        var card = new Border
        {
            Background = Theme.Brush("Surface"),
            // Logical bottom-right is the visual bottom-left: the tail points down to the avatar beside it.
            CornerRadius = new CornerRadius(16, 16, 4, 16), Padding = new Thickness(12, 9, 12, 10), Child = content
        };
        grid.Children.Add(card);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 4, 0) };
        footer.Children.Add(new TextBlock { Text = message.Time.ToLocalTime().ToString("HH:mm"), FontSize = 11, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center });
        var copyLabel = new TextBlock { Text = "העתקה", VerticalAlignment = VerticalAlignment.Center };
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal };
        copyContent.Children.Add(new TextBlock { Text = "\uE8C8", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11, Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        copyContent.Children.Add(copyLabel);
        var copy = new Button { Style = Ui.Style("SmallGhostButton"), Content = copyContent, FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0), ToolTip = "העתקת התשובה" };
        System.Windows.Automation.AutomationProperties.SetName(copy, "העתקת התשובה");
        copy.Click += async (_, _) =>
        {
            try { Clipboard.SetText(PlainText(message.Text)); copyLabel.Text = "הועתק ✓"; }
            catch { copyLabel.Text = "לא ניתן להעתיק"; }
            await Task.Delay(1600); copyLabel.Text = "העתקה";
        };
        footer.Children.Add(copy);
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        return grid;
    }

    public static Border Avatar() => new()
    {
        Width = 26, Height = 26, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 0),
        Background = (Brush)Application.Current.FindResource("AssistantBrush"),
        Child = new System.Windows.Shapes.Path { Data = ChatShape, Fill = Brushes.White, Width = 12, Height = 11, Stretch = Stretch.Uniform }
    };

    public static string PlainText(string markdown) => Regex.Replace(markdown, @"\*\*|__|`", "");

    /// <summary>Renders the small markdown subset AI replies use: paragraphs, headings, lists, code, bold, italic.</summary>
    public static RichTextBox Markdown(string text)
    {
        text = AnswerFormat.SplitInlineLists(text);
        var direction = DirectionOf(text);
        var doc = new FlowDocument { FlowDirection = direction, PagePadding = new Thickness(0), FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13.5, Foreground = Theme.Brush("Ink"), LineHeight = 20 };
        Paragraph? paragraph = null;
        List<string>? code = null;
        List? list = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                paragraph = null; list = null;
                if (code != null) { doc.Blocks.Add(CodeBlock(code)); code = null; continue; }
                code = [];
                continue;
            }
            if (code != null) { code.Add(raw); continue; }
            if (string.IsNullOrWhiteSpace(line) || Regex.IsMatch(line, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$")) { paragraph = null; list = null; continue; }
            var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                paragraph = null; list = null;
                var h = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Groups[1].Length <= 2 ? 16 : 15, Margin = new Thickness(0, 4, 0, 4), FlowDirection = direction };
                AddInlines(h.Inlines, heading.Groups[2].Value.TrimEnd('#', ' '));
                doc.Blocks.Add(h);
                continue;
            }
            var bullet = Regex.Match(line, @"^\s*[-*•]\s+(.+)$");
            var numbered = Regex.Match(line, @"^\s*(\d+)[.)]\s+(.+)$");
            if (bullet.Success || numbered.Success)
            {
                paragraph = null;
                var body = bullet.Success ? bullet.Groups[1].Value : numbered.Groups[2].Value;
                var marker = bullet.Success ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal;
                if (list == null || list.MarkerStyle != marker)
                {
                    list = new List { MarkerStyle = marker, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(22, 0, 0, 0), FlowDirection = direction };
                    if (numbered.Success && int.TryParse(numbered.Groups[1].Value, out var start) && start > 0) list.StartIndex = start;
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 2) };
                AddInlines(item.Inlines, body);
                list.ListItems.Add(new ListItem(item));
                continue;
            }
            list = null;
            if (paragraph == null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), FlowDirection = direction };
                doc.Blocks.Add(paragraph);
            }
            else paragraph.Inlines.Add(new LineBreak());
            AddInlines(paragraph.Inlines, Regex.Replace(line, @"^\s*>\s?", ""));
        }
        // An unterminated code fence still shows its code.
        if (code != null) doc.Blocks.Add(CodeBlock(code));
        // The last block's bottom margin would add empty space inside the card.
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return new RichTextBox
        {
            // IsDocumentEnabled lets the copy buttons inside code blocks receive clicks.
            Document = doc, IsReadOnly = true, IsDocumentEnabled = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), Margin = new Thickness(-4, 0, -4, 0), FocusVisualStyle = null, SelectionBrush = Theme.Brush("Accent"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    /// <summary>A code block (commands such as "podman machine ssh" or "pip install …") with its own copy button.</summary>
    private static BlockUIContainer CodeBlock(List<string> lines)
    {
        var text = string.Join("\n", lines).Trim('\n');
        var grid = new Grid { FlowDirection = FlowDirection.LeftToRight };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12.5,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush("Ink"), SelectionBrush = Theme.Brush("Accent"), TextAlignment = TextAlignment.Left
        });
        var copy = CopyButton(text, "העתקת הקוד");
        copy.VerticalAlignment = VerticalAlignment.Bottom; copy.Margin = new Thickness(6, 0, -5, -5);
        Grid.SetColumn(copy, 1); grid.Children.Add(copy);
        return new BlockUIContainer(AutoLineHeight(new Border { Child = grid, Background = Theme.Brush("Subtle"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 8, 7) })) { Margin = new Thickness(0, 2, 0, 10) };
    }
    /// <summary>Inline code (`pip install x`) is a chip that copies itself when clicked.</summary>
    /// <summary>Controls inside the message document would inherit its fixed 20px line height, which pushes icon glyphs up.</summary>
    private static T AutoLineHeight<T>(T element) where T : DependencyObject { TextBlock.SetLineHeight(element, double.NaN); return element; }
    private static bool IsWebAddress(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    /// <summary>A web address: clicking the text opens it in the browser, the icon beside it copies it. Left-to-right, so a
    /// URL inside a Hebrew sentence isn't split or reordered.</summary>
    private static Inline Link(string url, string label)
    {
        if (!IsWebAddress(url)) return new Run(label);
        var content = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        var text = new TextBlock { Text = label, Foreground = Theme.Brush("Link"), TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap, MaxWidth = 250, VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "פתיחה בדפדפן\n" + url };
        System.Windows.Automation.AutomationProperties.SetName(text, "פתיחת הקישור " + url);
        text.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { Process.Start(new ProcessStartInfo(new Uri(url).AbsoluteUri) { UseShellExecute = true }); } catch { }
        };
        content.Children.Add(text);
        var copy = CopyButton(url, "העתקת הקישור");
        copy.Width = copy.Height = 22; copy.FontSize = 11; copy.Margin = new Thickness(2, 0, 0, 0); copy.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(copy);
        return new InlineUIContainer(AutoLineHeight(content)) { BaselineAlignment = BaselineAlignment.Center };
    }
    private static Inline InlineCode(string code)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        content.Children.Add(new TextBlock { Text = code, FontFamily = Mono, FontSize = 12.5, Foreground = Theme.Brush("Ink"), TextWrapping = TextWrapping.Wrap, MaxWidth = 250, VerticalAlignment = VerticalAlignment.Center });
        var glyph = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10, Foreground = Theme.Brush("Muted"), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(glyph);
        var chip = new Button { Style = Ui.Style("ChipButton"), Content = content, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(1, 0, 1, 0), ToolTip = "לחצו להעתקה", Cursor = System.Windows.Input.Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(chip, "העתקה: " + code);
        chip.Click += async (_, _) =>
        {
            try { Clipboard.SetText(code); glyph.Text = ""; glyph.Foreground = Theme.Brush("Success"); } catch { }
            await Task.Delay(1400); glyph.Text = ""; glyph.Foreground = Theme.Brush("Muted");
        };
        return new InlineUIContainer(AutoLineHeight(chip)) { BaselineAlignment = BaselineAlignment.Center };
    }
    /// <summary>An icon-only copy button: no label or background, a check mark confirms the copy.</summary>
    private static Button CopyButton(string text, string name)
    {
        var button = new Button { Style = Ui.Style("IconButton"), Content = "", Width = 26, Height = 26, FontSize = 13, ToolTip = name, Foreground = Theme.Brush("Muted") };
        Ui.SetCornerRadius(button, new CornerRadius(13));
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += async (_, _) =>
        {
            try { Clipboard.SetText(text); button.Content = ""; button.Foreground = Theme.Brush("Success"); button.ToolTip = "הועתק"; } catch { }
            await Task.Delay(1400); button.Content = ""; button.Foreground = Theme.Brush("Muted"); button.ToolTip = name;
        };
        return button;
    }

    private static readonly Regex InlinePattern = new(@"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|`(?<c>[^`]+)`|\[(?<l>[^\]]+)\]\((?<lu>[^)\s]+)\)|(?<u>https?://[^\s<>()\[\]""'`]+[^\s<>()\[\]""'`.,;:!?])|(?<![\*\w])\*(?<i>[^\s*][^*]*?)\*(?![\*\w])", RegexOptions.Compiled);
    private static void AddInlines(InlineCollection inlines, string text)
    {
        int at = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups["b"].Success) inlines.Add(new Bold(new Run(m.Groups["b"].Value)));
            else if (m.Groups["c"].Success) inlines.Add(InlineCode(m.Groups["c"].Value));
            else if (m.Groups["l"].Success) inlines.Add(IsWebAddress(m.Groups["lu"].Value) ? Link(m.Groups["lu"].Value, m.Groups["l"].Value) : new Run(m.Groups["l"].Value) { Foreground = Theme.Brush("Link") });
            else if (m.Groups["u"].Success) inlines.Add(Link(m.Groups["u"].Value, m.Groups["u"].Value));
            else inlines.Add(new Italic(new Run(m.Groups["i"].Value)));
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }
}

/// <summary>Small colored badge identifying a source document's file type.</summary>
internal static class FileBadge
{
    public static Border For(string path)
    {
        var (label, color) = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => ("PDF", "#D93A3A"),
            ".docx" or ".doc" => ("W", "#2B5FD9"),
            ".xlsx" or ".xls" => ("X", "#1D7A45"),
            ".pptx" or ".ppt" => ("P", "#C8502A"),
            ".md" => ("MD", "#5D6F86"),
            ".txt" => ("TXT", "#5D6F86"),
            var other => (other.TrimStart('.').ToUpperInvariant() is { Length: > 0 and <= 4 } ext ? ext : "?", "#5D6F86")
        };
        return new Border
        {
            MinWidth = 18, Height = 18, Padding = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = label.Length > 1 ? 8.5 : 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight }
        };
    }
}
