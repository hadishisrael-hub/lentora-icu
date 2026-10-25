using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace PrinterColorProfiler;

public static class AppInfo
{
    // Public repository of the corresponding source.
    public const string SourceUrl = "https://github.com/hadishisrael-hub/lentora-icu";
    public static string Version => typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
}

// AGPL "Appropriate Legal Notices": license, no-warranty statement and where to get the source.
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "אודות Lentora ICU";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(FontFamilyProperty, "UiFont");

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Brand/icu-512.png")), Width = 56, Height = 56 });
        var name = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var word = new TextBlock { Text = "Lentora ICU", FontSize = 24, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right };
        word.SetResourceReference(TextBlock.FontFamilyProperty, "BrandFont");
        name.Children.Add(word);
        name.Children.Add(Line($"גרסה {AppInfo.Version}", "Caption"));
        head.Children.Add(name);
        stack.Children.Add(head);

        stack.Children.Add(Line("Lentora ICU היא תוכנה חופשית: מותר להפיץ ולשנות אותה לפי תנאי GNU Affero General Public License, גרסה 3 או כל גרסה מאוחרת יותר."));
        stack.Children.Add(Line("התוכנה מסופקת כפי שהיא, ללא כל אחריות, לרבות אחריות לסחירות או להתאמה למטרה מסוימת."));
        stack.Children.Add(Line("השם Lentora, הלוגו והאייקון הם סימני מסחר ואינם כלולים ברישיון הקוד (ראו TRADEMARKS.md)."));
        stack.Children.Add(Line("רכיבים: ArgyllCMS 3.5.0 (AGPL-3.0), OpenCvSharp ו־OpenCV (Apache-2.0), ‎.NET (MIT), Cal Sans (OFL 1.1).", "Caption"));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Open("רישיון", "LICENSE"));
        buttons.Children.Add(Open("רכיבי צד שלישי", "THIRD-PARTY-NOTICES.md"));
        var source = new Button { Content = "קוד המקור", IsEnabled = SourceUrlIsSet, ToolTip = SourceUrlIsSet ? AppInfo.SourceUrl : "הכתובת תפורסם עם הגרסה הציבורית" };
        source.Click += (_, _) => Process.Start(new ProcessStartInfo(AppInfo.SourceUrl) { UseShellExecute = true });
        buttons.Children.Add(source);
        var close = new Button { Content = "סגור", IsCancel = true, IsDefault = true };
        close.SetResourceReference(StyleProperty, "Primary");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        stack.Children.Add(buttons);
        Content = stack;
    }

    private static bool SourceUrlIsSet => !string.IsNullOrWhiteSpace(AppInfo.SourceUrl);

    private static TextBlock Line(string text, string style = "Body")
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private static Button Open(string label, string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        var button = new Button { Content = label, IsEnabled = File.Exists(path) };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        return button;
    }
}
