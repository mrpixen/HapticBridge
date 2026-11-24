using System;using System.IO;using System.Linq;using System.Threading.Tasks;using System.Windows.Controls;using System.Windows.Media;using System.Windows.Media.Imaging;using LovenseConnector;
internal static partial class ScreenSyncTests
{
    static async Task<Capture> ImageCalibration(ConnectorApp app,Capture capture,string folder,Link[] links,Playback[] players)
    {
        var pixels=ShieldPixels("100","25");string path=Path.Combine(folder,"synthetic-hud.png");
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4)));
        using(var file=File.Create(path))encoder.Save(file);
        var image=ScreenCalibrationImage.Load(path);File.Delete(path);
        Check(image.Valid&&image.Bgra.SequenceEqual(pixels.Bgra),"HUD image loads exact pixels and releases its file");
        File.WriteAllText(path,"not an image");bool rejected=false;try{ScreenCalibrationImage.Load(path);}catch(Exception ex)when(ex is NotSupportedException||ex is System.IO.FileFormatException){rejected=true;}
        Check(rejected,"invalid image rejected");File.Delete(path);
        await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");int before=links.Sum(l=>l.Positive.Count);
        await Invoke(app,"ApplyScreenImage",image);
        Check(capture.Disposed&&Get<Capture>(app,"screenCapture")==null&&!Get<bool>(app,"screenArmed"),"opening HUD image stops live capture and disarms");
        Check(Get<bool>(app,"screenFrozen")&&Get<HudPreview>(app,"screenPreview").CanSelect&&ReferenceEquals(Get<ScreenPixels>(app,"screenFrame"),image),"imported image is ready for region selection");
        Check(players.All(p=>p.Level(0)==0)&&links.Sum(l=>l.Positive.Count)==before,"image import never starts motor output");
        rejected=false;try{await Invoke(app,"ArmScreen");}catch(ArgumentException){rejected=true;}Check(rejected,"saved image cannot be armed as live capture");
        Check(!Get<Button>(app,"screenFreeze").IsEnabled,"imported still image has no misleading resume action");
        Call(app,"RequireFrozen");Check(Get<TextBlock>(app,"screenReading").Text.Contains("100"),"saved image uses normal HUD recognition and learning gate");
        await Invoke(app,"StopScreenCapture");Check(Get<ScreenPixels>(app,"screenFrame")==null&&!Get<bool>(app,"screenFrozen"),"stop clears imported image from preview");
        capture=new Capture();Set(app,"screenCapture",capture);await Feed(app,capture,"100",3,"25");
        Check(Get<Button>(app,"screenFreeze").IsEnabled,"normal capture restores freeze control");
        await Invoke(app,"ArmScreen");links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
        var pending=Invoke(app,"ApplyScreenImage",image);await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer=Invoke(app,"StopScreenCapture");links[0].Block.TrySetResult(true);await Task.WhenAll(pending,newer);links[0].Block=null;
        Check(Get<ScreenPixels>(app,"screenFrame")==null&&!Get<bool>(app,"screenFrozen"),"a newer stop prevents stale image restoration after a delayed output stop");
        capture=new Capture();Set(app,"screenCapture",capture);await Feed(app,capture,"100",3,"25");
        return capture;
    }
}
