using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LovenseConnector;

internal static class ScreenSyncTests
{
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new Exception("Screen sync: "+label);}
    static T Get<T>(object o,string name)=>(T)o.GetType().GetField(name,Members).GetValue(o);
    static void Set(object o,string name,object value)=>o.GetType().GetField(name,Members).SetValue(o,value);
    static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Members).Invoke(o,args);
    static Task Invoke(object o,string name,params object[] args)=>(Task)Call(o,name,args);
    sealed class Capture : IScreenCapture
    {
        public bool Foreground { get; set; }=true;
        public bool Disposed;
        public event Action<ScreenPixels> Frame {add{}remove{}}
        public event Action<string> Failed {add{}remove{}}
        public void Dispose()=>Disposed=true;
    }
    sealed class Link : ICommandLink,ILatestCommandLink
    {
        public ConcurrentQueue<int> Positive=new ConcurrentQueue<int>();
        public TaskCompletionSource<bool> Block,Entered;
        public int FailZero;
        public Task<string> Command(string command)=>Command(command,CancellationToken.None);
        public async Task<string> Command(string command,CancellationToken token)
        {
            if(Block!=null){Entered?.TrySetResult(true);await Block.Task;token.ThrowIfCancellationRequested();}
            if(command=="Vibrate:0;"&&FailZero>0){FailZero--;throw new IOException("Synthetic stop failure");}
            if(command.StartsWith("Vibrate")&&int.TryParse(command.Split(':')[1].TrimEnd(';'),out int level)&&level>0)Positive.Enqueue(level);
            return "OK";
        }
        public async Task<string> CommandLatest(Func<string> command,CancellationToken token)
        {
            if(Block!=null){Entered?.TrySetResult(true);await Block.Task;token.ThrowIfCancellationRequested();}
            var text=command();return text==null?"":await Command(text,token);
        }
    }
    static ScreenPixels Pixels(string text,string font="Bahnschrift",int size=40)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,180,80));dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily(font),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),size,Brushes.White,1),new Point(8,8));}
        var bitmap=new RenderTargetBitmap(180,80,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var bytes=new byte[180*80*4];bitmap.CopyPixels(bytes,720,0);return new ScreenPixels(bytes,180,80,ScreenCapture.Now);
    }
    static async Task Feed(ConnectorApp app,Capture capture,string health,int count=1)
    {
        for(int i=0;i<count;i++) {
            var type=typeof(ConnectorApp).GetNestedType("ScreenSample",BindingFlags.NonPublic);var sample=Activator.CreateInstance(type);Set(sample,"Source",capture);Set(sample,"Pixels",Pixels(health));Set(app,"screenPending",sample);await Invoke(app,"ScreenTick");await Task.Delay(110);
        }
    }
    internal static async Task Run()
    {
        var defaults=HudDefaultGlyphs.Create();Check(defaults.Length==60,"built-in glyphs generated for ten digits in three fonts and two weights");
        var profile=new ScreenProfile { Health=new HudRegion {Width=1,Height=1} };
        foreach(string number in new[]{"100","80","60","40","20","0"})profile.Digits.AddRange(HudVision.Learn(Pixels(number),profile.Health,number));
        foreach(string number in new[]{"100","80","60","40","20","0"})Check(HudVision.Read(Pixels(number),profile,defaults).Health==int.Parse(number),"learned health "+number);
        Check(HudVision.Read(Pixels(""),profile,defaults).Health==null,"blank HUD rejected");
        var temp=Path.Combine(Path.GetTempPath(),"HapticBridge-screen-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        var app=new ConnectorApp();Window window;using(var stream=AssetFiles.Open("MainWindow.xaml"))window=(Window)XamlReader.Load(stream);
        Set(app,"dataDir",temp);Set(app,"window",window);Set(app,"tabs",window.FindName("DeviceTabs"));Set(app,"hint",window.FindName("Hint"));Set(app,"summary",window.FindName("Summary"));Set(app,"activity",window.FindName("ActivityLog"));Set(app,"sweepButton",window.FindName("SweepButton"));
        var links=new[]{new Link(),new Link()};var players=links.Select(l=>new Playback(l,new[]{0})).ToArray();
        var devices=Get<Dictionary<ulong,Device>>(app,"devices");for(int i=0;i<2;i++){var d=new Device {Address=(ulong)i+1,Name="Synthetic actuator",Connected=true,Channels=new[]{0},Player=players[i]};d.Motors[0]=new MotorView {Channel=0,Output=new TextBlock()};devices.Add(d.Address,d);}
        Call(app,"InitializeScreenSync");Get<DispatcherTimer>(app,"screenTimer").Stop();Set(app,"screenProfile",profile);Call(app,"RefreshScreenRoutes");
        var capture=new Capture();Set(app,"screenCapture",capture);
        try {
            await Feed(app,capture,"100",3);await Feed(app,capture,"80",3);Check(links.All(l=>l.Positive.IsEmpty),"preview detects damage without actuator commands");Check(Get<TextBlock>(app,"screenHistory").Text.Contains("Damage"),"preview records confirmed damage");
            await Invoke(app,"ArmScreen");Check(Get<bool>(app,"screenArmed"),"explicit arm succeeds on a calibrated live HUD");await Feed(app,capture,"80",3);await Feed(app,capture,"60",3);await Task.Delay(350);
            Check(links.All(l=>!l.Positive.IsEmpty),"same damage event reaches both selected motors");Check(players.All(p=>p.Level(0)==0&&!p.Active(0)),"bounded damage automatically stops all motors");
            capture.Foreground=false;await Invoke(app,"ScreenTick");Check(!Get<bool>(app,"screenArmed"),"focus loss disarms");capture.Foreground=true;await Feed(app,capture,"40",3);Check(!Get<bool>(app,"screenArmed"),"returning to window never rearms output");
            await Invoke(app,"ArmScreen");await Feed(app,capture,"40",3);Set(app,"screenFrame",new ScreenPixels(Pixels("40").Bgra,180,80,ScreenCapture.Now-2));await Invoke(app,"ScreenTick");Check(!Get<bool>(app,"screenArmed"),"stale capture disarms");
            await Feed(app,capture,"40",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"40",3);await Feed(app,capture,"0",3);await Task.Delay(450);await Invoke(app,"ScreenTick");Check(!Get<bool>(app,"screenArmed")&&players.All(p=>p.Level(0)==0),"death cue ends and disarms before spectating");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);
            int[] before=links.Select(l=>l.Positive.Count).ToArray();links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Kill",Health=100});await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopped=Invoke(app,"DisarmScreen");Check(!Get<bool>(app,"screenArmed"),"disarm invalidates producer before a blocked stop completes");links[0].Block.TrySetResult(true);await stopped;links[0].Block=null;await Task.Delay(150);Check(links[0].Positive.Count==before[0],"blocked positive write canceled before reaching transport");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);links[0].FailZero=1;await Invoke(app,"DisarmScreen");Check(players[0].Level(0)<0,"failed stop remains unconfirmed");await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);Check(players[0].Level(0)==0,"failed stop retried for same output revision");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");links[0].FailZero=1;await Invoke(app,"DisarmScreen");await players[0].Start(0,new PatternSpec {mode="Steady",high=9,durationMs=1000},20);await Task.Delay(120);await Invoke(app,"RetryScreenStops",ScreenCapture.Now+2);Check(players[0].Active(0)&&players[0].Level(0)==9,"old screen stop retry cannot stop a newer manual owner");await players[0].StopAll();
            await Feed(app,capture,"100",3);links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();var arming=Invoke(app,"ArmScreen");await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));await Invoke(app,"DisarmScreen");links[0].Block.TrySetResult(true);await arming;links[0].Block=null;Check(!Get<bool>(app,"screenArmed"),"disarm cancels an arm waiting for existing output to stop");
            await Invoke(app,"StopScreenCapture");Check(capture.Disposed&&Get<IScreenCapture>(app,"screenCapture")==null&&Get<ScreenPixels>(app,"screenFrame")==null,"stop disposes capture and releases frame");
            await Feed(app,capture,"80",1);Check(Get<ScreenPixels>(app,"screenFrame")==null,"late callback from stopped capture ignored");
            Call(app,"SaveScreenProfile");string saved=File.ReadAllText(Path.Combine(temp,"screen-profile.json"));Check(!saved.Contains("Synthetic actuator")&&!saved.Contains("window",StringComparison.OrdinalIgnoreCase),"saved calibration contains no device or window identity");
        }finally {foreach(var link in links)link.Block?.TrySetResult(true);await Invoke(app,"StopScreenCapture");foreach(var p in players)await p.StopAll();window.Close();Directory.Delete(temp,true);}
        Console.WriteLine("Screen sync: "+checks+" assertions passed (synthetic pixels and transport; no screen or hardware access).");
    }
}
