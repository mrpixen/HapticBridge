using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LovenseConnector;

// Actual production client, UI handlers, route tick and playback; all I/O is fake.
// The fixture was emitted by modules 20/22/23 of the official public legacy SDK:
// https://api.feel-app.com/static/feeljs/1.2.1/feel.min.js
// Its transport, clock, timeline and identities were synthetic. No SDK code is copied here.
internal static class VideoPipelineReviewTests
{
    static int assertions;
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [STAThread]
    static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = Run();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { task.GetAwaiter().GetResult(); Console.WriteLine("Video pipeline: " + assertions + " assertions passed (offline, no visible window or hardware)."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Check(bool value, string label)
    { assertions++; if (!value) throw new Exception("Video pipeline: " + label); }
    static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, Fields).GetValue(value);
    static void Set(object value, string name, object field) => value.GetType().GetField(name, Fields).SetValue(value, field);
    static object Call(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Fields).Invoke(value, arguments);
    static Task Invoke(object value, string name) => (Task)Call(value, name);
    static async Task Until(Func<bool> predicate, string label, int limit = 2500)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.ElapsedMilliseconds > limit) throw new Exception("Timed out: " + label); await Task.Delay(10); }
        Check(true, label);
    }

    static async Task Run()
    {
        using var fixtureJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sdk-v3-packets.json")));
        var packets = fixtureJson.RootElement.GetProperty("packets");
        var sdkPlay = packets[0].GetProperty("message");
        var sdkStop = packets[1].GetProperty("message");
        string videoRoom = packets[0].GetProperty("channel").GetString();
        Check(sdkPlay.GetProperty("type").GetString() == "play" && sdkPlay.GetProperty("subtitles").GetArrayLength() > 1, "actual SDK fixture contains a nonempty v3 timeline");
        Check(sdkStop.GetProperty("type").GetString() == "stop", "actual SDK pause fixture is present");

        // Reuse the existing isolated transport fixture without widening its production visibility.
        var fixtureType = typeof(FeelConnectTests).GetNestedType("ServiceFixture", BindingFlags.NonPublic);
        using var fixture = (HttpMessageHandler)Activator.CreateInstance(fixtureType, true);
        Set(fixture, "VideoRoom", videoRoom);
        var connect = (Func<CancellationToken, Task<WebSocket>>)fixtureType.GetMethod("Connect").CreateDelegate(typeof(Func<CancellationToken, Task<WebSocket>>), fixture);
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, connect, enableLegacy: true);
        var motors = new SyntheticMotors();
        var player = new Playback(motors, new[] { 1, 2 });
        var device = new Device { Address = 1, Name = "Synthetic actuator", Connected = true, Player = player, Channels = new[] { 1, 2 } };
        foreach (int channel in device.Channels) device.Motors[channel] = new MotorView { Channel = channel };

        // Skip all field initializers: the normal app constructor resolves its data directory.
        // Do not call Main/Run, initialize the API or scanner, or show this WPF window.
        var app = (ConnectorApp)RuntimeHelpers.GetUninitializedObject(typeof(ConnectorApp));
        var window = new Window();
        var tabs = new TabControl(); tabs.Items.Add(new TabItem()); tabs.Items.Add(new TabItem());
        Set(app, "window", window); Set(app, "tabs", tabs); Set(app, "hint", new TextBlock());
        Set(app, "devices", new Dictionary<ulong, Device> { [device.Address] = device });
        Set(app, "feelClient", client); Set(app, "feelRoutes", new Dictionary<string, FeelRoute>());
        Set(app, "feelOwned", new Dictionary<string, Tuple<Playback, int>>()); Set(app, "feelWriteGate", new SemaphoreSlim(1, 1));
        Call(app, "InitializeFeelConnect"); Get<DispatcherTimer>(app, "feelTimer").Stop();
        Check(Get<TextBlock>(app, "feelValue").Text == "—", "unpaired monitor does not fabricate a zero signal");
        Check(Get<TextBlock>(app, "feelOutputState").Text == "Output off", "output state is independent of pairing and signal");
        Check(!Get<Button>(app, "feelConnectButton").IsEnabled && !Get<Button>(app, "feelDisconnectButton").IsEnabled,
            "empty unpaired state does not offer unavailable connect or disconnect actions");
        Get<PasswordBox>(app, "feelPairing").Password = "synthetic-ready-code";
        Check(Get<Button>(app, "feelConnectButton").IsEnabled, "entering a pairing value enables the deliberate Connect action");
        Get<PasswordBox>(app, "feelPairing").Clear();
        Call(app, "RefreshFeelRoutes");
        var routes = Get<Dictionary<string, FeelRoute>>(app, "feelRoutes").Values.OrderBy(r => r.Channel).ToArray();
        Check(routes.Length == 2 && routes.All(r => r.Enabled.IsChecked == true && r.Gain.Value == 100), "actual UI creates enabled routes at default gain");
        Check(!window.IsVisible, "test never shows an application window");

        async Task Push(JsonElement packet)
        {
            long accepted = client.Diagnostics.AcceptedCues;
            var message = JsonNode.Parse(packet.GetRawText()).AsObject();
            // Preserve all SDK cue data; translate only its artificial epoch to the real test clock.
            if (message.ContainsKey("serverTime")) message["serverTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Call(fixture, "LegacyPush", videoRoom, message.ToJsonString());
            await Until(() => client.Diagnostics.AcceptedCues > accepted, "injected SDK control is accepted by the actual client");
        }
        async Task PumpFor(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds) { await Invoke(app, "FeelTick"); await Task.Delay(40); }
        }

        try
        {
            await client.PairAsync("synthetic-pipeline-token", new[] { device.Name });
            await Until(() => Get<bool>(app, "feelConnected"), "actual status handler marks the simulated session connected");
            Check(!Get<bool>(app, "feelArmed"), "pairing alone stays disarmed");
            Check(Get<Button>(app, "feelDisconnectButton").IsEnabled && !Get<Button>(app, "feelConnectButton").IsEnabled,
                "paired session offers disconnect without an empty Connect action");
            Check(Get<TextBlock>(app, "feelValue").Text == "—", "paired session without cues keeps an unknown signal");
            await Push(sdkPlay);
            await Until(() => Get<double>(app, "feelPercent") == 25, "legacy SDK play reaches the actual UI at 25 percent");
            Check(Get<TextBlock>(app, "feelValue").Text == "25%", "received signal displays its decoded percentage while disarmed");
            Check(Get<TextBlock>(app, "feelOutputState").Text == "Output off", "receiving a positive signal does not imply output is enabled");
            await Invoke(app, "FeelTick");
            Check(!motors.Snapshot().Any(IsNonzero), "disarmed receiver never sends a nonzero command");

            routes[1].Gain.Value = 200;
            await Invoke(app, "ToggleFeelOutput");
            Check(Get<bool>(app, "feelArmed"), "actual arm action succeeds after stopping other output owners");
            await PumpFor(240);
            Check(motors.Snapshot().Contains("Vibrate1:5;"), "25 percent at default gain reaches native level 5");
            Check(motors.Snapshot().Contains("Vibrate2:10;"), "independent 200 percent gain reaches native level 10");
            await PumpFor(2100);
            Check(motors.Snapshot().Contains("Vibrate1:15;"), "later SDK subtitle 3 reaches native level 15");
            Check(motors.Snapshot().Contains("Vibrate1:20;") && motors.Snapshot().Contains("Vibrate2:20;"), "SDK subtitle 4 reaches full native intensity with gain saturation");
            Check(motors.Snapshot().Contains("Vibrate1:0;"), "queued SDK zero cues stop active output");

            await Push(sdkStop);
            await Until(() => Get<double>(app, "feelPercent") == 0 && player.Level(1) == 0 && player.Level(2) == 0, "SDK pause stops both routed motors");
            Check(Get<TextBlock>(app, "feelValue").Text == "0%", "received stop is a known zero rather than an absent signal");
            int afterPause = motors.Snapshot().Length;
            await PumpFor(650);
            Check(!motors.Snapshot().Skip(afterPause).Any(IsNonzero), "cancelled future cues do not restart after pause");

            routes[0].Enabled.IsChecked = false;
            routes[1].Gain.Value = 0;
            await Push(sdkPlay);
            await Until(() => Get<double>(app, "feelPercent") == 25, "second play remains decodable");
            int muted = motors.Snapshot().Length;
            await PumpFor(220);
            Check(!motors.Snapshot().Skip(muted).Any(IsNonzero), "unchecked route and zero gain both suppress output");
            routes[0].Enabled.IsChecked = true; routes[1].Gain.Value = 100;
            await Invoke(app, "DisarmFeelConnect");
            Check(Get<TextBlock>(app, "feelOutputState").Text == "Output paused", "explicit disarm distinguishes paused output from incoming signal");
            await Push(sdkStop);
            await Until(() => client.CurrentPercent == 0, "stopped receiver has no stale current intensity");
            await Invoke(app, "ToggleFeelOutput");
            int rearmed = motors.Snapshot().Length;
            await PumpFor(200);
            Check(!motors.Snapshot().Skip(rearmed).Any(IsNonzero), "arming after pause cannot resurrect an old timeline");

            await Push(sdkPlay);
            await Until(() => Get<double>(app, "feelPercent") == 25, "new play after rearming is accepted");
            await PumpFor(200);
            Check(client.Diagnostics.AcceptedCues >= 5, "real client diagnostic acceptance tracks synthetic controls");
            Get<ComboBox>(app, "feelPlaybackMode").SelectedIndex = 1;
            await Invoke(app, "ApplyFeelPlaybackMode");
            await Until(() => Get<bool>(app, "feelConnected"), "mode reconnect returns to paired state");
            Check(client.Diagnostics.PlaybackMode == "live", "actual mode action selects live compatibility");
            Check(!Get<bool>(app, "feelArmed") && client.CurrentPercent == 0 && player.Level(1) == 0 && player.Level(2) == 0,
                "mode reconnect stops both motors and does not rearm or retain an old cue");
            Check(Get<ComboBox>(app, "feelPlaybackMode").IsEnabled && !Get<bool>(app, "feelModeBusy"), "mode controls recover after successful reconnect");
            Check(Get<TextBlock>(app, "feelValue").Text == "—", "mode reconnect clears the earlier signal display");

            using var liveCue = JsonDocument.Parse("{\"ver\":2,\"percents\":[35],\"src\":\"synthetic-browser\"}");
            await Push(liveCue.RootElement);
            await Until(() => Get<TextBlock>(app, "feelValue").Text == "35%", "live packet supplies a known signal without enabling output");
            int discarded = motors.Snapshot().Length;
            await Invoke(app, "ToggleFeelOutput");await PumpFor(150);
            Check(!motors.Snapshot().Skip(discarded).Any(IsNonzero), "enabling after a discarded V2 packet never replays its final percentage");
            routes[0].Gain.Value=100;routes[1].Gain.Value=200;
            foreach(string samples in new[]{"[25,0]","[0,100,0]","[25,50,0]"})
            {
                int baseline=motors.Snapshot().Length;
                using var batch=JsonDocument.Parse("{\"ver\":2,\"percents\":"+samples+",\"src\":\"synthetic-browser\"}");
                await Push(batch.RootElement);
                double[] values=batch.RootElement.GetProperty("percents").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
                await Until(()=>motors.Snapshot().Length>=baseline+values.Length*2,"actual input event sends every V2 value through both WPF routes");
                var writes=motors.Snapshot().Skip(baseline).ToArray();
                Check(writes.Where(c=>c.StartsWith("Vibrate1:")).SequenceEqual(values.Select(v=>"Vibrate1:"+(int)Math.Round(v/5)+";")),"actual V2 motor one sequence is preserved");
                Check(writes.Where(c=>c.StartsWith("Vibrate2:")).SequenceEqual(values.Select(v=>"Vibrate2:"+Math.Min(20,(int)Math.Round(v/2.5))+";")),"actual V2 motor two applies independent gain");
            }
            {
                int baseline=motors.Snapshot().Length;
                var protocol=new FeelConnectProtocol();protocol.SetRooms(new[]{"demo-room"});
                using var json=JsonDocument.Parse("{\"room\":\"demo-room\",\"data\":{\"ver\":2,\"percents\":[25,50,0]}}");
                var output=protocol.Accept(json.RootElement,100,1700000000000);
                // Deliberately hold this dispatcher: output admission and device
                // writes must not rely on its pending cosmetic update callback.
                var delivered=Task.Run(()=>Call(app,"ReceiveFeelOutput",output));
                Check(delivered.Wait(1000),"input callback does not wait for a busy WPF dispatcher");
                var limit=Stopwatch.StartNew();while(motors.Snapshot().Length<baseline+6&&limit.ElapsedMilliseconds<1000)Thread.Sleep(5);
                Check(motors.Snapshot().Length==baseline+6,"entire batch reaches writer while the WPF dispatcher is occupied");
            }
            {
                using var positive=JsonDocument.Parse("{\"ver\":2,\"percents\":[25],\"src\":\"synthetic-browser\"}");
                await Push(positive.RootElement);
                await Until(()=>Get<double>(app,"feelPercent")==25&&player.Level(1)==5,"positive final batch is displayed before Stop race");
                int baseline=motors.Snapshot().Length;
                var stopped=new FeelConnectOutput(0,"Cue timeout",true);
                Check(Task.Run(()=>Call(app,"ReceiveFeelOutput",stopped)).Wait(1000),"Stop ingress completes while WPF dispatcher is occupied");
                Check(Invoke(app,"FeelTick").Wait(1000),"stale positive UI state cannot enter scalar playback after Stop ingress");
                Thread.Sleep(30);
                Check(!motors.Snapshot().Skip(baseline).Any(IsNonzero),"batch timeout cannot resurrect its final scalar percentage");
                await Until(()=>Get<FeelConnectOutput>(app,"feelLatestOutput")==stopped,"Stop display catches up after blocked dispatcher");

                var scalar=new FeelConnectOutput(75,"Video cues v3",false);
                Call(app,"ReceiveFeelOutput",scalar);
                await Until(()=>Get<FeelConnectOutput>(app,"feelLatestOutput")==scalar,"scalar signal ready for delayed Start race");
                var controlGate=Get<SemaphoreSlim>(player,"controls");await controlGate.WaitAsync();
                baseline=motors.Snapshot().Length;
                var pending=Invoke(app,"FeelTick");
                Check(!pending.IsCompleted,"sampled Start is held behind control gate");
                Check(Task.Run(()=>Call(app,"ReceiveFeelOutput",new FeelConnectOutput(0,"Video stopped",true))).Wait(1000),"new stop invalidates input before delayed UI refresh");
                controlGate.Release();await pending;
                Check(!motors.Snapshot().Skip(baseline).Any(IsNonzero),"waiting sampled Start never revives scalar input older than Stop");
            }
            await Invoke(app,"DisarmFeelConnect");
            await Push(liveCue.RootElement);
            await Until(() => Get<FeelConnectOutput>(app, "feelLatestOutput")?.Kind == "Cue timeout", "production freshness timeout reaches the UI", 7500);
            Check(Get<TextBlock>(app, "feelValue").Text == "—", "timed-out signal is unknown rather than a fabricated zero cue");
            Check(Get<TextBlock>(app, "feelCount").Text == "Video updates timed out", "timeout has a specific recovery status");
            Check(!Get<bool>(app, "feelArmed"), "signal and timeout changes never rearm output");

            Set(fixture, "DelayAuth", true);
            Get<ComboBox>(app, "feelPlaybackMode").SelectedIndex = 0;
            var reconnect = Invoke(app, "ApplyFeelPlaybackMode");
            await Until(() => Get<bool>(app, "feelModeBusy") && client.Diagnostics.Phase == "Pairing", "delayed reconnect remains cancellable");
            Check(!Get<ComboBox>(app, "feelPlaybackMode").IsEnabled, "mode selection is disabled during reconnect");
            await Invoke(app, "DisconnectFeelConnect");
            await reconnect;
            await Until(() => !Get<bool>(app, "feelArmed") && player.Level(1) == 0 && player.Level(2) == 0, "disconnect disarms and zeros actual playback");
            Check(!Get<bool>(app, "feelConnected") && !client.Diagnostics.Connected, "disconnect supersedes an in-progress mode reconnect");
            Check(Get<TextBlock>(app, "feelValue").Text == "—" && Get<TextBlock>(app, "feelOutputState").Text == "Output off", "disconnected monitor clears both signal knowledge and output ownership");
            Check(Get<ComboBox>(app, "feelPlaybackMode").IsEnabled && !Get<bool>(app, "feelModeBusy"), "mode controls recover after cancelled reconnect");
        }
        finally { await Invoke(app, "DisarmFeelConnect"); await player.StopAll(); await client.DisconnectAsync(); }
    }

    static bool IsNonzero(string command) => command.StartsWith("Vibrate", StringComparison.Ordinal) && !command.EndsWith(":0;", StringComparison.Ordinal);
    sealed class SyntheticMotors : ICommandLink, ILatestCommandLink
    {
        readonly ConcurrentQueue<string> commands = new();
        public string[] Snapshot() => commands.ToArray();
        public Task<string> Command(string value) => Command(value, CancellationToken.None);
        public Task<string> Command(string value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); commands.Enqueue(value); return Task.FromResult("OK"); }
        public Task<string> CommandLatest(Func<string> sample, CancellationToken token)
        { token.ThrowIfCancellationRequested(); string value = sample(); if (value != null) commands.Enqueue(value); return Task.FromResult(value == null ? "" : "OK"); }
    }
}
