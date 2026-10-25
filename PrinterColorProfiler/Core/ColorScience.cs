namespace PrinterColorProfiler.Core;

public static class ColorScience
{
    public static (double L, double A, double B) D65ToD50(double l, double a, double b)
    {
        static double Inverse(double t) => t * t * t > .008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        double x = .95047 * Inverse(fx), y = Inverse(fy), z = 1.08883 * Inverse(fz);
        double xx = 1.0478112 * x + .0228866 * y - .0501270 * z;
        double yy = .0295424 * x + .9904844 * y - .0170491 * z;
        double zz = -.0092345 * x + .0150436 * y + .7521316 * z;
        static double F(double t) => t > .008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fxx = F(xx / .96422), fyy = F(yy), fzz = F(zz / .82521);
        return (116 * fyy - 16, 500 * (fxx - fyy), 200 * (fyy - fzz));
    }

    /// <summary>Inverse of <see cref="ScanAnalyzer.ToLab"/>: Lab (D65) to sRGB 0..1, clipped.</summary>
    public static (double R, double G, double B) LabToSrgb(double l, double a, double b)
    {
        static double Inverse(double t) => t * t * t > .008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        double x = .95047 * Inverse(fx), y = Inverse(fy), z = 1.08883 * Inverse(fz);
        double r = 3.2404542 * x - 1.5371385 * y - .4985314 * z;
        double g = -.9692660 * x + 1.8760108 * y + .0415560 * z;
        double bl = .0556434 * x - .2040259 * y + 1.0572252 * z;
        static double Gamma(double c) => Math.Clamp(c <= .0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - .055, 0, 1);
        return (Gamma(r), Gamma(g), Gamma(bl));
    }
}
