using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace LovenseConnector
{
    internal sealed class FakeLink:ICommandLink,ILatestCommandLink
    {
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        public List<string> Commands=new List<string>();
        public string Block,Fail;
        public int DelayMs=12;
        public TaskCompletionSource<bool> Entered=new TaskCompletionSource<bool>();
        public TaskCompletionSource<bool> Release=new TaskCompletionSource<bool>();
        public string[] Snapshot() { lock(Commands) return Commands.ToArray(); }
        public Task<string> Command(string c) { return Command(c,CancellationToken.None); }
        public Task<string> Command(string c,CancellationToken cancel) { return CommandLatest(()=>c,cancel); }
        public async Task<string> CommandLatest(Func<string> sample,CancellationToken cancel)
        {
            await gate.WaitAsync(cancel);
            try {
                cancel.ThrowIfCancellationRequested();string c=sample();if(c==null)return ""; lock(Commands) Commands.Add(c);
                if(c==Block) { Block=null; Entered.TrySetResult(true); await Release.Task; }
                await Task.Delay(DelayMs); if(c==Fail) throw new InvalidOperationException("Simulated rejection"); return "OK";
            }
            finally { gate.Release(); }
        }
    }
    internal static class PlaybackTests
    {
        static int count;
        static void Check(bool ok,string name) { if(!ok) throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
        static void Reject(Action action,string name) { bool rejected=false; try { action(); } catch(ArgumentException) { rejected=true; } Check(rejected,name); }
        static async Task Wait(Func<bool> condition) { var end=DateTime.UtcNow.AddSeconds(3); while(!condition()) { if(DateTime.UtcNow>end) throw new Exception("Timed out"); await Task.Delay(10); } }
        static async Task Run()
        {
            var frames=new ReplyFramer();
            string binary="FLVS:"+(char)13+(char)10+(char)59+";";
            Check(ProtocolDiagnostics.Command(binary)=="FLVS [speed=13, start=10, end=59]","binary stroke trace distinguishes payload bytes from framing and newlines");
            Check(binary[5]==13&&binary[6]==10&&binary[7]==59&&binary.Length==9,"formatting leaves original wire command unchanged");
            Check(ProtocolDiagnostics.Command("Vibrate2:20;")=="Vibrate2:20;","ordinary command diagnostics stay unchanged");
            Check(!ProtocolDiagnostics.Command("FLVS:"+(char)10).Contains('\n'),"truncated binary command remains a single readable diagnostic line");
            var smooth=new LevelTransition(0);smooth.Sample(20,300,0);
            int first=smooth.Sample(18,300,100);Check(first>0,"retarget advances the previous envelope");
            Check(smooth.Sample(16,300,200)>first,"repeated changing targets keep moving");
            smooth=new LevelTransition(0);smooth.Sample(20,100,0);int mid=smooth.Sample(20,100,50);
            Check(smooth.Sample(20,1000,100)>=mid,"changing transition duration never rewinds");
            Check(smooth.Sample(0,1000,101)==0,"zero bypasses smoothing immediately");
            smooth=new LevelTransition(20);smooth.Sample(2,300,0);Check(smooth.Sample(2,300,300)==2,"downward transition reaches endpoint");
            Check(frames.Append("Vibrate1:").Length==0 && frames.Append("2;;OK;Li").SequenceEqual(new[]{"Vibrate1:2","OK"}) && frames.Append("ght:1;").SequenceEqual(new[]{"Light:1"}),"fragmented and combined replies with empty delimiters");
            Check(Replies.CommandMatches("Vibrate1:2;","Vibrate1:2",true),"Edge exact motor echo accepted");
            Check(!Replies.CommandMatches("Vibrate1:2;","Vibrate1:3",true)&&!Replies.CommandMatches("Vibrate1:2;","Vibrate1:2",false)&&!Replies.CommandMatches("Vibrate:2;","Vibrate:2",true),"wrong command, wrong value and unknown-device echoes rejected");
            Check(Replies.IsError("unkown,Status:1;")&&Replies.IsError("ERR")&&Replies.CommandMatches("Vibrate:0;","OK",false),"error and ordinary ACK framing");
            var spec=new PatternSpec { low=2,high=18,periodMs=1000,dutyPercent=25 };
            Check(spec.At(0)==18 && spec.At(249)==18 && spec.At(250)==2 && spec.At(1000)==18,"pulse duty and repeat boundary");
            spec.mode="Wave"; Check(spec.At(0)==2&&spec.At(500)==18&&spec.At(1000)==2,"wave extrema");
            spec.mode="Ramp"; spec.repeat=false; Check(spec.At(500)==10&&spec.At(2000)==18,"one-shot ramp holds end value");
            spec.phaseDegrees=180; Check(spec.At(0)==10,"phase offset");
            spec=new PatternSpec { mode="Custom",periodMs=1000,repeat=false,frames=new[]{new Frame { ms=0,level=0 },new Frame { ms=1000,level=20 }} };
            spec.Validate(); Check(spec.At(500)==0,"custom stepped envelope"); spec.interpolate=true; Check(spec.At(500)==10&&spec.At(1200)==20,"custom interpolation and endpoint");
            var copy=new JavaScriptSerializer().Deserialize<PatternSpec>(new JavaScriptSerializer().Serialize(spec)); copy.Validate(); Check(copy.At(500)==10,"saved profile round trip");
            spec.curve="smooth";spec.Validate();
            Check(spec.At(0)==0&&spec.At(250)==3&&spec.At(750)==17&&spec.At(1000)==20,"smooth curve eases both endpoints at native levels");
            Check(Enumerable.Range(0,1201).All(t=>spec.At(t)>=0&&spec.At(t)<=20),"smooth curve never overshoots motor bounds");
            copy=new JavaScriptSerializer().Deserialize<PatternSpec>(new JavaScriptSerializer().Serialize(spec));copy.Validate();
            Check(copy.At(250)==3&&copy.curve=="smooth","smooth interpolation survives serialization");
            spec.curve="linear";Check(spec.At(250)==5,"explicit linear curve preserves straight interpolation");
            spec.curve="step";Check(spec.At(999)==0&&spec.At(1000)==20,"explicit step overrides legacy interpolation flag");
            Reject(()=>new PatternSpec { mode="Custom",curve="invalid",frames=spec.frames }.Validate(),"unknown custom interpolation rejected");
            Reject(()=>new PatternSpec { high=21 }.Validate(),"reject level outside hardware range");
            Reject(()=>new PatternSpec { durationMs=-1 }.Validate(),"reject invalid duration");
            Reject(()=>new PatternSpec { stopAtMs=-1 }.Validate(),"reject invalid shared deadline");
            Reject(()=>new PatternSpec { mode="Custom",frames=new Frame[]{null,null} }.Validate(),"reject null keyframes");
            Reject(()=>new PatternSpec { mode="Custom",frames=new[]{new Frame(),new Frame { ms=50 }} }.Validate(),"reject points too close");
            var link=new FakeLink(); var player=new Playback(link,new[]{1,2});
            Reject(()=>player.MotorCommand(0,1),"reject nonexistent motor");
            Check(player.MotorCommand(2,20)=="Vibrate2:20;","independent channel routing");
            await player.Start(1,new PatternSpec { mode="Steady",high=15,durationMs=220 },4);
            await Wait(()=>player.Level(1)==4); Check(true,"per-motor intensity limit");
            await Wait(()=>!player.Active(1)&&player.Level(1)==0); Check(link.Snapshot().Contains("Vibrate1:0;"),"timed output ends with stop");
            await player.Start(1,new PatternSpec { mode="Steady",high=7,durationMs=0 },20);
            await player.Start(2,new PatternSpec { mode="Steady",high=3,durationMs=0 },20);
            await Wait(()=>player.Level(1)==7&&player.Level(2)==3); await player.Stop(1);
            Check(player.Level(1)==0&&player.Level(2)==3&&player.Active(2),"stop one motor preserves the other");
            player.LiveLevel(2,9); await Wait(()=>player.Level(2)==9); player.Ceiling(2,2); await Wait(()=>player.Level(2)==2); Check(true,"live level and live ceiling");
            await player.StopAll(); int stoppedAt=link.Snapshot().Length; await Task.Delay(250);
            Check(link.Snapshot().Length==stoppedAt&&!player.Running&&player.Level(2)==0,"stop cancels all future pattern writes");
            link=new FakeLink { Block="Preset:0;" }; player=new Playback(link,new[]{1,2});
            var hardware=player.HardwarePreset(1,500); await link.Entered.Task;
            var pending=player.Start(1,new PatternSpec { mode="Steady",high=20 },20); var stop=player.StopAll(); link.Release.SetResult(true);
            await Task.WhenAll(hardware,pending,stop); await Task.Delay(150);
            Check(!link.Snapshot().Contains("Preset:1;")&&!link.Snapshot().Contains("Vibrate1:20;")&&!player.Running,"stop invalidates queued starts and presets");
            link=new FakeLink { Fail="Preset:0;" }; player=new Playback(link,new[]{0}); bool failed=false;
            try { await player.StopAll(); } catch(InvalidOperationException) { failed=true; }
            Check(failed&&player.Level(0)==-1&&link.Snapshot().Contains("Vibrate:0;"),"failed preset stop remains unconfirmed while still trying motor stop");
            link=new FakeLink { Block="Vibrate1:5;",DelayMs=400 };player=new Playback(link,new[]{1,2});
            await player.Start(1,new PatternSpec { mode="Steady",high=5,durationMs=0 },20);await link.Entered.Task;
            await player.Start(2,new PatternSpec { mode="Steady",high=3,durationMs=0 },20);player.UpdateSteady(2,17,0);
            link.Release.TrySetResult(true);await Wait(()=>player.Level(2)==17);
            Check(!link.Snapshot().Contains("Vibrate2:3;"),"queued second motor samples latest target after delayed BLE gate opens");
            await player.StopAll();Check(player.Level(1)==0&&player.Level(2)==0,"both delayed channels stop with acknowledgements");
            link=new FakeLink { Block="Vibrate1:4;" };player=new Playback(link,new[]{1,2});
            await player.Start(1,new PatternSpec {mode="Steady",high=4,durationMs=0},20);await link.Entered.Task;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            await player.Start(2,new PatternSpec {mode="Steady",high=18,durationMs=2000,stopAtMs=120},20,clock);
            await Task.Delay(180);link.Release.TrySetResult(true);
            await Wait(()=>!player.Active(2));
            Check(!link.Snapshot().Contains("Vibrate2:18;")&&player.Level(2)==0,"shared deadline prevents stale positive output after a blocked transport");
            await player.StopAll();
            link=new FakeLink();player=new Playback(link,new[]{1,2});
            clock=System.Diagnostics.Stopwatch.StartNew();await Task.Delay(200);
            await player.Start(2,new PatternSpec {mode="Steady",high=11,durationMs=2000,stopAtMs=400},20,clock);
            await Wait(()=>player.Level(2)==11);await Wait(()=>!player.Active(2)&&player.Level(2)==0);
            Check(clock.ElapsedMilliseconds<1200,"late motor joins end on shared deadline instead of restarting duration");
        }
        public static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine(count+" playback checks passed."); return 0; }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
