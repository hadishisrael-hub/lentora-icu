namespace PrinterColorProfiler.Core;

public sealed record ToneEdit(double Black = 0, double Shadows = 0, double Midtones = 0, double Highlights = 0);
public sealed record GrayBalance(double ShadowA = 0, double ShadowB = 0, double MidA = 0, double MidB = 0, double HighA = 0, double HighB = 0);
public sealed record HueEdit(double Hue = 0, double Saturation = 0, double Lightness = 0)
{
    public bool IsNeutral => Hue == 0 && Saturation == 0 && Lightness == 0;
}
public sealed record HueBand(string Id, string Label, double Center, string Swatch);

// A non-destructive parameter set describing the wanted change in the PRINT, in Lab units
// (ΔL, Δa, Δb, chroma %, hue degrees). Every candidate is rebuilt from the original scan,
// its feedback and one of these, so an edit can be reopened and changed at any time.
public sealed record ProfileEdit
{
    public const double ToneRange = 8, GrayRange = 6, GlobalLRange = 8, GlobalABRange = 8, ContrastRange = 25, SaturationRange = 30,
        HueShiftRange = 20, HueSaturationRange = 40, HueLightnessRange = 8;

    public double Brightness { get; init; }
    public double Contrast { get; init; }   // percent
    public double Saturation { get; init; } // percent
    public double Warmth { get; init; }     // Δb
    public double Tint { get; init; }       // Δa, positive = magenta
    public ToneEdit Tone { get; init; } = new();
    public GrayBalance Gray { get; init; } = new();
    public Dictionary<string, HueEdit> Hues { get; init; } = [];

    public static IReadOnlyList<HueBand> Bands { get; } =
    [
        new("red", "אדום", 25, "#D93A3A"), new("orange", "כתום", 60, "#EE8A2E"), new("yellow", "צהוב", 92, "#E9C83A"),
        new("green", "ירוק", 140, "#3FA45B"), new("cyan", "תכלת", 200, "#2DB3C7"), new("blue", "כחול", 265, "#3463D1"),
        new("purple", "סגול", 305, "#8A4FD0"), new("magenta", "מג׳נטה", 345, "#D0479B"),
    ];

    public static ProfileEdit FromManual(ManualTuning? manual) => manual is null ? new() : new()
    {
        Brightness = manual.Brightness, Contrast = manual.Contrast, Saturation = manual.Saturation, Warmth = manual.Warmth, Tint = manual.Tint
    };

    public bool IsNeutral => Brightness == 0 && Contrast == 0 && Saturation == 0 && Warmth == 0 && Tint == 0
        && Tone == new ToneEdit() && Gray == new GrayBalance() && Hues.Values.All(h => h.IsNeutral);

    public HueEdit HueFor(string band) => Hues.TryGetValue(band, out var edit) ? edit : new();

    /// <summary>The wanted print change for a color given in Lab (D65), before paper-white protection.</summary>
    public (double dL, double dA, double dB) Delta(double l, double a, double b)
    {
        double l0 = l, a0 = a, b0 = b;

        // Global
        double contrast = 1 + Math.Clamp(Contrast, -ContrastRange, ContrastRange) / 100;
        double saturation = 1 + Math.Clamp(Saturation, -SaturationRange, SaturationRange) / 100;
        l = 50 + (l - 50) * contrast + Math.Clamp(Brightness, -GlobalLRange, GlobalLRange);
        a = a * saturation + Math.Clamp(Tint, -GlobalABRange, GlobalABRange);
        b = b * saturation + Math.Clamp(Warmth, -GlobalABRange, GlobalABRange);

        // Tone: overlapping bumps on the ORIGINAL lightness so the zones do not drift.
        double ws = Bump(l0, 20, 30), wm = Bump(l0, 50, 30), wh = Bump(l0, 80, 30);
        double wk = Math.Pow(Math.Clamp(1 - l0 / 25, 0, 1), 2);
        l += C(Tone.Black, ToneRange) * wk + C(Tone.Shadows, ToneRange) * ws + C(Tone.Midtones, ToneRange) * wm + C(Tone.Highlights, ToneRange) * wh;

        // Gray balance: only near-neutral colors.
        double chroma0 = Math.Sqrt(a0 * a0 + b0 * b0);
        double neutral = Math.Clamp(1 - chroma0 / 12, 0, 1);
        a += neutral * (C(Gray.ShadowA, GrayRange) * ws + C(Gray.MidA, GrayRange) * wm + C(Gray.HighA, GrayRange) * wh);
        b += neutral * (C(Gray.ShadowB, GrayRange) * ws + C(Gray.MidB, GrayRange) * wm + C(Gray.HighB, GrayRange) * wh);

        // Selective color by hue band, weighted by how colorful the original is.
        if (Hues.Count > 0 && chroma0 > 3)
        {
            double hue0 = (Math.Atan2(b0, a0) * 180 / Math.PI + 360) % 360;
            double colorful = Math.Clamp((chroma0 - 4) / 20, 0, 1);
            double shift = 0, scale = 0, lift = 0;
            foreach (var band in Bands)
            {
                var edit = HueFor(band.Id);
                if (edit.IsNeutral) continue;
                double distance = Math.Abs(((hue0 - band.Center + 540) % 360) - 180);
                double w = Math.Clamp(1 - distance / 45, 0, 1) * colorful;
                if (w <= 0) continue;
                shift += C(edit.Hue, HueShiftRange) * w;
                scale += C(edit.Saturation, HueSaturationRange) / 100 * w;
                lift += C(edit.Lightness, HueLightnessRange) * w;
            }
            double chroma = Math.Sqrt(a * a + b * b), hue = Math.Atan2(b, a) + shift * Math.PI / 180;
            chroma *= 1 + scale;
            a = chroma * Math.Cos(hue); b = chroma * Math.Sin(hue);
            l += lift;
        }

        return (Math.Clamp(l - l0, -15, 15), Math.Clamp(a - a0, -15, 15), Math.Clamp(b - b0, -15, 15));
    }

    private static double C(double v, double range) => Math.Clamp(v, -range, range);
    private static double Bump(double l, double center, double halfWidth)
    {
        double t = Math.Abs(l - center) / halfWidth;
        return t >= 1 ? 0 : (Math.Cos(t * Math.PI) + 1) / 2;
    }
}
