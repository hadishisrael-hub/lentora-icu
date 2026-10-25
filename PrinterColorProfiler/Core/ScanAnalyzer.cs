using OpenCvSharp;

namespace PrinterColorProfiler.Core;

public static class ScanAnalyzer
{
    public static ScanResult Analyze(string path)
    {
        using var source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("לא ניתן לקרוא את קובץ הסריקה.");
        var markers = FindMarkers(source);
        if (markers.Count < 4) throw new InvalidDataException("לא נמצאו ארבע פינות דף הכיול. יש לסרוק את כל הדף מחדש.");
        var ordered = Order(markers);
        using var transform = Cv2.GetPerspectiveTransform(ordered, new[]
        {
            new Point2f(140,140), new Point2f(Chart.Width-140,140),
            new Point2f(140,Chart.Height-140), new Point2f(Chart.Width-140,Chart.Height-140)
        });
        using var aligned = new Mat();
        Cv2.WarpPerspective(source, aligned, transform, new OpenCvSharp.Size(Chart.Width, Chart.Height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);
        var whiteLocations = new[] { new Rect(300, 130, 250, 100), new Rect(1100, 130, 250, 100), new Rect(1850, 130, 250, 100), new Rect(300, 2550, 250, 100), new Rect(1850, 2550, 250, 100) };
        var white = whiteLocations.Select(x => Median(aligned, x)).ToArray();
        double wr = white.Select(x => x.r).Order().ElementAt(2), wg = white.Select(x => x.g).Order().ElementAt(2), wb = white.Select(x => x.b).Order().ElementAt(2);
        if (Math.Min(wr, Math.Min(wg, wb)) < 145) throw new InvalidDataException("הלבן של הנייר כהה מדי בסריקה. נסה לסרוק שוב ללא תיקוני תמונה אוטומטיים.");
        var raw = Chart.Patches.Select(p =>
        {
            int x = Chart.Left + p.Column * (Chart.Cell + Chart.Gap) + Chart.Cell / 4;
            int y = Chart.Top + p.Row * (Chart.Cell + Chart.Gap) + Chart.Cell / 4;
            return (patch: p, value: Median(aligned, new Rect(x, y, Chart.Cell / 2, Chart.Cell / 2)));
        }).ToArray();
        var controlSpread = Enumerable.Range(0, Chart.Rows).Select(row => Distance(raw[row * Chart.Columns].value, raw[row * Chart.Columns + 19].value)).Average();
        if (controlSpread > 20) throw new InvalidDataException("הסריקה אינה עקבית מספיק. נסה לסרוק שוב.");
        var patches = new List<MeasuredPatch>(raw.Length);
        foreach (var (patch, value) in raw)
        {
            // Paper white removes the scanner's channel gain. Repeated controls
            // estimate a gentle left/right flat-field correction.
            var left = raw[patch.Row * Chart.Columns].value;
            var right = raw[patch.Row * Chart.Columns + 19].value;
            double t = patch.Column / 19.0;
            double offsetR = (left.r - right.r) * (t - .5) * .5;
            double offsetG = (left.g - right.g) * (t - .5) * .5;
            double offsetB = (left.b - right.b) * (t - .5) * .5;
            double r = Math.Clamp((value.r + offsetR) / wr, 0, 1);
            double g = Math.Clamp((value.g + offsetG) / wg, 0, 1);
            double b = Math.Clamp((value.b + offsetB) / wb, 0, 1);
            var lab = ToLab(r, g, b);
            patches.Add(new(patch, r, g, b, lab.l, lab.a, lab.b));
        }
        return new(patches, controlSpread, wr, wg, wb, path);
    }

    private static List<Point2f> FindMarkers(Mat image)
    {
        using var gray = new Mat(); using var binary = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, binary, 55, 255, ThresholdTypes.BinaryInv);
        Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        double minArea = image.Width * image.Height * .00012;
        return contours.Select(Cv2.BoundingRect)
            .Where(r => r.Width * r.Height > minArea && r.Width / (double)r.Height is > .55 and < 1.8)
            .Select(r => new Point2f(r.X + r.Width / 2f, r.Y + r.Height / 2f)).ToList();
    }
    private static Point2f[] Order(List<Point2f> points)
    {
        Point2f Closest(Func<Point2f, double> score) => points.OrderBy(score).First();
        return [Closest(p => p.X + p.Y), Closest(p => -p.X + p.Y), Closest(p => p.X - p.Y), Closest(p => -p.X - p.Y)];
    }
    private static (double r, double g, double b) Median(Mat mat, Rect roi)
    {
        using var region = new Mat(mat, roi);
        var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
        for (int y = 0; y < region.Height; y += 2)
        for (int x = 0; x < region.Width; x += 2)
        {
            var c = region.At<Vec3b>(y, x); bs.Add(c.Item0); gs.Add(c.Item1); rs.Add(c.Item2);
        }
        rs.Sort(); gs.Sort(); bs.Sort(); int mid = rs.Count / 2;
        return (rs[mid], gs[mid], bs[mid]);
    }
    private static double Distance((double r,double g,double b) a, (double r,double g,double b) b) => Math.Sqrt(Math.Pow(a.r-b.r,2)+Math.Pow(a.g-b.g,2)+Math.Pow(a.b-b.b,2));

    // Scanner RGB is an approximation of sRGB; paper/gray normalization makes it
    // useful for relative iteration, never a spectrophotometer measurement.
    public static (double l, double a, double b) ToLab(double r, double g, double b)
    {
        static double Lin(double c) => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        r = Lin(r); g = Lin(g); b = Lin(b);
        double x = (.4124564*r+.3575761*g+.1804375*b)/.95047;
        double y = .2126729*r+.7151522*g+.0721750*b;
        double z = (.0193339*r+.1191920*g+.9503041*b)/1.08883;
        static double F(double t) => t > .008856 ? Math.Cbrt(t) : 7.787*t + 16.0/116;
        return (116*F(y)-16, 500*(F(x)-F(y)), 200*(F(y)-F(z)));
    }
}
