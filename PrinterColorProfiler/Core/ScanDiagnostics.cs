namespace PrinterColorProfiler.Core;

// Scanner RGB gives relative evidence only. These figures are used to guide
// the next print, never presented as spectrophotometer accuracy.
public sealed record ScanDiagnostics(double MeanRgbError, double GrayCast, double SkyMagenta, int SkySamples, double ControlSpread)
{
    public static ScanDiagnostics From(ScanResult scan)
    {
        var usable = scan.Patches.Where(p => p.Patch.Group != "control").ToArray();
        double error = usable.Average(p =>
            (Math.Abs(p.R - p.Patch.Source.R / 255d) + Math.Abs(p.G - p.Patch.Source.G / 255d) + Math.Abs(p.B - p.Patch.Source.B / 255d)) / 3) * 100;
        var grays = usable.Where(p => p.Patch.Group == "gray" && p.Patch.Source.R is > 45 and < 220).ToArray();
        double cast = grays.Length == 0 ? 0 : grays.Average(p => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B))) * 100;
        var skies = usable.Where(p => FineTune.SkyWeight(p.Patch.Source) > .45).ToArray();
        // Positive: magenta excess (red/blue relative to green) in cyan-blue patches.
        double magenta = skies.Length == 0 ? 0 : skies.Average(p =>
            ((p.R - p.G) - (p.Patch.Source.R - p.Patch.Source.G) / 255d)) * 100;
        return new(error, cast, magenta, skies.Length, scan.ControlSpread);
    }
}
