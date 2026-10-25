using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PrinterColorProfiler.Core;

namespace PrinterColorProfiler;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==1 && e.Args[0]=="--check-argyll")
        {
            Environment.ExitCode=ProfileBuilder.FindArgyll()!=null?0:2;
            Shutdown();return;
        }
        if(e.Args.Length==1 && e.Args[0]=="--check-chart")
        {
            try
            {
                var image=Chart.LoadPackaged();
                var result=ScanAnalyzer.Analyze(Chart.PackagedPath);
                Environment.ExitCode=image.PixelWidth==1240 && result.Patches.Count==320 ? 0 : 3;
            }
            catch { Environment.ExitCode=3; }
            Shutdown();return;
        }
        if(e.Args.Length==3 && e.Args[0]=="--install")
        {
            try
            {
                var session=JsonSerializer.Deserialize<ProfileSession>(File.ReadAllText(e.Args[1])) ?? throw new InvalidDataException("Invalid session");
                WindowsServices.InstallDirect(session,session.Candidates[int.Parse(e.Args[2])]);
                Environment.ExitCode=0;
            }
            catch { Environment.ExitCode=1; }
            Shutdown();return;
        }
        if(e.Args.Length==2 && e.Args[0]=="--render-screens")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try { RenderScreens(e.Args[1]); Environment.ExitCode=0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(e.Args[1],"error.txt"),ex.ToString()); Environment.ExitCode=4; }
            Shutdown();return;
        }
        ThemeManager.Initialize();
        new MainWindow().Show();
    }

    // Development aid: renders every screen in both themes from demo data in a temporary store.
    // It never reads or writes the real profile folder or settings.
    private static void RenderScreens(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        ThemeManager.Persist=false;
        Store.RootOverride=Path.Combine(Path.GetTempPath(),"lentora-icu-render-"+Environment.ProcessId);
        var scan=ScanAnalyzer.Analyze(Chart.PackagedPath);
        var active=new ProfileSession{PrinterName="Canon PIXMA PRO-200",Paper="כרומו מט 170 גרם",Status="פעיל",Installed=true,
            CreatedAt=new DateTime(2026,10,21),ScanPath=Chart.PackagedPath,DriverSettingsBase64="demo",WhiteProtectionMode=PaperWhiteProtectionMode.SmartGradient};
        for(int i=1;i<=3;i++)
            active.Candidates.Add(new Candidate(i,Path.Combine(active.DirectoryPath,$"candidate_{i}.icc"),Path.Combine(active.DirectoryPath,$"candidate_{i}.ti3"),
                i==1?[]:[FineTune.Encode(FeedbackCatalog.Sky,2),FineTune.Encode(FeedbackCatalog.Darker,1)],new DateTime(2026,10,21,10+i,0,0),
                Verification:i==3?new ScanDiagnostics(4.2,2.1,.6,14,2.8):null)
            {
                Edit=i==3?new ProfileEdit{Brightness=1.5,Tone=new ToneEdit(Shadows:2,Highlights:-1),Gray=new GrayBalance(MidA:-1.5),Hues=new(){["blue"]=new HueEdit(Saturation:-10)}}:null
            });
        active.SelectedCandidate=2;
        var draft=new ProfileSession{PrinterName="KONICA MINOLTA bizhub C251i",Paper="נייר משרדי 80 גרם",CreatedAt=new DateTime(2026,10,24)};
        Store.Save(active);Store.Save(draft);
        try
        {
            foreach(var theme in new[]{AppTheme.Light,AppTheme.Dim})
            {
                ThemeManager.Apply(theme);
                foreach(var page in new[]{"home","choose","settings","print","scan","review","feedback","editor","ready","final"})
                {
                    var window=new MainWindow{ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-30000,Top=0,Width=1180,Height=page is "feedback" or "editor"?1700:860};
                    window.DemoBaseDiagnostics=new ScanDiagnostics(7.9,4.4,3.7,14,3.1);
                    window.Show();
                    window.ShowDemo(page,active,scan);
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                    var root=(FrameworkElement)window.Content;
                    var dpi=VisualTreeHelper.GetDpi(root);
                    var bitmap=new RenderTargetBitmap((int)(root.ActualWidth*dpi.DpiScaleX),(int)(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    // RenderTargetBitmap draws a right-to-left root mirrored; flip it back.
                    var upright=new TransformedBitmap(bitmap,new ScaleTransform(-1,1));
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(upright));
                    using(var file=File.Create(Path.Combine(outputDir,$"{theme.ToString().ToLowerInvariant()}-{page}.png")))encoder.Save(file);
                    window.Close();
                }
            }
        }
        finally { try { Directory.Delete(Store.RootOverride,true); } catch (IOException) { } }
    }
}
