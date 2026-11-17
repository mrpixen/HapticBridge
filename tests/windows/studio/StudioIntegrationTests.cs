using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using LovenseConnector;

// Real ConnectorApp Studio controls and Playback, with fake command links only.
// No normal application startup, profile reads/writes, Bluetooth, API listener,
// audio capture, network, file dialogs or visible window are used.
internal static class StudioIntegrationTests
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int assertions;
    [STAThread]
    static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Task work = Run(); var frame = new DispatcherFrame();
        _ = work.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        try { work.GetAwaiter().GetResult(); Console.WriteLine("Studio integration: " + assertions + " assertions passed (offline, no visible window or hardware)."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void Check(bool condition, string label)
    { assertions++; if (!condition) throw new Exception("Studio integration: " + label); }
    static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, Members).GetValue(value);
    static void Set(object value, string name, object field) => value.GetType().GetField(name, Members).SetValue(value, field);
    static object Call(object value, string method, params object[] args)
    {
        try { return value.GetType().GetMethod(method, Members).Invoke(value, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    static Task Invoke(object value, string method, params object[] args) => (Task)Call(value, method, args);
    static async Task Until(Func<bool> predicate, string label, int milliseconds = 2500)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.ElapsedMilliseconds > milliseconds) throw new Exception("Studio timeout: " + label); await Task.Delay(10); }
        Check(true, label);
    }
    static async Task Run()
    {
        await ThreeMotorsAndIndependentSelection();
        await DynamicJoinUsesSharedDeadline();
        await DelayedJoinCannotOutliveDeadline();
        await PatternJoinUsesCurrentPhase();
        await StopSupersedesPendingStart();
        await EditedPatternCannotStartOldSnapshot();
        await ReselectSurvivesEarlierStopFailure();
        await ReconnectedMotorRequiresExplicitSelection();
        await DisconnectStopsStudioGroup();
        await StopDoesNotCancelDisconnect();
        await DisconnectDoesNotReleaseReplacement();
        await ManualActionRevokesStudio();
        await DeviceSliderCannotRetargetStudio();
        await ImportIsAtomic();
    }

    static async Task ThreeMotorsAndIndependentSelection()
    {
        await using var fixture = new Fixture();
        int[] levels = { 4, 11, 18 };
        for (int i = 0; i < levels.Length; i++) fixture.Rows[i].Level.Value = levels[i];
        await fixture.Start();
        await Until(() => fixture.Levels().SequenceEqual(levels), "three selected motors reach their independent live levels");
        var first = fixture.Commands().Where(c => c.Nonzero).GroupBy(c => c.Motor).Select(g => g.First().At).ToArray();
        Check(first.Length == 3 && first.Max() - first.Min() < 200, "three-motor start shares one time origin without serial dwell");
        Check(Get<bool>(fixture.App, "studioRunning"), "Studio owns the running set");
        int marker = fixture.Commands().Length;
        fixture.Rows[1].Include.IsChecked = false;
        await Until(() => fixture.Levels()[1] == 0 && !fixture.Rows[1].Device.Player.Active(fixture.Rows[1].Channel), "deselect stops only that motor");
        await Task.Delay(220);
        Check(fixture.Levels()[0] == 4 && fixture.Levels()[2] == 18, "other motors continue when one is deselected");
        Check(!fixture.Commands().Skip(marker).Any(c => !c.Nonzero && (c.Motor == "dual/1" || c.Motor == "single/0")), "deselect does not send zero to unrelated motors");
        fixture.Rows[1].Include.IsChecked = true;
        await Until(() => fixture.Levels().SequenceEqual(levels), "reselected motor rejoins the running set");
        fixture.Rows[0].Level.Value = 7;
        await Until(() => fixture.Levels()[0] == 7, "live lane adjustment reaches its motor");
        Check(fixture.Levels()[1] == 11 && fixture.Levels()[2] == 18, "live adjustment preserves other lane levels");
        await fixture.Stop();
        Check(fixture.Levels().All(x => x == 0) && !Get<bool>(fixture.App, "studioRunning"), "global Stop releases Studio and zeros all motors");
        Check(!fixture.Window.IsVisible, "real shell remains invisible throughout testing");
    }

    static async Task DynamicJoinUsesSharedDeadline()
    {
        await using var fixture = new Fixture();
        fixture.Duration.Text = "900"; fixture.Rows[2].Include.IsChecked = false;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] > 0, "initial finite live motor starts");
        var clock = Get<Stopwatch>(fixture.App, "studioClock");
        while (clock.ElapsedMilliseconds < 350) await Task.Delay(10);
        fixture.Rows[2].Include.IsChecked = true;
        await Until(() => fixture.Levels()[2] > 0, "late selected motor joins before the shared end");
        while (clock.ElapsedMilliseconds < 1100) await Task.Delay(10);
        Check(fixture.Levels().All(x => x == 0), "late join ends at original shared deadline rather than a new duration");
        Call(fixture.App, "RefreshStudioPlaybackState");
        Check(!Get<bool>(fixture.App, "studioRunning"), "finished finite run releases ownership");
    }

    static async Task DelayedJoinCannotOutliveDeadline()
    {
        await using var fixture = new Fixture();
        fixture.Duration.Text = "700"; fixture.Rows[2].Include.IsChecked = false;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] > 0, "finite run is active before delayed join");
        var clock = Get<Stopwatch>(fixture.App, "studioClock");
        fixture.Single.HoldNextLatest = true;
        fixture.Rows[2].Include.IsChecked = true;
        await fixture.Single.LatestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        while (clock.ElapsedMilliseconds < 850) await Task.Delay(10);
        int marker = fixture.Single.Snapshot().Length;
        fixture.Single.ReleaseLatest.TrySetResult();
        await Until(() => !fixture.Rows[2].Device.Player.Active(0), "expired delayed join shuts down after command gate opens");
        Check(!fixture.Single.Snapshot().Skip(marker).Any(command => command.Nonzero), "queued late join never sends a nonzero level past shared deadline");
    }

    static async Task StopSupersedesPendingStart()
    {
        await using var fixture = new Fixture();
        fixture.Dual.HoldNextPresetStop = true;
        Task start = fixture.Start();
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stop = fixture.Stop();
        int marker = fixture.Commands().Length;
        fixture.Dual.ReleasePreset.TrySetResult();
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(220);
        Check(!fixture.Commands().Skip(marker).Any(c => c.Nonzero), "Stop during pending start prevents all later nonzero commands");
        Check(fixture.Levels().All(x => x == 0) && !Get<bool>(fixture.App, "studioRunning"), "superseded start cannot reclaim Studio ownership");
    }

    static async Task PatternJoinUsesCurrentPhase()
    {
        await using var fixture = new Fixture();
        var document = Get<CurveDocument>(fixture.App, "studioDocument");
        document.SetLength(1000); document.SetLoop(false);
        foreach (var lane in document.Lanes)
            document.ReplaceCurve(lane.Id, new[] { new CurvePoint(0, 4), new CurvePoint(400, 16), new CurvePoint(1000, 0) }, CurveInterpolation.Step);
        Call(fixture.App, "SetStudioMode", true); fixture.Rows[2].Include.IsChecked = false;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] == 4 && fixture.Levels()[1] == 4, "selected pattern lanes start together at their first cue");
        var clock = Get<Stopwatch>(fixture.App, "studioClock");
        while (clock.ElapsedMilliseconds < 550) await Task.Delay(10);
        int marker = fixture.Single.Snapshot().Length;
        fixture.Rows[2].Include.IsChecked = true;
        await Until(() => fixture.Levels().All(level => level == 16), "late pattern lane joins the current shared phase");
        Check(!fixture.Single.Snapshot().Skip(marker).Any(command => command.Level == 4), "late pattern lane does not replay the pattern beginning");
        while (clock.ElapsedMilliseconds < 1200) await Task.Delay(10);
        Check(fixture.Levels().All(level => level == 0), "one-shot pattern stops all lanes at its original end");
    }

    static async Task EditedPatternCannotStartOldSnapshot()
    {
        await using var fixture = new Fixture();
        var document = Get<CurveDocument>(fixture.App, "studioDocument");
        foreach (var lane in document.Lanes)
            document.ReplaceCurve(lane.Id, new[] { new CurvePoint(0, 12), new CurvePoint(document.LengthMs, 12) });
        Call(fixture.App, "SetStudioMode", true);
        fixture.Dual.HoldNextPresetStop = true;
        Task start = fixture.Start();
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        document.ReplaceCurve(document.Lanes[0].Id, new[] { new CurvePoint(0, 3), new CurvePoint(document.LengthMs, 3) });
        fixture.Dual.ReleasePreset.TrySetResult(); await start;
        await Task.Delay(150);
        Check(!fixture.Commands().Any(command => command.Nonzero), "editing during pending start prevents playback of the old curve snapshot");
        Check(!Get<bool>(fixture.App, "studioRunning"), "superseded pattern start stays stopped");
        await fixture.Start();
        await Until(() => fixture.Levels()[0] == 3 && fixture.Levels()[1] == 12, "explicit restart uses edited curve with independent other lanes");
        document.ReplaceCurve(document.Lanes[0].Id, new[] { new CurvePoint(0, 5), new CurvePoint(document.LengthMs, 5) });
        await Until(() => fixture.Levels().All(level => level == 0), "editing a running pattern stops its owned motors");
    }

    static async Task ReselectSurvivesEarlierStopFailure()
    {
        await using var fixture = new Fixture(); await fixture.Start();
        await Until(() => fixture.Levels().All(level => level == 12), "Studio is active before a delayed deselect");
        int earlierStarts = fixture.Dual.Snapshot().Count(command => command.Motor == "dual/1" && command.Nonzero);
        fixture.Dual.HoldAndFailNextLaneStop = true;
        fixture.Rows[0].Include.IsChecked = false;
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Rows[0].Include.IsChecked = true;
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        await Until(() => fixture.Levels().All(level => level == 12) &&
            fixture.Dual.Snapshot().Count(command => command.Motor == "dual/1" && command.Nonzero) > earlierStarts,
            "reselected lane starts after the older stop failure resolves");
        await Task.Delay(150);
        Check(fixture.Levels().All(level => level == 12) && Get<bool>(fixture.App, "studioRunning"), "superseded deselect failure does not stop the newer selection or unrelated lanes");
    }

    static async Task ManualActionRevokesStudio()
    {
        await using var fixture = new Fixture(); await fixture.Start();
        await Until(() => fixture.Levels().All(level => level > 0), "Studio is active before manual takeover");
        var row = fixture.Rows[0];
        Func<Task> action = () => row.Device.Player.Start(row.Channel,
            new PatternSpec { mode = "Steady", high = 8, durationMs = 0, transitionMs = 0 }, 20);
        await Invoke(fixture.App, "Manual", action);
        await Until(() => fixture.Levels().SequenceEqual(new[] { 8, 0, 0 }), "manual action takes one motor and stops the previous Studio group");
        Check(!Get<bool>(fixture.App, "studioRunning"), "manual device action revokes Studio ownership");
        fixture.Rows[0].Level.Value = 19;
        fixture.Rows[1].Include.IsChecked = false; fixture.Rows[1].Include.IsChecked = true;
        await Task.Delay(220);
        Check(fixture.Levels().SequenceEqual(new[] { 8, 0, 0 }), "Studio edits and target changes cannot override the newer manual source");
    }

    static async Task ReconnectedMotorRequiresExplicitSelection()
    {
        await using var fixture = new Fixture(); await fixture.Start();
        await Until(() => fixture.Levels().All(level => level > 0), "Studio is active before simulated transport loss");
        var row = fixture.Rows[2]; var previous = row.Device.Player;
        // Invoke the real transport-loss handler without touching a BLE adapter.
        Call(fixture.App, "Lost", row.Device);
        var owned = Get<Dictionary<string, Tuple<Playback, int>>>(fixture.App, "studioOwned");
        Check(row.Include.IsChecked == false && owned.Values.All(item => !ReferenceEquals(item.Item1, previous)), "disconnected motor is deselected and loses stale Studio ownership");
        var link = new FakeLink("reconnected"); row.Device.Player = new Playback(link, new[] { 0 }); row.Device.Connected = true;
        Call(fixture.App, "RefreshStudioMotors"); await Task.Delay(150);
        Check(!link.Snapshot().Any(command => command.Nonzero) && row.Include.IsChecked == false, "reconnected motor cannot automatically resume old Studio output");
        Check(fixture.Levels()[0] > 0 && fixture.Levels()[1] > 0, "unrelated motors continue through another device reconnect");
        row.Include.IsChecked = true;
        await Until(() => link.Snapshot().Any(command => command.Nonzero), "explicit selection joins the replacement player");
        Check(owned.Values.Any(item => ReferenceEquals(item.Item1, row.Device.Player)), "replacement player receives current Studio ownership");
    }

    static async Task DisconnectStopsStudioGroup()
    {
        await using var fixture = new Fixture(); await fixture.Start();
        await Until(() => fixture.Levels().All(level => level > 0), "all three motors are active before explicit device disconnect");
        var disconnected = fixture.Rows[2].Device; var previous = disconnected.Player;
        await Invoke(fixture.App, "Disconnect", disconnected);
        Check(!disconnected.Connected && disconnected.Player == null && previous.Level(0) == 0, "actual Disconnect confirms stop and releases its device");
        Check(fixture.Levels().All(level => level == 0) && !Get<bool>(fixture.App, "studioRunning"), "disconnecting one device also stops the other Studio-owned motors");
        Check(Get<Dictionary<string, Tuple<Playback, int>>>(fixture.App, "studioOwned").Count == 0, "explicit disconnect leaves no orphaned Studio ownership");
        int marker = fixture.Commands().Length; await Task.Delay(220);
        Check(!fixture.Commands().Skip(marker).Any(command => command.Nonzero), "old Studio output cannot restart after disconnect");
        var retained = fixture.Rows[0];
        await Invoke(fixture.App, "Manual", new Func<Task>(() => retained.Device.Player.Start(retained.Channel,
            new PatternSpec { mode = "Steady", high = 7, durationMs = 0, transitionMs = 0 }, 20)));
        await Until(() => fixture.Levels().SequenceEqual(new[] { 7, 0, 0 }), "manual takeover after disconnect controls only the requested remaining motor");
    }

    static async Task DeviceSliderCannotRetargetStudio()
    {
        await using var fixture = new Fixture();
        var row = fixture.Rows[0];
        Call(fixture.App, "BuildControls", row.Device);
        row.Level.Value = 7;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] == 7, "Studio runs before changing an advanced device slider");
        int marker = fixture.Commands().Length;
        row.Device.Motors[row.Channel].Level.Value = 19;
        await Task.Delay(250);
        Check(fixture.Levels()[0] == 7 && row.Level.Value == 7 && Get<bool>(fixture.App, "studioRunning"),
            "advanced device slider does not silently retarget a Studio-owned motor");
        Check(!fixture.Commands().Skip(marker).Any(command => command.Motor == "dual/1" && command.Level == 19),
            "editing an inactive surface sends no conflicting level to the Studio motor");
        await Invoke(fixture.App, "Manual", new Func<Task>(() => row.Device.Player.Start(row.Channel,
            new PatternSpec { mode = "Steady", high = (int)row.Device.Motors[row.Channel].Level.Value, durationMs = 0 }, 20)));
        await Until(() => fixture.Levels().SequenceEqual(new[] { 19, 0, 0 }),
            "explicit device Start still takes ownership using the prepared slider value");
        Check(!Get<bool>(fixture.App, "studioRunning"), "explicit device Start releases Studio ownership");
    }

    static async Task StopDoesNotCancelDisconnect()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await Until(() => fixture.Levels().All(level => level > 0), "Studio runs before disconnect and global-stop race");
        fixture.Dual.HoldNextLaneStop = true;
        var device = fixture.Rows[2].Device;
        Task disconnect = Invoke(fixture.App, "Disconnect", device);
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stop = fixture.Stop();
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        await Task.WhenAll(disconnect, stop).WaitAsync(TimeSpan.FromSeconds(3));
        Check(!device.Connected && device.Player == null,
            "global Stop during an awaited disconnect does not cancel the requested disconnect");
        Check(fixture.Levels().All(level => level == 0), "disconnect and global Stop leave all motors stopped");
    }

    static async Task DisconnectDoesNotReleaseReplacement()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await Until(() => fixture.Levels().All(level => level > 0), "Studio runs before replacement-connection race");
        fixture.Dual.HoldNextLaneStop = true;
        var device = fixture.Rows[2].Device;
        Task disconnect = Invoke(fixture.App, "Disconnect", device);
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = new Playback(new FakeLink("replacement"), new[] { 0 });
        device.Generation++; device.Player = replacement; device.Connected = true;
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
        Check(device.Connected && ReferenceEquals(device.Player, replacement),
            "pending disconnect cannot release a newer connection generation");
    }

    static async Task ImportIsAtomic()
    {
        await using var fixture = new Fixture();
        var document = Get<CurveDocument>(fixture.App, "studioDocument");
        var keys = fixture.Rows.Select(row => row.Device.Address.ToString("X12") + "/" + row.Channel).ToArray();
        foreach (string key in keys) document.ReplaceCurve(key, new[] { new CurvePoint(0, 0), new CurvePoint(100, 10), new CurvePoint(200, 4), new CurvePoint(6000, 0) });
        document.LinkCurves(keys[0], new[] { keys[0], keys[1] });
        fixture.Rows[1].Include.IsChecked = false; fixture.Rows[2].Level.Value = 16;
        string original = fixture.ImportSnapshot();
        var invalid = Preset(); invalid.lanes[2].points = new[] { new LovenseConnector.Frame { ms = 0, level = 0 }, new LovenseConnector.Frame { ms = 50, level = 21 } };
        bool rejected = false; try { Call(fixture.App, "ApplyStudioMix", invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected && fixture.ImportSnapshot() == original, "invalid final import lane leaves document, links, selections and levels untouched");

        bool failOnce = true;
        RoutedPropertyChangedEventHandler<double> failure = (s, e) => { if (failOnce) { failOnce = false; throw new InvalidOperationException("Synthetic UI update failure"); } };
        fixture.Rows[0].Level.ValueChanged += failure;
        rejected = false;
        try { Call(fixture.App, "ApplyStudioMix", Preset()); } catch (InvalidOperationException ex) { rejected = ex.Message == "Synthetic UI update failure"; }
        finally { fixture.Rows[0].Level.ValueChanged -= failure; }
        Check(rejected && fixture.ImportSnapshot() == original && !document.IsEditing, "mid-import UI failure atomically restores document and motor controls");
        Check(!fixture.Commands().Any(c => c.Nonzero), "failed imports never start output");

        Call(fixture.App, "ApplyStudioMix", Preset());
        Check(document.LengthMs == 300 && document.Lanes.All(lane => lane.Points.Count == 2), "valid short import replaces dense old curves without rescaling collisions");
        Check(fixture.Levels().All(x => x == 0) && !Get<bool>(fixture.App, "studioRunning"), "successful import stays stopped");
        Check(fixture.Rows.Select(row => (int)row.Level.Value).SequenceEqual(new[] { 9, 10, 11 }), "successful import applies independent lane levels");
        Check(fixture.Rows.Select(row => row.Include.IsChecked == true).SequenceEqual(new[] { true, false, true }), "successful import applies target selection");
    }
    static StudioPreset Preset() => new() { lengthMs = 300, loop = false, durationMs = 500, transitionMs = 0,
        lanes = Enumerable.Range(0, 3).Select(i => new StudioLanePreset { level = 9 + i, selected = i != 1, group = i, curve = "linear",
            points = new[] { new LovenseConnector.Frame { ms = 0, level = i + 1 }, new LovenseConnector.Frame { ms = 300, level = 0 } } }).ToArray() };

    sealed class Fixture : IAsyncDisposable
    {
        public ConnectorApp App { get; }
        public Window Window { get; }
        public FakeLink Dual { get; } = new("dual");
        public FakeLink Single { get; } = new("single");
        public MixerMotor[] Rows { get; }
        public TextBox Duration => Get<TextBox>(App, "mixerDuration");
        public Fixture()
        {
            // Avoid normal field initializers, which resolve the user's actual data directory.
            App = (ConnectorApp)RuntimeHelpers.GetUninitializedObject(typeof(ConnectorApp));
            foreach (var field in typeof(ConnectorApp).GetFields(Members))
                if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    field.SetValue(App, Activator.CreateInstance(field.FieldType));
            using (var stream = AssetFiles.Open("MainWindow.xaml")) Window = (Window)XamlReader.Load(stream);
            Set(App, "window", Window); Set(App, "tabs", (TabControl)Window.FindName("DeviceTabs"));
            Set(App, "hint", (TextBlock)Window.FindName("Hint")); Set(App, "summary", (TextBlock)Window.FindName("Summary"));
            Set(App, "activity", (TextBox)Window.FindName("ActivityLog")); Set(App, "sweepButton", (Button)Window.FindName("SweepButton"));
            Set(App, "feelWriteGate", new SemaphoreSlim(1, 1));
            var devices = Get<Dictionary<ulong, Device>>(App, "devices");
            AddDevice(1, "Synthetic dual actuator", Dual, new[] { 1, 2 });
            AddDevice(2, "Synthetic single actuator", Single, new[] { 0 });
            Call(App, "InitializeStudioControls"); Call(App, "RefreshStudioMotors");
            Rows = Get<Dictionary<string, MixerMotor>>(App, "mixerRows").Values.ToArray();
            Get<TextBox>(App, "mixerTransition").Text = "0";
            void AddDevice(ulong address, string name, FakeLink link, int[] channels)
            {
                var device = new Device { Address = address, Name = name, Connected = true, Supported = true, Channels = channels, Player = new Playback(link, channels),
                    Tab = new TabItem(), State = new TextBlock(), Details = new TextBlock(), ConnectButton = new Button(), Controls = new StackPanel() };
                foreach (int channel in channels) device.Motors[channel] = new MotorView { Channel = channel, Output = new TextBlock() };
                devices.Add(address, device);
            }
        }
        public Task Start() => Invoke(App, "Manual", new Func<Task>(() => Invoke(App, "StartStudio")));
        public Task Stop() => Invoke(App, "StopAll");
        public int[] Levels() => Rows.Select(row => row.Device.Player?.Level(row.Channel) ?? 0).ToArray();
        public Command[] Commands() => Dual.Snapshot().Concat(Single.Snapshot()).OrderBy(command => command.At).ToArray();
        public string ImportSnapshot()
        {
            var document = Get<CurveDocument>(App, "studioDocument");
            return JsonSerializer.Serialize(new { document.LengthMs, document.Loop,
                lanes = document.Lanes.Select(lane => new { lane.Id, lane.GroupId, lane.Interpolation, points = lane.Points.Select(point => new { point.Ms, point.Level }) }),
                rows = Rows.Select(row => new { level = row.Level.Value, selected = row.Include.IsChecked }),
                duration = Duration.Text, transition = Get<TextBox>(App, "mixerTransition").Text });
        }
        public async ValueTask DisposeAsync()
        {
            Dual.ReleasePreset.TrySetResult(); Dual.ReleaseLatest.TrySetResult(); Dual.ReleaseLaneStop.TrySetResult();
            Single.ReleasePreset.TrySetResult(); Single.ReleaseLatest.TrySetResult(); Single.ReleaseLaneStop.TrySetResult();
            await Stop(); Get<SemaphoreSlim>(App, "feelWriteGate").Dispose();
            Check(!Window.IsVisible, "fixture cleanup never displayed a window"); Window.Close();
        }
    }

    sealed record Command(string Motor, int Level, long At) { public bool Nonzero => Level > 0; }
    sealed class FakeLink : ICommandLink, ILatestCommandLink
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        readonly string device;
        readonly ConcurrentQueue<Command> commands = new();
        public bool HoldNextPresetStop, HoldNextLatest, HoldAndFailNextLaneStop, HoldNextLaneStop;
        public readonly TaskCompletionSource PresetEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasePreset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LatestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLatest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LaneStopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLaneStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeLink(string device) { this.device = device; }
        public Command[] Snapshot() => commands.ToArray();
        public Task<string> Command(string value) => Command(value, CancellationToken.None);
        public async Task<string> Command(string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value == "Preset:0;" && HoldNextPresetStop)
            { HoldNextPresetStop = false; PresetEntered.TrySetResult(); await ReleasePreset.Task.WaitAsync(token); }
            if (value == "Vibrate1:0;" && HoldAndFailNextLaneStop)
            { HoldAndFailNextLaneStop = false; LaneStopEntered.TrySetResult(); await ReleaseLaneStop.Task.WaitAsync(token); throw new InvalidOperationException("Synthetic lane stop failure"); }
            if (value == "Vibrate1:0;" && HoldNextLaneStop)
            { HoldNextLaneStop = false; LaneStopEntered.TrySetResult(); await ReleaseLaneStop.Task.WaitAsync(token); }
            Record(value); return "OK";
        }
        public async Task<string> CommandLatest(Func<string> sample, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (HoldNextLatest) { HoldNextLatest = false; LatestEntered.TrySetResult(); await ReleaseLatest.Task.WaitAsync(token); }
            token.ThrowIfCancellationRequested(); string value = sample(); if (value == null) return "";
            Record(value); return "OK";
        }
        void Record(string value)
        {
            if (!value.StartsWith("Vibrate", StringComparison.Ordinal)) return;
            int colon = value.IndexOf(':'); string motor = value.Substring(7, colon - 7);
            int level = int.Parse(value.Substring(colon + 1).TrimEnd(';'));
            if (motor.Length == 0 && device == "dual")
            { commands.Enqueue(new Command(device + "/1", level, Clock.ElapsedMilliseconds)); commands.Enqueue(new Command(device + "/2", level, Clock.ElapsedMilliseconds)); }
            else commands.Enqueue(new Command(device + "/" + (motor.Length == 0 ? "0" : motor), level, Clock.ElapsedMilliseconds));
        }
    }
}
