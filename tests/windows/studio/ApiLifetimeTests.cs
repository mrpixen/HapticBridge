using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

// API handoffs exercise real dispatch/playback with invisible controls and fake links.
internal static partial class StudioIntegrationTests
{
    static ApiRequest NativeApiRequest(string path, Dictionary<string, object> body, CancellationToken cancellation = default) =>
        new() { Method = "POST", Path = "/api/v1/" + path, Body = body, Cancellation = cancellation };
    static Task<object> NativeApi(Fixture fixture, string path, Dictionary<string, object> body, CancellationToken cancellation = default) =>
        (Task<object>)Call(fixture.App, "HandleApi", NativeApiRequest(path, body, cancellation));
    static IntegrationSession SyntheticApiSession(Fixture fixture)
    {
        Set(fixture.App, "apiEnabled", true);
        var session = new IntegrationSession { Id = "synthetic-session", Name = "Synthetic integration", TtlMs = 10000 };
        session.Touch(); Set(fixture.App, "integration", session); return session;
    }
    static Dictionary<string, object> ApiMotor(IntegrationSession session, string device, int channel, int level) =>
        new() { ["sessionId"] = session.Id, ["deviceId"] = device, ["motor"] = channel, ["level"] = level, ["durationMs"] = 10000 };
    static async Task ApiCancelled(Task request, string label)
    {
        bool cancelled = false;
        try { await request.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, label);
    }

    static async Task CancelledApiHandoffCannotAcquireSession()
    {
        await using var fixture = new Fixture(); Set(fixture.App, "apiEnabled", true);
        using var cancellation = new CancellationTokenSource(); fixture.Dual.HoldNextPresetStop = true;
        var request = NativeApi(fixture, "session", new() { ["name"] = "Synthetic cancelled handoff" }, cancellation.Token);
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel(); fixture.Dual.ReleasePreset.TrySetResult();
        await ApiCancelled(request, "a cancelled source handoff does not report an accepted session");
        Check(Get<IntegrationSession>(fixture.App, "integration") == null, "cancelled source handoff never acquires integration ownership");
        Check(fixture.Levels().All(level => level == 0), "cancelled handoff still finishes its admitted stops");
    }

    static async Task CancelledApiFrameStopsBeforeNextMotor()
    {
        await using var fixture = new Fixture(); var session = SyntheticApiSession(fixture);
        await NativeApi(fixture, "motor", ApiMotor(session, "000000000001", 1, 4));
        await Until(() => fixture.Levels()[0] == 4, "frame fixture reaches its starting level");
        fixture.Dual.HoldNextLaneStop = true; using var cancellation = new CancellationTokenSource();
        var request = NativeApi(fixture, "frame", new() { ["sessionId"] = session.Id,
            ["outputs"] = new[] { ApiMotor(session, "000000000001", 1, 0), ApiMotor(session, "000000000002", 0, 17) } }, cancellation.Token);
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel(); fixture.Dual.ReleaseLaneStop.TrySetResult();
        await ApiCancelled(request, "a frame observes cancellation after an awaited motor stop");
        Check(fixture.Levels()[0] == 0 && !fixture.Single.Snapshot().Any(command => command.Nonzero),
            "a cancelled frame completes its stop without starting the next motor");
    }

    static async Task CancelledApiStartCannotEscapePlaybackWaits()
    {
        foreach (var test in new[] { (Pattern: false, PresetWait: false), (Pattern: true, PresetWait: false), (Pattern: true, PresetWait: true) })
        {
            await using var fixture = new Fixture(); var session = SyntheticApiSession(fixture);
            var player = fixture.Rows[2].Device.Player;
            using var cancellation = new CancellationTokenSource();
            Task hardware = Task.CompletedTask; SemaphoreSlim controls = null;
            if (test.Pattern) Set(fixture.App, "json", new System.Web.Script.Serialization.JavaScriptSerializer());
            if (test.PresetWait)
            {
                hardware = player.HardwarePreset(1, 10000);
                await Until(() => player.Level(0) == -1, "synthetic firmware preset is active before handoff");
                fixture.Single.HoldNextPresetStop = true;
            }
            else { controls = Get<SemaphoreSlim>(player, "controls"); await controls.WaitAsync(); }
            Task request = test.Pattern
                ? NativeApi(fixture, "pattern", new() { ["sessionId"] = session.Id, ["deviceId"] = "000000000002", ["motor"] = 0,
                    ["pattern"] = new Dictionary<string, object> { ["mode"] = "Steady", ["high"] = 17, ["durationMs"] = 10000 } }, cancellation.Token)
                : NativeApi(fixture, "motor", ApiMotor(session, "000000000002", 0, 17), cancellation.Token);
            if (test.PresetWait) await fixture.Single.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            if (test.PresetWait) fixture.Single.ReleasePreset.TrySetResult(); else controls.Release();
            await ApiCancelled(request, test.PresetWait ? "cancelled pattern start checks its lifetime after StopPreset" :
                (test.Pattern ? "cancelled pattern" : "cancelled motor") + " start checks its lifetime after the playback gate");
            await hardware.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!player.Active(0) && !fixture.Single.Snapshot().Any(command => command.Nonzero),
                "cancellation during playback handoff cannot leave a positive job queued");
        }
    }

    static async Task ApiReleaseCannotRetargetReconnectedDevice()
    {
        await using var fixture=new Fixture();var session=SyntheticApiSession(fixture);
        await NativeApi(fixture,"motor",ApiMotor(session,"000000000002",0,6));
        var device=fixture.Rows[2].Device;var original=device.Player;
        await Until(()=>original.Level(0)==6,"session owns output on the original connection");
        original.CancelAll();device.Generation+=2;
        var link=new FakeLink("replacement");device.Player=new Playback(link,new[]{0});
        await Invoke(fixture.App,"RevokeIntegration",false);
        Check(link.Snapshot().Length==0,"revoking an old API session sends no stop to an unowned replacement connection");
        session=SyntheticApiSession(fixture);
        await NativeApi(fixture,"motor",ApiMotor(session,"000000000002",0,9));
        await Until(()=>device.Player.Level(0)==9,"a new session can explicitly own the replacement");
        await Invoke(fixture.App,"RevokeIntegration",false);
        Check(!device.Player.Running&&device.Player.Level(0)==0,"revoking the new session stops its actual replacement output");
    }

    static async Task ApiFrameCannotRetargetReconnectedDevice()
    {
        await using var fixture=new Fixture();var session=SyntheticApiSession(fixture);
        await NativeApi(fixture,"motor",ApiMotor(session,"000000000001",1,4));
        await Until(()=>fixture.Levels()[0]==4,"API frame starts with an active first motor");
        fixture.Dual.HoldNextLaneStop=true;
        var request=NativeApi(fixture,"frame",new() { ["sessionId"]=session.Id,
            ["outputs"]=new[]{ApiMotor(session,"000000000001",1,0),ApiMotor(session,"000000000002",0,17)} });
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var device=fixture.Rows[2].Device;device.Player.CancelAll();device.Generation+=2;
        var link=new FakeLink("replacement");var replacement=new Playback(link,new[]{0});device.Player=replacement;
        fixture.Dual.ReleaseLaneStop.TrySetResult();bool rejected=false;
        try{await request.WaitAsync(TimeSpan.FromSeconds(3));}catch(ApiFault fault)when(fault.Status==409){rejected=true;}
        await Task.Delay(100);
        Check(!replacement.Running&&!link.Snapshot().Any(command=>command.Nonzero),
            "a pending multi-motor API frame cannot start output on a connection replaced during an earlier stop");
        Check(rejected&&fixture.Levels()[0]==0,"stale API frame reports conflict while preserving the completed first stop");
        await NativeApi(fixture,"motor",ApiMotor(session,"000000000002",0,6));
        await Until(()=>replacement.Level(0)==6,"a fresh API request can address the reconnected device");
    }

    static async Task ApiPatternRejectsConnectionChangeDuringAdmission()
    {
        await using var fixture=new Fixture();var session=SyntheticApiSession(fixture);
        Set(fixture.App,"json",new System.Web.Script.Serialization.JavaScriptSerializer());
        var device=fixture.Rows[2].Device;var original=device.Player;var controls=Get<SemaphoreSlim>(original,"controls");
        await controls.WaitAsync();
        var request=NativeApi(fixture,"pattern",new() { ["sessionId"]=session.Id,["deviceId"]="000000000002",["motor"]=0,
            ["pattern"]=new Dictionary<string,object> { ["mode"]="Steady",["high"]=17,["durationMs"]=10000 } });
        original.CancelAll();device.Generation+=2;device.Player=new Playback(new FakeLink("replacement"),new[]{0});
        controls.Release();bool rejected=false;
        try{await request.WaitAsync(TimeSpan.FromSeconds(3));}catch(ApiFault fault)when(fault.Status==409){rejected=true;}
        Check(rejected,"API pattern must report a connection conflict instead of accepted after replacement cancelled its pending admission");
        Check(!original.Running&&!device.Player.Running,"neither old nor replacement player starts the stale API pattern");
    }

    static async Task AcceptedApiOutputKeepsItsLease()
    {
        await using var fixture = new Fixture(); var session = SyntheticApiSession(fixture);
        using var cancellation = new CancellationTokenSource();
        await NativeApi(fixture, "motor", ApiMotor(session, "000000000002", 0, 6), cancellation.Token);
        cancellation.Cancel(); await Task.Delay(150);
        Check(fixture.Rows[2].Device.Player.Active(0) && fixture.Levels()[2] == 6,
            "accepted output belongs to its session lease rather than the completed request lifetime");
        using var gate = new SemaphoreSlim(1, 1); Set(fixture.App, "apiGate", gate);
        Set(fixture.App, "json", new System.Web.Script.Serialization.JavaScriptSerializer());
        string token = ApiServer.NewToken(); CancellationToken accepted = default;
        using var server = new ApiServer(0, token, request => { accepted = request.Cancellation; return (Task<object>)Call(fixture.App, "DispatchApi", request); });
        string response = await NativeApiPost(server, token, "pattern", new() { ["sessionId"] = session.Id,
            ["deviceId"] = "000000000002", ["motor"] = 0,
            ["pattern"] = new Dictionary<string, object> { ["mode"] = "Steady", ["high"] = 9, ["durationMs"] = 10000 } });
        Check(response.StartsWith("HTTP/1.1 200") && response.Contains("\"accepted\":true"), "native API pattern start returns a successful HTTP response");
        server.Dispose(); await Task.Delay(150);
        Check(!accepted.IsCancellationRequested && fixture.Rows[2].Device.Player.Active(0) && fixture.Levels()[2] == 9,
            "successful response detaches request cancellation without shortening its accepted pattern");
    }

    static async Task<string> NativeApiPost(ApiServer server, string token, string path, Dictionary<string, object> body)
    {
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
        using var stream = client.GetStream(); string json = JsonSerializer.Serialize(body);
        byte[] bytes = Encoding.UTF8.GetBytes("POST /api/v1/" + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\nAuthorization: Bearer " + token +
            "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\n\r\n" + json);
        await stream.WriteAsync(bytes); using var reader = new StreamReader(stream, Encoding.UTF8);
        try { return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(7)); }
        catch (IOException) { return ""; }
    }

    static async Task TimedOutApiQueueCannotStartOutput()
    {
        await using var fixture = new Fixture();
        Set(fixture.App, "apiEnabled", true);
        using var gate = new SemaphoreSlim(1, 1); Set(fixture.App, "apiGate", gate);
        var sessionReply = await (Task<object>)Call(fixture.App, "HandleApi", new ApiRequest { Method = "POST", Path = "/api/v1/session",
            Body = new() { ["name"] = "Synthetic integration", ["ttlMs"] = 10000 } });
        string id = (string)sessionReply.GetType().GetProperty("sessionId").GetValue(sessionReply);
        Dictionary<string, object> Motor(string device, int channel, int level) => new() {
            ["sessionId"] = id, ["deviceId"] = device, ["motor"] = channel, ["level"] = level, ["durationMs"] = 10000 };
        await (Task<object>)Call(fixture.App, "HandleApi", new ApiRequest { Method = "POST", Path = "/api/v1/motor", Body = Motor("000000000001", 1, 4) });
        await Until(() => fixture.Levels()[0] == 4, "integration starts synthetic output before the slow stop");
        fixture.Dual.HoldNextLaneStop = true;
        string token = ApiServer.NewToken();
        using var server = new ApiServer(0, token, request => (Task<object>)Call(fixture.App, "DispatchApi", request));
        var first = NativeApiPost(server, token, "motor", Motor("000000000001", 1, 0));
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        long deadline = Get<IntegrationSession>(fixture.App, "integration").Deadline;
        var queued = NativeApiPost(server, token, "motor", Motor("000000000002", 0, 17));
        Check(await first == "" && await queued == "", "server closes both slow and queued requests at its deadline");
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        // A following dispatch proves that the expired queue has drained, rather
        // than relying on an arbitrary delay before checking for stale output.
        await ((Task<object>)Call(fixture.App, "DispatchApi", new ApiRequest { Method = "GET", Path = "/api/v1/devices", Body = new() })).WaitAsync(TimeSpan.FromSeconds(2));
        Check(!fixture.Single.Snapshot().Any(command => command.Nonzero), "a queued HTTP request cannot start output after its server timeout");
        Check(Get<IntegrationSession>(fixture.App, "integration").Deadline == deadline, "expired queued output does not renew the integration lease");
        Check(fixture.Levels()[0] == 0, "the admitted stop still completes after its request times out");
    }

}
