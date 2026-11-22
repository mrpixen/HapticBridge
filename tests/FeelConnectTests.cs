using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LovenseConnector;

// All fixtures are synthetic. These tests never contact an external service or physical device.
internal static class FeelConnectTests
{
    static int assertions;
    public static async Task Main()
    {
        PairingAndPackets();
        ImmediateAndRoomIsolation();
        ModernFormats();
        TimedPlaybackAndSeek();
        TipPatterns();
        InvalidAndBoundedInput();
        await TransportRoundTrip();
        await RejectedRoomAndCredentialPrivacy();
        await CancellationAndStaleSession();
        await ExpiredSession();
        await RoomRefresh();
        await RoomRefreshFailure();
        await RoomCreatedDuringPresence();
        await FirstCueDuringRefresh();
        await RemovedBufferedRoomAndPublisherLeave();
        await InvalidTransportStops();
        await HybridRouting();
        await TransportDiagnostics();
        await PayloadShapeDiagnostics();
        await DeviceRegistrationUpdates();
        await PlaybackModesAndReconnect();
        await PlaybackModeCancellation();
        await LegacyTerminalReasons();
        await ExplicitActionsSupersedeLegacyFailure();
        Console.WriteLine("FeelConnect: " + assertions + " assertions passed (synthetic offline fixtures).");
    }

    static void Assert(bool ok, string label)
    { assertions++; if (!ok) throw new Exception("FeelConnect test failed: " + label); }
    static void Near(double expected, double actual, string label) => Assert(Math.Abs(expected - actual) < .001, label);
    static FeelConnectProtocol Decoder()
    { var protocol = new FeelConnectProtocol(); protocol.SetRooms(new[] { "test-website", "test-video" }); return protocol; }
    static FeelConnectOutput Accept(FeelConnectProtocol protocol, string data, long now = 0, string room = "test-video", string messageType = "room:message", long? serverTime = null, string publisher = "fixture-publisher")
    {
        using var document = JsonDocument.Parse("{" +
            "\"room\":" + JsonSerializer.Serialize(room) + ",\"message_type\":" + JsonSerializer.Serialize(messageType) +
            ",\"publisher\":" + JsonSerializer.Serialize(publisher) + (serverTime.HasValue ? ",\"serverTime\":" + serverTime.Value : "") + ",\"data\":" + data + "}");
        return protocol.Accept(document.RootElement, now, 1700000000000);
    }
    static void PairingAndPackets()
    {
        Assert(FeelConnectPairing.Parse("synthetic-token").Value == "synthetic-token", "raw token");
        Assert(FeelConnectPairing.Parse("feelapp://authorize?token=fixture%2Bvalue").Value == "fixture+value", "URL token decoding");
        Assert(FeelConnectPairing.Parse("feelapp://connect?code=fixture123").IsConnectionCode, "connection code");
        Assert(!FeelConnectPairing.Parse("synthetic-token").ToString().Contains("synthetic-token"), "pairing ToString is redacted");
        foreach (string invalid in new[] { "", "abc", "https://untrusted.invalid/?token=fixture", "feelapp://authorize?token=one&token=two", "feelapp://authorize?code=code", "embedded\ncontrol", new string('x', 8193) })
        {
            bool rejected = false; try { FeelConnectPairing.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "invalid pairing rejected");
        }
        Assert(FeelConnectClient.TrySocketPacket("43/pubnub,17[{\"ok\":true}]", out int kind, out int? id, out string json) && kind == 3 && id == 17 && json == "[{\"ok\":true}]", "Socket.IO ACK frame");
        Assert(FeelConnectClient.TrySocketPacket("42/pubnub,[\"message\",{}]", out kind, out id, out json) && kind == 2 && id == null, "Socket.IO event frame");
        Assert(!FeelConnectClient.TrySocketPacket("42/other,[\"message\",{}]", out _, out _, out _), "foreign namespace ignored");
        Assert(!FeelConnectClient.TrySocketPacket("43/pubnub,999999999999999999999[]", out _, out _, out _), "ACK overflow rejected");
    }

    static void ImmediateAndRoomIsolation()
    {
        var p = Decoder();
        Assert(Accept(p, "{\"strength\":90}", 100, "foreign-room", "website:device_speed") == null && p.Percent == 0, "foreign room ignored");
        Near(100, Accept(p, "{\"strength\":100}", 100, "test-website", "website:device_speed").Percent, "full intensity supported");
        Assert(p.Tick(5099) == null, "immediate lease remains before deadline");
        Assert(p.Tick(5100).IsStop, "missing stop expires");
        Near(35, Accept(p, "{\"what\":\"device_speed\",\"strength\":\"35\"}", 6000).Percent, "legacy immediate shape");
        Assert(Accept(p, "{\"strength\":0}", 6010, "test-website", "website:device_speed").IsStop, "explicit zero stops");
        Near(75, Accept(p, "{\"ver\":2,\"percents\":[5,25,75]}", 7000).Percent, "v2 values latest wins");
        Assert(Accept(p, "{\"action\":\"leave\",\"uuid\":\"other\"}", 7050, "test-video", "system:presence") == null, "other publisher leave ignored");
        Assert(Accept(p, "{\"action\":\"leave\",\"uuid\":\"fixture-publisher\"}", 7100, "test-video", "system:presence").IsStop, "active publisher leave stops");
        Accept(p, "{}", 7200, "test-website", "website:rooms_changed");
        Assert(p.RoomsChanged, "room refresh signaled");
    }

    static void TimedPlaybackAndSeek()
    {
        var p = Decoder();
        Near(0, Accept(p, "{\"ver\":3,\"type\":\"play\",\"serverTime\":10000,\"subtitles\":[{\"time\":5000,\"subtitle\":0},{\"time\":5200,\"subtitle\":2},{\"time\":5500,\"subtitle\":4}]}", 1000, serverTime: 10100).Percent, "v3 starts current cue");
        Assert(p.Tick(1099) == null, "cue does not run early");
        Near(50, p.Tick(1100).Percent, "server timestamp delay compensated");
        Near(100, p.Tick(1400).Percent, "subtitle4 reaches100 percent");
        Assert(p.Tick(2900) == null && p.Percent == 100, "sparse video window holds its final value until the next chunk");
        Assert(p.Tick(75900).IsStop, "missing next video chunk expires its bounded lease");
        p = Decoder();
        Near(25, Accept(p, "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":1},{\"time\":1000,\"subtitle\":4}]}", 4000).Percent, "first batch");
        Near(50, Accept(p, "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":10000,\"subtitle\":2},{\"time\":10200,\"subtitle\":0}]}", 4100).Percent, "seek replaces cue queue");
        Assert(p.Tick(4300).IsStop, "seek replacement zero");
        Assert(p.Tick(5000) == null, "old seek batch cannot restart");
        Accept(p, "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":4},{\"time\":2000,\"subtitle\":4}]}", 6000);
        Assert(Accept(p, "{\"ver\":3,\"type\":\"stop\"}", 6050).IsStop && p.PendingCues == 0, "pause cancels queued events");
        Assert(p.Tick(8000) == null && p.Percent == 0, "pause remains stopped");
        Near(100, Accept(p, "{\"ver\":3,\"type\":\"play\",\"serverTime\":1000,\"subtitles\":[{\"time\":0,\"subtitle\":1},{\"time\":100,\"subtitle\":2},{\"time\":200,\"subtitle\":4}]}", 9000, serverTime: 1250).Percent, "overdue cues collapse to current value");
        Assert(Accept(p, "{\"ver\":3,\"type\":\"play\",\"subtitles\":[]}", 9100).IsStop, "empty batch stops");
    }

    static void TipPatterns()
    {
        var p = Decoder();
        Near(80, Accept(p, "{\"id\":\"one\",\"strength\":80,\"duration\":1}", 0, messageType: "website:tip").Percent, "finite constant tip");
        Assert(Accept(p, "{\"id\":\"one\",\"strength\":80,\"duration\":1}", 100, messageType: "website:tip") == null, "duplicate tip ignored");
        Assert(p.Tick(1000).IsStop, "tip duration stops");
        Near(50, Accept(p, "{\"strength\":50,\"duration\":2.5,\"pattern\":{\"0\":100,\"0.25\":0,\"0.5\":100}}", 2000, messageType: "website:tip").Percent, "pattern strength scaling");
        Assert(p.Tick(2250).IsStop, "pattern off point");
        Near(50, p.Tick(2500).Percent, "pattern first repeat");
        Assert(p.Tick(2750).IsStop, "pattern repeated off point");
        Near(50, p.Tick(4000).Percent, "pattern repeats throughout duration");
        Assert(p.Tick(4500).IsStop, "repeated pattern finite ending");
        var encoded = JsonSerializer.Serialize("{&#34;0&#34;:100,&#34;1&#34;:0}");
        Near(60, Accept(p, "{\"strength\":60,\"duration\":2,\"pattern\":" + encoded + "}", 5000, messageType: "website:tip").Percent, "HTML-encoded pattern JSON");
        Assert(Accept(p, "{\"strength\":100,\"duration\":1,\"pattern\":{\"0\":1,\"0.001\":2}}", 5100, messageType: "website:tip") == null, "unbounded pattern rate rejected");
        Assert(Accept(p, "{\"strength\":100,\"duration\":3600,\"pattern\":{\"0\":0,\"0.1\":100}}", 5100, messageType: "website:tip") == null, "too many repeated cues rejected");
        Assert(Accept(p, "{\"strength\":0,\"duration\":10}", 5200, messageType: "website:tip").IsStop, "zero tip stops");
    }

    static void ModernFormats()
    {
        var p = Decoder(); p.TargetUser = "fixture-user";
        Near(65, Accept(p, "{\"what\":\"device_percent\",\"payload\":65,\"target\":\"fixture-user\"}", 100, messageType: "device:position").Percent, "modern direct percentage");
        Assert(Accept(p, "{\"what\":\"device_percent\",\"payload\":100,\"target\":\"different-user\"}", 110, messageType: "device:position") == null, "modern command to another target rejected");
        Assert(Accept(p, "{\"what\":\"device_percent\",\"payload\":101,\"target\":\"fixture-user\"}", 110, messageType: "device:position") == null, "modern out-of-range payload rejected");
        Assert(Accept(p, "{\"what\":\"device_percent\",\"payload\":0,\"target\":\"fixture-user\"}", 120, messageType: "device:position").IsStop, "modern explicit zero stops");
        Near(75, Accept(p, "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":3}]} ", 200, messageType: "room:play").Percent, "modern room play uses verified v3 format");
        Assert(Accept(p, "{\"ver\":3,\"type\":\"stop\"}", 300, messageType: "room:stop").IsStop, "modern room stop clears timed queue");
        Assert(p.Tick(10000) == null && p.Percent == 0, "modern stopped queue cannot restart");
    }

    static void InvalidAndBoundedInput()
    {
        var p = Decoder();
        foreach (string data in new[] {
            "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":5}]}",
            "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":100,\"subtitle\":1},{\"time\":0,\"subtitle\":2}]}",
            "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":1},{\"time\":999999999,\"subtitle\":2}]}",
            "{\"ver\":2,\"percents\":[100,101]}", "{\"ver\":2,\"percents\":[]}",
            "{\"ver\":9,\"percents\":[100]}", "{\"ver\":2,\"percents\":[\"NaN\"]}" })
            Assert(Accept(p, data) == null && p.Percent == 0, "malformed/unsupported cue cannot activate");
        foreach (double strength in new[] { -1, 101, 10000 })
            Assert(Accept(p, "{\"strength\":" + strength + "}", messageType: "website:device_speed") == null, "out-of-range intensity rejected");
        Assert(Accept(p, "{\"ver\":3,\"type\":\"play\",\"serverTime\":0,\"subtitles\":[{\"time\":0,\"subtitle\":4}]}", serverTime: 999999) == null, "stale queued video not replayed");
        string huge = "{\"ver\":3,\"type\":\"play\",\"subtitles\":[" + string.Join(',', Enumerable.Repeat("{\"time\":0,\"subtitle\":4}", 10001)) + "]}";
        Assert(Accept(p, huge) == null, "excess cue count rejected");
    }

    static async Task TransportRoundTrip()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>();
        var statuses = new ConcurrentQueue<FeelConnectStatus>();
        client.OutputChanged += frames.Enqueue; client.StatusChanged += statuses.Enqueue;
        await client.PairAsync("feelapp://authorize?token=synthetic-pair-code", new[] { "Test device", "Test device", "Second device" });
        Assert(statuses.Last().Connected, "pair completes only when live connection ready");
        Assert(fixture.Joined.SetEquals(new[] { "test-website", "test-video" }), "website and video rooms joined");
        Assert(fixture.PresenceOnline == 1 && fixture.PresenceNames.SequenceEqual(new[] { "Test device", "Second device" }), "accurate deduplicated presence names");
        Assert(fixture.Authenticated, "namespace token authentication sent");
        fixture.Socket.Push("2heartbeat");
        await Until(() => fixture.Socket.Sent.Contains("3heartbeat"));
        Assert(fixture.Socket.Sent.Contains("3heartbeat"), "Engine.IO heartbeat pong");
        fixture.Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":100}"), fragment: true);
        await Until(() => frames.Any(x => x.Percent == 100));
        Assert(client.LastFrameAt.HasValue, "last input freshness exposed");
        Near(100, client.CurrentPercent, "arming snapshot retains an active cue");
        fixture.Socket.Push(Frame("foreign-room", "website:device_speed", "{\"strength\":33}"));
        await Task.Delay(60);
        Assert(!frames.Any(x => x.Percent == 33), "transport enforces allowed rooms");
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":1},{\"time\":80,\"subtitle\":4}]}"));
        await Until(() => frames.Any(x => x.Percent == 25));
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"stop\"}"));
        await Until(() => frames.Last().IsStop);
        Near(0, client.CurrentPercent, "arming snapshot does not replay a paused cue");
        int before = frames.Count;
        await Task.Delay(120);
        Assert(frames.Count == before, "transport pause prevents queued restart");
        await client.DisconnectAsync();
        Assert(frames.Last().IsStop && !statuses.Last().Connected, "disconnect zero and false status");
        Near(0, client.CurrentPercent, "disconnected snapshot is zero");
        Assert(fixture.PresenceOnline == 0, "disconnect withdraws presence");
        Assert(!string.Join('|', statuses.Select(x => x.ToString())).Contains("synthetic-pair-code"), "status never exposes QR token");
    }

    static async Task RejectedRoomAndCredentialPrivacy()
    {
        var fixture = new ServiceFixture { RejectRoom = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += frames.Enqueue;
        bool rejected = false;
        try { await client.PairAsync("synthetic-pair-code", Array.Empty<string>()); }
        catch (InvalidOperationException ex) { rejected = !ex.Message.Contains("synthetic-"); }
        Assert(rejected && frames.All(x => x.IsStop), "room ACK rejection fails closed without credentials");
        Assert(fixture.PresenceOnline == -1, "no online presence before room ACK success");
        var authFailure = new ServiceFixture { RejectAuth = true };
        using var http2 = new HttpClient(authFailure);
        await using var rejectedClient = new FeelConnectClient(http2, authFailure.Connect);
        rejected = false;
        try { await rejectedClient.PairAsync("synthetic-pair-code", Array.Empty<string>()); }
        catch (InvalidOperationException ex) { rejected = !ex.Message.Contains("synthetic-"); }
        Assert(rejected && authFailure.ConnectionCount == 0, "HTTP auth rejection does not start socket");
    }

    static async Task CancellationAndStaleSession()
    {
        var fixture = new ServiceFixture { DelayAuth = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var statuses = new ConcurrentQueue<FeelConnectStatus>(); var frames = new ConcurrentQueue<FeelConnectOutput>();
        client.StatusChanged += statuses.Enqueue; client.OutputChanged += frames.Enqueue;
        Task pairing = client.PairAsync("synthetic-first-code", Array.Empty<string>());
        await fixture.AuthEntered.Task;
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        try { await pairing; } catch (InvalidOperationException) { }
        Assert(fixture.ConnectionCount == 0 && !statuses.Any(x => x.Connected), "cancelled exchange cannot connect late");
        fixture.DelayAuth = false;
        await client.PairAsync("synthetic-second-code", Array.Empty<string>());
        MemorySocket oldSocket = fixture.Socket;
        oldSocket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":1},{\"time\":1000,\"subtitle\":4}]}"));
        await Until(() => frames.Any(x => x.Percent == 25));
        await client.PairAsync("synthetic-third-code", Array.Empty<string>());
        int before = frames.Count;
        oldSocket.Push(Frame("test-website", "website:device_speed", "{\"strength\":90}"));
        await Task.Delay(100);
        Assert(frames.Count == before && !frames.Any(x => x.Percent == 90), "replaced session cannot emit late commands");
        fixture.Socket.PushClose();
        await Until(() => !statuses.Last().Connected);
        Assert(frames.Last().IsStop, "unexpected socket close zeroes immediately");
    }

    static async Task ExpiredSession()
    {
        var fixture = new ServiceFixture { TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds() };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        bool expired = false;
        try { await client.PairAsync("synthetic-pair-code", Array.Empty<string>()); }
        catch (InvalidOperationException ex) { expired = ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase); }
        Assert(expired && fixture.ConnectionCount == 0, "expired FEC metadata fails before socket");
        // Advance the injected clock only after observing live output. A wall-clock expiry
        // competing with Until's deadline makes this test depend on runner scheduling.
        long activeNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var active = new ServiceFixture { TokenExpiry = activeNow + 30 };
        using var activeHttp = new HttpClient(active);
        await using var activeClient = new FeelConnectClient(activeHttp, active.Connect,
            utcNow: () => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref activeNow)));
        var frames = new ConcurrentQueue<FeelConnectOutput>(); var statuses = new ConcurrentQueue<FeelConnectStatus>();
        activeClient.OutputChanged += frames.Enqueue; activeClient.StatusChanged += statuses.Enqueue;
        await activeClient.PairAsync("synthetic-active-code", Array.Empty<string>());
        active.Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":70}"));
        await Until(() => frames.Any(x => x.Percent == 70));
        Interlocked.Add(ref activeNow, 31);
        await Until(() => statuses.Any(x => x.Phase == "Expired"));
        Assert(frames.Last().IsStop && !statuses.Last().Connected, "live token expiry stops without rearming");
    }

    static async Task RoomRefresh()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); var statuses = new ConcurrentQueue<FeelConnectStatus>();
        client.OutputChanged += frames.Enqueue; client.StatusChanged += statuses.Enqueue;
        await client.PairAsync("synthetic-refresh-code", Array.Empty<string>());
        int initialStatuses = statuses.Count;
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":2},{\"time\":2000,\"subtitle\":4}]}"));
        await Until(() => frames.Any(x => x.Percent == 50));
        fixture.VideoRoom = "replacement-video";
        fixture.Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
        await Until(() => fixture.Joined.Contains("replacement-video") && !fixture.Joined.Contains("test-video") && statuses.Last().Connected);
        Assert(frames.Last().IsStop && statuses.Any(x => x.Phase == "Refreshing" && x.Connected), "room changes clear output while remaining paired");
        Assert(statuses.Skip(initialStatuses).All(x => x.Connected), "valid room refresh preserves the consumer's enabled-output choice");
        await Task.Delay(2100);
        Assert(client.CurrentPercent == 0 && !frames.Any(x => x.Percent == 100), "old queued cue cannot resume after room refresh");
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[90]}"));
        fixture.Socket.Push(Frame("replacement-video", "room:message", "{\"ver\":2,\"percents\":[30]}"));
        await Until(() => frames.Any(x => x.Percent == 30));
        Assert(!frames.Any(x => x.Percent == 90), "removed room cannot drive output");
    }

    static async Task RoomRefreshFailure()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>();
        var statuses = new ConcurrentQueue<FeelConnectStatus>();
        client.OutputChanged += frames.Enqueue; client.StatusChanged += statuses.Enqueue;
        await client.PairAsync("synthetic-failed-refresh-code", Array.Empty<string>());
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[65]}"));
        await Until(() => client.CurrentPercent == 65);
        fixture.RejectRoom = true; fixture.VideoRoom = "replacement-video";
        fixture.Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
        await Until(() => !statuses.Last().Connected);
        Assert(frames.Last().IsStop && client.CurrentPercent == 0, "failed room refresh disconnects and clears output");
    }

    static async Task InvalidTransportStops()
    {
        foreach (int invalid in new[] { 0, 1, 2 })
        {
            var fixture = new ServiceFixture();
            using var http = new HttpClient(fixture);
            await using var client = new FeelConnectClient(http, fixture.Connect);
            var frames = new ConcurrentQueue<FeelConnectOutput>(); var statuses = new ConcurrentQueue<FeelConnectStatus>();
            client.OutputChanged += frames.Enqueue; client.StatusChanged += statuses.Enqueue;
            await client.PairAsync("synthetic-invalid-code", Array.Empty<string>());
            fixture.Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":70}"));
            await Until(() => frames.Any(x => x.Percent == 70));
            if (invalid == 0) fixture.Socket.Push("42/pubnub,[invalid json");
            else if (invalid == 1) fixture.Socket.PushBinary();
            else fixture.Socket.PushOversize();
            await Until(() => !statuses.Last().Connected);
            Assert(frames.Last().IsStop, "invalid JSON/binary/oversize transport stops output");
        }
    }

    static async Task RoomCreatedDuringPresence()
    {
        var fixture = new ServiceFixture { ChangeRoomDuringPresence = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); bool connected = false; int earlyNonzero = 0;
        client.StatusChanged += status => connected = status.Connected;
        client.OutputChanged += output => { frames.Enqueue(output); if (!connected && output.Percent > 0) Interlocked.Increment(ref earlyNonzero); };
        await client.PairAsync("synthetic-startup-code", Array.Empty<string>());
        await Until(() => fixture.Joined.Contains("startup-video") && !fixture.Joined.Contains("test-video"));
        Assert(earlyNonzero == 0, "startup cue remains buffered until pairing acknowledges readiness");
        Assert(frames.Any(x => x.Percent == 100), "fresh startup cue retained for receive-only preview");
        await Task.Delay(50);
        fixture.Socket.Push(Frame("startup-video", "room:message", "{\"ver\":2,\"percents\":[40]}"));
        await Until(() => frames.Any(x => x.Percent == 40));
        Assert(frames.Any(x => x.Percent == 40), "room creation during presence is retained and joined");
    }

    static async Task FirstCueDuringRefresh()
    {
        var fixture = new ServiceFixture { FastCueDuringJoin = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        bool connected = false, refreshing = false; int premature = 0;
        client.StatusChanged += status => { connected = status.Connected; refreshing = status.Phase == "Refreshing"; };
        client.OutputChanged += output => { if ((!connected || refreshing) && output.Percent > 0) Interlocked.Increment(ref premature); };
        await client.PairAsync("synthetic-refresh-buffer-code", Array.Empty<string>());
        fixture.VideoRoom = "replacement-video";
        fixture.Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
        await Until(() => client.Diagnostics.AcceptedCues == 1 && !refreshing);
        Assert(premature == 0 && client.CurrentPercent == 0, "untimed V2 received before join ACK is counted but cannot replay after refresh");
        fixture.Socket.Push(Frame("replacement-video", "room:message", "{\"ver\":2,\"percents\":[42]}"));
        await Until(() => client.CurrentPercent == 42);
        Assert(client.CurrentPercent == 42, "fresh post-refresh V2 can start output");
    }

    static async Task RemovedBufferedRoomAndPublisherLeave()
    {
        var fixture = new ServiceFixture { OldCueDuringRoomRefresh = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += frames.Enqueue;
        await client.PairAsync("synthetic-removed-buffer-code", Array.Empty<string>());
        fixture.VideoRoom = "replacement-video";
        fixture.Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
        await Until(() => fixture.Joined.Contains("replacement-video") && client.Diagnostics.Connected);
        await Task.Delay(80);
        Assert(client.CurrentPercent == 0 && !frames.Any(x => x.Percent > 0), "buffered cue from a removed room is never replayed");
        await client.DisconnectAsync();

        var leaving = new ServiceFixture { LeaveAfterBufferedCue = true };
        using var leavingHttp = new HttpClient(leaving);
        await using var leavingClient = new FeelConnectClient(leavingHttp, leaving.Connect);
        var leavingFrames = new ConcurrentQueue<FeelConnectOutput>(); leavingClient.OutputChanged += leavingFrames.Enqueue;
        await leavingClient.PairAsync("synthetic-leaving-buffer-code", Array.Empty<string>());
        Assert(leavingClient.CurrentPercent == 0 && !leavingFrames.Any(x => x.Percent > 0), "publisher leave cancels its buffered play before ready");
        Assert(leavingClient.Diagnostics.AcceptedCues == 1, "presence stop does not count as a new cue");
    }

    static string Frame(string room, string type, string data) => "42/pubnub,[\"message\",{\"room\":" + JsonSerializer.Serialize(room) + ",\"message_type\":" + JsonSerializer.Serialize(type) + ",\"data\":" + data + "}]";
    static async Task HybridRouting()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, enableLegacy: true);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += frames.Enqueue;
        await client.PairAsync("synthetic-hybrid-code", Array.Empty<string>());
        Assert(fixture.LegacyHeartbeatCount > 0 && fixture.LegacySubscribeCount > 0, "hybrid waits for legacy presence and subscription");
        Assert(http.Timeout == Timeout.InfiniteTimeSpan, "longpoll is not interrupted by short HTTP client timeout");
        fixture.LegacyPush("test-website", "{\"what\":\"device_speed\",\"strength\":42}");
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[81]}"));
        await Task.Delay(80);
        Assert(!frames.Any(x => x.Percent == 42) && frames.Any(x => x.Percent == 81), "website migration is scoped separately while authorized video works over FEC");
        fixture.Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":75}"));
        await Until(() => frames.Any(x => x.Percent == 75));
        fixture.LegacyPush("test-video", "{\"ver\":2,\"percents\":[55]}");
        await Until(() => frames.Any(x => x.Percent == 55));
        Assert(frames.Any(x => x.Percent == 75) && frames.Any(x => x.Percent == 55), "FEC website and legacy video both route through decoder");
        fixture.LegacyPush("test-video", "{\"ver\":3,\"type\":\"stop\"}");
        await Until(() => frames.Last().IsStop);
        Assert(client.CurrentPercent == 0, "legacy stop clears the live output snapshot");
        fixture.Socket.Push(Frame("test-video", "device:position", "{\"what\":\"device_percent\",\"payload\":63}"));
        await Until(() => client.CurrentPercent == 63);
        Assert(frames.Any(x => x.Percent == 63), "explicit modern position bypasses the legacy-video migration flag");
        fixture.Socket.Push(Frame("test-video", "room:play", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":4}]}"));
        await Until(() => client.CurrentPercent == 100);
        Assert(frames.Any(x => x.Percent == 100), "explicit modern timed play bypasses the legacy-video migration flag");
        fixture.Socket.Push(Frame("test-video", "room:stop", "{\"ver\":3,\"type\":\"stop\"}"));
        await Until(() => client.CurrentPercent == 0);
        Assert(frames.Last().IsStop, "explicit modern stop clears the timed cue under legacy-video migration flags");
        var diagnostics = client.Diagnostics;
        Assert(diagnostics.VideoRooms == 1 && diagnostics.FecMessages > 0 && diagnostics.LegacyMessages > 0 && diagnostics.AcceptedCues >= 3, "safe diagnostics expose both transports and accepted cues");
        string diagnosticJson = JsonSerializer.Serialize(diagnostics);
        Assert(diagnosticJson.Contains("\"videoRooms\"") && !diagnosticJson.Contains("test-video") && !diagnosticJson.Contains("synthetic-") && !diagnosticJson.Contains("fixture-publisher"), "diagnostics serialize aggregate fields without private identifiers");
        await client.DisconnectAsync();
        Assert(fixture.LegacyLeaveCount > 0 && fixture.PresenceOnline == 0, "hybrid disconnect withdraws both presence transports");
    }
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    static async Task TransportDiagnostics()
    {
        long bounded = 999_999_999;
        FeelConnectCounters.Increment(ref bounded); FeelConnectCounters.Increment(ref bounded);
        Assert(bounded == 1_000_000_000, "diagnostic counters saturate without overflow");
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, enableLegacy: true);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += frames.Enqueue;
        await client.PairAsync("synthetic-diagnostics-code", Array.Empty<string>());
        string stamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000).ToString();
        string oldStamp = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10000) * 10000).ToString();
        string Legacy(string room, object data, string published, int type = 0) => JsonSerializer.Serialize(new { c = room, d = data, p = new { t = published }, e = type });
        var nonzero = new { ver = 2, percents = new[] { 99 } };
        fixture.LegacyMessages.Writer.TryWrite(Legacy("test-video", "private-serialized-payload", stamp));
        fixture.LegacyMessages.Writer.TryWrite(Legacy("private-outside-room", nonzero, stamp));
        fixture.LegacyMessages.Writer.TryWrite(Legacy("test-video", nonzero, stamp, 2));
        fixture.LegacyMessages.Writer.TryWrite("{\"c\":\"test-video\",\"d\":{\"ver\":2,\"percents\":[99]}}");
        fixture.LegacyMessages.Writer.TryWrite(Legacy("test-video", nonzero, oldStamp));
        string valid = Legacy("test-video", new { ver = 2, percents = new[] { 27 } }, stamp);
        fixture.LegacyMessages.Writer.TryWrite(valid);
        fixture.LegacyMessages.Writer.TryWrite(valid);
        await Until(() => client.Diagnostics.Transport.LegacyDroppedReplay == 1);
        var legacy = client.Diagnostics.Transport;
        Assert(legacy.LegacyRawFrames == 7 && legacy.LegacyDroppedShape == 1 && legacy.LegacyDroppedScope == 1 &&
            legacy.LegacyDroppedType == 1 && legacy.LegacyDroppedTime == 1 && legacy.LegacyDroppedStale == 1 && legacy.LegacyDroppedReplay == 1,
            "raw legacy traffic reports each rejection category before decoder filtering");
        Assert(client.Diagnostics.AcceptedCues == 1 && client.CurrentPercent == 27 && !frames.Any(x => x.Percent == 99),
            "diagnostics preserve all rejection and replay filters");
        fixture.Socket.Push("42/pubnub,[\"device:position\",{\"payload\":99}]");
        fixture.Socket.Push("42/pubnub,[\"system:presence\",{\"action\":\"join\"}]");
        fixture.Socket.Push("42/pubnub,[\"private-unknown-event\",{\"private-key\":\"private-value\"}]");
        fixture.Socket.Push("42/pubnub,[\"message\",{},{}]");
        fixture.Socket.Push(Frame("test-video", "webshare:session_update", "{\"devices\":[]}"));
        fixture.Socket.Push(Frame("test-video", "private-unknown-message", "{\"percents\":[99]}"));
        fixture.Socket.Push(Frame("test-video", "private-unknown-message", "\"private-payload-value\""));
        await Until(() => client.Diagnostics.Transport.UnknownNonObjectDataMessages == 1);
        var transport = client.Diagnostics.Transport;
        Assert(transport.FecOtherEvents == 4 && transport.FecControlEvents == 1 && transport.FecSessionEvents == 1 && transport.FecUnknownEvents == 2,
            "non-message socket events use only fixed diagnostic categories");
        Assert(transport.SessionMetadataMessages == 1 && transport.UnknownObjectDataMessages == 2 &&
            transport.UnknownNonObjectDataMessages == 1 && transport.UnknownCueShapeMessages == 1,
            "unknown envelope shapes distinguish metadata and control-shaped data without values");
        Assert(client.Diagnostics.AcceptedCues == 1 && client.CurrentPercent == 27,
            "diagnostic-only events cannot activate or replace an output");
        string json = JsonSerializer.Serialize(client.Diagnostics);
        Assert(json.Contains("\"legacyRawFrames\":7") && json.Contains("\"fecOtherEvents\":4") &&
            !json.Contains("private-") && !json.Contains("test-video") && !json.Contains("synthetic-") && !json.Contains(stamp),
            "transport diagnostic serialization contains no private event, payload, scope, token or timestamp values");
    }

    static async Task PayloadShapeDiagnostics()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += frames.Enqueue;
        await client.PairAsync("synthetic-shape-code", Array.Empty<string>());
        void Envelope(object envelope) => fixture.Socket.Push("42/pubnub,[\"message\"," + JsonSerializer.Serialize(envelope) + "]");
        void Data(object data) => Envelope(new { room = "test-video", message_type = "private-kind", data });
        Envelope(new { room = "test-video", message_type = "private-kind" });
        Data(null); Data("private-plain-data"); Data(new[] { 99 }); Data(99); Data(true);
        Envelope(new { room = "test-video", message_type = "private-kind", ver = 2, percents = new[] { 99 } });
        Data("{\"ver\":2,\"percents\":[99],\"private-value\":\"private-marker\"}");
        Data("{\"notice\":\"private-marker\"}");
        Data("{private-malformed-json");
        Data("{\"percents\":[99],\"private-value\":\"" + new string('x', 17000) + "\"}");
        Envelope("{\"room\":\"private-room\",\"data\":{\"ver\":2,\"percents\":[99]}}");
        Envelope(new[] { 99 }); Envelope(null);
        Data("{\"private-value\":" + new string('[', 17) + "0" + new string(']', 17) + "}");
        Envelope("private-plain-envelope");
        await Until(() => client.Diagnostics.Transport.UnknownNonObjectDataMessages == 16);
        var shape = client.Diagnostics.Transport;
        Assert(shape.UnknownMissingDataMessages == 6 && shape.UnknownNullDataMessages == 1 &&
            shape.UnknownStringDataMessages == 6 && shape.UnknownArrayDataMessages == 1 && shape.UnknownScalarDataMessages == 2,
            "missing, null, string, array and scalar payload shapes are distinguished");
        Assert(shape.UnknownNonObjectEnvelopeMessages == 4 && shape.UnknownJsonObjectEnvelopeMessages == 1,
            "non-object outer envelopes distinguish a serialized object without routing it");
        Assert(shape.UnknownEnvelopeCueShapeMessages == 1 && shape.UnknownJsonObjectDataMessages == 2 && shape.UnknownJsonCueShapeMessages == 2,
            "control-shaped top-level and bounded serialized objects are counted without values");
        Assert(client.Diagnostics.AcceptedCues == 0 && client.CurrentPercent == 0 && frames.All(x => x.IsStop),
            "diagnostic shape inspection never interprets a payload as an active cue");
        string json = JsonSerializer.Serialize(client.Diagnostics);
        Assert(!json.Contains("private-") && !json.Contains("test-video") && !json.Contains("synthetic-"),
            "nested serialized private values cannot enter diagnostics");
    }

    static async Task DeviceRegistrationUpdates()
    {
        var fixture = new ServiceFixture { WebsiteLabel = "\u202E\nFixture\t " + new string('x', 100) };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        Assert(client.WebsiteName == "", "website label is empty before pairing");
        await client.PairAsync("synthetic-device-update-code", Array.Empty<string>());
        Assert(client.WebsiteName.Length == 80 && client.WebsiteName.StartsWith("Fixture ") &&
            !client.WebsiteName.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format),
            "local website label is bounded and strips control/format characters");
        Assert(!JsonSerializer.Serialize(client.Diagnostics).Contains("Fixture"), "website name stays outside numeric diagnostics");
        await client.UpdateDevicesAsync(new[] { "Later device", "Second device", "Later device", null });
        Assert(fixture.PresenceOnlineRequests == 2 && fixture.PresenceNames.SequenceEqual(new[] { "Later device", "Second device" }),
            "devices connected after pairing refresh the existing server registration");
        await client.UpdateDevicesAsync(new[] { "Second device", "Later device" });
        Assert(fixture.PresenceOnlineRequests == 2, "unchanged device sets do not create repeated presence requests");
        await client.UpdateDevicesAsync(Array.Empty<string>());
        Assert(fixture.PresenceOnlineRequests == 3 && fixture.PresenceNames.Length == 0, "device removal clears advertised device list");
        fixture.HoldDeviceUpdate = true;
        Task activeUpdate = client.UpdateDevicesAsync(new[] { "Held device" });
        await fixture.DeviceUpdateEntered.Task;
        Task queuedUpdate = client.UpdateDevicesAsync(new[] { "Queued device" });
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        foreach (Task task in new[] { activeUpdate, queuedUpdate })
            try { await task; } catch (OperationCanceledException) { }
        Assert(fixture.PresenceOnlineRequests == 4 && fixture.PresenceTransitions.Last() == 0 && fixture.PresenceOnline == 0,
            "disconnect cancels active/queued updates and sends offline after all online requests");
        await client.UpdateDevicesAsync(new[] { "Disconnected device" });
        Assert(fixture.PresenceOnlineRequests == 4 && client.WebsiteName == "", "disconnected updates cannot reannounce online or retain website label");
    }

    static async Task PlaybackModesAndReconnect()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        var outputs = new ConcurrentQueue<FeelConnectOutput>();
        client.OutputChanged += outputs.Enqueue;
        await client.PairAsync("feelapp://connect?code=synthetic-mode-code", new[] { "Initial device" });
        Assert(client.Diagnostics.PlaybackMode == "timed", "timed playback remains the default");
        AssertLegacyMode(fixture.LegacyHeartbeats.Last(), 3, "test-video");
        AssertLegacyMode(fixture.LegacySubscriptions.First(), 3, "test-video");
        string oldUuid = fixture.LegacyHeartbeats.Last().Uuid;
        fixture.LegacyPush("test-video", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":4},{\"time\":250,\"subtitle\":1}]}");
        await Until(() => client.CurrentPercent == 100);
        await client.UpdateDevicesAsync(new[] { "Later device" });
        int boundary = outputs.Count;
        fixture.VideoRoom = "replacement-video";
        await client.ReconnectAsync(FeelConnectPlaybackMode.Live);
        Assert(client.Diagnostics.Connected && client.Diagnostics.PlaybackMode == "live", "explicit reconnect selects live mode");
        AssertLegacyMode(fixture.LegacyHeartbeats.Last(), 2, "replacement-video");
        AssertLegacyMode(fixture.LegacySubscriptions.Last(), 2, "replacement-video");
        Assert(fixture.LegacyHeartbeats.Last().Uuid != oldUuid && fixture.LegacyLeaves.Contains(oldUuid),
            "mode reconnect leaves old presence and creates a new mobile UUID");
        Assert(fixture.AuthKinds.Count == 2 && fixture.AuthKinds.All(kind => kind == "connection-code"),
            "reconnect preserves connection-code exchange semantics");
        Assert(fixture.PresenceNames.SequenceEqual(new[] { "Later device" }), "reconnect retains the latest connected device names");
        await Task.Delay(300);
        Assert(outputs.Skip(boundary).Any(output => output.IsStop) && outputs.Skip(boundary).All(output => output.Percent == 0),
            "mode switch resets output and invalidates old queued cues");
        Assert(!JsonSerializer.Serialize(client.Diagnostics).Contains("synthetic-mode-code"), "mode diagnostics contain no pairing value");

        bool invalid = false;
        try { await client.ReconnectAsync((FeelConnectPlaybackMode)99); } catch (ArgumentOutOfRangeException) { invalid = true; }
        Assert(invalid && client.Diagnostics.Connected && client.Diagnostics.PlaybackMode == "live", "invalid reconnect mode preserves active session");
        invalid = false;
        try { await client.PairAsync("synthetic-invalid-mode", Array.Empty<string>(), playbackMode: (FeelConnectPlaybackMode)99); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Assert(invalid && fixture.AuthKinds.Count == 2, "invalid pair mode is rejected before replacing the session");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await client.ReconnectAsync(FeelConnectPlaybackMode.Timed, canceled.Token); } catch (OperationCanceledException) { }
        Assert(client.Diagnostics.Connected && client.Diagnostics.PlaybackMode == "live", "pre-canceled reconnect leaves existing mode alone");

        await client.DisconnectAsync();
        await client.PairAsync("synthetic-live-token", Array.Empty<string>(), playbackMode: FeelConnectPlaybackMode.Live);
        AssertLegacyMode(fixture.LegacyHeartbeats.Last(), 2, "replacement-video");
        Assert(fixture.AuthKinds.Last() == "token", "explicit live pairing supports ordinary QR tokens");
        await client.DisconnectAsync();
        invalid = false;
        try { await client.ReconnectAsync(FeelConnectPlaybackMode.Timed); } catch (InvalidOperationException) { invalid = true; }
        Assert(invalid, "disconnected client cannot reuse erased credentials");
        invalid = false;
        try { _ = new FeelConnectLegacy(http, new[] { "test-video" }, (_, _) => { }, CancellationToken.None, messageHandlerVersion: 1); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Assert(invalid, "unsupported legacy handler version is rejected");
    }

    static void AssertLegacyMode((string Uuid, string State) request, int expected, string videoRoom)
    {
        using var state = JsonDocument.Parse(request.State);
        Assert(state.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name)
            .SequenceEqual(new[] { videoRoom, "test-website" }.OrderBy(name => name)), "mode advertises only currently authorized rooms");
        Assert(state.RootElement.EnumerateObject().All(property => property.Value.GetProperty("messageHandlerVersion").GetInt32() == expected),
            "chosen handler version is advertised on website and video channels");
    }

    static async Task PlaybackModeCancellation()
    {
        var fixture = new ServiceFixture();
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-cancel-mode", Array.Empty<string>());
        fixture.DelayAuth = true;
        fixture.AuthEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task switching = client.ReconnectAsync(FeelConnectPlaybackMode.Live, cancellation.Token);
        await fixture.AuthEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try { await switching; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
        await client.DisconnectAsync();
        Assert(!client.Diagnostics.Connected && client.CurrentPercent == 0 && fixture.ConnectionCount == 1,
            "cancellation during mode exchange cannot restart old output or finish a new transport");

        fixture.DelayAuth = false;
        await client.PairAsync("synthetic-superseded-mode", Array.Empty<string>());
        int authCount = fixture.AuthKinds.Count;
        fixture.HoldLegacyLeave = true;
        switching = client.ReconnectAsync(FeelConnectPlaybackMode.Live);
        await fixture.LegacyLeaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disconnecting = client.DisconnectAsync();
        fixture.ReleaseLegacyLeave.TrySetResult();
        bool superseded = false;
        try { await switching; } catch (OperationCanceledException) { superseded = true; }
        await disconnecting;
        Assert(superseded && fixture.AuthKinds.Count == authCount && !client.Diagnostics.Connected,
            "disconnect during old-session cleanup supersedes queued mode reconnect without reauthorization");
    }

    static async Task LegacyTerminalReasons()
    {
        foreach (var failure in new[] {
            (Kind: "timeout", Phase: "Timeout", Message: "The legacy video request timed out. Pair again to reconnect."),
            (Kind: "http", Phase: "Rejected", Message: "The legacy video service rejected a request. Pair again to reconnect."),
            (Kind: "json", Phase: "Unsupported", Message: "The legacy video service returned an unsupported response. Pair again to reconnect."),
            (Kind: "shape", Phase: "Unsupported", Message: "The legacy video service returned an unsupported response. Pair again to reconnect."),
            (Kind: "connection", Phase: "Disconnected", Message: "The legacy video connection was lost. Pair again to reconnect.") })
        {
            using var fixture = new ServiceFixture { LegacyFault = failure.Kind, HoldLegacyLeave = true };
            using var http = new HttpClient(fixture);
            await using var client = new FeelConnectClient(http, fixture.Connect, enableLegacy: true);
            var statuses = new ConcurrentQueue<FeelConnectStatus>(); var frames = new ConcurrentQueue<FeelConnectOutput>();
            client.StatusChanged += statuses.Enqueue; client.OutputChanged += frames.Enqueue;
            // Consumer exceptions must not prevent the terminal cause being stored or cancellation running.
            client.StatusChanged += _ => throw new Exception("private-consumer-marker");
            client.OutputChanged += _ => throw new Exception("private-consumer-marker");
            await client.PairAsync("synthetic-terminal-code", Array.Empty<string>());
            fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[60]}"));
            await Until(() => client.CurrentPercent == 60);
            fixture.LegacyMessages.Writer.TryWrite("synthetic-failure-trigger");
            await fixture.LegacyLeaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(!client.Diagnostics.Connected && client.CurrentPercent == 0 && frames.Last().IsStop,
                failure.Kind + " stops output before held leave cleanup finishes");
            fixture.ReleaseLegacyLeave.TrySetResult();
            await Until(() => fixture.PresenceOnline == 0); await Task.Delay(30);
            Assert(statuses.Last().Phase == failure.Phase && statuses.Last().Message == failure.Message,
                failure.Kind + " retains authoritative terminal reason after sibling cancellation and cleanup");
            Assert(!JsonSerializer.Serialize(statuses.ToArray()).Contains("private-", StringComparison.Ordinal),
                failure.Kind + " never exposes upstream body, URI, exception or observer details");
        }
        using var startup = new ServiceFixture { LegacyFault = "json", FailLegacyPrime = true }; using var startupHttp = new HttpClient(startup);
        await using var startingClient = new FeelConnectClient(startupHttp, startup.Connect, enableLegacy: true);
        string rejected = "";
        try { await startingClient.PairAsync("synthetic-startup-failure", Array.Empty<string>()); }
        catch (InvalidOperationException error) { rejected = error.Message; }
        Assert(startingClient.Diagnostics.Phase == "Unsupported" && rejected == "The legacy video service returned an unsupported response. Pair again to reconnect.",
            "startup failure is preserved in the pairing result and final status");
    }

    static async Task ExplicitActionsSupersedeLegacyFailure()
    {
        foreach (bool replace in new[] { false, true })
        {
            using var fixture = new ServiceFixture { LegacyFault = "http", HoldLegacyLeave = true }; using var http = new HttpClient(fixture);
            await using var client = new FeelConnectClient(http, fixture.Connect, enableLegacy: true);
            var statuses = new ConcurrentQueue<FeelConnectStatus>(); client.StatusChanged += statuses.Enqueue;
            await client.PairAsync("synthetic-old-terminal", Array.Empty<string>());
            fixture.LegacyMessages.Writer.TryWrite("synthetic-failure-trigger");
            await fixture.LegacyLeaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            int marker = statuses.Count; fixture.LegacyFault = "";
            Task action = replace ? client.PairAsync("synthetic-replacement", Array.Empty<string>()) : client.DisconnectAsync();
            Assert(statuses.Last().Phase == "Disconnected" && !statuses.Last().Message.Contains("legacy", StringComparison.Ordinal),
                "explicit action overrides old failure while cleanup is still held");
            fixture.ReleaseLegacyLeave.TrySetResult(); await action;
            Assert(replace ? client.Diagnostics.Connected && statuses.Last().Phase == "Connected" :
                !client.Diagnostics.Connected && statuses.Last().Phase == "Disconnected" && statuses.Last().Message == "Disconnected",
                replace ? "new pairing owns the final status" : "explicit disconnect finishes with clean status");
            Assert(!statuses.Skip(marker).Any(status => status.Message.Contains("legacy video service rejected", StringComparison.Ordinal)),
                "old terminal reason cannot reappear after explicit cancellation or replacement");
        }
    }

    sealed class ServiceFixture : HttpMessageHandler
    {
        public MemorySocket Socket;
        public bool RejectAuth, RejectRoom, DelayAuth, Authenticated, ChangeRoomDuringPresence, FastCueDuringJoin, OldCueDuringRoomRefresh, LeaveAfterBufferedCue;
        public int ConnectionCount, PresenceOnline = -1, PresenceOnlineRequests;
        public bool HoldDeviceUpdate;
        public string WebsiteLabel = "Fixture";
        public readonly ConcurrentQueue<int> PresenceTransitions = new();
        public readonly TaskCompletionSource DeviceUpdateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? TokenExpiry;
        public string[] PresenceNames = Array.Empty<string>();
        public string VideoRoom = "test-video";
        public HashSet<string> Joined = new(StringComparer.Ordinal);
        public readonly Channel<string> LegacyMessages = Channel.CreateUnbounded<string>();
        public int LegacyHeartbeatCount, LegacySubscribeCount, LegacyLeaveCount;
        public readonly ConcurrentQueue<(string Uuid, string State)> LegacyHeartbeats = new(), LegacySubscriptions = new();
        public readonly ConcurrentQueue<string> LegacyLeaves = new(), AuthKinds = new();
        public bool HoldLegacyLeave;
        public string LegacyFault = "";
        public bool FailLegacyPrime;
        public readonly TaskCompletionSource LegacyLeaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLegacyLeave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long legacySequence;
        public void LegacyPush(string channel, string payload)
        {
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + Interlocked.Increment(ref legacySequence);
            LegacyMessages.Writer.TryWrite("{\"c\":" + JsonSerializer.Serialize(channel) + ",\"i\":\"fixture-publisher\",\"p\":{\"t\":\"" + stamp + "\"},\"d\":" + payload + "}");
        }
        public TaskCompletionSource AuthEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WebSocket> Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ConnectionCount++;
            Socket = new MemorySocket(OnSend);
            Socket.Push("0{\"sid\":\"synthetic-engine-id\",\"pingInterval\":25000,\"pingTimeout\":20000}");
            return Task.FromResult<WebSocket>(Socket);
        }
        void OnSend(MemorySocket socket, string packet)
        {
            if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal))
            {
                using var auth = JsonDocument.Parse(packet.Substring(10));
                Authenticated = auth.RootElement.GetProperty("token").GetString().Length > 0;
                socket.Push("40/pubnub,{\"sid\":\"synthetic-namespace-id\"}"); return;
            }
            if (!FeelConnectClient.TrySocketPacket(packet, out int type, out int? id, out string json) || type != 2 || !id.HasValue) return;
            using var data = JsonDocument.Parse(json);
            string action = data.RootElement[0].GetString(), room = data.RootElement[1].GetProperty("room_name").GetString();
            if (action == "room:join") Joined.Add(room); else if (action == "room:leave") Joined.Remove(room);
            if (action == "room:join" && room == "replacement-video" && FastCueDuringJoin)
                socket.Push(Frame(room, "room:message", "{\"ver\":2,\"percents\":[36]}"));
            socket.Push("43/pubnub," + id.Value + "[{\"ok\":" + (RejectRoom ? "false" : "true") + "}]");
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.Host == "ps.pndsn.com")
            {
                Assert(request.Method == HttpMethod.Get && !request.RequestUri.AbsolutePath.Contains("publish", StringComparison.OrdinalIgnoreCase), "legacy transport never publishes");
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .Where(part => part.Length == 2).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                if (request.RequestUri.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    LegacyHeartbeats.Enqueue((query["uuid"], query["state"]));
                    Interlocked.Increment(ref LegacyHeartbeatCount); return Json("{\"status\":200}");
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/leave", StringComparison.Ordinal))
                {
                    LegacyLeaves.Enqueue(query["uuid"]);
                    Interlocked.Increment(ref LegacyLeaveCount);
                    if (HoldLegacyLeave) { LegacyLeaveEntered.TrySetResult(); await ReleaseLegacyLeave.Task.WaitAsync(cancellationToken); }
                    return Json("{\"status\":200}");
                }
                LegacySubscriptions.Enqueue((query["uuid"], query["state"]));
                Interlocked.Increment(ref LegacySubscribeCount);
                bool prime = request.RequestUri.Query.Contains("&tt=0&", StringComparison.Ordinal);
                string message = prime ? "" : await LegacyMessages.Reader.ReadAsync(cancellationToken);
                if (prime && FailLegacyPrime || message == "synthetic-failure-trigger")
                {
                    switch (LegacyFault)
                    {
                        case "timeout": throw new TaskCanceledException("private-timeout-marker https://private.invalid/token");
                        case "http": return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private-body-marker") };
                        case "json": return Json("{private-invalid-json-marker");
                        case "shape": return Json("{\"t\":\"private-malformed-cursor\",\"m\":[]}");
                        case "connection": throw new HttpRequestException("private-connection-marker https://private.invalid/token");
                    }
                }
                string cursor = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000).ToString();
                return Json("{\"t\":{\"t\":\"" + cursor + "\",\"r\":1},\"m\":[" + message + "]}");
            }
            Assert(request.RequestUri.Scheme == "https" && request.RequestUri.Host == "api.feel-app.com", "credentials restricted to API host");
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/firmware", StringComparison.Ordinal)) return Json("{\"FC3_APP_SOCKETIO_TIPS_WEBSITES_CHANNELS_code\":1,\"FC3_APP_SOCKETIO_VIDEO_WEBSITES_CHANNELS_code\":0}");
            if (path.Contains("/auth/", StringComparison.Ordinal) || path.Contains("/authconncode/", StringComparison.Ordinal))
            {
                AuthKinds.Enqueue(path.Contains("/authconncode/", StringComparison.Ordinal) ? "connection-code" : "token");
                AuthEntered.TrySetResult();
                if (DelayAuth) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (RejectAuth) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sensitive upstream diagnostics") };
                string fec = "synthetic-fec-token";
                if (TokenExpiry.HasValue) fec = "fixture." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + TokenExpiry.Value + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
                return Json("{\"access_token\":\"synthetic-access-token\",\"fec_token\":" + JsonSerializer.Serialize(fec) + "}");
            }
            if (path.Contains("/realtime/", StringComparison.Ordinal)) return Json("{\"pubnub_channel\":\"test-website\",\"partner_website\":" + JsonSerializer.Serialize(WebsiteLabel) + "}");
            if (path.Contains("/myrooms/", StringComparison.Ordinal))
            {
                if (OldCueDuringRoomRefresh && Socket != null && VideoRoom == "replacement-video")
                {
                    Socket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"play\",\"subtitles\":[{\"time\":0,\"subtitle\":4}]}"));
                    await Task.Delay(50, cancellationToken);
                }
                return Json("{\"rooms\":[{\"pubnub_channel\":" + JsonSerializer.Serialize(VideoRoom) + "}]}");
            }
            if (path.Contains("/presence2/", StringComparison.Ordinal))
            {
                Assert(request.Method == HttpMethod.Put, "presence uses PUT");
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                PresenceOnline = body.RootElement.GetProperty("online").GetInt32();
                PresenceNames = body.RootElement.GetProperty("devices").EnumerateArray().Select(x => x.GetString()).ToArray();
                PresenceTransitions.Enqueue(PresenceOnline);
                if (PresenceOnline == 1)
                {
                    Interlocked.Increment(ref PresenceOnlineRequests);
                    if (HoldDeviceUpdate)
                    {
                        DeviceUpdateEntered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                }
                if (PresenceOnline == 1 && ChangeRoomDuringPresence)
                {
                    VideoRoom = "startup-video";
                    Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
                    Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":100}"));
                    await Task.Delay(50, cancellationToken);
                }
                if (PresenceOnline == 1 && LeaveAfterBufferedCue)
                {
                    Socket.Push(Frame("test-video", "room:message", "{\"ver\":3,\"type\":\"play\",\"src\":\"fixture-source\",\"subtitles\":[{\"time\":0,\"subtitle\":4}]}"));
                    Socket.Push(Frame("test-video", "system:presence", "{\"action\":\"leave\",\"uuid\":\"fixture-source\"}"));
                    await Task.Delay(50, cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
            }
            throw new Exception("Unexpected synthetic request operation");
        }
        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    sealed class MemorySocket : WebSocket
    {
        readonly Channel<(byte[] Data, bool End, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], bool, WebSocketMessageType)>();
        readonly Action<MemorySocket, string> onSend;
        readonly CancellationTokenSource abort = new();
        WebSocketState state = WebSocketState.Open;
        public ConcurrentQueue<string> Sent = new();
        public MemorySocket(Action<MemorySocket, string> onSend) { this.onSend = onSend; }
        public void Push(string text, bool fragment = false)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            if (data.Length > 8192)
            {
                for (int offset = 0; offset < data.Length; offset += 8192)
                    incoming.Writer.TryWrite((data.Skip(offset).Take(8192).ToArray(), offset + 8192 >= data.Length, WebSocketMessageType.Text));
            }
            else if (fragment)
            {
                incoming.Writer.TryWrite((data.Take(data.Length / 2).ToArray(), false, WebSocketMessageType.Text));
                incoming.Writer.TryWrite((data.Skip(data.Length / 2).ToArray(), true, WebSocketMessageType.Text));
            }
            else incoming.Writer.TryWrite((data, true, WebSocketMessageType.Text));
        }
        public void PushClose() => incoming.Writer.TryWrite((Array.Empty<byte>(), true, WebSocketMessageType.Close));
        public void PushBinary() => incoming.Writer.TryWrite((new byte[] { 1 }, true, WebSocketMessageType.Binary));
        public void PushOversize()
        {
            for (int i = 0; i < 129; i++) incoming.Writer.TryWrite((new byte[8192], i == 128, WebSocketMessageType.Text));
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; abort.Cancel(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override void Dispose() { Abort(); }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, abort.Token);
            var item = await incoming.Reader.ReadAsync(linked.Token);
            if (item.Data.Length > buffer.Count) throw new Exception("Fixture fragment exceeds read buffer");
            item.Data.AsSpan().CopyTo(buffer.AsSpan()); return new WebSocketReceiveResult(item.Data.Length, item.Type, item.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string text = Encoding.UTF8.GetString(buffer); Sent.Enqueue(text); onSend(this, text); return Task.CompletedTask;
        }
    }
}
