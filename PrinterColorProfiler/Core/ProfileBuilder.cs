using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PrinterColorProfiler.Core;

public static class ProfileBuilder
{
    public static string? FindArgyll()
    {
        var paths = new List<string>();
        var env = Environment.GetEnvironmentVariable("PRINTER_PROFILER_ARGYLL");
        if (!string.IsNullOrWhiteSpace(env)) paths.Add(env);
        paths.AddRange(Directory.EnumerateDirectories(AppContext.BaseDirectory, "Argyll_V*", SearchOption.TopDirectoryOnly).Select(p => Path.Combine(p,"bin")));
        paths.Add(Path.Combine(AppContext.BaseDirectory,"ArgyllCMS","bin"));
        paths.AddRange(new[] { @"C:\Argyll\bin", @"C:\ArgyllCMS\bin", @"C:\Program Files\Argyll\bin", @"C:\Program Files (x86)\Argyll\bin" });
        paths.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        return paths.Select(p => Path.Combine(p, "colprof.exe")).FirstOrDefault(File.Exists);
    }

    public static async Task<Candidate> BuildAsync(ProfileSession session, ScanResult scan, string[] feedback,
        PaperWhiteProtectionMode whiteProtectionMode = PaperWhiteProtectionMode.SmartGradient, double whiteProtectionStrength = .7,
        ProfileEdit? edit = null)
    {
        edit ??= new ProfileEdit();
        string exe = FindArgyll() ?? throw new InvalidOperationException("ArgyllCMS אינו מותקן. יש להתקין אותו או להגדיר PRINTER_PROFILER_ARGYLL לפני יצירת ICC.");
        int number = session.Candidates.Count + 1;
        string baseName = $"candidate_{number}";
        Directory.CreateDirectory(session.DirectoryPath);
        string ti3 = Path.Combine(session.DirectoryPath, baseName + ".ti3");
        string icc = Path.Combine(session.DirectoryPath, baseName + ".icc");
        WriteTi3Edited(scan, ti3, feedback, whiteProtectionMode, whiteProtectionStrength, edit);
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = session.DirectoryPath,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Keep the embedded profile description ASCII for older graphics applications.
        string description = $"ICU_Profile_{session.Id:N}_{number}";
        foreach (var arg in new[] { "-v", "-q", "m", "-a", "l", "-D", description, baseName }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("לא ניתן להפעיל את מנוע יצירת הפרופיל.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("יצירת הפרופיל ארכה זמן רב מדי."); }
        string log = await stdout + "\n" + await stderr;
        File.WriteAllText(Path.Combine(session.DirectoryPath, baseName + ".log"), log);
        string icm = Path.Combine(session.DirectoryPath,baseName+".icm");
        if(process.ExitCode==0 && !File.Exists(icc) && File.Exists(icm))File.Move(icm,icc,true);
        if (process.ExitCode != 0 || !File.Exists(icc)) throw new InvalidOperationException("יצירת הפרופיל נכשלה. פרטים ביומן המתקדם.");
        var candidate = new Candidate(number, icc, ti3, feedback, DateTime.Now)
        {
            WhiteProtectionMode = whiteProtectionMode,
            WhiteProtectionStrength = whiteProtectionStrength,
            Edit = edit
        };
        session.Candidates.Add(candidate); session.SelectedCandidate = number - 1; Store.Save(session);
        return candidate;
    }

    private static void WriteTi3(ScanResult scan, string path, string[] feedback)
        => WriteTi3Edited(scan, path, feedback, PaperWhiteProtectionMode.SmartGradient, .7, new ProfileEdit());

    private static void WriteTi3Protected(ScanResult scan, string path, string[] feedback,
        PaperWhiteProtectionMode whiteProtectionMode, double whiteProtectionStrength)
        => WriteTi3Edited(scan,path,feedback,whiteProtectionMode,whiteProtectionStrength,new ProfileEdit());

    private static void WriteTi3Edited(ScanResult scan, string path, string[] feedback,
        PaperWhiteProtectionMode whiteProtectionMode, double whiteProtectionStrength, ProfileEdit edit)
    {
        var lines = new List<string> { "CTI3", "", "DESCRIPTOR \"Printer Color Profiler relative scanner measurements\"", "ORIGINATOR \"Printer Color Profiler\"", "DEVICE_CLASS \"OUTPUT\"", "COLOR_REP \"RGB_LAB\"", "NUMBER_OF_FIELDS 7", "BEGIN_DATA_FORMAT", "SAMPLE_ID RGB_R RGB_G RGB_B LAB_L LAB_A LAB_B", "END_DATA_FORMAT", $"NUMBER_OF_SETS {scan.Patches.Count}", "BEGIN_DATA" };
        var adjusted=scan.Patches.Select(m=>ApplyEdit(m,
            ApplyFeedbackProtected(m,feedback,whiteProtectionMode,whiteProtectionStrength),
            edit,whiteProtectionMode,whiteProtectionStrength)).ToArray();
        bool smooth=feedback.Any(x => FineTune.Parse(x).Id is FeedbackCatalog.Gradients or FeedbackCatalog.Banding);
        for(int i=0;i<scan.Patches.Count;i++)
        {
            var m=scan.Patches[i];
            var (l, a, b) = adjusted[i];
            if(smooth)
            {
                double sum=0,sl=0,sa=0,sb=0;
                foreach(var (neighbor,j) in scan.Patches.Select((p,j)=>(p,j)))
                {
                    double dr=m.Patch.Source.R-neighbor.Patch.Source.R,dg=m.Patch.Source.G-neighbor.Patch.Source.G,db=m.Patch.Source.B-neighbor.Patch.Source.B;
                    double w=Math.Exp(-(dr*dr+dg*dg+db*db)/(2*42.0*42.0));
                    sum+=w;sl+=w*adjusted[j].l;sa+=w*adjusted[j].a;sb+=w*adjusted[j].b;
                }
                if(sum>0)
                {
                    double correctionWeight=PaperWhiteProtection.CorrectionWeight(m.Patch.Source,whiteProtectionMode,whiteProtectionStrength);
                    l+=Math.Clamp((sl/sum-l)*.12,-2,2)*correctionWeight;
                    a+=Math.Clamp((sa/sum-a)*.12,-2,2)*correctionWeight;
                    b+=Math.Clamp((sb/sum-b)*.12,-2,2)*correctionWeight;
                }
            }
            // Bradford adaptation from D65 scanner working space to D50 PCS.
            (l, a, b) = ColorScience.D65ToD50(l, a, b);
            lines.Add(FormattableString.Invariant($"{m.Patch.Index + 1} {m.Patch.Source.R / 2.55:F4} {m.Patch.Source.G / 2.55:F4} {m.Patch.Source.B / 2.55:F4} {l:F4} {a:F4} {b:F4}"));
        }
        lines.Add("END_DATA"); lines.Add("");
        File.WriteAllLines(path, lines, Encoding.ASCII);
    }

    private static (double l,double a,double b) ApplyFeedback(MeasuredPatch m, string[] feedback)
        => ApplyFeedbackProtected(m,feedback,PaperWhiteProtectionMode.SmartGradient,.7);

    private static (double l,double a,double b) ApplyEdit(MeasuredPatch measurement,
        (double l,double a,double b) value, ProfileEdit edit,
        PaperWhiteProtectionMode whiteProtectionMode, double whiteProtectionStrength)
    {
        if(edit.IsNeutral)return value;
        // The edit describes the wanted print change; the model moves the opposite
        // way so that the ICC inverse produces it.
        double weight=PaperWhiteProtection.CorrectionWeight(measurement.Patch.Source,whiteProtectionMode,whiteProtectionStrength);
        var (dl,da,db)=edit.Delta(value.l,value.a,value.b);
        return (
            Math.Clamp(value.l-dl*weight,0,100),
            Math.Clamp(value.a-da*weight,-128,127),
            Math.Clamp(value.b-db*weight,-128,127));
    }

    private static (double l,double a,double b) ApplyFeedbackProtected(MeasuredPatch m, string[] feedback,
        PaperWhiteProtectionMode whiteProtectionMode, double whiteProtectionStrength)
    {
        double l=m.L,a=m.A,b=m.LabB;
        // Small bounded changes in the measurement model become part of the
        // actual ICC built by colprof. Weighting keeps unrelated colors stable.
        // Every entry moves the model TOWARD the problem the user saw ("too dark"
        // -> model darker); the ICC inverse then prints the opposite way.
        double chroma=Math.Sqrt(a*a+b*b);
        double skin = m.Patch.Group == "skin" ? 1 : 0;
        double red = Math.Clamp((a-12)/55,0,1) * Math.Clamp((b+5)/65,0,1);
        double blue = Math.Clamp((-b-8)/60,0,1);
        double green = Math.Clamp((-a-8)/50,0,1);
        foreach (string entry in feedback)
        {
            var (item, strength) = FineTune.Parse(entry);
            double oldL=l, oldA=a, oldB=b;
            switch (item)
            {
                case FeedbackCatalog.Darker: l-=2; break; case FeedbackCatalog.Lighter: l+=2; break;
                case FeedbackCatalog.ContrastHigh: l-=l<50 ? 1.5*(50-l)/50 : -1.5*(l-50)/50; break;
                case FeedbackCatalog.ContrastLow: l+=l<50 ? 1.5*(50-l)/50 : -1.5*(l-50)/50; break;
                case FeedbackCatalog.BlackBlocked: l-=l<30 ? 2.5*(1-l/30) : 0; break;
                case FeedbackCatalog.BlackWeak: l+=l<35 ? 1.5*(1-l/35) : 0; break;
                case FeedbackCatalog.WhitesBurnt: l+=l>85 ? 1.8*(l-85)/15 : 0; break;
                case FeedbackCatalog.SkinYellow: b+=2.5*skin; break;
                case FeedbackCatalog.SkinRed: a+=2.5*skin; break;
                case FeedbackCatalog.SkinMagenta: a+=2*skin; b-=1*skin; break;
                case FeedbackCatalog.SkinGreen: a-=2*skin; break;
                case FeedbackCatalog.SkinDark: l-=2*skin; break;
                case FeedbackCatalog.SkinLight: l+=2*skin; break;
                case FeedbackCatalog.RedsStrong: a+=2*red; break;
                case FeedbackCatalog.RedsWeak: a-=2*red; break;
                case FeedbackCatalog.BluesStrong: b-=2*blue; break;
                case FeedbackCatalog.BluesWeak: b+=2*blue; break;
                case FeedbackCatalog.GreensStrong: a-=2*green; break;
                case FeedbackCatalog.GreensWeak: a+=2*green; break;
                case FeedbackCatalog.YellowsStrong: b+=2*Math.Clamp(b/75,0,1); break;
                case FeedbackCatalog.YellowsWeak: b-=2*Math.Clamp(b/75,0,1); break;
                case FeedbackCatalog.GraysCast: if(chroma<8){a/=.9;b/=.9;} break;
                case FeedbackCatalog.ShadowDetail: l-=l<35 ? 2*(35-l)/35 : 0;break;
                case FeedbackCatalog.HighlightDetail: l+=l>80 ? 2*(l-80)/20 : 0;break;
                case FeedbackCatalog.Sky:
                    // Move the modeled printer response toward the observed excess;
                    // the ICC inverse then reduces magenta only around sky colors.
                    double sky = FineTune.SkyWeight(m.Patch.Source);
                    a += 2.0 * sky;
                    b -= 1.2 * sky;
                    break;
            }
            l=oldL+(l-oldL)*strength;
            a=oldA+(a-oldA)*strength;
            b=oldB+(b-oldB)*strength;
        }
        // Bound the entire iteration, including multiple selected issues.
        l=m.L+Math.Clamp(l-m.L,-7,7);
        a=m.A+Math.Clamp(a-m.A,-7,7);
        b=m.LabB+Math.Clamp(b-m.LabB,-7,7);
        double correctionWeight=PaperWhiteProtection.CorrectionWeight(m.Patch.Source,whiteProtectionMode,whiteProtectionStrength);
        l=m.L+(l-m.L)*correctionWeight;
        a=m.A+(a-m.A)*correctionWeight;
        b=m.LabB+(b-m.LabB)*correctionWeight;
        return (Math.Clamp(l,0,100),Math.Clamp(a,-128,127),Math.Clamp(b,-128,127));
    }
}
