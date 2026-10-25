namespace PrinterColorProfiler.Core;

public static class FineTune
{
    public const string SkyMagentaLabel = "שמיים ורודים / סגולים מדי";
    public static double SkyWeight(Rgb rgb)
    {
        double r = rgb.R / 255d, g = rgb.G / 255d, b = rgb.B / 255d;
        double cyanBlue = Math.Clamp((b - r - .07) / .28, 0, 1) * Math.Clamp((g - r - .03) / .25, 0, 1);
        double brightness = Math.Clamp((r + g + b - .55) / .45, 0, 1);
        return cyanBlue * brightness;
    }

    /// <summary>Parses "id|strength"; the first part is normalized to a stable feedback id.</summary>
    public static (string Id, double Strength) Parse(string value)
    {
        var parts = value.Split('|', 2);
        return (FeedbackCatalog.Resolve(parts[0]), parts.Length == 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var strength) ? Math.Clamp(strength, 0, 4) : 1);
    }

    public static string Encode(string idOrLabel, double strength) =>
        FeedbackCatalog.Resolve(idOrLabel) + "|" + Math.Clamp(strength, 0, 4).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
