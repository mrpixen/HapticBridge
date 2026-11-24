using System;
using System.Collections;
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

internal static partial class ScreenSyncTests
{
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new Exception("Screen sync: "+label);}
    static T Get<T>(object o,string name)=>(T)o.GetType().GetField(name,Members).GetValue(o);
    static void Set(object o,string name,object value)=>o.GetType().GetField(name,Members).SetValue(o,value);
    static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Members).Invoke(o,args);
    static Task Invoke(object o,string name,params object[] args)=>(Task)Call(o,name,args);
    static double[] CueTimes(ConnectorApp app)=>((IEnumerable)Get<object>(app,"screenOwned")).Cast<object>().Select(t=>Get<double>(t,"LastCue")).ToArray();
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var item in Descendants(child))yield return item;
    }
    sealed class Capture : IScreenCapture
    {
        bool foreground=true;
        public int ForegroundReads;
        public bool Foreground { get { ForegroundReads++;return foreground; } set { foreground=value; } }
        public bool Disposed;
        public event Action<ScreenPixels> Frame {add{}remove{}}
        public event Action<string> Failed {add{}remove{}}
        public void Dispose()=>Disposed=true;
    }
    sealed class AudioSource : IAudioSource
    {
        public string Error=>null;
        public string OutputName=>"Synthetic source";
        public void Start(){}
        public double Read(bool bassOnly)=>0;
        public void Dispose(){}
    }
    sealed class Link : ICommandLink,ILatestCommandLink
    {
        public ConcurrentQueue<int> Positive=new ConcurrentQueue<int>();
        public ConcurrentQueue<(int Channel,int Level)> Samples=new ConcurrentQueue<(int Channel,int Level)>();
        public TaskCompletionSource<bool> Block,Entered;
        public int FailZero,Commands,LatestSamples;
        public Task<string> Command(string command)=>Command(command,CancellationToken.None);
        public async Task<string> Command(string command,CancellationToken token)
        {
            Interlocked.Increment(ref Commands);
            if(Block!=null){Entered?.TrySetResult(true);await Block.Task;token.ThrowIfCancellationRequested();}
            if(command=="Vibrate:0;"&&FailZero>0){FailZero--;throw new IOException("Synthetic stop failure");}
            if(command.StartsWith("Vibrate")&&int.TryParse(command.Split(':')[1].TrimEnd(';'),out int level)&&level>0) {
                Positive.Enqueue(level);var lane=command.Substring(7,command.IndexOf(':')-7);Samples.Enqueue((lane.Length==0?0:int.Parse(lane),level));
            }
            return "OK";
        }
        public async Task<string> CommandLatest(Func<string> command,CancellationToken token)
        {
            Interlocked.Increment(ref LatestSamples);
            if(Block!=null){Entered?.TrySetResult(true);await Block.Task;token.ThrowIfCancellationRequested();}
            var text=command();return text==null?"":await Command(text,token);
        }
    }
    static ScreenPixels Pixels(string text,string font="Bahnschrift",int size=40)
    {
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,180,80));dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily(font),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal),size,Brushes.White,1),new Point(8,8));}
        var bitmap=new RenderTargetBitmap(180,80,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var bytes=new byte[180*80*4];bitmap.CopyPixels(bytes,720,0);return new ScreenPixels(bytes,180,80,ScreenCapture.Now);
    }
    static ScreenPixels ShieldPixels(string health,string shield)
    {
        var hp=Pixels(health);var armor=Pixels(shield);var bytes=new byte[360*80*4];
        for(int row=0;row<80;row++){Array.Copy(hp.Bgra,row*720,bytes,row*1440,720);Array.Copy(armor.Bgra,row*720,bytes,row*1440+720,720);}
        return new ScreenPixels(bytes,360,80,ScreenCapture.Now);
    }
    static async Task Feed(ConnectorApp app,Capture capture,string health,int count=1,string shield=null)
    {
        for(int i=0;i<count;i++) {
            var type=typeof(ConnectorApp).GetNestedType("ScreenSample",BindingFlags.NonPublic);var sample=Activator.CreateInstance(type);Set(sample,"Source",capture);Set(sample,"Pixels",shield==null?Pixels(health):ShieldPixels(health,shield));Set(app,"screenPending",sample);await Invoke(app,"ScreenTick");await Task.Delay(110);
        }
    }
    internal static async Task Run()
    {
        DiagnosticsRemainAnonymousAndReadOnly();
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
            await OutputChangesAndPendingArm(app,capture,links,players);
            await RapidDamageThenDeath(app,capture,links,players);
            await SimultaneousCombatEvents(app,capture,links,players);
            await AdvancedSliderCannotRetargetScreen(app,capture,devices[1],links[0]);
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);
            int[] before=links.Select(l=>l.Positive.Count).ToArray();links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Kill",Health=100});await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopped=Invoke(app,"DisarmScreen");Check(!Get<bool>(app,"screenArmed"),"disarm invalidates producer before a blocked stop completes");links[0].Block.TrySetResult(true);await stopped;links[0].Block=null;await Task.Delay(150);Check(links[0].Positive.Count==before[0],"blocked positive write canceled before reaching transport");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);links[0].FailZero=1;await Invoke(app,"DisarmScreen");Check(players[0].Level(0)<0,"failed stop remains unconfirmed");await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);Check(players[0].Level(0)==0,"failed stop retried for same output revision");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");links[0].FailZero=1;await Invoke(app,"DisarmScreen");await players[0].Start(0,new PatternSpec {mode="Steady",high=9,durationMs=1000},20);await Task.Delay(120);await Invoke(app,"RetryScreenStops",ScreenCapture.Now+2);Check(players[0].Active(0)&&players[0].Level(0)==9,"old screen stop retry cannot stop a newer manual owner");await players[0].StopAll();
            await Feed(app,capture,"100",3);links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();var arming=Invoke(app,"ArmScreen");await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));await Invoke(app,"DisarmScreen");links[0].Block.TrySetResult(true);await arming;links[0].Block=null;Check(!Get<bool>(app,"screenArmed"),"disarm cancels an arm waiting for existing output to stop");
            // Cancellation must also cover Manual's earlier source handoff, before ArmScreen's
            // own StopPlayers await. No real audio capture or output is used here.
            await Feed(app,capture,"100",3);
            Set(app,"audioLabel",new TextBlock());Set(app,"audioCapture",new AudioSource());Set(app,"audioTargets",new[]{new MixerMotor {Device=devices[1],Channel=0}});
            links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            arming=Invoke(app,"ArmScreen");await Task.WhenAny(arming,links[0].Entered.Task).WaitAsync(TimeSpan.FromSeconds(2));if(arming.IsCompleted)await arming;await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Invoke(app,"DisarmScreen");links[0].Block.TrySetResult(true);await arming;links[0].Block=null;
            Check(!Get<bool>(app,"screenArmed"),"disarm during a slow source handoff cancels the pending screen arm");
            await Invoke(app,"StopScreenCapture");Check(capture.Disposed&&Get<IScreenCapture>(app,"screenCapture")==null&&Get<ScreenPixels>(app,"screenFrame")==null,"stop disposes capture and releases frame");
            await Feed(app,capture,"80",1);Check(Get<ScreenPixels>(app,"screenFrame")==null,"late callback from stopped capture ignored");
            Call(app,"SaveScreenProfile");string saved=File.ReadAllText(Path.Combine(temp,"screen-profile.json"));Check(!saved.Contains("Synthetic actuator")&&!saved.Contains("window",StringComparison.OrdinalIgnoreCase),"saved calibration contains no device or window identity");
            var coordinatePanel=Descendants(Get<TabControl>(app,"tabs")).OfType<Expander>().Single(e=>(string)e.Header=="Region coordinates");
            coordinatePanel.IsExpanded=true;
            var fields=Descendants(coordinatePanel).OfType<TextBox>().ToArray();
            Get<Action<HudRegion>>(Get<HudPreview>(app,"screenPreview"),"Selected")(new HudRegion {X=.2,Y=.3,Width=.1,Height=.1});
            Check(fields.Select(f=>double.Parse(f.Text,CultureInfo.InvariantCulture)).SequenceEqual(new[]{20d,30d,10d,10d}),"dragged HUD selection refreshes already-open coordinate fields");
            await Invoke(app,"UndoScreenCalibration");
            Check(profile.Health.Width==1&&fields[2].Text=="100"&&!Get<Button>(app,"screenUndoCalibration").IsEnabled,"undo restores the region and coordinates and disables when history is empty");
            Get<ComboBox>(app,"screenRegion").SelectedIndex=1;
            profile.BadgeMask=Enumerable.Repeat((byte)1,1024).ToArray();var previousBadge=profile.Badge;
            Call(app,"SetScreenRegion",new HudRegion {X=.2,Y=.3,Width=.1,Height=.1});
            Check(profile.BadgeMask==null,"changing badge region clears its incompatible learned mask");
            profile.KillLevel=7;
            await Invoke(app,"UndoScreenCalibration");
            Check(profile.BadgeMask.SequenceEqual(Enumerable.Repeat((byte)1,1024))&&profile.Badge.X==previousBadge.X&&profile.KillLevel==7,"undo recovers learned badge without reverting feedback settings");
            int digitCount=profile.Digits.Count;
            await Invoke(app,"ResetScreenCalibration");Check(profile.Digits.Count==0&&profile.BadgeMask==null,"reset clears learned calibration");
            await Invoke(app,"UndoScreenCalibration");Check(profile.Digits.Count==digitCount&&profile.BadgeMask!=null,"reset can be undone with independent copies of learned digits");
            bool invalid=false;try{Call(app,"SetScreenRegion",new HudRegion {X=2,Width=.1,Height=.1});}catch(TargetInvocationException ex)when(ex.InnerException is ArgumentException){invalid=true;}
            Check(invalid&&!Get<Button>(app,"screenUndoCalibration").IsEnabled,"invalid region leaves calibration and undo history unchanged");
            profile.Health=new HudRegion {Width=.5,Height=1};profile.BadgeMask=null;
            Get<ComboBox>(app,"screenRegion").SelectedIndex=2;
            Call(app,"SetScreenRegion",new HudRegion {X=.5,Width=.5,Height=1});
            Check(fields[0].Text=="50"&&fields[2].Text=="50","shield selection uses its own numeric region coordinates");
            await Invoke(app,"SetScreenShieldEnabled",true);
            Check(profile.ReadShield&&Get<CheckBox>(app,"screenReadShield").IsChecked==true,"shield reading explicitly enabled and reflected in its control");
            Set(app,"screenFrozen",true);Set(app,"screenFrame",ShieldPixels("100","25"));Get<TextBox>(app,"screenHealthText").Text="25";
            digitCount=profile.Digits.Count;await Invoke(app,"LearnScreenDigits");
            Check(profile.Digits.Count==digitCount+2,"learning uses the selected shield digits");
            await Invoke(app,"UndoScreenCalibration");Check(profile.Digits.Count==digitCount,"shield glyph learning can be undone");
            await Invoke(app,"LearnScreenDigits");
            var reading=HudVision.Read(ShieldPixels("100","25"),profile,defaults);
            Check(reading.Health==100&&reading.Shield==25&&reading.ShieldConfidence>=.82,"same synthetic frame recognizes health and shield independently");
            Check(HudVision.Read(ShieldPixels("100",""),profile,defaults).Shield==null,"blank shield is unknown rather than zero");
            Check(HudVision.Read(ShieldPixels("100","80"),profile,defaults).Shield==null,"shield values above 50 are rejected");
            Set(app,"screenFrozen",false);capture=new Capture();Set(app,"screenCapture",capture);Get<HudEventFilter>(app,"screenFilter").Reset();
            before=links.Select(l=>l.Positive.Count).ToArray();await Feed(app,capture,"100",3,"25");await Feed(app,capture,"100",3,"10");
            Check(links.Select(l=>l.Positive.Count).SequenceEqual(before)&&Get<TextBlock>(app,"screenHistory").Text.Contains("−15 shield"),"shield-only damage appears in preview without output");
            await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3,"10");await Feed(app,capture,"100",3,"5");await Task.Delay(350);
            Check(links.Select((l,i)=>l.Positive.Count>before[i]).All(x=>x)&&players.All(p=>p.Level(0)==0&&!p.Active(0)),"shield-only damage reaches both selected motors and stops within its bounded pulse");
            await Invoke(app,"SetScreenShieldEnabled",false);
            Check(!Get<bool>(app,"screenArmed")&&!profile.ReadShield&&Get<CheckBox>(app,"screenReadShield").IsChecked==false,"changing shield mode disarms output and updates its control");
            Check(HudVision.Read(ShieldPixels("100","25"),profile,defaults).Shield==null,"disabled shield reading preserves health-only behavior");
            await Invoke(app,"UndoScreenCalibration");Check(profile.ReadShield&&!Get<bool>(app,"screenArmed"),"undo restores shield calibration without rearming");
            Call(app,"SaveScreenProfile");var restored=System.Text.Json.JsonSerializer.Deserialize<ScreenProfile>(File.ReadAllText(Path.Combine(temp,"screen-profile.json")));
            Check(restored.Validate()&&restored.ReadShield&&restored.Shield.X==.5&&restored.Shield.Width==.5,"saved profile preserves shield region and enabled state");
            await SpectatorSuppression(app,capture,links,players);
            SelectedRegionsRemainVisible(app);
            await DeathTextFeedback(app,capture,links,players);
            await BadgeLearning(app);
            await CalibrationFiles(app,capture,temp,links,players);
            capture=await CaptureStopFailures(app,capture,links,players);
            capture=await StoppedCaptureDiscardsPendingTick(app,capture,links);
            capture=await ScreenShortcutGameplay(app,capture,links,players);
            await EventRoutingAndPersistence(app,capture,temp);
        }finally {foreach(var link in links)link.Block?.TrySetResult(true);await Invoke(app,"StopScreenCapture");foreach(var p in players)await p.StopAll();window.Close();Directory.Delete(temp,true);}
        Console.WriteLine("Screen sync: "+checks+" assertions passed (synthetic pixels and transport; no screen or hardware access).");
    }

    static async Task<Capture> CaptureStopFailures(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        foreach(string reason in new[]{"stop","delayed stop","capture failure","focus loss"}) {
            Set(app,"screenCapture",capture);capture.Foreground=true;
            await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3,"25");
            Check(Get<bool>(app,"screenArmed"),reason+" fixture starts armed with a current synthetic HUD");
            links[0].FailZero=1;
            if(reason=="delayed stop") {
                links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
                var pending=Invoke(app,"StopScreenCapture");
                try {
                    await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Check(Get<TextBlock>(app,"screenStatus").Text=="Stopping output…","capture stopping does not claim output is off before its stop returns");
                }finally {links[0].Block.TrySetResult(true);await pending;links[0].Block=null;}
            }
            else if(reason=="stop")await Invoke(app,"StopScreenCapture");
            else {
                if(reason=="capture failure")Set(app,"screenFailure","Capture interrupted · restart preview");
                else capture.Foreground=false;
                await Invoke(app,"ScreenTick");
            }
            Check(!Get<bool>(app,"screenArmed")&&players[0].Level(0)<0&&Get<TextBlock>(app,"screenStatus").Text.Contains("unconfirmed"),reason+" retains the failed device-stop warning");
            await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);
            Check(players[0].Level(0)==0&&!Get<TextBlock>(app,"screenStatus").Text.Contains("unconfirmed"),reason+" clears the warning only after a confirmed retry");
            await Invoke(app,"StopScreenCapture");capture=new Capture();
        }
        Set(app,"screenCapture",capture);return capture;
    }

    static async Task<Capture> StoppedCaptureDiscardsPendingTick(ConnectorApp app,Capture capture,Link[] links)
    {
        Set(app,"screenCapture",capture);capture.Foreground=true;
        await Feed(app,capture,"100",3,"25");await Invoke(app,"ArmScreen");await Feed(app,capture,"100",2,"25");
        links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
        capture.Foreground=false;
        var tick=Feed(app,capture,"75",1,"25");
        try {
            await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Invoke(app,"StopScreenCapture");
        }finally {links[0].Block.TrySetResult(true);await tick;links[0].Block=null;}
        Check(Get<ScreenPixels>(app,"screenFrame")==null,"a pending recognition tick cannot restore a frame after Stop capture");
        Check(Get<TextBlock>(app,"screenReading").Text=="Health —    Badge —","stopped capture keeps its cleared HUD reading");
        Check(Get<TextBlock>(app,"screenStatus").Text=="Capture off · output off","a late focus-loss stop cannot overwrite the newer capture status");
        capture=new Capture();Set(app,"screenCapture",capture);return capture;
    }

    static async Task EventRoutingAndPersistence(ConnectorApp app,Capture capture,string folder)
    {
        var devices=Get<Dictionary<ulong,Device>>(app,"devices");var routes=(IDictionary)Get<object>(app,"screenRoutes");
        foreach(var old in routes.Values.Cast<object>())Get<CheckBox>(old,"Include").IsChecked=false;
        var link=new Link();var player=new Playback(link,new[]{1,2});
        var device=new Device {Address=3,Name="Synthetic dual",Connected=true,Channels=new[]{1,2},Player=player};
        foreach(int channel in device.Channels)device.Motors[channel]=new MotorView {Channel=channel,Output=new TextBlock()};devices.Add(device.Address,device);
        Call(app,"RefreshScreenRoutes");var dual=routes.Values.Cast<object>().Where(r=>ReferenceEquals(Get<Device>(r,"Device"),device)).OrderBy(r=>Get<int>(r,"Channel")).ToArray();
        Dictionary<string,CheckBox> Choices(object route)=>Get<Dictionary<string,CheckBox>>(route,"Events");
        var first=Choices(dual[0]);var second=Choices(dual[1]);
        Check(first.Values.Concat(second.Values).All(c=>c.IsChecked==true),"new motors receive all three event types by default");
        first["Kill"].IsChecked=false;second["Damage"].IsChecked=false;second["Death"].IsChecked=false;
        Get<Slider>(dual[0],"Ceiling").Value=8;Get<Slider>(dual[1],"Ceiling").Value=17;
        var profile=Get<ScreenProfile>(app,"screenProfile");profile.Health=new HudRegion {Width=1,Height=1};profile.ReadShield=false;
        var kill=Descendants(Get<TabControl>(app,"tabs")).OfType<Slider>().Single(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="Kill event strength");kill.Value=18;
        Set(app,"screenCapture",capture);capture.Foreground=true;
        async Task Arm(){Set(app,"screenFrame",Pixels("100"));await Invoke(app,"ArmScreen");Check(Get<bool>(app,"screenArmed"),"routed test arms only after explicit request");}
        try {
            first["Damage"].IsChecked=false;
            await Arm();double[] previousCue=CueTimes(app);int previousSamples=link.Positive.Count;
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Damage",Health=80,Amount=20});
            Check(CueTimes(app).SequenceEqual(previousCue)&&link.Positive.Count==previousSamples,"an event without any assigned motor does not consume the feedback cooldown");
            first["Damage"].IsChecked=true;await Invoke(app,"DisarmScreen");
            foreach(var ev in new[]{new HudEvent {Kind="Damage",Health=20,Amount=10},new HudEvent {Kind="Kill",Health=100},new HudEvent {Kind="Death",Health=0}}) {
                await Arm();int marker=link.Samples.Count;
                await Invoke(app,"SendScreenCue",ev);await Task.Delay(550);
                var samples=link.Samples.Skip(marker).ToArray();int expected=ev.Kind=="Kill"?2:1;
                Check(samples.Any()&&samples.All(s=>s.Channel==expected&&s.Level<=(expected==1?8:17)),ev.Kind+" reaches only its assigned motor within that motor's ceiling");
                Check(player.Level(1)==0&&player.Level(2)==0&&!player.Active(1)&&!player.Active(2),ev.Kind+" leaves both motors stopped at the end");
                await Invoke(app,"DisarmScreen");
            }
            await Arm();link.Block=new TaskCompletionSource<bool>();link.Entered=new TaskCompletionSource<bool>();int before=link.Positive.Count;
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Kill",Health=100});await link.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            second["Kill"].IsChecked=false;
            Check(!Get<bool>(app,"screenArmed"),"changing an event assignment immediately cancels armed output while a write is blocked");
            link.Block.TrySetResult(true);await Task.Delay(200);link.Block=null;
            Check(link.Positive.Count==before,"an excluded event cannot escape an already-queued device write");
            second["Kill"].IsChecked=true;
            first["Damage"].IsChecked=false;first["Death"].IsChecked=false;second["Kill"].IsChecked=false;
            bool rejected=false;Set(app,"screenFrame",Pixels("100"));try{await Invoke(app,"ArmScreen");}catch(ArgumentException){rejected=true;}
            Check(rejected&&!Get<bool>(app,"screenArmed"),"all event assignments off cannot arm an empty route");
            first["Damage"].IsChecked=true;first["Death"].IsChecked=true;second["Kill"].IsChecked=true;
            string path=Path.Combine(folder,"screen-feedback.json"),saved=File.ReadAllText(path);
            var preferences=System.Text.Json.JsonSerializer.Deserialize<ScreenFeedbackPreferences>(saved);
            Check(preferences.Valid()&&preferences.KillLevel==18&&preferences.Motors.Count==4,"event strengths and all motor choices save without a separate calibration save");
            Check(!saved.Contains("Synthetic")&&!saved.Contains("000000000003/")&&!saved.Contains("screenFrame")&&!saved.Contains("screenArmed"),"feedback file omits names, raw device addresses, captured pixels and output-enabled state");
            // Rebuild in reversed discovery order to prove preferences follow motor identity, not row order.
            var original=devices.Values.Reverse().ToArray();devices.Clear();foreach(var d in original)devices.Add(d.Address,d);
            routes.Clear();Get<StackPanel>(app,"screenMotorPanel").Children.Clear();Call(app,"LoadScreenFeedback");Call(app,"RefreshScreenRoutes");
            var reloaded=routes.Values.Cast<object>().Where(r=>ReferenceEquals(Get<Device>(r,"Device"),device)).OrderBy(r=>Get<int>(r,"Channel")).ToArray();
            Check(Get<Slider>(reloaded[0],"Ceiling").Value==8&&Get<Slider>(reloaded[1],"Ceiling").Value==17&&Choices(reloaded[0])["Kill"].IsChecked==false&&Choices(reloaded[1])["Damage"].IsChecked==false&&Choices(reloaded[1])["Kill"].IsChecked==true,"reloaded assignments and ceilings stay on the same motor after discovery order changes");
            Check(routes.Values.Cast<object>().Where(r=>!ReferenceEquals(Get<Device>(r,"Device"),device)).All(r=>Get<CheckBox>(r,"Include").IsChecked==false)&&!Get<bool>(app,"screenArmed"),"reloading retains excluded devices and never enables output");
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                Get<Slider>(reloaded[0],"Ceiling").Value=9;
                Check(Get<TextBlock>(app,"screenFeedbackStatus").Visibility==Visibility.Visible&&File.ReadAllText(path)==saved&&!File.Exists(path+".tmp"),"a locked save preserves previous settings, removes temporary data and reports session-only changes");
            }
            Check((bool)Call(app,"SaveScreenFeedback")&&Get<TextBlock>(app,"screenFeedbackStatus").Visibility==Visibility.Collapsed,"saving recovers after the destination becomes writable");
            File.WriteAllText(path,"{\"Version\":99}");Call(app,"LoadScreenFeedback");
            Check(Get<bool>(app,"screenFeedbackLoadFailed")&&Get<ScreenFeedbackPreferences>(app,"screenFeedback").Motors.Count==0&&!Get<bool>(app,"screenArmed"),"invalid saved feedback is rejected without enabling capture or output");
            File.Delete(path);profile.DamageLevel=6;profile.KillLevel=11;profile.DeathLevel=9;Call(app,"LoadScreenFeedback");
            var migrated=Get<ScreenFeedbackPreferences>(app,"screenFeedback");
            Check(migrated.DamageLevel==6&&migrated.KillLevel==11&&migrated.DeathLevel==9,"older calibration strengths remain the defaults when no feedback file exists");
        }finally {link.Block?.TrySetResult(true);await Invoke(app,"DisarmScreen");await player.StopAll();}
    }

    static async Task RapidDamageThenDeath(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");int damage=profile.DamageLevel,death=profile.DeathLevel;
        profile.DamageLevel=4;profile.DeathLevel=19;
        try {
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);
            int[] before=links.Select(l=>l.Positive.Count).ToArray();await Feed(app,capture,"80",3);
            // Playback samples asynchronously every 100 ms. Feed's 110 ms delay is
            // not an acknowledgement, particularly on a busy CI host.
            var received=System.Diagnostics.Stopwatch.StartNew();
            while(links.Select((l,i)=>l.Positive.Count<=before[i]).Any(x=>x)&&received.ElapsedMilliseconds<500)await Task.Delay(10);
            int[] markers=links.Select(l=>l.Positive.Count).ToArray();
            Check(markers.Select((n,i)=>n>before[i]).All(x=>x),"rapid sequence has already delivered damage feedback");
            await Feed(app,capture,"0",2);
            // Keep the cooldown active at the confirming frame even on a slow CI host.
            foreach(var target in ((IEnumerable)Get<object>(app,"screenOwned")).Cast<object>())Set(target,"LastCue",ScreenCapture.Now);
            await Feed(app,capture,"0",1);await Task.Delay(450);await Invoke(app,"ScreenTick");
            Check(links.Select((l,i)=>l.Positive.Skip(markers[i]).Contains(19)).All(x=>x),"confirmed death after rapid damage still delivers its separate configured pulse");
            Check(!Get<bool>(app,"screenArmed")&&players.All(p=>p.Level(0)==0),"priority death still ends output and disarms before spectating");
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");
            profile.DamageLevel=0;double[] prior=CueTimes(app);int[] counts=links.Select(l=>l.Positive.Count).ToArray();
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Damage",Health=80,Amount=20});
            Check(CueTimes(app).SequenceEqual(prior)&&links.Select(l=>l.Positive.Count).SequenceEqual(counts),"a muted event sends no output and does not delay the next event");
        }finally {profile.DamageLevel=damage;profile.DeathLevel=death;await Invoke(app,"DisarmScreen");}
    }

    static async Task SimultaneousCombatEvents(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");var health=profile.Health;var badge=profile.Badge;var mask=profile.BadgeMask;
        var routes=((IDictionary)Get<object>(app,"screenRoutes")).Values.Cast<object>().ToArray();
        var choices=routes.Select(r=>Get<Dictionary<string,CheckBox>>(r,"Events")).ToArray();
        profile.Health=new HudRegion {Width=.5,Height=1};profile.Badge=new HudRegion {X=.5,Width=.5,Height=1};
        profile.BadgeMask=HudVision.Normalize(HudVision.Extract(ShieldPixels("100","8"),profile.Badge),32,32);
        async Task Ready()
        {
            await Feed(app,capture,"100",3,"");await Invoke(app,"ArmScreen");await Feed(app,capture,"100",4,"");
        }
        try {
            choices[0]["Kill"].IsChecked=false;choices[1]["Damage"].IsChecked=false;
            await Ready();var before=links.Select(l=>l.Positive.Count).ToArray();
            await Feed(app,capture,"80",3,"8");await Task.Delay(450);
            Check(Get<TextBlock>(app,"screenHistory").Text.Contains("Kill")&&Get<TextBlock>(app,"screenHistory").Text.Contains("Damage"),"synthetic HUD confirms damage and a new kill badge in the same frame");
            Check(links[0].Positive.Skip(before[0]).Any()&&links[1].Positive.Skip(before[1]).Contains(profile.KillLevel),"same-frame damage and kill both reach their independently assigned motors");
            Check(Get<TextBlock>(app,"screenHistory").Text.Split(Environment.NewLine).Take(2).All(line=>line.Contains("queued · 1 motor")),"history reports separate queued motor counts for simultaneous events");
            Check(players.All(p=>p.Level(0)==0),"both overlapping cues stay bounded");
            await Invoke(app,"DisarmScreen");
            choices[0]["Kill"].IsChecked=true;choices[1]["Damage"].IsChecked=true;
            await Ready();before=links.Select(l=>l.Positive.Count).ToArray();
            await Feed(app,capture,"80",3,"8");await Task.Delay(450);
            Check(links.Select((l,i)=>l.Positive.Skip(before[i]).Contains(profile.KillLevel)).All(x=>x),"kill feedback wins when damage and kill share a motor in the same frame");
            var history=Get<TextBlock>(app,"screenHistory").Text.Split(Environment.NewLine).Take(2).ToArray();
            Check(history.Any(line=>line.Contains("Kill · queued · 2 motors"))&&history.Any(line=>line.Contains("Damage")&&line.EndsWith("cooldown")),"history distinguishes the shared-motor kill cue from its suppressed damage event");
            await Invoke(app,"DisarmScreen");await Ready();before=links.Select(l=>l.Positive.Count).ToArray();
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Kill",Health=100});await Task.Delay(80);
            await Invoke(app,"SendScreenCue",new HudEvent {Kind="Damage",Health=80,Amount=20});await Task.Delay(450);
            Check(links.Select((l,i)=>l.Positive.Skip(before[i]).Contains(profile.KillLevel)&&l.Positive.Skip(before[i]).All(v=>v>=profile.KillLevel*3/4)).All(x=>x),"a lower-priority hit during a kill cue cannot interrupt its second pulse");
        }finally {
            await Invoke(app,"DisarmScreen");profile.Health=health;profile.Badge=badge;profile.BadgeMask=mask;
            foreach(var route in choices)foreach(var choice in route.Values)choice.IsChecked=true;
        }
    }

    static async Task AdvancedSliderCannotRetargetScreen(ConnectorApp app,Capture capture,Device device,Link link)
    {
        device.Supported=true;device.Controls=new StackPanel();Call(app,"BuildControls",device);
        await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);
        int marker=link.Positive.Count;
        await Invoke(app,"SendScreenCue",new HudEvent {Kind="Damage",Health=80,Amount=20});
        device.Motors[0].Level.Value=19;
        await Task.Delay(350);
        var cue=link.Positive.Skip(marker).ToArray();
        Check(cue.Any()&&cue.All(level=>level<=7),"an inactive advanced device slider cannot raise a screen-owned damage cue above its configured level");
        Check(Get<bool>(app,"screenArmed")&&device.Motors[0].Level.Value==19&&device.Player.Level(0)==0,
            "advanced slider prepares the next manual value while the bounded screen cue completes normally");
        await Invoke(app,"Manual",new Func<Task>(()=>device.Player.Start(0,new PatternSpec {mode="Steady",high=(int)device.Motors[0].Level.Value,durationMs=1000,transitionMs=0},20)));
        Check(!Get<bool>(app,"screenArmed")&&device.Player.Level(0)==19,"explicit manual Start still takes ownership using the prepared advanced-slider value");
        await device.Player.StopAll();
    }

    static async Task OutputChangesAndPendingArm(ConnectorApp app,Capture capture,Link[] links,Playback[] players)
    {
        var status=Get<TextBlock>(app,"screenStatus");var button=Get<Button>(app,"screenOutput");
        async Task Following()
        {
            await Feed(app,capture,"100",3);await Invoke(app,"ArmScreen");await Feed(app,capture,"100",3);
            Check(Get<bool>(app,"screenArmed")&&status.Text=="Following HUD · output enabled","transition fixture starts with armed output and a live HUD");
        }
        async Task WaitForStops()
        {
            var deadline=DateTime.UtcNow.AddSeconds(2);
            while(status.Text!="Preview only · output off"&&DateTime.UtcNow<deadline)await Task.Delay(10);
        }
        var route=((IDictionary)Get<object>(app,"screenRoutes")).Values.Cast<object>().First();
        var included=Get<CheckBox>(route,"Include");var ceiling=Get<Slider>(route,"Ceiling");
        await Following();included.IsChecked=false;await WaitForStops();
        Check(!Get<bool>(app,"screenArmed")&&(string)button.Content=="Enable output"&&status.Text=="Preview only · output off","deselecting a motor replaces stale output-enabled status after stopping");
        included.IsChecked=true;await WaitForStops();
        await Following();ceiling.Value=19;await WaitForStops();
        Check(!Get<bool>(app,"screenArmed")&&status.Text=="Preview only · output off","changing a motor ceiling reports preview-only status");
        ceiling.Value=20;await WaitForStops();
        var damage=Descendants(Get<TabControl>(app,"tabs")).OfType<Slider>().Single(s=>System.Windows.Automation.AutomationProperties.GetName(s)=="Damage event strength");
        await Following();double savedLevel=damage.Value;damage.Value=savedLevel-1;await WaitForStops();
        Check(!Get<bool>(app,"screenArmed")&&status.Text=="Preview only · output off","changing event strength reports preview-only status");
        damage.Value=savedLevel;await WaitForStops();
        await Following();await Invoke(app,"DisarmScreen");
        Check(!Get<bool>(app,"screenArmed")&&status.Text=="Preview only · output off","explicit Disable output reports output off");
        await Following();links[0].FailZero=1;await Invoke(app,"DisarmScreen");
        Check(players[0].Level(0)<0&&status.Text.Contains("unconfirmed"),"control disarm preserves unconfirmed stop status");
        await Invoke(app,"RetryScreenStops",ScreenCapture.Now+1);
        Check(players[0].Level(0)==0&&status.Text=="Preview only · output off","confirmed retry clears the unconfirmed status without rearming");
        foreach(string currentHealth in new[]{"0",""})
        {
            await Feed(app,capture,"100",3);int[] positive=links.Select(l=>l.Positive.Count).ToArray();
            links[0].Block=new TaskCompletionSource<bool>();links[0].Entered=new TaskCompletionSource<bool>();
            var arming=Invoke(app,"ArmScreen");await links[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try {await Feed(app,capture,currentHealth);}
            finally {links[0].Block.TrySetResult(true);}
            await arming;links[0].Block=null;
            Check(!Get<bool>(app,"screenArmed")&&(string)button.Content=="Enable output"&&status.Text.Contains("health"),"zero or unreadable health during a slow handoff prevents arming");
            Check(links.Select(l=>l.Positive.Count).SequenceEqual(positive),"rejected handoff never commands positive output");
        }
    }

    static void DiagnosticsRemainAnonymousAndReadOnly()
    {
        var app=new ConnectorApp();var capture=new Capture();var link=new Link();
        string Summary()=>(string)Call(app,"StudioDiagnosticSummary");
        string initial=Summary();
        Check(initial.Contains("Screen capture active: False")&&initial.Contains("Screen output enabled: False")&&
            initial.Contains("Screen preview frozen: False")&&initial.Contains("Screen frame: none"),"diagnostics distinguish inactive capture and output without starting either");
        Check(initial.Contains("Screen shield reading enabled: False")&&initial.Contains("Screen learned digit templates present: False")&&
            initial.Contains("Screen learned kill badge present: False")&&initial.Contains("Screen selected connected motors: 0"),"diagnostics report absent learned templates and targets honestly");
        const string privateText="PRIVATE-DIAGNOSTIC-SENTINEL";
        var windows=new ComboBox();windows.Items.Add(new CaptureWindow {Handle=new IntPtr(0x123456),Title=privateText});windows.SelectedIndex=0;
        Set(app,"screenWindows",windows);Set(app,"screenStatus",new TextBlock {Text=privateText+" status"});Set(app,"screenHistory",new TextBlock {Text=privateText+" health history"});
        Set(app,"screenReading",new TextBlock {Text=privateText+" recognized values"});Set(app,"screenFailure",privateText+" capture failure");Set(app,"apiToken",privateText+" token");
        var profile=Get<ScreenProfile>(app,"screenProfile");profile.ReadShield=true;profile.Health.X=.123456;
        profile.Digits.Add(new HudGlyph {Digit=8,Aspect=1,Mask=Enumerable.Repeat((byte)1,384).ToArray()});profile.BadgeMask=Enumerable.Repeat((byte)1,1024).ToArray();
        var routes=(IDictionary)Get<object>(app,"screenRoutes");var devices=Get<Dictionary<ulong,Device>>(app,"devices");
        void AddRoute(ulong address,bool connected,bool selected,bool supported=true)
        {
            var device=new Device {Address=address,Name=privateText,Connected=connected,Channels=new[]{0},Player=supported?new Playback(link,new[]{0}):null};devices.Add(address,device);
            var route=Activator.CreateInstance(typeof(ConnectorApp).GetNestedType("ScreenRoute",BindingFlags.NonPublic));
            Set(route,"Device",device);Set(route,"Channel",0);Set(route,"Include",new CheckBox {IsChecked=selected});routes.Add(privateText+address,route);
        }
        AddRoute(0xABCDEF012345,true,true);AddRoute(2,true,false);AddRoute(3,false,true);AddRoute(4,true,true,false);
        Set(app,"screenCapture",capture);Set(app,"screenArmed",true);Set(app,"screenGeneration",23);Set(app,"operatorGeneration",17);
        var frame=new ScreenPixels(Enumerable.Repeat((byte)97,256).ToArray(),8,8,ScreenCapture.Now);Set(app,"screenFrame",frame);
        string live=Summary();
        Check(live.Contains("Screen capture active: True")&&live.Contains("Screen output enabled: True")&&live.Contains("Screen frame: fresh"),"diagnostics distinguish fresh preview from armed output");
        Check(live.Contains("Screen shield reading enabled: True")&&live.Contains("Screen learned digit templates present: True")&&live.Contains("Screen learned kill badge present: True"),"diagnostics report settings and template presence without claiming recognition accuracy");
        Check(live.Contains("Screen selected connected motors: 1"),"diagnostic target count excludes disconnected, unselected and unsupported motors");
        Check(!live.Contains(privateText)&&!live.Contains("ABCDEF012345")&&!live.Contains(".123456")&&!live.Contains(Convert.ToBase64String(profile.BadgeMask))&&
            !live.Contains(Convert.ToBase64String(frame.Bgra)),"diagnostics omit identities, calibration coordinates, images, status text and recognized history");
        Set(app,"screenFrame",new ScreenPixels(frame.Bgra,8,8,ScreenCapture.Now-10));Check(Summary().Contains("Screen frame: stale"),"old preview frames are marked stale");
        Set(app,"screenFrozen",true);Check(Summary().Contains("Screen frame: frozen")&&Summary().Contains("Screen preview frozen: True"),"frozen preview is distinguished from a stale live stream");
        Set(app,"screenFrozen",false);Set(app,"screenFrame",null);Check(Summary().Contains("Screen frame: none"),"active capture can report that no preview frame has arrived");
        Set(app,"screenFrame",new ScreenPixels(frame.Bgra,8,8,ScreenCapture.Now));Set(app,"screenCapture",null);
        Check(Summary().Contains("Screen capture active: False")&&Summary().Contains("Screen frame: stale"),"a stored frame is not reported as fresh after capture ends");
        Check(link.Commands==0&&link.LatestSamples==0&&capture.ForegroundReads==0&&!capture.Disposed,"diagnostics never sample foreground, change capture or send device commands");
        Check(Get<bool>(app,"screenArmed")&&Get<int>(app,"screenGeneration")==23&&Get<int>(app,"operatorGeneration")==17&&
            ReferenceEquals(Get<ScreenProfile>(app,"screenProfile"),profile)&&Get<string>(app,"screenFailure")==privateText+" capture failure", "diagnostics leave source ownership, calibration and error state unchanged");
    }
}
