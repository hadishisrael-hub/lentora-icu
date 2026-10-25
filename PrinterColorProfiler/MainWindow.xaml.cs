using Microsoft.Win32;
using PrinterColorProfiler.Core;
using System.Diagnostics;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PrinterColorProfiler;

public partial class MainWindow : Window
{
    private enum Kind { Primary, Secondary, Ghost }

    private static readonly string[] StepNames = ["מדפסת ונייר", "הגדרות הדפסה", "הדפסת דפי כיול", "סריקה", "בדיקה ושיפור", "התקנה ואימות"];
    private const int MaxCandidates = 8;

    private ProfileSession? session;
    private ScanResult? scan;
    // Set only by the screenshot renderer, which has no printed scan.
    internal ScanDiagnostics? DemoBaseDiagnostics { get; set; }
    private readonly BitmapSource reference;
    private readonly List<(ToggleButton Chip, Slider Strength, string Id)> checks = [];

    public MainWindow()
    {
        InitializeComponent();
        reference = new BitmapImage(new Uri("pack://application:,,,/Assets/Reference.png"));
        UpdateThemeButton();
        Home();
    }

    private ProfileSession Current => session ?? throw new InvalidOperationException("אין תהליך פעיל.");

    // ---------- Building blocks ----------

    private void Page(string title, int step, string? subtitle = null)
    {
        Body.Children.Clear();
        HideStatus();
        Scroller.ScrollToTop();
        bool inFlow = step > 0 && session != null;
        Rail.Visibility = inFlow ? Visibility.Visible : Visibility.Collapsed;
        RailColumn.Width = inFlow ? new GridLength(230) : new GridLength(0);
        HomeButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (inFlow) RenderSteps(step);
        Body.Children.Add(Text(title, "H1"));
        if (subtitle != null) Body.Children.Add(Muted(subtitle));
        Body.Children.Add(new Border { Height = 10 });
    }

    private void RenderSteps(int step)
    {
        RailTitle.Text = Current.PrinterName;
        RailSubtitle.Text = Current.Paper;
        Steps.Children.Clear();
        for (int i = 1; i <= StepNames.Length; i++)
        {
            bool done = i < step, active = i == step;
            var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(active || done ? 0 : 1.5) };
            circle.SetResourceReference(Border.BackgroundProperty, active ? "Accent" : done ? "AccentSoft" : "Panel");
            circle.SetResourceReference(Border.BorderBrushProperty, "Line");
            var number = new TextBlock { Text = done ? "✓" : i.ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, active ? "OnAccent" : done ? "AccentText" : "Muted");
            circle.Child = number;
            var label = new TextBlock { Text = StepNames[i - 1], Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "Muted");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(circle); row.Children.Add(label);
            Steps.Children.Add(row);
            if (i < StepNames.Length)
            {
                var line = new Border { Width = 2, Height = 16, Margin = new Thickness(13, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
                line.SetResourceReference(Border.BackgroundProperty, done ? "AccentText" : "Line");
                Steps.Children.Add(line);
            }
        }
    }

    private TextBlock Text(string value, string style = "Body") => new() { Text = value, Style = (Style)FindResource(style) };
    private TextBlock Muted(string value) => Text(value, "Caption");

    private Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return new Border { Style = (Style)FindResource("Card"), Child = stack };
    }

    private Border Notice(string message, string tone)
    {
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 8, 0, 8) };
        border.SetResourceReference(Border.BackgroundProperty, tone + "Soft");
        var text = new TextBlock { Text = message };
        text.SetResourceReference(TextBlock.ForegroundProperty, tone);
        border.Child = text;
        return border;
    }

    private Border Badge(string text, string? tone = null)
    {
        var badge = new Border { Style = (Style)FindResource("Badge") };
        if (tone != null) badge.SetResourceReference(Border.BackgroundProperty, tone + "Soft");
        var label = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, tone ?? "Muted");
        badge.Child = label;
        return badge;
    }

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private Button Btn(string label, Action action, Kind kind = Kind.Secondary, string? tip = null)
    {
        var b = new Button { Content = label, ToolTip = tip };
        if (kind != Kind.Secondary) b.Style = (Style)FindResource(kind.ToString());
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } };
        return b;
    }

    private Button AsyncBtn(string label, string busy, Func<Task> action, Kind kind = Kind.Primary, string? tip = null)
    {
        var b = new Button { Content = label, ToolTip = tip };
        if (kind != Kind.Secondary) b.Style = (Style)FindResource(kind.ToString());
        b.Click += async (_, _) =>
        {
            var bar = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 8) };
            var host = b.Parent as Panel;
            host?.Children.Add(bar);
            b.IsEnabled = false; b.Content = busy; Mouse.OverrideCursor = Cursors.AppStarting;
            try { await action(); }
            catch (Exception ex) { Error(ex); }
            finally { b.IsEnabled = true; b.Content = label; Mouse.OverrideCursor = null; host?.Children.Remove(bar); }
        };
        return b;
    }

    private void Error(Exception ex)
    {
        ShowStatus(ex.Message, "Danger");
        MessageBox.Show(this, ex.Message, "הפעולה לא הושלמה", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void Info(string message) => ShowStatus(message, "Success");
    private void ShowStatus(string message, string tone)
    {
        StatusLabel.Text = message;
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, tone);
        StatusBar.SetResourceReference(Border.BackgroundProperty, tone + "Soft");
        StatusBar.Visibility = Visibility.Visible;
    }
    private void HideStatus() { StatusBar.Visibility = Visibility.Collapsed; StatusLabel.Text = ""; }

    private void DismissStatus_Click(object sender, RoutedEventArgs e) => HideStatus();
    private void HomeButton_Click(object sender, RoutedEventArgs e) => Home();
    private void ThemeButton_Click(object sender, RoutedEventArgs e) { ThemeManager.Toggle(); UpdateThemeButton(); }
    private void UpdateThemeButton() => ThemeButton.Content = ThemeManager.Current == AppTheme.Light ? "☾" : "☀";
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ---------- Home ----------

    private void Home()
    {
        session = null;
        Page("הפרופילים שלי", 0, "פרופיל צבע למדפסת מתוך הדפסה, סריקה במשרד ומשוב שלך — בלי מכשיר מדידה.");
        Body.Children.Add(Row(
            Btn("+ פרופיל צבע חדש", Choose, Kind.Primary),
            Btn("פתח את תיקיית הפרופילים", () => OpenDirectory(Store.Root), Kind.Ghost, Store.Root)));
        if (ProfileBuilder.FindArgyll() == null)
            Body.Children.Add(Notice("מנוע בניית הפרופילים (ArgyllCMS) לא נמצא. השאר את תיקיית Argyll_V3.5.0 לצד התוכנה, או הגדר את PRINTER_PROFILER_ARGYLL לתיקיית bin שלו.", "Warning"));

        var profiles = Store.Load();
        if (profiles.Count == 0)
        {
            var steps = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (n, title, text) in new[] { ("1", "הדפס", "דף מדידה ותמונת בדיקה, בהגדרות שבהן אתה עובד."), ("2", "סרוק", "את דף המדידה, בסורק המשרדי הרגיל."), ("3", "בדוק ושפר", "השווה הדפסות ותקן במשוב עד שזה נראה נכון.") })
            {
                var tile = new Border { Style = (Style)FindResource("Tile"), Child = new StackPanel { Children = { Text(n, "H1"), Text(title, "H2"), Muted(text) } } };
                steps.Children.Add(tile);
            }
            Body.Children.Add(Card(Text("עדיין אין פרופילים", "H2"), Muted("כך זה עובד:"), steps));
            return;
        }

        var grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var p in profiles) grid.Children.Add(ProfileCard(p));
        Body.Children.Add(grid);
    }

    private Border ProfileCard(ProfileSession p)
    {
        string? tone = p.Status switch { "פעיל" => "Success", "דורש בדיקה" => "Danger", _ => null };
        var badges = Row(Badge(p.Status, tone), Badge(p.Installed ? "מותקן ב־Windows" : "לא מותקן"), Badge($"{p.Candidates.Count} מועמדים"));
        var more = new Button { Content = "⋯", Style = (Style)FindResource("Icon"), ToolTip = "פעולות נוספות" };
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft };
        void Item(string header, Action action) { var item = new MenuItem { Header = header }; item.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } }; menu.Items.Add(item); }
        Item("פתח את תיקיית הפרופיל", () => OpenProfileDirectory(p));
        if (p.Installed && p.Candidates.Count > 0)
            Item("הפעל שוב את הפרופיל במדפסת", () => { session = p; WindowsServices.InstallAndVerify(Current, Current.Candidates[Current.SelectedCandidate]); Current.Status = "ממתין לבדיקה אחרונה"; Store.Save(Current); FinalCheck(); });
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };

        var actions = Row(Btn("המשך", () => { session = p; Resume(); }, Kind.Primary, "ממשיך מהשלב שבו עצרת"));
        if (p.Candidates.Count > 0) actions.Children.Add(Btn("צור מועמד נוסף", () => EditExisting(p), Kind.Secondary, "טוען את סריקת הכיול המקורית ומאפשר לשפר בלי להדפיס דף מדידה חדש"));
        actions.Children.Add(more);

        var header = new DockPanel();
        var title = Text(p.PrinterName, "H2"); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap; title.ToolTip = p.PrinterName;
        header.Children.Add(title);
        var card = Card(header, Muted($"{p.Paper} · נוצר ב־{p.CreatedAt:d}"), badges, actions);
        card.Width = 414; card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
    private static void OpenProfileDirectory(ProfileSession profile)
    {
        string? candidatePath = profile.SelectedCandidate >= 0 && profile.SelectedCandidate < profile.Candidates.Count
            ? profile.Candidates[profile.SelectedCandidate].IccPath : null;
        if (!string.IsNullOrWhiteSpace(candidatePath) && File.Exists(candidatePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{candidatePath}\"") { UseShellExecute = true });
        else OpenDirectory(profile.DirectoryPath);
    }
    private void EditExisting(ProfileSession profile)
    {
        session = profile;
        if (profile.Candidates.Count == 0) throw new InvalidOperationException("לפרופיל הזה עדיין אין מועמד ICC לעריכה.");
        if (profile.SelectedCandidate < 0 || profile.SelectedCandidate >= profile.Candidates.Count)
            profile.SelectedCandidate = profile.Candidates.Count - 1;
        if (!File.Exists(profile.ScanPath))
            throw new InvalidOperationException("לעריכת הפרופיל דרושה סריקת הכיול המקורית, והיא לא נמצאה בתיקיית הפרופיל.");
        scan = ScanAnalyzer.Analyze(profile.ScanPath);
        Review();
    }
    private void Resume()
    {
        if (Current.Installed) FinalCheck();
        else if (Current.Candidates.Count > 0) Review();
        else if (File.Exists(Current.ScanPath)) { scan = ScanAnalyzer.Analyze(Current.ScanPath); BuildFirst(); }
        else Settings();
    }

    // ---------- Steps 1-4 ----------

    private void Choose()
    {
        session = null;
        Page("פרופיל צבע חדש", 0, "בחר את המדפסת ואת סוג הנייר שבו תשתמש בעבודה היומיומית. לכל שילוב של מדפסת ונייר יש פרופיל משלו.");
        var printers = new ComboBox { ItemsSource = WindowsServices.Printers(), Margin = new Thickness(0, 4, 0, 14) };
        var paper = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
        Body.Children.Add(Card(Text("מדפסת", "H2"), printers, Text("נייר או חומר", "H2"), paper, Muted("למשל: כרומו מט 170 גרם, נייר צילום מבריק, מדבקה לבנה.")));
        Body.Children.Add(Row(
            Btn("המשך", () =>
            {
                if (printers.SelectedItem is not string printer || string.IsNullOrWhiteSpace(paper.Text)) throw new InvalidOperationException("בחר מדפסת וכתוב את שם הנייר.");
                session = new ProfileSession { PrinterName = printer, Paper = paper.Text.Trim() }; Store.Save(Current); Settings();
            }, Kind.Primary),
            Btn("ביטול", Home, Kind.Ghost)));
    }

    private void Settings()
    {
        Page("הגדרות ההדפסה", 2, "הפרופיל נכון רק להגדרות שבהן הוא נוצר. התוכנה שומרת אותן ומשתמשת בהן בכל הדפסה בתהליך.");
        var tips = new StackPanel();
        foreach (var tip in new[] { "בחר את סוג הנייר ואת איכות ההדפסה שבהם תעבוד.", "כבה שיפורי תמונה וצבע אוטומטיים של הדרייבר.", "אל תשנה את ההגדרות האלה בין ההדפסות." })
            tips.Children.Add(Text("•  " + tip));
        bool saved = !string.IsNullOrEmpty(Current.DriverSettingsBase64);
        var card = Card(Text("לפני שמתחילים", "H2"), tips, Row(saved ? Badge("✓ ההגדרות נשמרו", "Success") : Badge("ההגדרות עדיין לא נשמרו", "Warning")));
        Body.Children.Add(card);
        Body.Children.Add(Row(
            Btn(saved ? "שנה את הגדרות המדפסת" : "פתח הגדרות מדפסת", () =>
            {
                var dialog = new PrintDialog(); using (var server = new LocalPrintServer()) dialog.PrintQueue = server.GetPrintQueue(Current.PrinterName);
                if (dialog.ShowDialog() != true) return;
                Current.DriverSettingsBase64 = WindowsServices.CaptureDevMode(Current.PrinterName);
                using (var stream = new MemoryStream()) { dialog.PrintTicket.SaveTo(stream); Current.PrintTicketXml = Convert.ToBase64String(stream.ToArray()); }
                Store.Save(Current); Settings(); Info("הגדרות הדרייבר נשמרו.");
            }, saved ? Kind.Secondary : Kind.Primary, "פותח את חלון ההדפסה של Windows. בחר הגדרות ולחץ הדפס — שום דבר לא יודפס בשלב זה."),
            Btn("המשך להדפסה", () => { if (string.IsNullOrEmpty(Current.DriverSettingsBase64)) throw new InvalidOperationException("פתח ושמור תחילה את הגדרות המדפסת."); PrintChart(); }, saved ? Kind.Primary : Kind.Secondary)));
    }

    private void PrintChart()
    {
        Page("הדפסת דפי הכיול", 3, "על אותו נייר ובאותן הגדרות. התוכנה מגדירה את כיוון הדף לפני כל הדפסה.");
        BitmapSource? chart = null;
        try { chart = Chart.LoadPackaged(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var measure = PrintCard(chart, "דף מדידה", "לאורך · 320 דגימות צבע. זה הדף שתסרוק.", Btn("הדפס דף מדידה", () =>
        {
            var bitmap = Chart.LoadPackaged();
            File.Copy(Chart.PackagedPath, System.IO.Path.Combine(Current.DirectoryPath, "chart.png"), true);
            if (WindowsServices.PrintImage(Current, bitmap, "דף מדידת צבע", PageOrientation.Portrait)) Info("דף המדידה נשלח לתור ההדפסה לאורך.");
        }));
        var photo = PrintCard(reference, "תמונת בדיקה", "לרוחב · נשמרת להשוואה מול המועמדים הבאים.", Btn("הדפס תמונת בדיקה", () =>
        {
            if (WindowsServices.PrintImage(Current, reference, "תמונת בדיקה לפני כיול", PageOrientation.Landscape)) Info("תמונת הבדיקה נשלחה לתור ההדפסה לרוחב.");
        }));
        Grid.SetColumn(measure, 0); Grid.SetColumn(photo, 2);
        grid.Children.Add(measure); grid.Children.Add(photo);
        Body.Children.Add(grid);
        Body.Children.Add(Row(Btn("הדפסתי — להמשיך לסריקה", Scan, Kind.Primary), Btn("חזרה להגדרות", Settings, Kind.Ghost)));
    }

    private Border PrintCard(BitmapSource? image, string title, string caption, Button action)
    {
        var preview = new Border { Height = 190, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(10) };
        preview.SetResourceReference(Border.BackgroundProperty, "Panel2");
        if (image != null) preview.Child = new Image { Source = image, Stretch = Stretch.Uniform };
        var card = Card(preview, Text(title, "H2"), Muted(caption), Row(action));
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    private void Scan()
    {
        Page("הוספת הסריקה", 4, "סרוק את דף המדידה ופתח את הקובץ כאן.");
        var zone = new Grid { Height = 220, AllowDrop = true, Margin = new Thickness(0, 4, 0, 8), Background = Brushes.Transparent };
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 2, StrokeDashArray = [6, 4] };
        dash.SetResourceReference(Shape.StrokeProperty, "Line");
        dash.SetResourceReference(Shape.FillProperty, "Panel");
        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = new TextBlock { Text = "⇩", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        inner.Children.Add(glyph);
        var title = Text("גרור לכאן את סריקת דף המדידה", "H2"); title.HorizontalAlignment = HorizontalAlignment.Center; inner.Children.Add(title);
        var hint = Muted("TIFF או PNG מומלצים · JPEG באיכות גבוהה אפשרי"); hint.HorizontalAlignment = HorizontalAlignment.Center; inner.Children.Add(hint);
        var pick = Btn("בחר קובץ…", () => { var dialog = new OpenFileDialog { Filter = "תמונות|*.tif;*.tiff;*.png;*.jpg;*.jpeg" }; if (dialog.ShowDialog() == true) LoadScan(dialog.FileName); }, Kind.Primary);
        pick.HorizontalAlignment = HorizontalAlignment.Center; pick.Margin = new Thickness(0, 10, 0, 0);
        inner.Children.Add(pick);
        zone.Children.Add(dash); zone.Children.Add(inner);
        zone.DragEnter += (_, _) => dash.SetResourceReference(Shape.StrokeProperty, "Focus");
        zone.DragLeave += (_, _) => dash.SetResourceReference(Shape.StrokeProperty, "Line");
        zone.Drop += (_, e) =>
        {
            dash.SetResourceReference(Shape.StrokeProperty, "Line");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) try { LoadScan(files[0]); } catch (Exception ex) { Error(ex); }
        };
        Body.Children.Add(zone);
        var tips = new StackPanel();
        foreach (var tip in new[] { "סרוק את כל הדף, כולל ארבעת הסימנים השחורים בפינות.", "כבה תיקוני צבע ושיפורי תמונה אוטומטיים בסורק.", "השתמש באותו סורק ובאותן הגדרות בכל סבב." })
            tips.Children.Add(Text("•  " + tip));
        Body.Children.Add(Card(Text("לסריקה טובה", "H2"), tips));
        Body.Children.Add(Row(Btn("חזרה להדפסה", PrintChart, Kind.Ghost)));
    }

    private void LoadScan(string path)
    {
        ShowStatus("מנתח את הסריקה…", "Warning");
        scan = ScanAnalyzer.Analyze(path);
        Current.ScanPath = System.IO.Path.Combine(Current.DirectoryPath, "scan" + System.IO.Path.GetExtension(path)); File.Copy(path, Current.ScanPath, true); Store.Save(Current);
        BuildFirst();
    }

    // ---------- Step 5: candidates ----------

    private ScanDiagnostics? BaseDiagnostics() => DemoBaseDiagnostics ?? (scan != null ? ScanDiagnostics.From(scan) : null);

    private void BuildFirst()
    {
        Page("הסריקה נקלטה", 5, "המדידה עברה את בדיקת העקביות. עכשיו נבנה ממנה פרופיל ראשון לבדיקה.");
        if (BaseDiagnostics() is { } d) Body.Children.Add(DiagnosticsCard(d, "מדידת הבסיס — לפני כיול", null));
        if (ProfileBuilder.FindArgyll() == null)
            Body.Children.Add(Notice("כדי ליצור ICC דרוש ArgyllCMS. אפשר להגדיר את תיקיית bin שלו דרך PRINTER_PROFILER_ARGYLL.", "Warning"));
        Body.Children.Add(Row(AsyncBtn("צור מועמד ראשון", "בונה פרופיל…", async () =>
        {
            await ProfileBuilder.BuildAsync(Current, scan!, [], Current.WhiteProtectionMode, Current.WhiteProtectionStrength);
            Review();
        })));
    }

    private void Review()
    {
        var candidate = Current.Candidates[Current.SelectedCandidate];
        Page($"מועמד {candidate.Number}: איך נראית ההדפסה?", 5, "הדפס את תמונת הבדיקה דרך המועמד והשווה להדפסה הקודמת. אפשר לחזור לכל מועמד.");

        var picker = new WrapPanel();
        foreach (var (c, index) in Current.Candidates.Select((c, i) => (c, i)))
        {
            var chip = new ToggleButton { Content = $"מועמד {c.Number}", Style = (Style)FindResource("Chip"), IsChecked = index == Current.SelectedCandidate,
                ToolTip = $"{c.CreatedAt:g}\n{(c.Feedback.Length == 0 ? "ללא תיקוני משוב" : string.Join(", ", c.Feedback.Select(f => FeedbackCatalog.LabelOf(FineTune.Parse(f).Id))))}" };
            chip.Click += (_, _) => { Current.SelectedCandidate = index; Store.Save(Current); Review(); };
            picker.Children.Add(chip);
        }
        Body.Children.Add(picker);
        Body.Children.Add(Row(Badge("הגנת לבן: " + WhiteProtectionLabel(candidate.WhiteProtectionMode)),
            Badge(candidate.Feedback.Length == 0 ? "ללא תיקוני משוב" : $"{candidate.Feedback.Length} תיקוני משוב"),
            Badge(candidate.EffectiveEdit.IsNeutral ? "ללא עריכה" : "עריכה: " + EditSummary(candidate.EffectiveEdit))));

        scan ??= File.Exists(Current.ScanPath) ? ScanAnalyzer.Analyze(Current.ScanPath) : null;
        var baseline = BaseDiagnostics();
        if (candidate.Verification is not null)
        {
            Body.Children.Add(DiagnosticsCard(candidate.Verification, $"מדידת מועמד {candidate.Number}", baseline));
            if (baseline != null)
            {
                double change = candidate.Verification.MeanRgbError - baseline.MeanRgbError;
                Body.Children.Add(change < -.5 ? Notice($"הסטייה היחסית קטנה ב־{-change:F1} נקודות אחוז לעומת הבסיס.", "Success")
                    : change > .5 ? Notice($"הסטייה היחסית גדלה ב־{change:F1} נקודות אחוז לעומת הבסיס.", "Danger")
                    : Notice("הסטייה היחסית דומה לבסיס.", "Warning"));
            }
        }
        else if (baseline != null)
        {
            Body.Children.Add(DiagnosticsCard(baseline, "מדידת הבסיס — לפני כיול", null));
            Body.Children.Add(Muted("כדי לקבל מדדים גם למועמד: הדפס דף מדידה דרכו, סרוק אותו והוסף את הסריקה."));
        }

        Body.Children.Add(Card(Text("בדיקה בהדפסה", "H2"), Muted("כל ההדפסות עוברות דרך המועמד הנבחר ובהגדרות שנשמרו."), Row(
            Btn("הדפס תמונת בדיקה", () =>
            {
                var converted = ConvertWithCandidate(reference, candidate);
                if (WindowsServices.PrintImage(Current, converted, $"תמונת בדיקה — מועמד {candidate.Number}", PageOrientation.Landscape)) Info("תמונת המועמד נשלחה לתור ההדפסה לרוחב.");
            }, Kind.Secondary, "להשוואה בעין מול הדפסת הבסיס"),
            Btn("הדפס דף מדידה דרך המועמד", () =>
            {
                var converted = ConvertWithCandidate(Chart.LoadPackaged(), candidate);
                if (WindowsServices.PrintImage(Current, converted, $"דף מדידה — מועמד {candidate.Number}", PageOrientation.Portrait)) Info("דף מדידה למועמד נשלח להדפסה. סרוק אותו ללא שיפור תמונה אוטומטי.");
            }, Kind.Secondary, "לקבלת מדדים מספריים למועמד"),
            Btn("הוסף סריקת מדידה…", () =>
            {
                var dialog = new OpenFileDialog { Filter = "תמונות|*.tif;*.tiff;*.png;*.jpg;*.jpeg" };
                if (dialog.ShowDialog() != true) return;
                var measured = ScanAnalyzer.Analyze(dialog.FileName);
                string destination = System.IO.Path.Combine(Current.DirectoryPath, $"candidate_{candidate.Number}_scan" + System.IO.Path.GetExtension(dialog.FileName));
                File.Copy(dialog.FileName, destination, true);
                Current.Candidates[Current.SelectedCandidate] = candidate with { VerificationScanPath = destination, Verification = ScanDiagnostics.From(measured) };
                Store.Save(Current); Review(); Info("הסריקה נמדדה ונשמרה. השווה את המדדים לבסיס ולמועמדים קודמים.");
            }, Kind.Ghost))));

        var decision = Row(Btn("✓ נראה טוב — להמשיך", Ready, Kind.Primary),
            Btn("שפר לפי מה שראית", Feedback, Kind.Secondary, "בחירת בעיות מוכרות: כהה מדי, עור צהוב וכדומה"),
            Btn("ערוך פרופיל", Editor, Kind.Secondary, "ערכים מדויקים: טונים, איזון אפור וצבע סלקטיבי"));
        if (Current.SelectedCandidate > 0)
            decision.Children.Add(Btn("נהיה גרוע יותר — חזור לקודם", () => { Current.SelectedCandidate--; Store.Save(Current); Review(); }, Kind.Ghost));
        decision.Children.Add(Btn("פרטים מתקדמים", AdvancedDetails, Kind.Ghost));
        Body.Children.Add(Card(Text("מה ההחלטה?", "H2"), decision));
    }

    private static BitmapSource ConvertWithCandidate(BitmapSource image, Candidate candidate)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        return new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgr24), new ColorContext(new Uri(candidate.IccPath)), PixelFormats.Bgr24);
    }

    private Border DiagnosticsCard(ScanDiagnostics d, string title, ScanDiagnostics? baseline)
    {
        var tiles = new WrapPanel();
        tiles.Children.Add(Metric("סטייה יחסית ממוצעת", $"{d.MeanRgbError:F1}%", baseline is null ? null : d.MeanRgbError - baseline.MeanRgbError, "כמה הצבעים המודפסים רחוקים מהמקור, בממוצע. נמוך יותר עדיף."));
        tiles.Children.Add(Metric("אי־נייטרליות באפור", $"{d.GrayCast:F1}%", baseline is null ? null : d.GrayCast - baseline.GrayCast, "גוון צבע באפורים. נמוך יותר עדיף. גם הסורק עשוי לתרום לערך הזה."));
        tiles.Children.Add(Metric("עודף מג׳נטה בשמיים", $"{d.SkyMagenta:+0.0;-0.0;0.0}%", baseline is null ? null : Math.Abs(d.SkyMagenta) - Math.Abs(baseline.SkyMagenta), $"נמדד ב־{d.SkySamples} דגימות תכלת. קרוב לאפס עדיף."));
        return Card(Text(title, "H2"), tiles, Muted("להשוואה בין הדפסות באותו סורק ובאותן הגדרות; זו אינה מדידת מעבדה."));
    }

    private Border Metric(string label, string value, double? delta, string tip)
    {
        var stack = new StackPanel { MinWidth = 170 };
        stack.Children.Add(Muted(label));
        var number = Text(value, "H1"); number.FlowDirection = FlowDirection.LeftToRight; number.HorizontalAlignment = HorizontalAlignment.Right; number.Margin = new Thickness(0);
        stack.Children.Add(number);
        if (delta is double change)
        {
            var text = new TextBlock { Text = Math.Abs(change) < .05 ? "ללא שינוי מהבסיס" : $"{(change < 0 ? "▼" : "▲")} {Math.Abs(change):F1} מהבסיס", FontSize = 13 };
            text.SetResourceReference(TextBlock.ForegroundProperty, Math.Abs(change) < .05 ? "Muted" : change < 0 ? "Success" : "Danger");
            stack.Children.Add(text);
        }
        return new Border { Style = (Style)FindResource("Tile"), Child = stack, ToolTip = tip };
    }

    private (UIElement Card, Func<(PaperWhiteProtectionMode Mode, double Strength)> Read) PaperWhitePicker(Action? changed = null)
    {
        var protectionChoices = new Dictionary<PaperWhiteProtectionMode, RadioButton>();
        var extendedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 2, 0, 4) };
        var radios = new StackPanel();
        foreach (var (mode, label, tip) in new[]
        {
            (PaperWhiteProtectionMode.SmartGradient, "הגנה חכמה והדרגתית (מומלץ)", "מרסנת תיקונים ככל שמתקרבים ללבן, ושומרת על צבעים בהירים ופרטים."),
            (PaperWhiteProtectionMode.ExactWhiteOnly, "לבן מוחלט בלבד", "רק לבן מלא (255) נשאר ללא נגיעה."),
            (PaperWhiteProtectionMode.Extended, "הגנה מורחבת", "טווח הגנה רחב יותר סביב הלבן, בעוצמה שתבחר.")
        })
        {
            var radio = new RadioButton { Content = label, GroupName = "PaperWhiteProtection", ToolTip = tip };
            radio.Checked += (_, _) => { extendedRow.IsEnabled = mode == PaperWhiteProtectionMode.Extended; changed?.Invoke(); };
            protectionChoices[mode] = radio; radios.Children.Add(radio);
        }
        var strengthSlider = new Slider { Minimum = 1, Maximum = 3, Value = Current.WhiteProtectionStrength < .48 ? 1 : Current.WhiteProtectionStrength < .84 ? 2 : 3, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200, TickPlacement = TickPlacement.BottomRight, Margin = new Thickness(10, 0, 10, 0) };
        strengthSlider.ValueChanged += (_, _) => changed?.Invoke();
        protectionChoices[Current.WhiteProtectionMode].IsChecked = true;
        extendedRow.IsEnabled = Current.WhiteProtectionMode == PaperWhiteProtectionMode.Extended;
        extendedRow.Children.Add(Muted("עדינה")); extendedRow.Children.Add(strengthSlider); extendedRow.Children.Add(Muted("חזקה"));
        radios.Children.Add(extendedRow);
        var card = Card(Text("הגנת לבן הנייר", "H2"), Muted("מונעת מהתיקונים לצבוע את לבן הנייר ואת הגוונים הקרובים אליו."), radios);
        return (card, () => (protectionChoices.First(x => x.Value.IsChecked == true).Key, strengthSlider.Value switch { 1 => .35, 2 => .68, _ => 1 }));
    }

    private void Feedback()
    {
        var selectedCandidate = Current.Candidates[Current.SelectedCandidate];
        Page("מה עדיין מפריע בהדפסה?", 5, $"בחר את מה שראית וקבע עוצמה מ־1 (עדין) עד 4 (חזק). התיקונים מצטברים על מועמד {selectedCandidate.Number}; כל מועמד נשמר.");

        // Problems, grouped as chips with a strength row per selected chip
        checks.Clear();
        var groups = new StackPanel();
        foreach (var group in FeedbackCatalog.Groups)
        {
            var chips = new WrapPanel();
            var strengths = new StackPanel();
            foreach (var option in FeedbackCatalog.All.Where(o => o.Group == group))
            {
                var chip = new ToggleButton { Content = option.Label, Style = (Style)FindResource("Chip"), ToolTip = string.IsNullOrEmpty(option.Hint) ? null : option.Hint };
                var slider = new Slider { Minimum = 1, Maximum = 4, Value = 1, TickFrequency = .5, IsSnapToTickEnabled = true, Width = 220, Margin = new Thickness(12, 0, 12, 0) };
                var value = new TextBlock { Text = "1.0", Width = 36, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString("F1");
                var name = new TextBlock { Text = option.Label, Width = 220, VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(name); row.Children.Add(slider); row.Children.Add(value);
                chip.Checked += (_, _) => row.Visibility = Visibility.Visible;
                chip.Unchecked += (_, _) => row.Visibility = Visibility.Collapsed;
                checks.Add((chip, slider, option.Id));
                chips.Children.Add(chip); strengths.Children.Add(row);
            }
            groups.Children.Add(Text(group, "H2"));
            groups.Children.Add(chips);
            groups.Children.Add(strengths);
            groups.Children.Add(new Border { Height = 8 });
        }
        Body.Children.Add(Card(Text("בעיות שראית", "H2"), Muted("אפשר לבחור כמה בעיות יחד. כל השינויים יחד מוגבלים כדי לא לקלקל צבעים אחרים."), new Border { Height = 8 }, groups));
        Body.Children.Add(Card(Text("רוצה לכוון במדויק?", "H2"),
            Muted("בעורך הפרופיל אפשר לקבוע ערכים מדויקים: טונים, איזון אפור וצבע סלקטיבי. העריכה הנוכחית של המועמד נשמרת גם כאן."),
            Row(Btn("פתח את עורך הפרופיל", Editor, Kind.Secondary))));
        var (paperCard, readPaper) = PaperWhitePicker();
        Body.Children.Add(paperCard);

        var actions = Row(
            AsyncBtn("צור מועמד נוסף", "בונה פרופיל…", async () =>
            {
                var selected = checks.Where(x => x.Chip.IsChecked == true).Select(x => FineTune.Encode(x.Id, x.Strength.Value)).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("בחר לפחות בעיה אחת. לשינוי ערכים מדויקים השתמש בעורך הפרופיל.");
                if (Current.Candidates.Count >= MaxCandidates) throw new InvalidOperationException("הגעת לשמונה מועמדים. בחר את הטוב ביותר או התחל מדידה חדשה.");
                scan ??= ScanAnalyzer.Analyze(Current.ScanPath);
                var cumulative = selectedCandidate.Feedback.Concat(selected).ToArray();
                var (whiteMode, whiteStrength) = readPaper();
                Current.WhiteProtectionMode = whiteMode; Current.WhiteProtectionStrength = whiteStrength; Store.Save(Current);
                await ProfileBuilder.BuildAsync(Current, scan, cumulative, whiteMode, whiteStrength, selectedCandidate.EffectiveEdit); Review();
            }),
            Btn("חזרה למועמד", Review, Kind.Ghost));
        Body.Children.Add(actions);
        Body.Children.Add(Muted($"נשמרו {Current.Candidates.Count} מתוך {MaxCandidates} מועמדים אפשריים."));
    }

    // ---------- Profile editor ----------

    private void Editor()
    {
        var candidate = Current.Candidates[Current.SelectedCandidate];
        var original = candidate.EffectiveEdit;
        Page($"עריכת פרופיל — מועמד {candidate.Number}", 5,
            "כל ערך מתאר את השינוי שתראה בהדפסה. העריכה נשמרת כפרמטרים ונבנית מחדש מהסריקה המקורית, כך שאפשר לחזור אליה ולשנות.");

        var sliders = new List<Slider>();
        var hues = original.Hues.Where(h => !h.Value.IsNeutral).ToDictionary(h => h.Key, h => h.Value);
        bool loading = true;
        Func<(PaperWhiteProtectionMode Mode, double Strength)> readPaper = () => (Current.WhiteProtectionMode, Current.WhiteProtectionStrength);
        Func<ProfileEdit>? collect = null;

        // Preview: the reference photo with the wanted print change applied. Lab is cached once.
        double scale = 520.0 / reference.PixelWidth;
        var bgr = new FormatConvertedBitmap(new TransformedBitmap(reference, new ScaleTransform(scale, scale)), PixelFormats.Bgr24, null, 0);
        int width = bgr.PixelWidth, height = bgr.PixelHeight, stride = width * 3;
        var sourcePixels = new byte[stride * height];
        bgr.CopyPixels(sourcePixels, stride, 0);
        var labs = new (double L, double A, double B, Rgb Rgb)[width * height];
        Parallel.For(0, labs.Length, i =>
        {
            byte b0 = sourcePixels[i * 3], g0 = sourcePixels[i * 3 + 1], r0 = sourcePixels[i * 3 + 2];
            var lab = ScanAnalyzer.ToLab(r0 / 255d, g0 / 255d, b0 / 255d);
            labs[i] = (lab.l, lab.a, lab.b, new Rgb(r0, g0, b0));
        });
        var before = new Image { Source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, sourcePixels, stride), Stretch = Stretch.Uniform };
        var after = new Image { Stretch = Stretch.Uniform };
        void RenderPreview()
        {
            if (collect == null) return;
            var edit = collect();
            var (mode, strength) = readPaper();
            var output = new byte[sourcePixels.Length];
            Parallel.For(0, labs.Length, i =>
            {
                var (l, a, b, rgb) = labs[i];
                double weight = PaperWhiteProtection.CorrectionWeight(rgb, mode, strength);
                var (dl, da, db) = edit.Delta(l, a, b);
                var (r, g, bl) = ColorScience.LabToSrgb(l + dl * weight, a + da * weight, b + db * weight);
                output[i * 3] = (byte)Math.Round(bl * 255); output[i * 3 + 1] = (byte)Math.Round(g * 255); output[i * 3 + 2] = (byte)Math.Round(r * 255);
            });
            after.Source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, output, stride);
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => { timer.Stop(); RenderPreview(); };
        void Changed() { if (loading) return; timer.Stop(); timer.Start(); }

        var previewGrid = new Grid();
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition()); previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement Pane(Image image, string title)
        {
            var frame = new Border { Height = 230, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = image };
            frame.SetResourceReference(Border.BackgroundProperty, "Panel2");
            var stack = new StackPanel(); stack.Children.Add(Muted(title)); stack.Children.Add(frame);
            return stack;
        }
        var beforePane = Pane(before, "לפני העריכה"); var afterPane = Pane(after, "אחרי העריכה");
        Grid.SetColumn(beforePane, 0); Grid.SetColumn(afterPane, 2);
        previewGrid.Children.Add(beforePane); previewGrid.Children.Add(afterPane);
        Body.Children.Add(Card(previewGrid, Muted("התצוגה מראה את השינוי שהעריכה תוסיף להדפסה הנוכחית. היא לא הדמיה מדויקת של הנייר.")));

        Slider S(StackPanel panel, string label, string low, string high, double range, double value, string unit, string? tip = null)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), ToolTip = tip };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var lowText = Muted(low); lowText.VerticalAlignment = VerticalAlignment.Center; lowText.TextAlignment = TextAlignment.Right; lowText.Margin = new Thickness(0, 0, 8, 0);
            var highText = Muted(high); highText.VerticalAlignment = VerticalAlignment.Center; highText.Margin = new Thickness(8, 0, 0, 0);
            var slider = new Slider { Minimum = -range, Maximum = range, Value = Math.Clamp(value, -range, range), SmallChange = .5, LargeChange = 1, TickFrequency = .5, IsSnapToTickEnabled = true, ToolTip = "לחיצה כפולה מאפסת" };
            var number = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
            void Show() => number.Text = slider.Value.ToString("+0.0;-0.0;0", System.Globalization.CultureInfo.InvariantCulture) + unit;
            Show();
            slider.ValueChanged += (_, _) => { Show(); Changed(); };
            slider.MouseDoubleClick += (_, _) => slider.Value = 0;
            Grid.SetColumn(name, 0); Grid.SetColumn(lowText, 1); Grid.SetColumn(slider, 2); Grid.SetColumn(highText, 3); Grid.SetColumn(number, 4);
            row.Children.Add(name); row.Children.Add(lowText); row.Children.Add(slider); row.Children.Add(highText); row.Children.Add(number);
            panel.Children.Add(row);
            sliders.Add(slider);
            return slider;
        }

        // Global
        var global = new StackPanel();
        var brightness = S(global, "בהירות", "כהה", "בהיר", ProfileEdit.GlobalLRange, original.Brightness, " L");
        var contrast = S(global, "ניגודיות", "חלשה", "חזקה", ProfileEdit.ContrastRange, original.Contrast, "%");
        var saturation = S(global, "רוויה", "עדינה", "חזקה", ProfileEdit.SaturationRange, original.Saturation, "%");
        var warmth = S(global, "טמפרטורה", "קר", "חם", ProfileEdit.GlobalABRange, original.Warmth, " b", "Δb: שלילי כחול יותר, חיובי צהוב יותר");
        var tint = S(global, "גוון", "ירוק", "מג׳נטה", ProfileEdit.GlobalABRange, original.Tint, " a", "Δa: שלילי ירוק יותר, חיובי מג׳נטה יותר");
        Body.Children.Add(Card(Text("כללי", "H2"), global));

        // Tone. The black slider is shown as depth: positive = deeper black = negative ΔL.
        var tone = new StackPanel();
        var black = S(tone, "עומק שחור", "פתוח", "עמוק", ProfileEdit.ToneRange, -original.Tone.Black, " L", "השחורים (בהירות מתחת ל־25). ערך חיובי מעמיק את השחור.");
        var shadows = S(tone, "צללים", "כהה", "בהיר", ProfileEdit.ToneRange, original.Tone.Shadows, " L", "סביב בהירות 20");
        var mids = S(tone, "אמצע", "כהה", "בהיר", ProfileEdit.ToneRange, original.Tone.Midtones, " L", "סביב בהירות 50");
        var highs = S(tone, "הבהרות", "כהה", "בהיר", ProfileEdit.ToneRange, original.Tone.Highlights, " L", "סביב בהירות 80. לבן הנייר מוגן.");
        Body.Children.Add(Card(Text("טונים", "H2"), Muted("ΔL לכל אזור בהירות. האזורים חופפים ומתחברים בצורה חלקה."), tone));

        // Gray balance
        var gray = new StackPanel();
        var shadowA = S(gray, "צללים · ירוק/מג׳נטה", "ירוק", "מג׳נטה", ProfileEdit.GrayRange, original.Gray.ShadowA, " a");
        var shadowB = S(gray, "צללים · כחול/צהוב", "כחול", "צהוב", ProfileEdit.GrayRange, original.Gray.ShadowB, " b");
        var midA = S(gray, "אמצע · ירוק/מג׳נטה", "ירוק", "מג׳נטה", ProfileEdit.GrayRange, original.Gray.MidA, " a");
        var midB = S(gray, "אמצע · כחול/צהוב", "כחול", "צהוב", ProfileEdit.GrayRange, original.Gray.MidB, " b");
        var highA = S(gray, "הבהרות · ירוק/מג׳נטה", "ירוק", "מג׳נטה", ProfileEdit.GrayRange, original.Gray.HighA, " a");
        var highB = S(gray, "הבהרות · כחול/צהוב", "כחול", "צהוב", ProfileEdit.GrayRange, original.Gray.HighB, " b");
        Body.Children.Add(Card(Text("איזון אפור", "H2"), Muted("משפיע רק על צבעים כמעט ניטרליים. אם האפור יוצא ורדרד, הזז לכיוון ירוק."), gray));

        // Selective color
        var bandChips = new WrapPanel();
        var bandSliders = new StackPanel();
        string selectedBand = ProfileEdit.Bands[0].Id;
        var hueShift = S(bandSliders, "גוון", "−", "+", ProfileEdit.HueShiftRange, 0, "°", "סיבוב הגוון במעלות");
        var hueSat = S(bandSliders, "רוויה", "עדינה", "חזקה", ProfileEdit.HueSaturationRange, 0, "%");
        var hueLight = S(bandSliders, "בהירות", "כהה", "בהיר", ProfileEdit.HueLightnessRange, 0, " L");
        var chipsById = new Dictionary<string, ToggleButton>();
        void RefreshChip(string id)
        {
            var band = ProfileEdit.Bands.First(b => b.Id == id);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = (Brush)new BrushConverter().ConvertFromString(band.Swatch)!, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = band.Label + (hues.TryGetValue(id, out var e) && !e.IsNeutral ? " •" : ""), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            chipsById[id].Content = content;
        }
        void SelectBand(string id)
        {
            selectedBand = id;
            foreach (var (key, chip) in chipsById) chip.IsChecked = key == id;
            bool wasLoading = loading; loading = true;
            var edit = hues.TryGetValue(id, out var e) ? e : new HueEdit();
            hueShift.Value = edit.Hue; hueSat.Value = edit.Saturation; hueLight.Value = edit.Lightness;
            loading = wasLoading;
        }
        void StoreBand()
        {
            if (loading) return;
            var edit = new HueEdit(hueShift.Value, hueSat.Value, hueLight.Value);
            if (edit.IsNeutral) hues.Remove(selectedBand); else hues[selectedBand] = edit;
            RefreshChip(selectedBand);
        }
        foreach (var band in ProfileEdit.Bands)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("Chip") };
            chipsById[band.Id] = chip;
            chip.Click += (_, _) => SelectBand(band.Id);
            bandChips.Children.Add(chip);
            RefreshChip(band.Id);
        }
        foreach (var s in new[] { hueShift, hueSat, hueLight }) s.ValueChanged += (_, _) => StoreBand();
        Body.Children.Add(Card(Text("צבע סלקטיבי", "H2"), Muted("בחר טווח צבע וכוון אותו. צבעים חלשים ואפורים לא מושפעים. • מסמן טווח שנערך."), bandChips, bandSliders));

        var (paperCard, paperReader) = PaperWhitePicker(Changed);
        readPaper = paperReader;
        Body.Children.Add(paperCard);

        ProfileEdit BuildEdit() => new()
        {
            Brightness = brightness.Value, Contrast = contrast.Value, Saturation = saturation.Value, Warmth = warmth.Value, Tint = tint.Value,
            Tone = new ToneEdit(-black.Value, shadows.Value, mids.Value, highs.Value),
            Gray = new GrayBalance(shadowA.Value, shadowB.Value, midA.Value, midB.Value, highA.Value, highB.Value),
            Hues = new Dictionary<string, HueEdit>(hues)
        };
        collect = BuildEdit;

        Body.Children.Add(Row(
            AsyncBtn("בנה מועמד מהעריכה", "בונה פרופיל…", async () =>
            {
                var edit = BuildEdit();
                var (whiteMode, whiteStrength) = readPaper();
                bool same = EditKey(edit) == EditKey(original) && whiteMode == candidate.WhiteProtectionMode && Math.Abs(whiteStrength - candidate.WhiteProtectionStrength) < .01;
                if (same) throw new InvalidOperationException("לא שינית אף ערך מאז המועמד הנוכחי.");
                if (Current.Candidates.Count >= MaxCandidates) throw new InvalidOperationException("הגעת לשמונה מועמדים. בחר את הטוב ביותר או התחל מדידה חדשה.");
                scan ??= ScanAnalyzer.Analyze(Current.ScanPath);
                Current.WhiteProtectionMode = whiteMode; Current.WhiteProtectionStrength = whiteStrength; Store.Save(Current);
                await ProfileBuilder.BuildAsync(Current, scan, candidate.Feedback, whiteMode, whiteStrength, edit);
                Review(); Info($"מועמד {Current.Candidates[Current.SelectedCandidate].Number} נבנה מהעריכה.");
            }, Kind.Primary, "בונה מועמד חדש מהסריקה המקורית, עם תיקוני המשוב של מועמד זה והערכים שבחרת"),
            Btn("אפס הכול", () =>
            {
                loading = true;
                foreach (var slider in sliders) slider.Value = 0;
                hues.Clear();
                foreach (var id in chipsById.Keys) RefreshChip(id);
                loading = false;
                SelectBand(selectedBand); Changed();
            }),
            Btn("חזרה למועמד", Review, Kind.Ghost)));
        Body.Children.Add(Muted($"נשמרו {Current.Candidates.Count} מתוך {MaxCandidates} מועמדים אפשריים. לחיצה כפולה על פס מאפסת אותו."));

        SelectBand(selectedBand);
        loading = false;
        RenderPreview();
    }

    private static string EditKey(ProfileEdit edit) => System.Text.Json.JsonSerializer.Serialize(edit with { Hues = edit.Hues.Where(h => !h.Value.IsNeutral).OrderBy(h => h.Key).ToDictionary(h => h.Key, h => h.Value) });

    private static string EditSummary(ProfileEdit e)
    {
        var parts = new List<string>();
        void Add(string name, double v, string unit) { if (Math.Abs(v) >= .05) parts.Add($"{name} ‎{v.ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture)}{unit}"); }
        Add("בהירות", e.Brightness, ""); Add("ניגודיות", e.Contrast, "%"); Add("רוויה", e.Saturation, "%"); Add("טמפרטורה", e.Warmth, ""); Add("גוון", e.Tint, "");
        Add("עומק שחור", -e.Tone.Black, ""); Add("צללים", e.Tone.Shadows, ""); Add("אמצע", e.Tone.Midtones, ""); Add("הבהרות", e.Tone.Highlights, "");
        if (e.Gray != new GrayBalance()) parts.Add("איזון אפור");
        foreach (var band in ProfileEdit.Bands) if (!e.HueFor(band.Id).IsNeutral) parts.Add(band.Label);
        return parts.Count == 0 ? "ללא עריכה" : string.Join(" · ", parts);
    }

    private void AdvancedDetails()
    {
        var c = Current.Candidates[Current.SelectedCandidate];
        string adjustments = c.Feedback.Length == 0 ? "אין" : string.Join("\n", c.Feedback.Select(x => { var (id, strength) = FineTune.Parse(x); return $"{FeedbackCatalog.LabelOf(id)}: {strength:F1}"; }));
        MessageBox.Show(this, $"ICC: {c.IccPath}\nTI3: {c.Ti3Path}\nסריקת אימות: {c.VerificationScanPath ?? "אין"}\nהגנת לבן הנייר: {WhiteProtectionLabel(c.WhiteProtectionMode)}\nעריכה: {EditSummary(c.EffectiveEdit)}\n\nתיקוני משוב:\n{adjustments}\n\nמדידות הסורק יחסיות בלבד.", "פרטים מתקדמים");
    }

    private static string WhiteProtectionLabel(PaperWhiteProtectionMode mode) => mode switch
    {
        PaperWhiteProtectionMode.ExactWhiteOnly => "לבן מוחלט בלבד",
        PaperWhiteProtectionMode.Extended => "הגנה מורחבת",
        _ => "חכמה והדרגתית"
    };

    // ---------- Step 6 ----------

    private void Ready()
    {
        var candidate = Current.Candidates[Current.SelectedCandidate];
        Page("הפרופיל מוכן", 6, $"מועמד {candidate.Number} נבחר. הגרסאות הקודמות נשמרות בתיקיית הפרופיל.");
        var path = Muted(candidate.IccPath); path.FlowDirection = FlowDirection.LeftToRight; path.TextAlignment = TextAlignment.Right;
        Body.Children.Add(Card(Text("התקנה במדפסת", "H2"),
            Text("ההתקנה מוסיפה את הפרופיל לספריית הצבע של Windows ומגדירה אותו כברירת המחדל של המדפסת. ייתכן שתתבקש הרשאת מנהל."),
            Row(Btn("התקן והפעל", () => { WindowsServices.InstallAndVerify(Current, Current.Candidates[Current.SelectedCandidate]); Current.Installed = true; Current.Status = "ממתין לבדיקה אחרונה"; Store.Save(Current); FinalCheck(); }, Kind.Primary))));
        Body.Children.Add(Card(Text("שמירה וייצוא", "H2"), path, Row(
            Btn("שמור בפרופיל ופתח את התיקייה", () => { Current.Status = "גרסה חדשה נשמרה"; Store.Save(Current); OpenProfileDirectory(Current); Info("הגרסה נשמרה בתיקיית הפרופיל."); }),
            Btn("ייצא עותק ICC…", SaveCandidateCopy, Kind.Secondary, "שם הקובץ חייב להיות באנגלית כדי שתוכנות גרפיקה יזהו אותו."))));
        Body.Children.Add(Row(Btn("חזרה למועמד", Review, Kind.Ghost), Btn("לרשימת הפרופילים", Home, Kind.Ghost)));
    }

    private void SaveCandidateCopy()
    {
        var candidate = Current.Candidates[Current.SelectedCandidate];
        while (true)
        {
            var dialog = new SaveFileDialog
            {
                Title = "שמור עותק ICC",
                Filter = "פרופיל ICC (*.icc;*.icm)|*.icc;*.icm",
                DefaultExt = ".icc", AddExtension = true, OverwritePrompt = true, CheckPathExists = true,
                InitialDirectory = Current.DirectoryPath,
                FileName = $"ICU_Profile_{Current.Id:N}_{candidate.Number}.icc"
            };
            if (dialog.ShowDialog(this) != true) return;
            string fileName = System.IO.Path.GetFileName(dialog.FileName);
            string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string extension = System.IO.Path.GetExtension(fileName);
            bool safeName = stem.Length > 0 && stem.All(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-');
            bool safeExtension = extension.Equals(".icc", StringComparison.OrdinalIgnoreCase) || extension.Equals(".icm", StringComparison.OrdinalIgnoreCase);
            if (!safeName || !safeExtension)
            {
                MessageBox.Show(this, "שם הקובץ חייב להכיל אותיות באנגלית, מספרים, מקף או קו תחתון, ולהסתיים ב־.icc או .icm.", "שם קובץ לא נתמך", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            string destination = System.IO.Path.GetFullPath(dialog.FileName);
            string source = System.IO.Path.GetFullPath(candidate.IccPath);
            if (destination.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                Info("המועמד כבר נשמר במיקום המקורי. פתח את תיקיית הפרופיל כדי להשתמש בו.");
                return;
            }
            if (Current.Candidates.Any(x => !string.IsNullOrWhiteSpace(x.IccPath) && System.IO.Path.GetFullPath(x.IccPath).Equals(destination, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "השם הזה שייך לגרסה קודמת. בחר שם חדש כדי לשמור את ההיסטוריה.", "הגרסה נשמרת", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            Info($"נשמר עותק ICC: {destination}");
            return;
        }
    }

    private void FinalCheck()
    {
        Page("בדיקה אחרונה", 6, "מוודאים שההדפסה דרך הפרופיל המותקן נראית כמו המועמד שאישרת.");
        Body.Children.Add(Card(Text("1. הדפס שוב את תמונת הבדיקה", "H2"),
            Muted("ההדפסה עוברת כרגיל דרך המדפסת, בלי המרה של התוכנה — כמו שיקרה בעבודה היומיומית."),
            Row(Btn("הדפס בדיקה אחרונה", () => { if (WindowsServices.PrintImage(Current, reference, "בדיקה אחרונה לאחר התקנה", PageOrientation.Landscape)) Info("תמונת הבדיקה האחרונה נשלחה לתור ההדפסה לרוחב."); }))));
        Body.Children.Add(Card(Text("2. האם היא נראית כמו זו שאישרת?", "H2"), Row(
            Btn("✓ כן, זהה", Calibrated, Kind.Primary),
            Btn("✕ לא, יש הבדל", () => { Current.Status = "דורש בדיקה"; Store.Save(Current); ShowStatus("הפרופיל נשמר אך לא סומן כפעיל. בדוק בדרייבר שניהול הצבע מוגדר לפרופיל ולא לתיקון אוטומטי.", "Danger"); }),
            Btn("פרטים מתקדמים", () => { var c = Current.Candidates[Current.SelectedCandidate]; MessageBox.Show(this, $"ICC: {c.IccPath}\nTI3: {c.Ti3Path}\nמדידות הסורק יחסיות בלבד.", "פרטים מתקדמים"); }, Kind.Ghost))));
    }

    private void Calibrated()
    {
        Current.Status = "פעיל"; Store.Save(Current);
        Page("המדפסת מכוילת", 6);
        var check = new TextBlock { Text = "✓", FontSize = 44, HorizontalAlignment = HorizontalAlignment.Left };
        check.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        Body.Children.Add(Card(check, Text($"{Current.PrinterName} · {Current.Paper}", "H2"), Muted("הפרופיל פעיל. אם תחליף נייר, דיו או הגדרות הדפסה — צור פרופיל חדש לשילוב הזה."), Row(Btn("חזרה לפרופילים", Home, Kind.Primary))));
    }

    // ---------- Screenshot renderer ----------

    internal void ShowDemo(string page, ProfileSession demo, ScanResult demoScan)
    {
        session = demo; scan = demoScan;
        switch (page)
        {
            case "home": Home(); break;
            case "choose": Choose(); break;
            case "settings": Settings(); break;
            case "print": PrintChart(); break;
            case "scan": Scan(); break;
            case "review": Review(); break;
            case "feedback":
                Feedback();
                foreach (var c in checks.Where(c => c.Id is FeedbackCatalog.Sky or FeedbackCatalog.SkinYellow)) { c.Chip.IsChecked = true; c.Strength.Value = 2; }
                break;
            case "editor": Editor(); break;
            case "ready": Ready(); break;
            case "final": FinalCheck(); break;
        }
    }
}
