using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
// No normal application startup, profile reads/writes, Bluetooth, external
// network, audio capture, file dialogs or visible window are used. API cases
// use ephemeral loopback listeners with synthetic credentials only.
internal static partial class StudioIntegrationTests
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
        await ShortcutPreferencesAndConflicts();
        await ShortcutCancelsEachPendingStart();
        await TimedOutApiQueueCannotStartOutput();
        await CancelledApiHandoffCannotAcquireSession();
        await CancelledApiFrameStopsBeforeNextMotor();
        await CancelledApiStartCannotEscapePlaybackWaits();
        await AcceptedApiOutputKeepsItsLease();
        await ExportOrderMatchesImportSlots();
        await InvertedPatternRequiresRestart();
        await DraftRecoverySurvivesRestartAndFailures();
        await DuplicateCurvesStayIndependent();
        await ScreenSyncTests.Run();
        await AudioRoutingTests.Run();
        LegacyDraftTests.Run();
        await ThreeMotorsAndIndependentSelection();
        await DynamicJoinUsesSharedDeadline();
        await EditedDurationCannotExtendRunningSession();
        await InvalidDraftTimingCannotBreakRunningJoin();
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
        await DevicePatternCannotResumeAfterStop();
        await DevicePatternCannotReachReconnectedPlayer();
        await ImportIsAtomic();
        await UnassignedMixLanesSurviveRoundTrip();
        await NoDeviceDraftRoundTripAndPreview();
        await FailedStopCannotDisplayOutputOff();
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

    static async Task EditedDurationCannotExtendRunningSession()
    {
        await using var fixture = new Fixture();
        fixture.Duration.Text = "900"; fixture.Rows[2].Include.IsChecked = false;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] > 0, "finite session starts before editing its draft duration");
        var clock = Get<Stopwatch>(fixture.App, "studioClock");
        while (clock.ElapsedMilliseconds < 200) await Task.Delay(10);
        fixture.Duration.Text = "0";
        fixture.Rows[2].Level.Value = 7;
        fixture.Rows[2].Include.IsChecked = true;
        await Until(() => fixture.Levels()[2] == 7, "late motor joins with its current level after draft duration changes");
        Check(fixture.Levels()[0] == 12 && fixture.Levels()[1] == 12, "late motor's level does not retarget existing motors");
        while (clock.ElapsedMilliseconds < 1200) await Task.Delay(10);
        Check(fixture.Levels().All(level => level == 0) && fixture.Rows.All(row => !row.Device.Player.Active(row.Channel)),
            "editing duration to until-stopped cannot extend any motor beyond the captured finite session deadline");
        Call(fixture.App, "RefreshStudioPlaybackState");
        Check(!Get<bool>(fixture.App, "studioRunning"), "completed session releases ownership despite an until-stopped draft duration");
        Check(fixture.Duration.Text == "0", "the edited duration is retained for the next deliberate start");
    }

    static async Task InvalidDraftTimingCannotBreakRunningJoin()
    {
        await using var fixture = new Fixture();
        fixture.Duration.Text = "900"; fixture.Rows[2].Include.IsChecked = false;
        await fixture.Start();
        await Until(() => fixture.Levels()[0] > 0, "finite session starts before partially editing timing fields");
        var clock = Get<Stopwatch>(fixture.App, "studioClock");
        fixture.Duration.Text = "editing";
        Get<TextBox>(fixture.App, "mixerTransition").Text = "editing";
        fixture.Rows[2].Level.Value = 5;
        fixture.Rows[2].Include.IsChecked = true;
        await Until(() => fixture.Levels()[2] == 5, "late motor joins from captured session timing while draft fields are incomplete");
        Check(Get<bool>(fixture.App, "studioRunning") && fixture.Levels()[0] == 12 && fixture.Levels()[1] == 12,
            "an incomplete draft timing edit does not abort current output");
        while (clock.ElapsedMilliseconds < 1200) await Task.Delay(10);
        Check(fixture.Levels().All(level => level == 0) && fixture.Rows.All(row => !row.Device.Player.Active(row.Channel)),
            "late join with invalid draft timing still respects the captured deadline");
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

    static async Task UnassignedMixLanesSurviveRoundTrip()
    {
        await using var fixture = new Fixture();
        var preset = DraftPreset(5);
        Call(fixture.App, "ApplyStudioMix", preset);
        var saved = (StudioPreset)Call(fixture.App, "ExportStudioMix");
        Check(saved.lanes.Length == 5, "importing five lanes with three motors retains every lane on export");
        Check(JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(preset),
            "surplus draft lanes preserve order, curves, link groups, levels and selections");
        Check(fixture.Commands().Length == 0, "loading and exporting extra curves never sends motor commands");
    }

    static async Task NoDeviceDraftRoundTripAndPreview()
    {
        await using var fixture = new Fixture(withDevices: false);
        var preset = DraftPreset(3);
        Call(fixture.App, "ApplyStudioMix", preset);
        Check(Get<CurveDocument>(fixture.App, "studioDocument").Lanes.Count == 3,
            "a mix can be opened before any device is connected");
        Check(fixture.Rows.Length == 0 && Get<Dictionary<ulong, Device>>(fixture.App, "devices").Count == 0,
            "draft lanes do not create synthetic connected devices or motor routes");
        var saved = (StudioPreset)Call(fixture.App, "ExportStudioMix");
        Check(JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(preset),
            "a disconnected draft preserves the complete file on export");
        Call(fixture.App, "StartStudioPreview");
        await Until(() => Get<Stopwatch>(fixture.App, "studioPreviewClock")?.ElapsedMilliseconds >= 40,
            "visual preview advances without a hardware session");
        Check(!Get<bool>(fixture.App, "studioRunning") && fixture.Commands().Length == 0,
            "visual preview never starts physical playback");
        Check(Get<TextBlock>(fixture.App, "studioPreviewLabel").Text.Contains("Visual only", StringComparison.OrdinalIgnoreCase),
            "visual preview explicitly distinguishes simulated levels from physical output");
        await fixture.Stop();
        Check(Get<Stopwatch>(fixture.App, "studioPreviewClock") == null,
            "Stop all also ends the visual preview clock");
        bool refused = false;
        try { await Invoke(fixture.App, "StartStudio"); } catch (ArgumentException) { refused = true; }
        Check(refused && fixture.Commands().Length == 0, "unassigned drafts cannot be started as physical motors");
    }

    static async Task ExportOrderMatchesImportSlots()
    {
        string MotorSnapshot(ConnectorApp app)
        {
            var document=Get<CurveDocument>(app,"studioDocument");
            return JsonSerializer.Serialize(Get<Dictionary<string,MixerMotor>>(app,"mixerRows").Select(pair=>new {
                key=pair.Key,level=pair.Value.Level.Value,selected=pair.Value.Include.IsChecked,
                points=document.GetLane(pair.Key).Points,curve=document.GetLane(pair.Key).Interpolation,
                linkedCount=document.LinkedLaneIds(pair.Key).Length }));
        }
        void CheckRoundTrip(Fixture fixture,StudioPreset saved,string before)
        {
            string encoded=JsonSerializer.Serialize(saved);
            Check(!encoded.Contains("Synthetic")&&!Get<Dictionary<string,MixerMotor>>(fixture.App,"mixerRows").Keys.Any(encoded.Contains),
                "ordered export contains no device names or motor identifiers");
            Call(fixture.App,"ApplyStudioMix",saved);
            Check(MotorSnapshot(fixture.App)==before,"save/reopen retains each current motor's curve, level, selection and links");
            Check(JsonSerializer.Serialize((StudioPreset)Call(fixture.App,"ExportStudioMix"))==encoded,
                "ordered round trip retains all unassigned curves and linked group metadata");
            Check(fixture.Commands().Length==0,"ordered export/import does not send motor commands");
        }
        await using(var fixture=new Fixture())
        {
            var original=DraftPreset(5);Call(fixture.App,"ApplyStudioMix",original);
            fixture.Rows[0].Device.Connected=false;Call(fixture.App,"RefreshStudioMotors");
            string before=MotorSnapshot(fixture.App);var saved=(StudioPreset)Call(fixture.App,"ExportStudioMix");
            Check(saved.lanes[0].level==original.lanes[2].level&&saved.lanes[0].points[0].level==original.lanes[2].points[0].level,
                "export puts the still-connected motor before earlier-discovered disconnected slots");
            CheckRoundTrip(fixture,saved,before);
        }
        await using(var fixture=new Fixture(withDevices:false))
        {
            Call(fixture.App,"ApplyStudioMix",DraftPreset(2));
            var device=new Device { Address=2,Name="Synthetic late actuator",Connected=true,Supported=true,Channels=new[]{0},Player=new Playback(fixture.Single,new[]{0}),
                Tab=new TabItem(),State=new TextBlock(),Details=new TextBlock(),ConnectButton=new Button(),Controls=new StackPanel() };
            device.Motors[0]=new MotorView {Channel=0,Output=new TextBlock()};
            Get<Dictionary<ulong,Device>>(fixture.App,"devices").Add(device.Address,device);Call(fixture.App,"RefreshStudioMotors");
            var row=Get<Dictionary<string,MixerMotor>>(fixture.App,"mixerRows").Single();row.Value.Level.Value=17;row.Value.Include.IsChecked=false;
            Get<CurveDocument>(fixture.App,"studioDocument").ReplaceCurve(row.Key,new[]{new CurvePoint(0,9),new CurvePoint(300,7)},CurveInterpolation.Step);
            string before=MotorSnapshot(fixture.App);var saved=(StudioPreset)Call(fixture.App,"ExportStudioMix");
            Check(saved.lanes[0].level==17&&saved.lanes[0].points[0].level==9,
                "export puts a newly discovered motor before drafts authored earlier");
            Check(saved.lanes[1].group==saved.lanes[2].group&&saved.lanes[0].group!=saved.lanes[1].group,
                "moving assigned lanes first preserves linked draft groups");
            CheckRoundTrip(fixture,saved,before);
        }
    }

    static async Task InvertedPatternRequiresRestart()
    {
        await using var fixture=new Fixture();
        var preset=new StudioPreset {lengthMs=1000,loop=true,durationMs=0,transitionMs=0,
            lanes=Enumerable.Range(0,3).Select(i=>new StudioLanePreset {level=12,selected=true,group=i<2?0:1,curve="linear",
                points=new[]{new LovenseConnector.Frame {ms=0,level=i<2?6:8},new LovenseConnector.Frame {ms=1000,level=i<2?6:8}}}).ToArray()};
        Call(fixture.App,"ApplyStudioMix",preset);await fixture.Start();
        await Until(()=>fixture.Levels().SequenceEqual(new[]{6,6,8}),"original linked pattern plays before inversion");
        var document=Get<CurveDocument>(fixture.App,"studioDocument");string key=document.Lanes[0].Id;int marker=fixture.Commands().Length;
        document.InvertLevels(key);
        await Until(()=>!Get<bool>(fixture.App,"studioRunning")&&fixture.Levels().All(level=>level==0),"inverting a playing curve stops the entire owned Studio group");
        Check(document.Undo()&&document.GetLane(key).Points[0].Level==6,"inversion remains undoable after stopping output");
        Check(document.Redo()&&document.GetLane(key).Points[0].Level==14,"redo restores the inverted curve without playback");
        await Task.Delay(160);
        Check(!fixture.Commands().Skip(marker).Any(command=>command.Nonzero),"invert, undo and redo never restart motor output");
        await fixture.Start();await Until(()=>fixture.Levels().SequenceEqual(new[]{14,14,8}),"explicit Start plays the inverted linked group and preserves the independent motor");
        await fixture.Stop();
    }

    static async Task DraftRecoverySurvivesRestartAndFailures()
    {
        string folder=Path.Combine(Path.GetTempPath(),"HapticBridge-draft-test-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(folder,"studio-draft.mix.json");
        Directory.CreateDirectory(folder);
        try
        {
            string saved;
            await using(var fixture=new Fixture(withDevices:false))
            {
                Set(fixture.App,"dataDir",folder);Call(fixture.App,"InitializeStudioDraftRecovery");
                fixture.Duration.Text="750";
                Check(!(bool)Call(fixture.App,"SaveStudioDraft")&&!File.Exists(path),"timing changes without an authored pattern do not replace the previous draft");
                Call(fixture.App,"ApplyStudioMix",DraftPreset(3));
                await Until(()=>File.Exists(path),"edited mix is saved after the debounce",5000);
                saved=File.ReadAllText(path);
                Check(saved==StudioDraftStore.Encode((StudioPreset)Call(fixture.App,"ExportStudioMix")),"automatic draft preserves the complete mix");
                fixture.Duration.Text="unfinished input";
                Check(!(bool)Call(fixture.App,"SaveStudioDraft")&&File.ReadAllText(path)==saved,"invalid timing leaves the previous valid draft intact");
                fixture.Duration.Text="1200";
                var doc=Get<CurveDocument>(fixture.App,"studioDocument");
                doc.BeginEdit();doc.MovePoint(doc.Lanes[0].Id,0,0,7);
                Check(!(bool)Call(fixture.App,"SaveStudioDraft")&&File.ReadAllText(path)==saved,"an unfinished drag is not written as a committed draft");
                doc.CancelEdit();Call(fixture.App,"FinishStudioDraftSaving");saved=File.ReadAllText(path);
                Check(StudioDraftStore.Load(path).durationMs==1200,"closing flushes pending valid changes without waiting for debounce");
            }
            foreach(bool withDevices in new[]{false,true})
            {
                await using var fixture=new Fixture(withDevices);
                Set(fixture.App,"dataDir",folder);Call(fixture.App,"InitializeStudioDraftRecovery");
                Check(Get<MenuItem>(fixture.App,"studioRestoreDraftChoice").IsEnabled&&fixture.Commands().Length==0,
                    "startup offers previous-draft recovery without output commands");
                await Invoke(fixture.App,"RestoreStudioDraft");
                Check(StudioDraftStore.Encode((StudioPreset)Call(fixture.App,"ExportStudioMix"))==saved,"explicit recovery restores links, levels, selections, timing and curves");
                Check(!fixture.Commands().Any(c=>c.Nonzero)&&!Get<bool>(fixture.App,"studioRunning"),"recovery never starts motor playback");
                Call(fixture.App,"FinishStudioDraftSaving");
            }
            bool rejected=false;
            using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try {StudioDraftStore.Save(path,StudioDraftStore.Encode(DraftPreset(1)));}catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException){rejected=true;}
            }
            Check(rejected&&File.ReadAllText(path)==saved,"failed file replacement preserves the valid previous draft");
            Check(!File.Exists(path+".tmp.mix.json"),"failed save cleans its temporary file");
            File.WriteAllText(path,"{broken draft");
            await using(var fixture=new Fixture(withDevices:false))
            {
                Set(fixture.App,"dataDir",folder);Call(fixture.App,"InitializeStudioDraftRecovery");
                Check(!Get<MenuItem>(fixture.App,"studioRestoreDraftChoice").IsEnabled&&Get<CurveDocument>(fixture.App,"studioDocument").Lanes.Count==0,
                    "malformed saved draft does not prevent a fresh editor session");
                Check(File.ReadAllText(path)=="{broken draft"&&fixture.Commands().Length==0,"startup leaves an unreadable draft unchanged without output");
            }
        }
        finally {Directory.Delete(folder,true);}
    }

    static async Task DuplicateCurvesStayIndependent()
    {
        foreach(bool withDevices in new[]{false,true})
        {
            await using var fixture = new Fixture(withDevices);
            var original = DraftPreset(3);
            Call(fixture.App, "ApplyStudioMix", original);
            var document = Get<CurveDocument>(fixture.App, "studioDocument");
            var editor = Get<CurveEditorControl>(fixture.App, "studioEditor");
            var source = document.Lanes[1];
            editor.ActiveLaneId = source.Id;
            var before = source.Points.ToArray();
            Call(fixture.App, "DuplicateStudioCurve");
            var copy = document.GetLane(editor.ActiveLaneId);
            Check(document.Lanes.Count == 4 && copy.Id != source.Id && !Get<Dictionary<string,MixerMotor>>(fixture.App,"mixerRows").ContainsKey(copy.Id),
                "duplicating a connected or offline curve creates a selected unassigned editing lane");
            Check(copy.Points.SequenceEqual(before) && copy.Interpolation == source.Interpolation && copy.GroupId != source.GroupId,
                "duplicate retains shape and interpolation without joining the source link group");
            document.MovePoint(copy.Id, 0, 0, 19);
            Check(source.Points.SequenceEqual(before) && document.Lanes[0].Points.SequenceEqual(before),
                "editing the duplicate leaves both linked source curves untouched");
            var saved = (StudioPreset)Call(fixture.App, "ExportStudioMix");
            Check(saved.lanes[3].level == original.lanes[1].level && saved.lanes[3].selected == original.lanes[1].selected,
                "duplicate preserves source level and selection metadata");
            Call(fixture.App, "ApplyStudioMix", saved);
            Check(JsonSerializer.Serialize(Call(fixture.App, "ExportStudioMix")) == JsonSerializer.Serialize(saved),
                "independent copy survives saving and reopening with or without hardware slots");
            Check(fixture.Commands().Length == 0, "duplicate and round-trip never start output");
        }
        await using var capped = new Fixture(withDevices: false);
        var maximum = DraftPreset(1); maximum.lanes = Enumerable.Repeat(maximum.lanes[0],64).ToArray();
        Call(capped.App, "ApplyStudioMix", maximum);
        var snapshot = JsonSerializer.Serialize(Call(capped.App, "ExportStudioMix"));
        bool rejected = false;
        try { Call(capped.App, "DuplicateStudioCurve"); } catch(ArgumentException) { rejected = true; }
        Check(rejected && snapshot == JsonSerializer.Serialize(Call(capped.App, "ExportStudioMix")),
            "duplicating at the lane limit leaves the mix unchanged");
    }

    static StudioPreset DraftPreset(int count) => new()
    {
        lengthMs = 300, loop = true, durationMs = 500, transitionMs = 0,
        lanes = Enumerable.Range(0, count).Select(i => new StudioLanePreset
        {
            level = 4 + i, selected = i % 2 == 0, group = i / 2, curve = "linear",
            points = new[] { new LovenseConnector.Frame { ms = 0, level = i / 2 + 1 }, new LovenseConnector.Frame { ms = 300, level = 0 } }
        }).ToArray()
    };

    static async Task FailedStopCannotDisplayOutputOff()
    {
        await using var fixture = new Fixture();
        fixture.Dual.HoldAndFailNextLaneStop = true;
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        bool failed = false;
        try { await fixture.Rows[0].Device.Player.Stop(fixture.Rows[0].Channel); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && fixture.Levels()[0] < 0, "synthetic failed stop leaves the sent level unknown");
        Call(fixture.App, "RefreshStudioDock");
        string source = ((TextBlock)fixture.Window.FindName("OutputSource")).Text;
        Check(source.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase),
            "idle output with a failed stop is shown as unconfirmed rather than off");
        await fixture.Stop();
        Call(fixture.App, "RefreshStudioDock");
        Check(((TextBlock)fixture.Window.FindName("OutputSource")).Text == "Output off",
            "a later confirmed stop restores the output-off state");
    }

    sealed class Fixture : IAsyncDisposable
    {
        public ConnectorApp App { get; }
        public Window Window { get; }
        public FakeLink Dual { get; } = new("dual");
        public FakeLink Single { get; } = new("single");
        public MixerMotor[] Rows { get; }
        public TextBox Duration => Get<TextBox>(App, "mixerDuration");
        public Fixture(bool withDevices = true)
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
            if (withDevices)
            {
                AddDevice(1, "Synthetic dual actuator", Dual, new[] { 1, 2 });
                AddDevice(2, "Synthetic single actuator", Single, new[] { 0 });
            }
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
            Get<DispatcherTimer>(App,"studioDraftTimer")?.Stop();
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
