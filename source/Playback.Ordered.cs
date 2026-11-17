using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LovenseConnector
{
    internal sealed class OrderedOutputOwner
    {
        int closed;
        public bool IsOpen => Volatile.Read(ref closed)==0;
        public void Close() => Interlocked.Exchange(ref closed,1);
    }
    internal sealed record OrderedRouteSettings(bool Enabled, double Gain);
    internal sealed record OrderedMotorRoute(int Channel, Func<OrderedRouteSettings> Settings);
    internal sealed record OrderedWriteStatistics(long Submitted, long Expired, long Overloaded, int Pending);

    internal sealed partial class Playback
    {
        // These limits bound stale work, not sample spacing or vibration intensity.
        internal const int OrderedCapacity=1000, OrderedFreshnessMs=1000, OrderedLeaseMs=5000;
        sealed class OrderedMotor
        {
            public OrderedOutputOwner Owner;
            public OrderedMotorRoute Route;
            public CancellationTokenSource Cancel=new();
            public long LeaseUntil;
        }
        sealed record OrderedSample(double Percent, long Deadline, object Batch, OrderedMotor[] Motors);
        readonly Dictionary<int,OrderedMotor> orderedMotors=new();
        readonly Queue<OrderedSample> orderedQueue=new();
        readonly HashSet<OrderedMotor> orderedZeros=new();
        readonly SemaphoreSlim orderedWake=new(0,1);
        bool orderedWorker;
        long orderedSubmitted,orderedExpired,orderedOverloaded;
        static long OrderedNow => Environment.TickCount64;
        public OrderedWriteStatistics OrderedStatistics { get { lock(sync) return new(orderedSubmitted,orderedExpired,orderedOverloaded,orderedQueue.Count); } }

        // Callers publish immutable settings snapshots. The callback runs at write
        // readiness and must never read WPF controls or block on a dispatcher.
        public bool EnqueueOrdered(OrderedOutputOwner owner, IReadOnlyList<double> samples, IReadOnlyList<OrderedMotorRoute> routes)
        {
            if(owner==null||samples==null||routes==null)throw new ArgumentNullException();
            if(samples.Count<1||samples.Count>OrderedCapacity)throw new ArgumentException("An ordered batch needs 1–1000 samples.");
            var copy=samples.ToArray();
            if(copy.Any(p=>!double.IsFinite(p)||p<0||p>100))throw new ArgumentException("Invalid ordered percentage.");
            if(routes.Select(r=>r.Channel).Distinct().Count()!=routes.Count)throw new ArgumentException("Duplicate ordered motor.");
            foreach(var route in routes) { MotorCommand(route.Channel,0);if(route.Settings==null)throw new ArgumentException("Missing route settings."); }
            lock(sync)
            {
                if(!owner.IsOpen||stopping>0||preset!=null)return false;
                long now=OrderedNow;
                var selected=new List<OrderedMotor>();
                foreach(var route in routes)
                {
                    var settings=route.Settings();
                    if(settings==null||!settings.Enabled)continue;
                    if(!orderedMotors.TryGetValue(route.Channel,out var motor)||!ReferenceEquals(motor.Owner,owner))
                    {
                        InvalidateOrdered(route.Channel);
                        if(jobs.Remove(route.Channel,out var old))old.Cancel.Cancel();
                        revisions[route.Channel]++;
                        motor=new OrderedMotor { Owner=owner,Route=route };
                        orderedMotors[route.Channel]=motor;
                    }
                    motor.Route=route;motor.LeaseUntil=now+OrderedLeaseMs;selected.Add(motor);
                }
                if(selected.Count==0)return false;
                // Preserve FIFO normally. At capacity discard complete oldest
                // pending batch remainders, never replace them with their peak.
                while(orderedQueue.Count+copy.Length>OrderedCapacity)
                {
                    var batch=orderedQueue.Peek().Batch;
                    do { var dropped=orderedQueue.Dequeue();orderedOverloaded++;foreach(var m in dropped.Motors)orderedZeros.Add(m); }
                    while(orderedQueue.Count>0&&ReferenceEquals(orderedQueue.Peek().Batch,batch));
                }
                var identity=new object();var motors=selected.ToArray();
                foreach(double p in copy)orderedQueue.Enqueue(new(p,now+OrderedFreshnessMs,identity,motors));
                if(!orderedWorker) { orderedWorker=true;_ = Task.Run(DrainOrdered); }
                if(orderedWake.CurrentCount==0)orderedWake.Release();
                return true;
            }
        }
        bool OwnsOrdered(OrderedMotor motor) => orderedMotors.TryGetValue(motor.Route.Channel,out var current)&&ReferenceEquals(current,motor);
        // sync is held; cancellation happens before waiting for the BLE/control gate.
        void InvalidateOrdered(int channel)
        {
            if(orderedMotors.Remove(channel,out var old)) { old.Cancel.Cancel();orderedZeros.Remove(old); }
        }
        public Task CancelOrdered(OrderedOutputOwner owner, int? channel=null)
        {
            var stopped=new List<(int Channel,int Revision)>();
            lock(sync)
            {
                foreach(var pair in orderedMotors.ToArray())
                    if(ReferenceEquals(pair.Value.Owner,owner)&&(!channel.HasValue||pair.Key==channel.Value))
                    { InvalidateOrdered(pair.Key);stopped.Add((pair.Key,++revisions[pair.Key])); }
            }
            return Task.WhenAll(stopped.Select(p=>ZeroOrderedRevision(p.Channel,p.Revision)));
        }
        // A stale sampled-video stop cannot revoke a newer ordered batch.
        public Task CancelSampled(int channel)
        {
            int revision;
            lock(sync)
            {
                if(orderedMotors.ContainsKey(channel)||!jobs.Remove(channel,out var old))return Task.CompletedTask;
                old.Cancel.Cancel();revision=++revisions[channel];
            }
            return ZeroOrderedRevision(channel,revision);
        }
        async Task ZeroOrderedRevision(int channel,int revision)
        {
            await controls.WaitAsync().ConfigureAwait(false);
            try
            {
                bool submitted=false;
                Func<string> sample=()=> { lock(sync) { if(revisions[channel]!=revision)return null;submitted=true;return MotorCommand(channel,0); } };
                var latest=link as ILatestCommandLink;
                string reply=latest!=null?await latest.CommandLatest(sample,CancellationToken.None).ConfigureAwait(false):await SendOrderedFallback(sample,CancellationToken.None).ConfigureAwait(false);
                if(submitted&&reply!="") { lock(sync) { if(revisions[channel]!=revision)return;sent[channel]=0; }Emit(channel,0,"Stopped"); }
            }
            catch { bool current;lock(sync) { current=revisions[channel]==revision;if(current)sent[channel]=-1; }if(current)Emit(channel,-1,"Stop unconfirmed");throw; }
            finally { controls.Release(); }
        }
        async Task<string> SendOrderedFallback(Func<string> sample,CancellationToken token)
        { token.ThrowIfCancellationRequested();string command=sample();return command==null?"":await link.Command(command,token).ConfigureAwait(false); }

        async Task DrainOrdered()
        {
            while(true)
            {
                OrderedSample sample=null;OrderedMotor zero=null;
                List<(int Channel,int Revision)> expired=new();
                lock(sync)
                {
                    long now=OrderedNow;
                    foreach(var pair in orderedMotors.ToArray())
                        if(!pair.Value.Owner.IsOpen||pair.Value.LeaseUntil<=now||pair.Value.Route.Settings()?.Enabled!=true)
                        { InvalidateOrdered(pair.Key);expired.Add((pair.Key,++revisions[pair.Key])); }
                    while(orderedQueue.Count>0&&orderedQueue.Peek().Deadline<=now)
                    { var old=orderedQueue.Dequeue();orderedExpired++;foreach(var motor in old.Motors)if(OwnsOrdered(motor))orderedZeros.Add(motor); }
                    if(orderedZeros.Count>0) { zero=orderedZeros.First();orderedZeros.Remove(zero); }
                    else if(orderedQueue.Count>0)sample=orderedQueue.Dequeue();
                    else if(orderedMotors.Count==0&&expired.Count==0) { orderedWorker=false;return; }
                }
                foreach(var item in expired)
                    try { await ZeroOrderedRevision(item.Channel,item.Revision).ConfigureAwait(false); }
                    catch(Exception ex) { Error?.Invoke(ex.Message); }
                if(zero!=null) { await WriteOrdered(zero,0,long.MaxValue).ConfigureAwait(false);continue; }
                if(sample!=null)
                {
                    bool stale=false;
                    foreach(var motor in sample.Motors)
                        stale|=await WriteOrdered(motor,sample.Percent,sample.Deadline).ConfigureAwait(false);
                    if(stale)lock(sync) { orderedExpired++;foreach(var motor in sample.Motors)if(OwnsOrdered(motor))orderedZeros.Add(motor); }
                    continue;
                }
                await orderedWake.WaitAsync(100).ConfigureAwait(false); // lease checks only; never paces samples
            }
        }
        async Task<bool> WriteOrdered(OrderedMotor motor,double percent,long deadline)
        {
            bool expired=false;int level=-1;
            try
            {
                Func<string> sample=()=>
                {
                    lock(sync)
                    {
                        if(!OwnsOrdered(motor)||!motor.Owner.IsOpen||motor.Cancel.IsCancellationRequested)return null;
                        if(OrderedNow>=deadline) { expired=true;return null; }
                        var settings=motor.Route.Settings();
                        if(settings==null||!settings.Enabled||!double.IsFinite(settings.Gain)||settings.Gain<0||settings.Gain>200||OrderedNow>=motor.LeaseUntil)return null;
                        level=Math.Clamp((int)Math.Round(percent*settings.Gain/500.0,MidpointRounding.AwayFromZero),0,20);
                        return MotorCommand(motor.Route.Channel,level);
                    }
                };
                var latest=link as ILatestCommandLink;
                string reply=latest!=null?await latest.CommandLatest(sample,motor.Cancel.Token).ConfigureAwait(false):await SendOrderedFallback(sample,motor.Cancel.Token).ConfigureAwait(false);
                if(reply!=""&&level>=0)
                {
                    bool owns;lock(sync) { owns=OwnsOrdered(motor);if(owns) { sent[motor.Route.Channel]=level;orderedSubmitted++; } }
                    if(owns)Emit(motor.Route.Channel,level,"Video batch");
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex)
            {
                Error?.Invoke(ex.Message);
                try { await CancelOrdered(motor.Owner,motor.Route.Channel).ConfigureAwait(false); }catch(Exception stop) { Error?.Invoke(stop.Message); }
            }
            return expired;
        }
    }
}
