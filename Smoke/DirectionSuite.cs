using PrinterColorProfiler.Core;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smoke;

// End to end: every correction is written to a TI3, built into a real ICC by colprof and
// applied through the same WPF conversion the app uses before printing. The test then checks
// that the device RGB sent to the printer moves the way the user asked (higher RGB = lighter).
internal static class DirectionSuite
{
    private sealed record Probe(string Name, byte R, byte G, byte B);
    private delegate double Measure(double[] rgb);

    private static readonly Probe Gray20 = new("gray 20", 20, 20, 20), Gray40 = new("gray 40", 40, 40, 40), Gray70 = new("gray 70", 70, 70, 70),
        Gray128 = new("gray 128", 128, 128, 128), Gray200 = new("gray 200", 200, 200, 200), Gray235 = new("gray 235", 235, 235, 235),
        Red = new("red", 215, 45, 45), Blue = new("blue", 45, 75, 205), Teal = new("teal", 60, 160, 140), SkyBlue = new("sky", 110, 175, 230);

    private static double Light(double[] c) => (c[0] + c[1] + c[2]) / 3;
    private static double Redness(double[] c) => c[0] - (c[1] + c[2]) / 2;
    private static double Blueness(double[] c) => c[2] - (c[0] + c[1]) / 2;
    private static double Spread(double[] c) => c.Max() - c.Min();
    private static double Warmth(double[] c) => c[0] - c[2];
    private static double Magenta(double[] c) => (c[0] + c[2]) / 2 - c[1];

    public static void Run(ScanResult scan)
    {
        string colprof = ProfileBuilder.FindArgyll() ?? throw new InvalidOperationException("ArgyllCMS colprof.exe was not found.");
        string work = Path.Combine(AppContext.BaseDirectory, "direction-smoke");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var skinSource = scan.Patches.First(p => p.Patch.Group == "skin").Patch.Source;
        var skin = new Probe("skin", skinSource.R, skinSource.G, skinSource.B);
        Probe[] probes = [Gray20, Gray40, Gray70, Gray128, Gray200, Gray235, Red, Blue, Teal, SkyBlue, skin];

        string F(string id) => FineTune.Encode(id, 2);
        // (case, feedback, manual tuning, probe, measure, expected sign of the change)
        var cases = new (string Name, string[] Feedback, ProfileEdit Edit, (Probe Probe, Measure Measure, int Sign)[] Checks)[]
        {
            ("too dark", [F(FeedbackCatalog.Darker)], new(), [(Gray128, Light, +1)]),
            ("too light", [F(FeedbackCatalog.Lighter)], new(), [(Gray128, Light, -1)]),
            ("contrast too strong", [F(FeedbackCatalog.ContrastHigh)], new(), [(Gray70, Light, +1), (Gray200, Light, -1)]),
            ("contrast too weak", [F(FeedbackCatalog.ContrastLow)], new(), [(Gray70, Light, -1), (Gray200, Light, +1)]),
            ("blocked blacks", [F(FeedbackCatalog.BlackBlocked)], new(), [(Gray40, Light, +1)]),
            ("weak blacks", [F(FeedbackCatalog.BlackWeak)], new(), [(Gray40, Light, -1)]),
            ("burnt whites", [F(FeedbackCatalog.WhitesBurnt)], new(), [(Gray235, Light, -1)]),
            ("lost shadow detail", [F(FeedbackCatalog.ShadowDetail)], new(), [(Gray40, Light, +1)]),
            ("lost highlight detail", [F(FeedbackCatalog.HighlightDetail)], new(), [(Gray235, Light, -1)]),
            ("skin too dark", [F(FeedbackCatalog.SkinDark)], new(), [(skin, Light, +1)]),
            ("skin too light", [F(FeedbackCatalog.SkinLight)], new(), [(skin, Light, -1)]),
            ("skin too yellow", [F(FeedbackCatalog.SkinYellow)], new(), [(skin, Blueness, +1)]),
            ("reds too strong", [F(FeedbackCatalog.RedsStrong)], new(), [(Red, Redness, -1)]),
            ("blues too strong", [F(FeedbackCatalog.BluesStrong)], new(), [(Blue, Blueness, -1)]),
            ("sky too magenta", [F(FeedbackCatalog.Sky)], new(), [(SkyBlue, Magenta, -1)]),
            ("manual brightness +", [], new() { Brightness = 3 }, [(Gray128, Light, +1)]),
            ("manual brightness -", [], new() { Brightness = -3 }, [(Gray128, Light, -1)]),
            ("manual contrast +", [], new() { Contrast = 12 }, [(Gray70, Light, -1), (Gray200, Light, +1)]),
            ("manual saturation +", [], new() { Saturation = 15 }, [(Teal, Spread, +1)]),
            ("manual saturation -", [], new() { Saturation = -15 }, [(Teal, Spread, -1)]),
            ("manual warmth +", [], new() { Warmth = 3 }, [(Gray128, Warmth, +1)]),
            ("manual tint + (magenta)", [], new() { Tint = 3 }, [(Gray128, Magenta, +1)]),
            ("editor shadows +", [], new() { Tone = new ToneEdit(Shadows: 4) }, [(Gray40, Light, +1)]),
            ("editor midtones +", [], new() { Tone = new ToneEdit(Midtones: 4) }, [(Gray128, Light, +1)]),
            ("editor highlights -", [], new() { Tone = new ToneEdit(Highlights: -4) }, [(Gray200, Light, -1)]),
            ("editor deeper black", [], new() { Tone = new ToneEdit(Black: -6) }, [(Gray20, Light, -1)]),
            ("editor gray mid magenta", [], new() { Gray = new GrayBalance(MidA: 3) }, [(Gray128, Magenta, +1)]),
            ("editor gray mid yellow", [], new() { Gray = new GrayBalance(MidB: 3) }, [(Gray128, Blueness, -1)]),
            ("editor blue saturation -", [], new() { Hues = new() { ["blue"] = new HueEdit(Saturation: -30) } }, [(Blue, Blueness, -1)]),
            ("editor red lighter", [], new() { Hues = new() { ["red"] = new HueEdit(Lightness: 6) } }, [(Red, Light, +1)]),
        };

        // colprof runs are independent processes, so build them in parallel; the WPF lookups stay on this thread.
        var profiles = new string[cases.Length + 1];
        Parallel.For(0, cases.Length + 1, i => profiles[i] = i == cases.Length
            ? Build(colprof, work, "base", scan, [], new ProfileEdit())
            : Build(colprof, work, $"case{i}", scan, cases[i].Feedback, cases[i].Edit));
        var baseline = Lookup(profiles[cases.Length], probes);

        var failures = new List<string>();
        for (int i = 0; i < cases.Length; i++)
        {
            var (name, _, _, checks) = cases[i];
            var result = Lookup(profiles[i], probes);
            foreach (var (probe, measure, sign) in checks)
            {
                double change = measure(result[probe]) - measure(baseline[probe]);
                bool ok = change * sign > .4;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-24} {probe.Name,-9} change {change,6:+0.00;-0.00} (expected {(sign > 0 ? "up" : "down")})");
                if (!ok) failures.Add($"{name} / {probe.Name}: {change:+0.00;-0.00}");
            }
        }

        // The synthetic chart has neutral grays, so the gray-cast correction is checked on the model:
        // it must exaggerate the measured cast so the ICC inverse removes more of it.
        var apply = typeof(ProfileBuilder).GetMethod("ApplyFeedback", BindingFlags.NonPublic | BindingFlags.Static)!;
        var gray = scan.Patches.First(p => p.Patch.Group == "gray" && p.Patch.Source.R is > 90 and < 160);
        var cast = gray with { A = 3, LabB = -2 };
        var corrected = ((double l, double a, double b))apply.Invoke(null, [cast, new[] { F(FeedbackCatalog.GraysCast) }])!;
        bool castOk = corrected.a > cast.A + .1 && corrected.b < cast.LabB - .05;
        Console.WriteLine($"  {(castOk ? "ok  " : "FAIL")} {"grays not neutral",-24} model    a {cast.A:+0.00;-0.00}->{corrected.a:+0.00;-0.00}, b {cast.LabB:+0.00;-0.00}->{corrected.b:+0.00;-0.00}");
        if (!castOk) failures.Add("grays not neutral: model cast was not exaggerated");

        // Selective color must leave neutrals alone, and gray balance must leave saturated colors alone.
        var hueOnly = new ProfileEdit { Hues = new() { ["blue"] = new HueEdit(10, 30, 5), ["red"] = new HueEdit(-10, -30, -5) } };
        var grayOnly = new ProfileEdit { Gray = new GrayBalance(4, 4, 4, 4, 4, 4) };
        bool isolated = hueOnly.Delta(50, 0, 0) == (0, 0, 0) && hueOnly.Delta(80, 1, -1) == (0, 0, 0) && grayOnly.Delta(50, 40, 30) == (0, 0, 0);
        Console.WriteLine($"  {(isolated ? "ok  " : "FAIL")} {"editor isolation",-24} hue bands skip neutrals, gray balance skips saturated colors");
        if (!isolated) failures.Add("editor isolation");

        if (failures.Count > 0) throw new Exception("Correction direction failures:\n" + string.Join("\n", failures));
        Console.WriteLine($"Correction direction OK: {cases.Length} ICC builds + gray-cast and editor isolation checks");
    }

    private static string Build(string colprof, string work, string name, ScanResult scan, string[] feedback, ProfileEdit edit)
    {
        var write = typeof(ProfileBuilder).GetMethod("WriteTi3Edited", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(ProfileBuilder), "WriteTi3Edited");
        write.Invoke(null, [scan, Path.Combine(work, name + ".ti3"), feedback, PaperWhiteProtectionMode.SmartGradient, .7, edit]);
        var start = new ProcessStartInfo(colprof) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "m", "-a", "l", "-D", "Direction " + name, name }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        string icc = Path.Combine(work, name + ".icc"), icm = Path.Combine(work, name + ".icm");
        if (process.ExitCode == 0 && !File.Exists(icc) && File.Exists(icm)) File.Move(icm, icc, true);
        if (process.ExitCode != 0 || !File.Exists(icc)) throw new Exception($"colprof failed for {name}: {output}");
        return icc;
    }

    private static Dictionary<Probe, double[]> Lookup(string icc, Probe[] probes)
    {
        // One 8x8 block per probe avoids any resampling at block edges.
        const int block = 8;
        int width = probes.Length * block, stride = width * 3;
        var pixels = new byte[stride * block];
        for (int y = 0; y < block; y++)
            for (int i = 0; i < probes.Length; i++)
                for (int x = 0; x < block; x++)
                {
                    int o = y * stride + (i * block + x) * 3;
                    pixels[o] = probes[i].B; pixels[o + 1] = probes[i].G; pixels[o + 2] = probes[i].R;
                }
        var source = BitmapSource.Create(width, block, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var converted = new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgr24), new ColorContext(new Uri(icc)), PixelFormats.Bgr24);
        var output = new byte[stride * block];
        converted.CopyPixels(output, stride, 0);
        var result = new Dictionary<Probe, double[]>();
        for (int i = 0; i < probes.Length; i++)
        {
            int o = (block / 2) * stride + (i * block + block / 2) * 3;
            result[probes[i]] = [output[o + 2], output[o + 1], output[o]];
        }
        return result;
    }
}
