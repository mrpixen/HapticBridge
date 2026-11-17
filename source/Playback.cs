using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LovenseConnector
{
    internal interface ICommandLink
    {
        Task<string> Command(string command);
        Task<string> Command(string command, CancellationToken cancel);
    }
    internal interface ILatestCommandLink { Task<string> CommandLatest(Func<string> command,CancellationToken cancel); }
    public sealed class Frame { public int ms { get; set; } public int level { get; set; } }
    public sealed class PatternSpec
    {
        public int version { get; set; }
        public string name { get; set; }
        public string mode { get; set; }
        public int low { get; set; }
        public int high { get; set; }
        public int periodMs { get; set; }
        public int durationMs { get; set; }
        // Optional deadline on the shared pattern clock. Duration is a lease
        // from this motor's start; the deadline keeps late joins in sync.
        public int stopAtMs { get; set; }
        public int dutyPercent { get; set; }
        public int phaseDegrees { get; set; }
        public int transitionMs { get; set; }
        public bool repeat { get; set; }
        public bool interpolate { get; set; }
        public string curve { get; set; }
        public Frame[] frames { get; set; }
        public PatternSpec() { version=1; name="My pattern"; mode="Pulse"; low=0; high=3; periodMs=2000; durationMs=10000; dutyPercent=50; repeat=true; frames=new Frame[0]; }
        public void Validate()
        {
            if(version!=1) throw new ArgumentException("Unsupported pattern format version.");
            if(!new[]{"Steady","Pulse","Wave","Ramp","Custom"}.Contains(mode)) throw new ArgumentException("Unknown pattern type.");
            if(low<0 || high>20 || low>high) throw new ArgumentException("Levels must be from 0 to 20, with minimum no higher than maximum.");
            if(periodMs<200 || periodMs>600000) throw new ArgumentException("Cycle length must be between 200 and 600000 milliseconds.");
            if(durationMs<0 || durationMs>3600000) throw new ArgumentException("Duration must be 0 (until stopped) through 3600000 ms.");
            if(stopAtMs<0 || stopAtMs>3600000) throw new ArgumentException("Shared-clock deadline must be 0 (none) through 3600000 ms.");
            if(transitionMs<0||transitionMs>10000) throw new ArgumentException("Transition must be 0–10000 milliseconds.");
            if(dutyPercent<1 || dutyPercent>99 || phaseDegrees<0 || phaseDegrees>359) throw new ArgumentException("Duty must be 1–99%; phase must be 0–359 degrees.");
            if(mode=="Custom")
            {
                if(curve!=null&&curve!="linear"&&curve!="smooth"&&curve!="step") throw new ArgumentException("Unknown curve interpolation.");
                if(frames==null || frames.Length<2 || frames.Length>512 || frames.Any(f=>f==null) || frames[0].ms!=0) throw new ArgumentException("Custom patterns need 2–512 points, starting at 0 ms.");
                for(int i=0;i<frames.Length;i++)
                {
                    if(frames[i].level<0 || frames[i].level>20 || frames[i].ms<0 || frames[i].ms>periodMs) throw new ArgumentException("Custom points must have levels 0–20 within the cycle length.");
                    if(i>0 && frames[i].ms-frames[i-1].ms<100) throw new ArgumentException("Custom points must be ordered and at least 100 ms apart.");
                }
            }
        }
        public int At(double elapsedMs)
        {
            double time=elapsedMs+periodMs*(phaseDegrees/360.0);
            time=repeat ? time%periodMs : Math.Min(time,periodMs);
            double fraction=time/periodMs, value=high;
            switch(mode)
            {
                case "Pulse": value=fraction < dutyPercent/100.0 ? high : low; break;
                case "Wave": value=low+(high-low)*(0.5-0.5*Math.Cos(fraction*Math.PI*2)); break;
                case "Ramp": value=low+(high-low)*fraction; break;
                case "Custom":
                    var first=frames[0]; var last=frames[frames.Length-1];
                    if(time>=last.ms) value=last.level;
                    else {
                        for(int i=1;i<frames.Length;i++) if(time<frames[i].ms) {
                            var a=frames[i-1]; var b=frames[i];
                            double blend=(time-a.ms)/(b.ms-a.ms);
                            if(curve=="smooth") blend=blend*blend*(3-2*blend);
                            bool continuous=curve=="smooth"||curve=="linear"||(curve==null&&interpolate);
                            value=continuous ? a.level+(b.level-a.level)*blend : a.level; break;
                        }
                    }
                    break;
            }
            return Math.Max(0,Math.Min(20,(int)Math.Round(value,MidpointRounding.AwayFromZero)));
        }
    }

    internal sealed class MotorJob
    {
        public PatternSpec Spec;
        public Stopwatch Clock=Stopwatch.StartNew();
        public Stopwatch Renewed=Stopwatch.StartNew();
        public CancellationTokenSource Cancel=new CancellationTokenSource();
        public int Ceiling=20;
    }

    internal sealed class Playback
    {
        readonly ICommandLink link;
        readonly int[] channels;
        readonly Dictionary<int,MotorJob> jobs=new Dictionary<int,MotorJob>();
        readonly Dictionary<int,int> sent=new Dictionary<int,int>();
        readonly object sync=new object();
        readonly SemaphoreSlim controls=new SemaphoreSlim(1,1);
        readonly Dictionary<int,int> revisions=new Dictionary<int,int>();
        int epoch, stopping;
        CancellationTokenSource preset;
        public event Action<int,int,string> Changed;
        public event Action<string> Error;
        public Playback(ICommandLink link,int[] channels) { this.link=link; this.channels=channels; foreach(int c in channels) { sent[c]=0; revisions[c]=0; } }
        public bool Running { get { lock(sync) return jobs.Count>0 || preset!=null; } }
        public string MotorCommand(int channel,int level)
        {
            if(!channels.Contains(channel)) throw new ArgumentException("This motor is not supported by the selected toy.");
            if(level<0 || level>20) throw new ArgumentOutOfRangeException("level");
            return (channel==0 ? "Vibrate" : "Vibrate"+channel) + ":" + level + ";";
        }
        void Emit(int channel,int level,string state) { if(Changed!=null) Changed(channel,level,state); }
        public int Level(int channel) { lock(sync) return sent.ContainsKey(channel)?sent[channel]:0; }
        public bool Active(int channel) { lock(sync) return jobs.ContainsKey(channel); }
        public bool UpdateSteady(int channel,int level,int durationMs,int transitionMs=0)
        {
            MotorCommand(channel,level); if(durationMs<0||durationMs>3600000) throw new ArgumentOutOfRangeException("durationMs");
            if(transitionMs<0||transitionMs>10000) throw new ArgumentOutOfRangeException("transitionMs");
            lock(sync) { MotorJob job; if(jobs.TryGetValue(channel,out job)&&job.Spec.mode=="Steady") { job.Spec.high=level; job.Spec.durationMs=durationMs; job.Spec.transitionMs=transitionMs; job.Renewed.Restart(); return true; } return false; }
        }
        public void LiveLevel(int channel,int level) { MotorCommand(channel,level); lock(sync) { MotorJob job; if(jobs.TryGetValue(channel,out job) && job.Spec.mode=="Steady") job.Spec.high=level; } }
        public void Ceiling(int channel,int level) { MotorCommand(channel,level); lock(sync) { MotorJob job; if(jobs.TryGetValue(channel,out job)) job.Ceiling=level; } }
        public async Task Start(int channel,PatternSpec spec,int ceiling,Stopwatch sharedClock=null)
        {
            spec.Validate(); MotorCommand(channel,0);
            if(ceiling<0||ceiling>20) throw new ArgumentOutOfRangeException("ceiling");
            int generation,revision;
            lock(sync) { if(stopping>0) return; generation=epoch; revision=++revisions[channel]; }
            await controls.WaitAsync();
            try
            {
                lock(sync) if(generation!=epoch || revision!=revisions[channel]) return;
                await StopPreset();
                MotorJob job;
                lock(sync)
                {
                    if(generation!=epoch || revision!=revisions[channel]) return;
                    MotorJob previous; if(jobs.TryGetValue(channel,out previous)) previous.Cancel.Cancel();
                    job=new MotorJob { Spec=spec, Ceiling=ceiling }; if(sharedClock!=null) job.Clock=sharedClock; jobs[channel]=job;
                }
                Run(channel,job);
            }
            finally { controls.Release(); }
        }
        async void Run(int channel,MotorJob job)
        {
            int last=-1;
            var smoother=new LevelTransition(Math.Max(0,Level(channel)));
            var animation=Stopwatch.StartNew();
            try
            {
                while(!job.Cancel.IsCancellationRequested)
                {
                    double elapsed=job.Clock.Elapsed.TotalMilliseconds;
                    if((job.Spec.durationMs>0 && job.Renewed.Elapsed.TotalMilliseconds>=job.Spec.durationMs)||(job.Spec.stopAtMs>0&&elapsed>=job.Spec.stopAtMs)) break;
                    int level=-1;
                    Func<string> sample=()=> {
                        lock(sync) {
                            bool expired=(job.Spec.durationMs>0&&job.Renewed.Elapsed.TotalMilliseconds>=job.Spec.durationMs)||(job.Spec.stopAtMs>0&&job.Clock.Elapsed.TotalMilliseconds>=job.Spec.stopAtMs);
                            int target=expired?0:Math.Min(job.Ceiling,job.Spec.At(job.Clock.Elapsed.TotalMilliseconds));
                            level=smoother.Sample(target,target==0?0:job.Spec.transitionMs,animation.Elapsed.TotalMilliseconds);
                        }
                        return level==last?null:MotorCommand(channel,level);
                    };
                    var latest=link as ILatestCommandLink;string reply;
                    if(latest!=null) reply=await latest.CommandLatest(sample,job.Cancel.Token).ConfigureAwait(false);
                    else { string command=sample();reply=command==null?"":await link.Command(command,job.Cancel.Token).ConfigureAwait(false); }
                    if(reply!="")
                    {
                        last=level;
                        lock(sync) { MotorJob current; if(jobs.TryGetValue(channel,out current) && Object.ReferenceEquals(current,job)) { sent[channel]=level; Emit(channel,level,job.Spec.mode); } }
                    }
                    await Task.Delay(100,job.Cancel.Token).ConfigureAwait(false);
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(Error!=null) Error(ex.Message); }
            // All loop exceptions are handled above; cleanup may await on C# 5.
            {
                await controls.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool owns;
                    lock(sync) { MotorJob current; owns=jobs.TryGetValue(channel,out current)&&Object.ReferenceEquals(current,job); if(owns) jobs.Remove(channel); }
                    if(owns)
                    {
                        try { await link.Command(MotorCommand(channel,0)).ConfigureAwait(false); lock(sync) sent[channel]=0; Emit(channel,0,"Stopped"); }
                        catch(Exception ex) { lock(sync) sent[channel]=-1; Emit(channel,-1,"Stop unconfirmed"); if(Error!=null) Error(ex.Message); }
                    }
                }
                finally { controls.Release(); job.Cancel.Dispose(); }
            }
        }
        public async Task Stop(int channel)
        {
            MotorCommand(channel,0);
            lock(sync) { revisions[channel]++; MotorJob job; if(jobs.TryGetValue(channel,out job)) { jobs.Remove(channel); job.Cancel.Cancel(); } }
            await controls.WaitAsync().ConfigureAwait(false);
            try { await StopPreset().ConfigureAwait(false); await link.Command(MotorCommand(channel,0)).ConfigureAwait(false); lock(sync) sent[channel]=0; Emit(channel,0,"Stopped"); }
            catch { lock(sync) sent[channel]=-1; Emit(channel,-1,"Stop unconfirmed"); throw; }
            finally { controls.Release(); }
        }
        public void CancelAll()
        {
            lock(sync) { epoch++; var previous=jobs.Values.ToArray(); jobs.Clear(); var oldPreset=preset; preset=null; foreach(var job in previous) job.Cancel.Cancel(); if(oldPreset!=null) oldPreset.Cancel(); }
        }
        public async Task StopAll()
        {
            lock(sync) { stopping++; CancelAll(); }
            await controls.WaitAsync().ConfigureAwait(false);
            try { await ZeroAll().ConfigureAwait(false); }
            finally { lock(sync) stopping--; controls.Release(); }
        }
        async Task ZeroAll()
        {
            // Preset zero terminates firmware playback; Vibrate zero clears all motor levels.
            Exception failure=null;
            try { await link.Command("Preset:0;").ConfigureAwait(false); } catch(Exception ex) { failure=ex; }
            try { await link.Command("Vibrate:0;").ConfigureAwait(false); } catch(Exception ex) { failure=ex; }
            foreach(var c in channels) { lock(sync) sent[c]=failure==null?0:-1; Emit(c,failure==null?0:-1,failure==null?"Stopped":"Stop unconfirmed"); }
            if(failure!=null) throw failure;
        }
        async Task StopPreset()
        {
            bool had; lock(sync) { had=preset!=null; if(had) { var old=preset; preset=null; old.Cancel(); } }
            if(had) await ZeroAll();
        }
        public async Task HardwarePreset(int index,int durationMs)
        {
            if(index<1||index>10||durationMs<500||durationMs>60000) throw new ArgumentException("Preset must be 1–10 and duration 0.5–60 seconds.");
            int generation; lock(sync) { if(stopping>0) return; CancelAll(); generation=epoch; }
            var cancel=new CancellationTokenSource();
            await controls.WaitAsync();
            Exception failure=null;
            try
            {
                lock(sync) { if(generation!=epoch) { cancel.Dispose(); return; } preset=cancel; }
                await ZeroAll();
                await link.Command("Preset:"+index+";",cancel.Token);
                lock(sync) if(Object.ReferenceEquals(preset,cancel)) foreach(var c in channels) { sent[c]=-1; Emit(c,-1,"Hardware preset " + index); }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { failure=ex; }
            finally { controls.Release(); }
            try {
                if(failure==null) await Task.Delay(durationMs,cancel.Token);
            }
            catch(OperationCanceledException) { }
            {
                await controls.WaitAsync();
                try { bool owns; lock(sync) { owns=Object.ReferenceEquals(preset,cancel); if(owns) preset=null; } if(owns) await ZeroAll(); }
                finally { controls.Release(); cancel.Dispose(); }
            }
            if(failure!=null) throw failure;
        }
    }
    internal sealed class LevelTransition
    {
        double value,from,started; int target=-1,previousDuration;
        public LevelTransition(double initial) { value=from=initial; }
        public int Sample(int next,int durationMs,double now)
        {
            if(next==0)durationMs=0;
            if(target>=0) { double old=previousDuration<=0?1:Math.Max(0,Math.Min(1,(now-started)/previousDuration)); value=from+(target-from)*old*old*(3-2*old); }
            if(next!=target||durationMs!=previousDuration) { from=value;target=next;started=now; }
            previousDuration=durationMs;
            double t=durationMs<=0?1:Math.Max(0,Math.Min(1,(now-started)/durationMs));
            value=from+(target-from)*t*t*(3-2*t);
            return Math.Max(0,Math.Min(20,(int)Math.Round(value,MidpointRounding.AwayFromZero)));
        }
    }
}
