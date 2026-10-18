using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Knowledge.Core;

namespace Chat;
/// <summary>A short tour of what is not obvious, on the first start and from the ⋯ menu ("הסבר על האפליקציה"). Each step can open a part of the app
/// (the assistant switcher, the new assistant form, the menu), dims the panel except what it explains (outlined) and shows a
/// short card beside it with "הבא" / "הקודם" and a close button. Moving on closes what the step opened.</summary>
public partial class MainWindow
{
    /// <summary>Area, when set, is what stays undimmed around the outlined Target (the whole menu around one item).</summary>
    private sealed record TourStep(Func<FrameworkElement?> Target, string Title, string Text, Action? Enter = null, Func<FrameworkElement?>? Area = null);
    private List<TourStep> tour = [];
    private int tourIndex;
    private Grid? tourLayer;
    /// <summary>The new assistant form the tour opened (never saved), closed when the tour moves on.</summary>
    private ChatTypesPage? tourForm;
    /// <summary>The first start without an API key: the tour ends on the key and then opens the AI settings to type it.</summary>
    private bool tourOpensSettings;
    private void StartTour() => StartTour(false);
    private void StartTour(bool firstRun)
    {
        CloseOverlay(false); CloseSources();
        if (HistoryView.Visibility == Visibility.Visible || ScreenLayer.Visibility == Visibility.Visible) ShowPage("chat");
        // The switcher and the form cannot open while an answer is on its way.
        var free = running.Count == 0 && personalAllowed;
        FrameworkElement? Popover() => OverlayHost.Content as FrameworkElement;
        FrameworkElement? InForm(string name) => tourForm?.FindName(name) as FrameworkElement;
        tourOpensSettings = firstRun && BuildMode.Local && (config == null || string.IsNullOrEmpty(AiCredentials.Resolve(config.Ai)) || LocalAiCatalog.Load().Models.Count == 0);
        var noModels = BuildMode.Local && LocalAiCatalog.Load().Models.Count == 0;
        tour = [new(() => null, firstRun ? "ברוכים הבאים ל־Chaty" : "סיור קצר", tourOpensSettings ? "כמה דברים שכדאי להכיר, ובסוף נגדיר את החיבור ל־AI." : "כמה דברים שכדאי להכיר.")];
        if (free)
        {
            tour.Add(new(() => ChatTypePicker, "עוזרים", "לחיצה על שם הצ׳אט למעלה פותחת את הרשימה: הצ׳אט הכללי והעוזרים שלכם."));
            tour.Add(new(() => Descendants(Popover()).OfType<Button>().FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "עוזר אישי חדש"),
                "עוזר אישי חדש", "ברשימה, ״עוזר אישי חדש״ יוצר עוזר שעונה מהמסמכים שלכם.", () => PickChatType(this, new()), Popover));
            tour.Add(new(() => InForm("SourcesArea"), "עוזר אישי", "מוסיפים תיקיות או מסמכים, והעוזר עונה רק מהם, עם המקור.", OpenTourForm));
            tour.Add(new(() => InForm("CustomPrompt"), "הנחיות", "אפשר לכתוב לעוזר איך לענות, למשל באיזה טון או מה לציין תמיד.", OpenTourForm));
        }
        if (Showing(ModeSwitch)) tour.Add(new(() => ModeSwitch, "מצב תשובה", "מהיר: תשובה קצרה. רגיל: מאוזנת. מעמיק: מלאה ומפורטת."));
        if (Showing(MaxSearchButton)) tour.Add(new(() => MaxSearchButton, "חיפוש מקסימלי", "כשמשהו לא נמצא, מחפש לעומק."));
        if (BuildMode.Local && running.Count == 0)
        {
            tour.Add(new(() => Descendants(Popover()).OfType<Button>().FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "הגדרות AI"),
                "הגדרות AI", "נמצאות בתפריט של שלוש הנקודות למעלה.", () => ShowOptions(this, new()), Popover));
            // Without config.json there is nothing to choose yet: the file comes first, then the key.
            var settingsText = noModels ? "קודם שומרים את קובץ ההגדרות config.json בתיקייה שמוצגת כאן ולוחצים על רענון. אחר כך בוחרים מודל ומדביקים את מפתח ה־API."
                : "כאן בוחרים מודל ומדביקים את מפתח ה־API האישי.";
            if (tourOpensSettings) settingsText += " בסיום המסך יישאר פתוח להגדרה.";
            tour.Add(new(() => tourSettings is not { } s ? null : noModels ? s.MissingArea : s.KeyArea is { } key && Showing(key) ? key : s, "הגדרות AI", settingsText, OpenTourSettings));
        }
        if (!tourOpensSettings) tour.Add(new(() => null, "זהו", "את השאלה האחרונה אפשר לערוך עם העיפרון שמתחתיה."));
        tourIndex = 0;
        if (tourLayer == null)
        {
            tourLayer = new Grid { Background = Brushes.Transparent };
            Grid.SetRowSpan(tourLayer, 2); System.Windows.Controls.Panel.SetZIndex(tourLayer, 100);
            // Clicks on the dimmed part do nothing: only the card's buttons move the tour.
            tourLayer.MouseLeftButtonDown += (_, e) => e.Handled = true;
            PanelRoot.Children.Add(tourLayer);
            PanelRoot.SizeChanged += (_, _) => { if (tourLayer.Visibility == Visibility.Visible) ShowTourStep(); };
        }
        tourLayer.Visibility = Visibility.Visible;
        ShowTourStep();
    }
    /// <summary>The AI settings screen the tour opened; closed (cancelled, nothing saved) when the tour moves on.</summary>
    private LocalSettingsPanel? tourSettings;
    private void OpenTourSettings()
    {
        _ = OpenLocalSettingsAsync();
        tourSettings = ScreenHost.Content as LocalSettingsPanel;
    }
    /// <summary>A new assistant form for the tour to point into; the same one stays open for the steps that use it.</summary>
    private void OpenTourForm()
    {
        if (tourForm != null && ScreenHost.Content == tourForm) return;
        OpenChatTypeDialog(null);
        tourForm = ScreenHost.Content as ChatTypesPage;
    }
    /// <summary>Closes what the previous step opened (a popover, the form), unless the next step uses the same form.</summary>
    private void TourReset(TourStep? next)
    {
        CloseOverlay(false);
        if (tourSettings != null) { if (ScreenHost.Content == tourSettings) { tourSettings.Cancel(); CloseScreen(tourSettings); } tourSettings = null; }
        if (tourForm != null && (next?.Enter != (Action)OpenTourForm || ScreenHost.Content != tourForm)) { if (ScreenHost.Content == tourForm) CloseScreen(tourForm); tourForm = null; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var below in Descendants(child)) yield return below; }
    }
    private void EndTour()
    {
        if (tourLayer == null || tourLayer.Visibility != Visibility.Visible) return;
        var keepSettings = tourOpensSettings && tourSettings != null && ScreenHost.Content == tourSettings;
        if (keepSettings) { tourSettings = null; tourOpensSettings = false; }
        TourReset(null);
        tourLayer.Visibility = Visibility.Collapsed;
        ClearTourLayer();
        if (!keepSettings) Question.Focus();
        if (tourOpensSettings) { tourOpensSettings = false; _ = OpenLocalSettingsAsync(); }
    }
    /// <summary>Removes the step's parts and stops the outline's pulse, which would otherwise keep running unseen.</summary>
    private void ClearTourLayer()
    {
        foreach (var part in tourLayer!.Children.OfType<UIElement>()) part.BeginAnimation(OpacityProperty, null);
        tourLayer.Children.Clear();
    }
    /// <summary>On the panel now: laid out, and it and every parent up to the panel visible.</summary>
    private bool Showing(FrameworkElement element)
    {
        if (element.ActualWidth <= 0) return false;
        for (DependencyObject? at = element; at != null && at != PanelRoot; at = VisualTreeHelper.GetParent(at))
            if (at is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    private bool TourOpen => tourLayer?.Visibility == Visibility.Visible;
    private void ShowTourStep()
    {
        if (tourLayer == null) return;
        var step = tour[tourIndex];
        TourReset(step);
        step.Enter?.Invoke();
        // The tour's own dimming replaces the popover's.
        Scrim.Background = Brushes.Transparent;
        PanelRoot.UpdateLayout();
        var size = new Size(PanelRoot.ActualWidth, PanelRoot.ActualHeight);
        ClearTourLayer();
        // The dimmed panel with a rounded hole around the part this step explains, outlined in white.
        Rect? Bounds(FrameworkElement? element)
        {
            if (element == null || !Showing(element)) return null;
            var bounds = element.TransformToAncestor(PanelRoot).TransformBounds(new Rect(element.RenderSize));
            bounds.Inflate(4, 4);
            bounds.Intersect(new Rect(size));
            return bounds;
        }
        var mark = Bounds(step.Target());
        Rect? hole = mark is { } m && Bounds(step.Area?.Invoke()) is { } area ? Rect.Union(area, m) : mark;
        Geometry shade = new RectangleGeometry(new Rect(size));
        if (hole is { } h) shade = new CombinedGeometry(GeometryCombineMode.Exclude, shade, new RectangleGeometry(h, 10, 10));
        tourLayer.Children.Add(new System.Windows.Shapes.Path { Data = shade, Fill = new SolidColorBrush(Color.FromArgb(0xC4, 0x0B, 0x11, 0x20)), IsHitTestVisible = false });
        if (mark is { } ring)
        {
            var outline = new Border { Width = ring.Width + 6, Height = ring.Height + 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(ring.X - 3, ring.Y - 3, 0, 0),
                CornerRadius = new CornerRadius(13), BorderBrush = Theme.Brush("AssistantBrush"), BorderThickness = new Thickness(3), IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 } };
            tourLayer.Children.Add(outline);
            if (SystemParameters.ClientAreaAnimation) outline.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(750)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        var card = TourCard(step);
        // Under the part when there is room, otherwise above it; a step without a part is in the middle.
        var width = Math.Min(340, size.Width - 32);
        card.Width = width;
        card.Measure(new Size(width, double.PositiveInfinity));
        var height = card.DesiredSize.Height;
        double top;
        var below = true;
        if (hole is { } spot) { below = spot.Bottom + 18 + height <= size.Height - 12; top = below ? spot.Bottom + 18 : Math.Max(12, spot.Top - 18 - height); }
        else top = Math.Max(12, (size.Height - height) / 2);
        // A small arrow on the card's edge, under (or over) the middle of the part it explains.
        if (mark is { } part)
        {
            var left = (size.Width - width) / 2;
            var x = Math.Clamp(part.X + part.Width / 2, left + 22, left + width - 22);
            var arrow = new Border { Width = 16, Height = 16, Background = Theme.Brush("Surface"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(x - 8, below ? top - 7 : top + height - 9, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45), IsHitTestVisible = false };
            tourLayer.Children.Add(arrow);
        }
        card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Top; card.Margin = new Thickness(0, top, 0, 0);
        tourLayer.Children.Add(card);
        if (SystemParameters.ClientAreaAnimation) card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        Dispatcher.BeginInvoke(() => card.MoveFocus(new TraversalRequest(FocusNavigationDirection.Last)), System.Windows.Threading.DispatcherPriority.Input);
    }
    private Border TourCard(TourStep step)
    {
        var last = tourIndex == tour.Count - 1;
        var body = new StackPanel();
        var top = new Grid();
        top.Children.Add(new TextBlock { Text = $"{tourIndex + 1} מתוך {tour.Count}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("AssistantBrush"), VerticalAlignment = VerticalAlignment.Center });
        var close = CloseButton("סגירת הסיור", EndTour); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, -6, -8, -6);
        top.Children.Add(close);
        body.Children.Add(top);
        body.Children.Add(new TextBlock { Text = step.Title, FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
        body.Children.Add(new TextBlock { Text = step.Text, FontSize = 13.5, LineHeight = 20, Foreground = Theme.Brush("Secondary"), TextWrapping = TextWrapping.Wrap });
        // Progress dots, then the buttons.
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < tour.Count; i++)
            dots.Children.Add(new Border { Width = i == tourIndex ? 16 : 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0), Background = Theme.Brush(i == tourIndex ? "AssistantBrush" : "Line") });
        footer.Children.Add(dots);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (tourIndex > 0)
        {
            var back = new Button { Style = Ui.Style("GhostButton"), Content = "הקודם", MinHeight = 36, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0) };
            back.Click += (_, _) => { tourIndex--; ShowTourStep(); };
            buttons.Children.Add(back);
        }
        var next = new Button { Style = Ui.Style("AssistantButton"), Content = last ? "סיום" : "הבא", MinHeight = 36, MinWidth = 76, Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        next.Click += (_, _) => { if (last) EndTour(); else { tourIndex++; ShowTourStep(); } };
        buttons.Children.Add(next);
        footer.Children.Add(buttons);
        body.Children.Add(footer);
        var card = Card(body, 16);
        card.Padding = new Thickness(16, 12, 16, 14);
        System.Windows.Automation.AutomationProperties.SetName(card, $"סיור באפליקציה, שלב {tourIndex + 1} מתוך {tour.Count}: {step.Title}");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(card, System.Windows.Automation.AutomationLiveSetting.Polite);
        return card;
    }
}
