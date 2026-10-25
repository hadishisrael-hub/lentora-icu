using System.Text.Json;

namespace PrinterColorProfiler.Core;

public record Rgb(byte R, byte G, byte B);
public record Patch(int Index, int Row, int Column, Rgb Source, string Group);
public record MeasuredPatch(Patch Patch, double R, double G, double B, double L, double A, double LabB);
public record ScanResult(List<MeasuredPatch> Patches, double ControlSpread, double WhiteR, double WhiteG, double WhiteB, string ScanPath);
public enum PaperWhiteProtectionMode { SmartGradient, ExactWhiteOnly, Extended }
public sealed record ManualTuning(double Brightness = 0, double Contrast = 0, double Saturation = 0,
    double Warmth = 0, double Tint = 0);

public record Candidate(int Number, string IccPath, string Ti3Path, string[] Feedback, DateTime CreatedAt,
    string? VerificationScanPath = null, ScanDiagnostics? Verification = null)
{
    public PaperWhiteProtectionMode WhiteProtectionMode { get; init; } = PaperWhiteProtectionMode.SmartGradient;
    public double WhiteProtectionStrength { get; init; } = .7;
    // Kept so sessions from before the profile editor still load; new candidates store Edit.
    public ManualTuning ManualTuning { get; init; } = new();
    public ProfileEdit? Edit { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public ProfileEdit EffectiveEdit => Edit ?? ProfileEdit.FromManual(ManualTuning);
}

public sealed class ProfileSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PrinterName { get; set; } = "";
    public string Paper { get; set; } = "";
    public string DriverSettingsBase64 { get; set; } = "";
    public string PrintTicketXml { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Status { get; set; } = "בהכנה";
    public string ScanPath { get; set; } = "";
    public PaperWhiteProtectionMode WhiteProtectionMode { get; set; } = PaperWhiteProtectionMode.SmartGradient;
    public double WhiteProtectionStrength { get; set; } = .7;
    public List<Candidate> Candidates { get; set; } = [];
    public int SelectedCandidate { get; set; } = -1;
    public bool Installed { get; set; }
    public string DirectoryPath => Path.Combine(Store.Root, Id.ToString("N"));
}

public static class Store
{
    public static string AppDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrinterColorProfiler");
    // Overridden only by the screenshot renderer so it never touches real profiles.
    public static string? RootOverride { get; set; }
    public static string Root => RootOverride ?? Path.Combine(AppDataRoot, "Profiles");
    public static void Save(ProfileSession session)
    {
        Directory.CreateDirectory(session.DirectoryPath);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "session.json"), JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static List<ProfileSession> Load()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "session.json", SearchOption.AllDirectories)
            .Select(path => { try { return JsonSerializer.Deserialize<ProfileSession>(File.ReadAllText(path)); } catch { return null; } })
            .OfType<ProfileSession>().OrderByDescending(x => x.CreatedAt).ToList();
    }
}
