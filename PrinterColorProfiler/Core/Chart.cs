using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrinterColorProfiler.Core;

public static class Chart
{
    public static string PackagedPath => Path.Combine(AppContext.BaseDirectory, "Assets", "CalibrationChart.png");
    public static BitmapSource LoadPackaged()
    {
        if (!File.Exists(PackagedPath)) throw new FileNotFoundException("דף הכיול חסר מחבילת ההתקנה.", PackagedPath);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(PackagedPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        if (image.PixelWidth != Width / 2 || image.PixelHeight != Height / 2)
            throw new InvalidDataException("קובץ דף הכיול אינו בגודל הצפוי.");
        return image;
    }
    public const int Columns = 20;
    public const int Rows = 16;
    public const int Width = 2480;
    public const int Height = 3508;
    public const int Left = 170;
    public const int Top = 360;
    public const int Cell = 106;
    public const int Gap = 6;
    public static IReadOnlyList<Patch> Patches { get; } = MakePatches();

    private static IReadOnlyList<Patch> MakePatches()
    {
        var list = new List<Patch>();
        for (int row = 0; row < Rows; row++)
        for (int col = 0; col < Columns; col++)
        {
            int index = row * Columns + col;
            Rgb rgb; string group;
            if (col == 0 || col == 19) { var v = (byte)Math.Round(row * 255.0 / (Rows - 1)); rgb = new(v, v, v); group = "control"; }
            else if (row == 0) { var v = (byte)Math.Round((col - 1) * 255.0 / 17); rgb = new(v, v, v); group = "gray"; }
            else if (row == 1) { var v = (byte)Math.Round((col - 1) * 255.0 / 17); rgb = new(v, v, v); group = "gray"; }
            else if (row == 2) { rgb = (col % 6) switch { 0 => new(255,0,0), 1 => new(0,255,0), 2 => new(0,0,255), 3 => new(0,255,255), 4 => new(255,0,255), _ => new(255,255,0) }; group = "primary"; }
            else if (row == 3) { int t = (col - 1) * 12; rgb = new((byte)Math.Min(255, 125 + t / 2), (byte)Math.Min(255, 75 + t / 3), (byte)Math.Min(255, 55 + t / 4)); group = "skin"; }
            else
            {
                int k = index - 80;
                int r = (k * 73 + row * 19) % 256, g = (k * 151 + col * 13) % 256, b = (k * 211 + row * 29) % 256;
                rgb = new((byte)r, (byte)g, (byte)b); group = "color";
            }
            list.Add(new(index, row, col, rgb, group));
        }
        return list;
    }

    public static BitmapSource Render()
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            var black = Brushes.Black;
            foreach (var (x, y) in new[] { (100d, 100d), (Width - 180d, 100d), (100d, Height - 180d), (Width - 180d, Height - 180d) })
                dc.DrawRectangle(black, null, new Rect(x, y, 80, 80));
            foreach (var p in Patches)
            {
                var color = Color.FromRgb(p.Source.R, p.Source.G, p.Source.B);
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(Left + p.Column * (Cell + Gap), Top + p.Row * (Cell + Gap), Cell, Cell));
            }
        }
        var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(drawing);
        target.Freeze();
        return target;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
    }
}
