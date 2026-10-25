using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrinterColorProfiler.Core;

public static class WindowsServices
{
    [DllImport("winspool.drv", EntryPoint="OpenPrinterW", SetLastError=true, CharSet=CharSet.Unicode)] private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError=true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", EntryPoint="DocumentPropertiesW", SetLastError=true, CharSet=CharSet.Unicode)] private static extern int DocumentProperties(IntPtr hwnd, IntPtr printer, string name, IntPtr output, IntPtr input, int mode);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool InstallColorProfileW(string? machine, string profile);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool AssociateColorProfileWithDeviceW(string? machine, string profile, string device);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool WcsSetDefaultColorProfile(int scope, string device, int type, int subtype, int id, string profile);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool WcsGetDefaultColorProfileSize(int scope, string device, int type, int subtype, int id, out uint size);
    [DllImport("mscms.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool WcsGetDefaultColorProfile(int scope, string device, int type, int subtype, int id, uint size, System.Text.StringBuilder name);

    public static string[] Printers()
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues().Select(q => q.Name).OrderBy(x => x).ToArray();
    }
    public static string CaptureDevMode(string printerName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new InvalidOperationException("לא ניתן לקרוא את הגדרות המדפסת.");
        try
        {
            int size = DocumentProperties(IntPtr.Zero, handle, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0) throw new InvalidOperationException("הדרייבר אינו מספק את הגדרותיו.");
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                if (DocumentProperties(IntPtr.Zero, handle, printerName, ptr, IntPtr.Zero, 2) < 0) throw new InvalidOperationException("קריאת הגדרות הדרייבר נכשלה.");
                byte[] bytes = new byte[size]; Marshal.Copy(ptr, bytes, 0, size); return Convert.ToBase64String(bytes);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        finally { ClosePrinter(handle); }
    }

    public static bool DriverSettingsMatch(ProfileSession session) => CaptureDevMode(session.PrinterName) == session.DriverSettingsBase64;

    public static bool PrintImage(ProfileSession session, BitmapSource bitmap, string title, PageOrientation orientation)
    {
        var dialog = new PrintDialog();
        using (var server = new LocalPrintServer()) dialog.PrintQueue = server.GetPrintQueue(session.PrinterName);
        if (!string.IsNullOrEmpty(session.PrintTicketXml))
        {
            using var stream = new MemoryStream(Convert.FromBase64String(session.PrintTicketXml));
            dialog.PrintTicket = new PrintTicket(stream);
        }
        dialog.PrintTicket ??= dialog.PrintQueue.DefaultPrintTicket;
        dialog.PrintTicket.PageOrientation = orientation;
        if (dialog.ShowDialog() != true) return false;
        if (dialog.PrintTicket.PageOrientation != orientation)
            throw new InvalidOperationException(orientation == PageOrientation.Portrait ? "דף הכיול חייב להיות מוגדר להדפסה לאורך. שנה את כיוון הדף בחלון ההדפסה." : "תמונת הבדיקה חייבת להיות מוגדרת להדפסה לרוחב. שנה את כיוון הדף בחלון ההדפסה.");
        var validation = dialog.PrintQueue.MergeAndValidatePrintTicket(dialog.PrintQueue.DefaultPrintTicket, dialog.PrintTicket);
        if (validation.ValidatedPrintTicket.PageOrientation != orientation)
            throw new InvalidOperationException("הדרייבר לא קיבל את כיוון הדף הדרוש. בדוק את הגדרות המדפסת.");
        dialog.PrintTicket = validation.ValidatedPrintTicket;
        var visual = CreatePageVisual(bitmap, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        dialog.PrintVisual(visual, title);
        return true;
    }

    public static DrawingVisual CreatePageVisual(BitmapSource bitmap, double printableWidth, double printableHeight)
    {
        if (printableWidth <= 0 || printableHeight <= 0) throw new InvalidOperationException("המדפסת לא דיווחה על שטח הדפסה תקין.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double w = printableWidth, h = printableHeight;
            double scale = Math.Min(w / bitmap.PixelWidth, h / bitmap.PixelHeight);
            double iw = bitmap.PixelWidth * scale, ih = bitmap.PixelHeight * scale;
            dc.DrawImage(bitmap, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
        }
        return visual;
    }

    public static string InstallAndVerify(ProfileSession session, Candidate candidate)
    {
        if (string.IsNullOrEmpty(session.DriverSettingsBase64)) throw new InvalidOperationException("חסרות הגדרות המדפסת שנשמרו בתחילת הכיול.");
        try { return InstallDirect(session,candidate); }
        catch (UnauthorizedAccessException)
        {
            var info=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=true,Verb="runas" };
            info.ArgumentList.Add("--install");info.ArgumentList.Add(Path.Combine(session.DirectoryPath,"session.json"));info.ArgumentList.Add(session.SelectedCandidate.ToString());
            using var child=Process.Start(info) ?? throw new InvalidOperationException("לא ניתן לפתוח הרשאות להתקנת הפרופיל.");
            child.WaitForExit();
            if(child.ExitCode!=0)throw new InvalidOperationException("התקנת הפרופיל בהרשאת מנהל נכשלה.");
            return VerifyDefault(session.PrinterName,$"PCP_{session.Id:N}_{candidate.Number}.icc");
        }
    }
    public static string InstallDirect(ProfileSession session, Candidate candidate)
    {
        string fileName=$"PCP_{session.Id:N}_{candidate.Number}.icc";
        string source=Path.Combine(session.DirectoryPath,fileName);
        File.Copy(candidate.IccPath,source,true);
        if (!InstallColorProfileW(null, source))
        {
            int error=Marshal.GetLastWin32Error();
            if(error==5)throw new UnauthorizedAccessException("נדרשת הרשאת מנהל להתקנת הפרופיל.");
            throw new InvalidOperationException($"Windows לא התקין את הפרופיל ({error}).");
        }
        if (!AssociateColorProfileWithDeviceW(null, fileName, session.PrinterName)) throw new InvalidOperationException($"Windows לא שייך את הפרופיל למדפסת ({Marshal.GetLastWin32Error()}).");
        if (!WcsSetDefaultColorProfile(1, session.PrinterName, 0, 0, 0, fileName)) throw new InvalidOperationException("Windows לא הגדיר את הפרופיל כברירת מחדל.");
        return VerifyDefault(session.PrinterName,fileName);
    }
    private static string VerifyDefault(string printerName,string fileName)
    {
        if (!WcsGetDefaultColorProfileSize(1, printerName, 0, 0, 0, out uint size)) throw new InvalidOperationException("לא ניתן לאמת את פרופיל ברירת המחדל.");
        var buffer = new System.Text.StringBuilder((int)size / 2 + 2);
        if (!WcsGetDefaultColorProfile(1, printerName, 0, 0, 0, size, buffer) || !string.Equals(Path.GetFileName(buffer.ToString()), fileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows לא אישר שהפרופיל פעיל במדפסת.");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"spool","drivers","color",fileName);
    }
}
