using PrinterColorProfiler.Core;
using System.IO;
using OpenCvSharp;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smoke;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length==4 && args[0]=="--paper-white-suite")
        {
            PaperWhiteSuite.Run(args[1],args[2],args[3]);
            return;
        }
        if(args.Length==2 && args[0]=="--verify-chart")
        {
            var measurements=ScanAnalyzer.Analyze(args[1]);
            if(measurements.Patches.Count!=320)throw new Exception("Packaged chart is incomplete");
            using var image=Cv2.ImDecode(File.ReadAllBytes(args[1]),ImreadModes.Color);
            if(image.Width!=1240 || image.Height!=1754)throw new Exception("Packaged chart has wrong dimensions");
            Console.WriteLine("Packaged chart OK: 320 patches");
            return;
        }
        PaperWhiteSuite.VerifyProtectionRules();
        var target=Path.Combine(Path.GetTempPath(),"pcp-chart-smoke.png");
        Chart.SavePng(Chart.Render(),target);
        if(args.Length==2 && args[0]=="--export-chart")
        {
            using var full=Cv2.ImRead(target);
            using var half=new Mat();
            Cv2.Resize(full,half,new OpenCvSharp.Size(1240,1754),interpolation:InterpolationFlags.Nearest);
            Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
            Cv2.ImEncode(".png",half,out var bytes);
            File.WriteAllBytes(args[1],bytes);
            Console.WriteLine($"Exported calibration chart: {args[1]}");
            return;
        }
        var result=ScanAnalyzer.Analyze(target);
        if(result.Patches.Count!=320 || result.ControlSpread>5)throw new Exception($"Chart roundtrip failed: {result.Patches.Count}, {result.ControlSpread}");
        var diagnostics=ScanDiagnostics.From(result);
        if(diagnostics.MeanRgbError>.1 || diagnostics.SkySamples<10)throw new Exception("Relative diagnostics failed");
        var apply=typeof(ProfileBuilder).GetMethod("ApplyFeedback",BindingFlags.NonPublic|BindingFlags.Static)!;
        var sky=result.Patches.OrderByDescending(p=>FineTune.SkyWeight(p.Patch.Source)).First();
        var neutral=result.Patches.First(p=>p.Patch.Group=="gray");
        var label=FineTune.Encode(FineTune.SkyMagentaLabel,2);
        var corrected=((double l,double a,double b))apply.Invoke(null,[sky,new[]{label}])!;
        var unchanged=((double l,double a,double b))apply.Invoke(null,[neutral,new[]{label}])!;
        if(corrected.a<=sky.A+1 || Math.Abs(unchanged.a-neutral.A)>.01)throw new Exception("Localized sky correction failed");
        static double Error(ScanResult r)=>r.Patches.Average(p=>(Math.Abs(p.R-p.Patch.Source.R/255.0)+Math.Abs(p.G-p.Patch.Source.G/255.0)+Math.Abs(p.B-p.Patch.Source.B/255.0))/3);
        if(Error(result)>.04)throw new Exception($"Patch alignment failed: {Error(result):F3}");
        Console.WriteLine($"Chart roundtrip OK: {result.Patches.Count} patches, error {Error(result):F3}");
        using var source=Cv2.ImRead(target);
        using var warped=new Mat();
        using var map=Cv2.GetPerspectiveTransform(
            new[]{new Point2f(0,0),new Point2f(Chart.Width,0),new Point2f(0,Chart.Height),new Point2f(Chart.Width,Chart.Height)},
            new[]{new Point2f(65,40),new Point2f(Chart.Width-35,75),new Point2f(40,Chart.Height-55),new Point2f(Chart.Width-10,Chart.Height-30)});
        Cv2.WarpPerspective(source,warped,map,new OpenCvSharp.Size(Chart.Width,Chart.Height),borderMode:BorderTypes.Constant,borderValue:Scalar.White);
        var warpedPath=Path.Combine(Path.GetTempPath(),"pcp-chart-warped-smoke.png");
        Cv2.ImWrite(warpedPath,warped);
        var second=ScanAnalyzer.Analyze(warpedPath);
        if(second.Patches.Count!=320 || second.ControlSpread>8 || Error(second)>.06)throw new Exception($"Warped chart failed: {second.Patches.Count}, {second.ControlSpread}, {Error(second):F3}");
        Console.WriteLine($"Perspective roundtrip OK: {second.Patches.Count} patches, error {Error(second):F3}");
        string? argyll=ProfileBuilder.FindArgyll();
        if(argyll!=null)
        {
            string work=Path.Combine(AppContext.BaseDirectory,"argyll-smoke");Directory.CreateDirectory(work);
            string ti3=Path.Combine(work,"smoke.ti3");
            foreach(var extension in new[]{".icc",".icm"}){string old=Path.Combine(work,"smoke"+extension);if(File.Exists(old))File.Delete(old);}
            typeof(ProfileBuilder).GetMethod("WriteTi3",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[result,ti3,Array.Empty<string>()]);
            string tunedTi3=Path.Combine(work,"smoke-tuned.ti3");
            typeof(ProfileBuilder).GetMethod("WriteTi3",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[result,tunedTi3,new[]{label}]);
            if(File.ReadAllText(ti3)==File.ReadAllText(tunedTi3))throw new Exception("Fine tune was not written to ICC source measurements");
            var psi=new ProcessStartInfo(argyll){WorkingDirectory=work,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-v","-q","m","-a","l","-D","Smoke Profile","smoke"})psi.ArgumentList.Add(arg);
            using var process=Process.Start(psi)!;
            string output=process.StandardOutput.ReadToEnd(),errors=process.StandardError.ReadToEnd();process.WaitForExit();
            string icm=Path.Combine(work,"smoke.icm"),icc=Path.Combine(work,"smoke.icc");
            if(process.ExitCode==0 && !File.Exists(icc) && File.Exists(icm))File.Move(icm,icc,true);
            if(process.ExitCode!=0 || !File.Exists(icc))throw new Exception($"Argyll failed: {errors}\n{output}");
            Console.WriteLine($"Argyll ICC roundtrip OK: {new FileInfo(icc).Length} bytes");
            var bgr=new FormatConvertedBitmap(Chart.Render(),PixelFormats.Bgr24,null,0);
            var converted=new ColorConvertedBitmap(bgr,new ColorContext(PixelFormats.Bgr24),new ColorContext(new Uri(icc)),PixelFormats.Bgr24);
            var pixel=new byte[3];converted.CopyPixels(new Int32Rect(500,500,1,1),pixel,3,0);
            Console.WriteLine($"ICC image conversion OK: {pixel[0]},{pixel[1]},{pixel[2]}");
            DirectionSuite.Run(result);
            VerifyEditPersistence(result);
        }
    }

    // The editor stores parameters per candidate: they must survive save/load, old sessions must map
    // their manual tuning, and a real build through the app path must record the edit. Uses a temp store.
    private static void VerifyEditPersistence(ScanResult scan)
    {
        Store.RootOverride=Path.Combine(Path.GetTempPath(),"icu-edit-smoke-"+Environment.ProcessId);
        try
        {
            var edit=new ProfileEdit{Brightness=1.5,Contrast=-5,Tone=new ToneEdit(-2,3,0,-1),Gray=new GrayBalance(MidA:-1.5,HighB:2),
                Hues=new(){["blue"]=new HueEdit(5,-20,1)}};
            var session=new ProfileSession{PrinterName="Smoke",Paper="Smoke",ScanPath="scan.png"};
            var built=ProfileBuilder.BuildAsync(session,scan,[FineTune.Encode(FeedbackCatalog.Sky,1)],PaperWhiteProtectionMode.SmartGradient,.7,edit).GetAwaiter().GetResult();
            if(!File.Exists(built.IccPath))throw new Exception("Editor build did not create an ICC");
            var loaded=Store.Load().Single();
            var stored=loaded.Candidates.Single().EffectiveEdit;
            string Key(ProfileEdit e)=>System.Text.Json.JsonSerializer.Serialize(e);
            if(Key(stored)!=Key(edit))throw new Exception($"Edit did not survive save/load:\n{Key(edit)}\n{Key(stored)}");
            var legacy=System.Text.Json.JsonSerializer.Deserialize<Candidate>("""{"Number":1,"IccPath":"a.icc","Ti3Path":"a.ti3","Feedback":[],"CreatedAt":"2026-10-01T00:00:00","ManualTuning":{"Brightness":2,"Contrast":0,"Saturation":5,"Warmth":0,"Tint":-1}}""")!;
            var mapped=legacy.EffectiveEdit;
            if(legacy.Edit!=null || mapped.Brightness!=2 || mapped.Saturation!=5 || mapped.Tint!=-1)throw new Exception("Legacy manual tuning was not mapped to an edit");
            Console.WriteLine("Profile edit persistence OK: real build, save/load, legacy manual tuning");
        }
        finally { Store.RootOverride=null; try{Directory.Delete(Path.Combine(Path.GetTempPath(),"icu-edit-smoke-"+Environment.ProcessId),true);}catch(IOException){} }
    }
}
