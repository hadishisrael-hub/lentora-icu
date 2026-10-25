using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrinterColorProfiler.Core;

namespace Smoke;

internal static class PaperWhiteSuite
{
    private const int Width = 1240;
    private const int Height = 1754;

    public static void VerifyProtectionRules()
    {
        var white = new Rgb(255, 255, 255);
        if (PaperWhiteProtection.CorrectionWeight(white, PaperWhiteProtectionMode.ExactWhiteOnly) != 0 ||
            PaperWhiteProtection.CorrectionWeight(white, PaperWhiteProtectionMode.SmartGradient) != 0 ||
            PaperWhiteProtection.CorrectionWeight(white, PaperWhiteProtectionMode.Extended, 0) != 0)
            throw new Exception("Paper white lock changed exact white.");

        double exactOnlyNearWhite = PaperWhiteProtection.CorrectionWeight(new Rgb(250, 250, 250), PaperWhiteProtectionMode.ExactWhiteOnly);
        double smartNearWhite = PaperWhiteProtection.CorrectionWeight(new Rgb(250, 250, 250), PaperWhiteProtectionMode.SmartGradient);
        double extendedNearWhite = PaperWhiteProtection.CorrectionWeight(new Rgb(250, 250, 250), PaperWhiteProtectionMode.Extended, 1);
        if (exactOnlyNearWhite != 1 || smartNearWhite >= exactOnlyNearWhite || extendedNearWhite >= smartNearWhite)
            throw new Exception("Paper white modes do not apply the expected progressive protection.");

        double previous = 0;
        for (int value = 255; value >= 200; value--)
        {
            double weight = PaperWhiteProtection.CorrectionWeight(new Rgb((byte)value, (byte)value, (byte)value), PaperWhiteProtectionMode.SmartGradient);
            if (weight + 1e-9 < previous || weight - previous > .06)
                throw new Exception($"Smart paper white protection is discontinuous or non-monotonic near RGB {value}: {previous:F4} -> {weight:F4}.");
            previous = weight;
        }

        double neutral = PaperWhiteProtection.CorrectionWeight(new Rgb(248, 248, 248), PaperWhiteProtectionMode.SmartGradient);
        double brightColor = PaperWhiteProtection.CorrectionWeight(new Rgb(220, 238, 255), PaperWhiteProtectionMode.SmartGradient);
        if (brightColor <= neutral)
            throw new Exception("Smart protection reduced correction more for a colored highlight than for a near-white neutral.");

        var feedbackMethod = typeof(ProfileBuilder).GetMethod("ApplyFeedback", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ProfileBuilder), "ApplyFeedback");
        var measuredWhite = new MeasuredPatch(new Patch(0, 0, 0, white, "gray"), 1, 1, 1, 100, 0, 0);
        var locked = ((double L, double A, double B))feedbackMethod.Invoke(null, [measuredWhite, new[] { FineTune.Encode("לבנים שרופים", 4) }])!;
        if (Math.Abs(locked.L - 100) > .0001 || Math.Abs(locked.A) > .0001 || Math.Abs(locked.B) > .0001)
            throw new Exception("Highlight feedback changed exact white.");

        const string legacySession = "{\"PrinterName\":\"Legacy printer\",\"Candidates\":[{\"Number\":1,\"IccPath\":\"old.icc\",\"Ti3Path\":\"old.ti3\",\"Feedback\":[],\"CreatedAt\":\"2026-10-24T00:00:00\"}]}";
        var restored = JsonSerializer.Deserialize<ProfileSession>(legacySession);
        if (restored?.WhiteProtectionMode != PaperWhiteProtectionMode.SmartGradient ||
            restored.Candidates[0].WhiteProtectionMode != PaperWhiteProtectionMode.SmartGradient)
            throw new Exception("Legacy session JSON did not retain compatible paper white defaults.");
        Console.WriteLine("Paper white protection rules OK: exact lock, smooth falloff, neutral-sensitive weighting, and bright-color preservation.");
    }

    public static void Run(string scanPath, string photoPath, string outputDirectory)
    {
        VerifyProtectionRules();
        Directory.CreateDirectory(outputDirectory);
        string oldProfile = Path.Combine(outputDirectory, "legacy-smoothing.icm");
        if (!File.Exists(oldProfile)) throw new FileNotFoundException("The pre-change legacy profile is missing.", oldProfile);

        var scan = ScanAnalyzer.Analyze(scanPath);
        string protectedTi3 = Path.Combine(outputDirectory, "protected-smoothing.ti3");
        string protectedIcc = Path.Combine(outputDirectory, "protected-smoothing.icc");
        string protectedIcm = Path.Combine(outputDirectory, "protected-smoothing.icm");
        foreach (string path in new[] { protectedTi3, protectedIcc, protectedIcm })
            if (File.Exists(path)) File.Delete(path);

        var write = typeof(ProfileBuilder).GetMethod("WriteTi3Protected", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ProfileBuilder), "WriteTi3Protected");
        write.Invoke(null, [scan, protectedTi3, new[] { "banding" }, PaperWhiteProtectionMode.SmartGradient, .7]);
        string legacyTi3 = Path.Combine(outputDirectory, "legacy-smoothing.ti3");
        VerifyWhiteData(scan, legacyTi3, protectedTi3);

        string colprof = ProfileBuilder.FindArgyll() ?? throw new InvalidOperationException("ArgyllCMS colprof.exe was not found.");
        RunColprof(colprof, outputDirectory, "protected-smoothing");
        if (!File.Exists(protectedIcc) && File.Exists(protectedIcm)) File.Move(protectedIcm, protectedIcc);
        if (!File.Exists(protectedIcc)) throw new IOException("ArgyllCMS did not create the protected ICC profile.");

        var source = LoadBitmap(photoPath);
        BitmapSource testChart = RenderTestChart(source);
        string chartPath = Path.Combine(outputDirectory, "PaperWhiteTest.png");
        SavePng(testChart, chartPath);

        BitmapSource none = ToBgr24(testChart);
        BitmapSource oldPreview = ConvertThroughProfile(testChart, oldProfile);
        BitmapSource newPreview = ConvertThroughProfile(testChart, protectedIcc);
        string nonePath = Path.Combine(outputDirectory, "Preview-No-ICU-Profile.png");
        string oldPreviewPath = Path.Combine(outputDirectory, "Preview-Legacy-Profile.png");
        string newPreviewPath = Path.Combine(outputDirectory, "Preview-Protected-Profile.png");
        SavePng(none, nonePath);
        SavePng(oldPreview, oldPreviewPath);
        SavePng(newPreview, newPreviewPath);
        string comparisonPath = Path.Combine(outputDirectory, "PaperWhite-Comparison.png");
        SavePng(RenderComparison(none, oldPreview, newPreview), comparisonPath);

        var sourceBytes = Pixels(none);
        var oldBytes = Pixels(oldPreview);
        var newBytes = Pixels(newPreview);
        var regions = new Dictionary<string, (int X, int Y, int W, int H)>
        {
            ["exact_white"] = (60, 150, 230, 95),
            ["neutral_highlights"] = (335, 155, 865, 105),
            ["cream_tones"] = (40, 335, 1160, 105),
            ["highlight_gradients"] = (40, 485, 1160, 200),
            ["photo_highlights"] = (40, 825, 1160, 790),
            ["whole_chart"] = (0, 0, Width, Height)
        };
        var metrics = new Dictionary<string, object>();
        foreach (var (name, region) in regions)
        {
            metrics[name] = new
            {
                old_profile_vs_source = Measure(sourceBytes, oldBytes, region, 4),
                protected_profile_vs_source = Measure(sourceBytes, newBytes, region, 4),
                old_vs_protected = Measure(oldBytes, newBytes, region, 4)
            };
        }
        var result = new
        {
            source_scan = scanPath,
            source_photo = photoPath,
            old_profile = oldProfile,
            new_profile = protectedIcc,
            new_profile_feedback = new[] { "banding" },
            protection_mode = PaperWhiteProtectionMode.SmartGradient.ToString(),
            color_difference = "CIEDE2000 in sRGB round-trip previews made by the WPF color-management path",
            physical_print_status = "Not measured; this report is a digital ICC simulation only.",
            files = new { chart = chartPath, comparison = comparisonPath, no_profile_preview = nonePath, old_profile_preview = oldPreviewPath, protected_profile_preview = newPreviewPath },
            regions = metrics
        };
        string jsonPath = Path.Combine(outputDirectory, "PaperWhite-Comparison-Metrics.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Paper white artifacts created: {outputDirectory}");
        Console.WriteLine($"Test chart: {chartPath}");
        Console.WriteLine($"Comparison: {comparisonPath}");
        Console.WriteLine($"Metrics: {jsonPath}");
    }

    private static void VerifyWhiteData(ScanResult scan, string legacyTi3, string protectedTi3)
    {
        var old = ReadLabRows(legacyTi3);
        var current = ReadLabRows(protectedTi3);
        var exactWhite = scan.Patches.Where(x => x.Patch.Source is { R: 255, G: 255, B: 255 }).ToArray();
        if (exactWhite.Length < 2) throw new Exception("The calibration chart has too few exact-white samples.");
        double oldMean = 0, protectedMean = 0;
        foreach (var patch in exactWhite)
        {
            int id = patch.Patch.Index + 1;
            var expected = ColorScience.D65ToD50(patch.L, patch.A, patch.LabB);
            double oldDelta = LabDistance(old[id], expected), newDelta = LabDistance(current[id], expected);
            oldMean += oldDelta;
            protectedMean += newDelta;
            if (newDelta > .0002) throw new Exception($"Protected TI3 changed exact white sample {id} by ΔE76={newDelta:F4}.");
        }
        oldMean /= exactWhite.Length;
        protectedMean /= exactWhite.Length;
        if (oldMean <= protectedMean + .1) throw new Exception("The legacy smoothing fixture did not reproduce a measurable white change.");
        Console.WriteLine($"Exact white TI3: legacy mean ΔE76 {oldMean:F4}; protected mean ΔE76 {protectedMean:F4}.");
    }

    private static Dictionary<int, (double L, double A, double B)> ReadLabRows(string path)
    {
        var rows = new Dictionary<int, (double L, double A, double B)>();
        bool inData = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Trim() == "BEGIN_DATA") { inData = true; continue; }
            if (!inData || line.Trim() == "END_DATA") continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7 || !int.TryParse(fields[0], out int id)) continue;
            rows[id] = (double.Parse(fields[4], CultureInfo.InvariantCulture),
                        double.Parse(fields[5], CultureInfo.InvariantCulture),
                        double.Parse(fields[6], CultureInfo.InvariantCulture));
        }
        return rows;
    }

    private static double LabDistance((double L, double A, double B) a, (double L, double A, double B) b) =>
        Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2));

    private static void RunColprof(string exe, string directory, string baseName)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "-v", "-q", "m", "-a", "l", "-D", "Paper White Protected Smoothing", baseName }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ArgyllCMS colprof.exe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string log = Task.WhenAll(stdout, stderr).GetAwaiter().GetResult().Aggregate((a, b) => a + Environment.NewLine + b);
        File.WriteAllText(Path.Combine(directory, baseName + ".log"), log);
        if (process.ExitCode != 0) throw new InvalidOperationException($"ArgyllCMS profile generation failed ({process.ExitCode}). See {baseName}.log.");
    }

    private static BitmapSource RenderTestChart(BitmapSource photo)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            DrawText(dc, "Paper White Protection Test · לבן נייר והבהקים", 32, 40, 38, Brushes.Black);
            DrawText(dc, "לבן מוחלט", 20, 55, 115, Brushes.Black);
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(55, 145, 240, 105));

            DrawText(dc, "אפור ניטרלי · RGB", 20, 335, 115, Brushes.Black);
            byte[] neutrals = [255, 252, 249, 246, 243, 240, 236, 232, 226, 218];
            for (int i = 0; i < neutrals.Length; i++)
            {
                byte v = neutrals[i];
                double x = 335 + i * 86.5;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(v, v, v)), new Pen(Brushes.Gray, 1), new Rect(x, 145, 82, 105));
                DrawText(dc, v.ToString(CultureInfo.InvariantCulture), 14, x + 4, 254, Brushes.Black);
            }

            DrawText(dc, "גווני שמנת וצבעים בהירים", 20, 40, 300, Brushes.Black);
            Color[] creams =
            [
                Color.FromRgb(255, 254, 247), Color.FromRgb(255, 250, 235), Color.FromRgb(255, 245, 224),
                Color.FromRgb(249, 239, 220), Color.FromRgb(245, 236, 224), Color.FromRgb(239, 246, 255),
                Color.FromRgb(230, 244, 250), Color.FromRgb(255, 235, 225), Color.FromRgb(234, 241, 222)
            ];
            for (int i = 0; i < creams.Length; i++)
                dc.DrawRectangle(new SolidColorBrush(creams[i]), new Pen(Brushes.Gray, 1), new Rect(40 + i * 130, 330, 124, 88));

            DrawText(dc, "מעברי הבהקה", 20, 40, 455, Brushes.Black);
            DrawGradient(dc, 40, 490, 1160, 54, Color.FromRgb(205, 205, 205), Colors.White, "אפור ניטרלי");
            DrawGradient(dc, 40, 555, 1160, 54, Color.FromRgb(226, 210, 184), Colors.White, "חם");
            DrawGradient(dc, 40, 620, 1160, 54, Color.FromRgb(205, 224, 250), Colors.White, "קר");

            DrawText(dc, "פרטי תמונה בהירים: עננים, הרים, עור, פרחים ובד", 20, 40, 710, Brushes.Black);
            double photoWidth = 1160, photoHeight = photo.PixelHeight * photoWidth / photo.PixelWidth;
            dc.DrawImage(photo, new Rect(40, 750, photoWidth, Math.Min(photoHeight, 950)));
        }
        var rendered = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static void DrawGradient(DrawingContext dc, double x, double y, double width, double height, Color start, Color end, string label)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        dc.DrawRectangle(brush, new Pen(Brushes.Gray, 1), new Rect(x, y, width, height));
        DrawText(dc, label, 14, x + 8, y + 2, Brushes.Black);
    }

    private static void DrawText(DrawingContext dc, string value, double size, double x, double y, Brush color)
    {
        var text = new FormattedText(value, CultureInfo.GetCultureInfo("he-IL"), FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, color, 1.0);
        dc.DrawText(text, new Point(x, y));
    }

    private static BitmapSource ConvertThroughProfile(BitmapSource source, string profilePath)
    {
        var input = ToBgr24(source);
        var device = new ColorConvertedBitmap(input, new ColorContext(PixelFormats.Bgr24), new ColorContext(new Uri(profilePath)), PixelFormats.Bgr24);
        var preview = new ColorConvertedBitmap(device, new ColorContext(new Uri(profilePath)), new ColorContext(PixelFormats.Bgr24), PixelFormats.Bgr24);
        preview.Freeze();
        return preview;
    }

    private static BitmapSource ToBgr24(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource RenderComparison(BitmapSource none, BitmapSource oldProfile, BitmapSource newProfile)
    {
        const int panelWidth = 740;
        const int panelHeight = 1047;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, panelWidth * 3 + 80, panelHeight + 100));
            string[] labels = ["ללא פרופיל ICU", "הפרופיל הישן", "הפרופיל החדש · הגנה חכמה"];
            BitmapSource[] images = [none, oldProfile, newProfile];
            for (int i = 0; i < images.Length; i++)
            {
                double x = 20 + i * (panelWidth + 20);
                DrawText(dc, labels[i], 22, x, 12, Brushes.Black);
                dc.DrawImage(images[i], new Rect(x, 48, panelWidth, panelHeight));
            }
        }
        var rendered = new RenderTargetBitmap(panelWidth * 3 + 80, panelHeight + 100, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        int stride = source.PixelWidth * 3;
        var bytes = new byte[stride * source.PixelHeight];
        source.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private static object Measure(byte[] first, byte[] second, (int X, int Y, int W, int H) region, int step)
    {
        var delta = new List<double>();
        for (int y = region.Y; y < Math.Min(Height, region.Y + region.H); y += step)
        for (int x = region.X; x < Math.Min(Width, region.X + region.W); x += step)
        {
            int i = (y * Width + x) * 3;
            var a = ScanAnalyzer.ToLab(first[i + 2] / 255d, first[i + 1] / 255d, first[i] / 255d);
            var b = ScanAnalyzer.ToLab(second[i + 2] / 255d, second[i + 1] / 255d, second[i] / 255d);
            delta.Add(DeltaE2000(a, b));
        }
        delta.Sort();
        return new
        {
            samples = delta.Count,
            mean_delta_e_2000 = delta.Count == 0 ? 0 : delta.Average(),
            p95_delta_e_2000 = delta.Count == 0 ? 0 : delta[(int)Math.Floor((delta.Count - 1) * .95)],
            maximum_delta_e_2000 = delta.Count == 0 ? 0 : delta[^1]
        };
    }

    private static double DeltaE2000((double l, double a, double b) x, (double l, double a, double b) y)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        static double Deg(double radians) => radians * 180 / Math.PI;
        static double Hue(double a, double b)
        {
            double h = Deg(Math.Atan2(b, a));
            return h < 0 ? h + 360 : h;
        }

        double c1 = Math.Sqrt(x.a * x.a + x.b * x.b), c2 = Math.Sqrt(y.a * y.a + y.b * y.b);
        double cBar = (c1 + c2) / 2;
        double c7 = Math.Pow(cBar, 7), g = .5 * (1 - Math.Sqrt(c7 / (c7 + Math.Pow(25, 7))));
        double a1 = (1 + g) * x.a, a2 = (1 + g) * y.a;
        double cp1 = Math.Sqrt(a1 * a1 + x.b * x.b), cp2 = Math.Sqrt(a2 * a2 + y.b * y.b);
        double hp1 = Hue(a1, x.b), hp2 = Hue(a2, y.b);
        double dL = y.l - x.l, dC = cp2 - cp1;
        double dh;
        if (cp1 * cp2 == 0) dh = 0;
        else if (Math.Abs(hp2 - hp1) <= 180) dh = hp2 - hp1;
        else if (hp2 <= hp1) dh = hp2 - hp1 + 360;
        else dh = hp2 - hp1 - 360;
        double dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(Rad(dh / 2));
        double lBar = (x.l + y.l) / 2, cpBar = (cp1 + cp2) / 2;
        double hpBar;
        if (cp1 * cp2 == 0) hpBar = hp1 + hp2;
        else if (Math.Abs(hp1 - hp2) <= 180) hpBar = (hp1 + hp2) / 2;
        else if (hp1 + hp2 < 360) hpBar = (hp1 + hp2 + 360) / 2;
        else hpBar = (hp1 + hp2 - 360) / 2;
        double t = 1 - .17 * Math.Cos(Rad(hpBar - 30)) + .24 * Math.Cos(Rad(2 * hpBar)) +
                   .32 * Math.Cos(Rad(3 * hpBar + 6)) - .20 * Math.Cos(Rad(4 * hpBar - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hpBar - 275) / 25, 2));
        double rc = 2 * Math.Sqrt(Math.Pow(cpBar, 7) / (Math.Pow(cpBar, 7) + Math.Pow(25, 7)));
        double sl = 1 + .015 * Math.Pow(lBar - 50, 2) / Math.Sqrt(20 + Math.Pow(lBar - 50, 2));
        double sc = 1 + .045 * cpBar, sh = 1 + .015 * cpBar * t, rt = -Math.Sin(Rad(2 * dTheta)) * rc;
        double lTerm = dL / sl, cTerm = dC / sc, hTerm = dH / sh;
        return Math.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + rt * cTerm * hTerm);
    }

    private static void SavePng(BitmapSource image, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }
}
