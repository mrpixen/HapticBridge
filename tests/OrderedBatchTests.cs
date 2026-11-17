using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

internal static class OrderedBatchTests
{
    static int assertions;
    static void Check(bool value,string message) { assertions++;if(!value)throw new Exception("Ordered batch: "+message); }
    public static int Main()
    {
        try { Run().GetAwaiter().GetResult();Console.WriteLine("Ordered batches: "+assertions+" assertions passed.");return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
    static async Task Until(Func<bool> ready,string message,int timeout=3000)
    { var clock=Stopwatch.StartNew();while(!ready()) { if(clock.ElapsedMilliseconds>timeout)throw new Exception(message);await Task.Delay(5); }Check(true,message); }
    static FeelConnectOutput Parse(FeelConnectProtocol protocol,string samples,long now=100)
    {
        using var json=JsonDocument.Parse("{\"room\":\"demo-room\",\"data\":{\"ver\":2,\"percents\":"+samples+"}}");
        return protocol.Accept(json.RootElement,now,1700000000000);
    }
    static FeelConnectProtocol Protocol() { var p=new FeelConnectProtocol();p.SetRooms(new[]{"demo-room"});return p; }
    static OrderedMotorRoute Route(int channel,double gain=100)=>new(channel,()=>new(true,gain));
    static async Task Run()
    {
        foreach(var item in new[]{("[25,0]",new[]{5,0}),("[0,100,0]",new[]{0,20,0}),("[25,50,0]",new[]{5,10,0}),("[25,25,0]",new[]{5,5,0})})
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();
            var output=Parse(Protocol(),item.Item1);
            Check(!output.IsStop&&output.Samples.Count==item.Item2.Length,"batch zero is a sample, not a preemptive Stop");
            player.EnqueueOrdered(owner,output.Samples,new[]{Route(1)});
            await Until(()=>link.Commands.Count>=item.Item2.Length,"every accepted sample reaches actual fake writer");
            Check(link.Commands.ToArray().SequenceEqual(item.Item2.Select(n=>"Vibrate1:"+n+";")),"exact ordered native sequence "+item.Item1);
            owner.Close();await player.CancelOrdered(owner);
        }
        {
            var protocol=Protocol();var link=new FakeLink();var player=new Playback(link,new[]{1});
            var result=Parse(protocol,"[25,50,101]");
            Check(result==null&&protocol.V2Diagnostics.AcceptedBatches==0,"invalid suffix commits no protocol output or statistics");
            try { player.EnqueueOrdered(new(),new[]{25.0,101},new[]{Route(1)});throw new Exception("accepted invalid batch"); }catch(ArgumentException) { }
            await Task.Delay(30);Check(link.Commands.IsEmpty&&!player.Running,"writer validates whole batch before claiming or dispatching");
            var valid=Parse(protocol,"[25,0]");Check(valid.Samples is not double[],"event samples cannot be mutated through an array cast");
            Check(protocol.Tick(5100).IsStop,"zero-ending batch still has a bounded input lease");
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1,2,3});
            await player.Start(3,new PatternSpec { mode="Steady",high=7,durationMs=0 },20);
            await Until(()=>link.Commands.Contains("Vibrate3:7;"),"unrelated manual motor is running");
            var owner=new OrderedOutputOwner();OrderedRouteSettings first=new(true,100),second=new(true,200);
            var routes=new[]{new OrderedMotorRoute(1,()=>Volatile.Read(ref first)),new OrderedMotorRoute(2,()=>Volatile.Read(ref second))};
            using(var gate=link.BlockReadiness())
            {
                player.EnqueueOrdered(owner,new[]{25.0,50,0},routes);
                Volatile.Write(ref first,new(true,50));
            }
            await Until(()=>link.Commands.Count(c=>c.StartsWith("Vibrate1:"))==3&&link.Commands.Count(c=>c.StartsWith("Vibrate2:"))==3,"both gain routes dispatch every sample");
            Check(link.Commands.Where(c=>c.StartsWith("Vibrate1:")).SequenceEqual(new[]{"Vibrate1:3;","Vibrate1:5;","Vibrate1:0;"}),"gain is read at readiness, native half rounds away from zero");
            Check(link.Commands.Where(c=>c.StartsWith("Vibrate2:")).SequenceEqual(new[]{"Vibrate2:10;","Vibrate2:20;","Vibrate2:0;"}),"second motor gain is independent");
            owner.Close();await player.CancelOrdered(owner);
            Check(player.Active(3)&&player.Level(3)==7,"video cancellation preserves unrelated manual output");
            await player.StopAll();
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();
            var ack=link.BlockNextAck();player.EnqueueOrdered(owner,new[]{25.0,100,0},new[]{Route(1)});
            await Until(()=>link.Commands.Count==1,"first write entered blocked acknowledgement");
            owner.Close();var stopped=player.CancelOrdered(owner);
            Check(!stopped.IsCompleted,"Stop waits for acknowledgement before reusing writer");
            ack.SetResult();await stopped;await Task.Delay(40);
            Check(link.Commands.ToArray().SequenceEqual(new[]{"Vibrate1:5;","Vibrate1:0;"}),"operator Stop preempts every queued nonzero after blocked ACK");
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();
            var ack=link.BlockNextAck();player.EnqueueOrdered(owner,new[]{25.0,100,0},new[]{Route(1)});
            await Until(()=>link.Commands.Count==1,"expiry fixture wrote first positive");
            await Task.Delay(Playback.OrderedFreshnessMs+60);ack.SetResult();
            await Until(()=>player.Level(1)==0&&link.Commands.Count>=2,"expired trailing zero causes prompt clearing after ACK");
            Check(!link.Commands.Contains("Vibrate1:20;"),"expired queued peak never reaches hardware");
            Check(player.OrderedStatistics.Expired==2,"expired samples counted including trailing zero");
            owner.Close();await player.CancelOrdered(owner);
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();
            using(var gate=link.BlockReadiness())
            {
                player.EnqueueOrdered(owner,Enumerable.Repeat(25.0,1000).ToArray(),new[]{Route(1)});
                await Until(()=>player.OrderedStatistics.Pending==999,"one queued sample waits at actual readiness");
                player.EnqueueOrdered(owner,Enumerable.Repeat(50.0,999).Append(0).ToArray(),new[]{Route(1)});
                Check(player.OrderedStatistics.Pending==1000&&player.OrderedStatistics.Overloaded==999,"overload drops whole oldest pending remainder and bounds queue");
            }
            await Until(()=>player.OrderedStatistics.Pending==0&&player.Level(1)==0,"replacement batch drains to final zero");
            Check(link.Commands.Count(c=>c=="Vibrate1:10;")==999,"fresh replacement samples are neither coalesced nor peak substituted");
            owner.Close();await player.CancelOrdered(owner);
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();
            using(var gate=link.BlockReadiness())
            {
                player.EnqueueOrdered(owner,new[]{25.0,100,0},new[]{Route(1)});
                await player.Start(1,new PatternSpec { mode="Steady",high=3,durationMs=0 },20);
            }
            await Until(()=>link.Commands.Contains("Vibrate1:3;"),"manual source takes ownership while ordered write waits");
            Check(!link.Commands.Any(c=>c=="Vibrate1:5;"||c=="Vibrate1:20;"),"ownership change invalidates pending video samples");
            owner.Close();await player.CancelOrdered(owner);Check(player.Active(1),"old owner cancellation cannot stop new manual owner");
            await player.StopAll();
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var owner=new OrderedOutputOwner();int sampledCurrent=1;
            var controls=(SemaphoreSlim)typeof(Playback).GetField("controls",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(player);
            await controls.WaitAsync();
            var oldStart=player.Start(1,new PatternSpec { mode="Steady",high=19,durationMs=1000 },20,isCurrent:()=>Volatile.Read(ref sampledCurrent)==1);
            Volatile.Write(ref sampledCurrent,0);
            player.EnqueueOrdered(owner,new[]{25.0,50,0},new[]{Route(1)});
            controls.Release();await oldStart;
            await Until(()=>link.Commands.Count>=3,"V2 dispatch survives sampled Start waiting at control gate");
            Check(link.Commands.ToArray().SequenceEqual(new[]{"Vibrate1:5;","Vibrate1:10;","Vibrate1:0;"}),"stale sampled Start cannot erase a newly admitted ordered batch");
            await player.Start(1,new PatternSpec { mode="Steady",high=19 },20,isCurrent:()=>false);
            Check(player.Active(1),"already-stale sampled request does not revoke ordered ownership");
            owner.Close();await player.CancelOrdered(owner);
        }
        {
            var link=new FakeLink();var player=new Playback(link,new[]{1});var router=new FeelOrderedRouter();
            router.Publish(new[]{new FeelOrderedTarget(player,Route(1))});router.Submit(new[]{100.0});router.Enable();
            await Task.Delay(40);Check(link.Commands.IsEmpty,"arming never replays batches received while disarmed");
            router.Submit(new[]{25.0,0});await Until(()=>link.Commands.Count>=2,"fresh post-enable batch works");
            using(var gate=link.BlockReadiness())
            {
                router.Submit(new[]{100.0,0});var stop=router.Reset(true);router.Submit(new[]{100.0});gate.Dispose();await stop;
            }
            await Task.Delay(40);Check(!link.Commands.Contains("Vibrate1:20;"),"disarm invalidates waiting samples and rejects fresh admission");
        }
    }
    sealed class FakeLink : ICommandLink,ILatestCommandLink
    {
        readonly SemaphoreSlim gate=new(1,1);
        TaskCompletionSource nextAck;
        public readonly ConcurrentQueue<string> Commands=new();
        public IDisposable BlockReadiness() { gate.Wait();return new Release(gate); }
        public TaskCompletionSource BlockNextAck() { var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);nextAck=ack;return ack; }
        public Task<string> Command(string command)=>Command(command,CancellationToken.None);
        public Task<string> Command(string command,CancellationToken cancel)=>CommandLatest(()=>command,cancel);
        public async Task<string> CommandLatest(Func<string> sample,CancellationToken cancel)
        {
            await gate.WaitAsync(cancel);
            try
            {
                cancel.ThrowIfCancellationRequested();var command=sample();if(command==null)return "";
                Commands.Enqueue(command);var ack=Interlocked.Exchange(ref nextAck,null);
                // Match BleLink: once written, await its ACK before releasing gate,
                // even if cancellation arrives after submission.
                if(ack!=null)await ack.Task;
                return "OK";
            }
            finally { gate.Release(); }
        }
        sealed class Release(SemaphoreSlim value) : IDisposable
        { SemaphoreSlim semaphore=value;public void Dispose()=>Interlocked.Exchange(ref semaphore,null)?.Release(); }
    }
}
