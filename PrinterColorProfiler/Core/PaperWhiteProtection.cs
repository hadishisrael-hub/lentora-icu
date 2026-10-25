namespace PrinterColorProfiler.Core;

public static class PaperWhiteProtection
{
    public static double CorrectionWeight(Rgb source, PaperWhiteProtectionMode mode, double strength = .7)
    {
        if (source.R == 255 && source.G == 255 && source.B == 255) return 0;
        if (mode == PaperWhiteProtectionMode.ExactWhiteOnly) return 1;

        var sourceLab = ScanAnalyzer.ToLab(source.R / 255d, source.G / 255d, source.B / 255d);
        var pcsLab = ColorScience.D65ToD50(sourceLab.l, sourceLab.a, sourceLab.b);
        double distance = Math.Sqrt(Math.Pow(100 - pcsLab.L, 2) + pcsLab.A * pcsLab.A + pcsLab.B * pcsLab.B);
        double radius = mode == PaperWhiteProtectionMode.Extended ? 24 : 14;
        double t = Math.Clamp(distance / radius, 0, 1);
        double smooth = t * t * (3 - 2 * t);
        double protection = (1 - smooth) * (mode == PaperWhiteProtectionMode.SmartGradient ? 1 : Math.Clamp(strength, 0, 1));
        return 1 - protection;
    }
}
