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
        TimedPlaybackAndSeek();
        TipPatterns();
        InvalidAndBoundedInput();
        await TransportRoundTrip();
        await RejectedRoomAndCredentialPrivacy();
        await CancellationAndStaleSession();
        await ExpiredSession();
        await RoomRefresh();
        await RoomCreatedDuringPresence();
        await InvalidTransportStops();
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
        Assert(p.Tick(2900).IsStop, "final cue bounded watchdog");
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
        var active = new ServiceFixture { TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds() };
        using var activeHttp = new HttpClient(active);
        await using var activeClient = new FeelConnectClient(activeHttp, active.Connect);
        var frames = new ConcurrentQueue<FeelConnectOutput>(); var statuses = new ConcurrentQueue<FeelConnectStatus>();
        activeClient.OutputChanged += frames.Enqueue; activeClient.StatusChanged += statuses.Enqueue;
        await activeClient.PairAsync("synthetic-active-code", Array.Empty<string>());
        active.Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":70}"));
        await Until(() => frames.Any(x => x.Percent == 70));
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
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[60]}"));
        await Until(() => frames.Any(x => x.Percent == 60));
        fixture.VideoRoom = "replacement-video";
        fixture.Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
        await Until(() => fixture.Joined.Contains("replacement-video") && !fixture.Joined.Contains("test-video") && statuses.Last().Connected);
        Assert(frames.Last().IsStop && statuses.Any(x => x.Phase == "Connecting" && x.Message.Contains("Updating")), "room changes zero output and revoke output ownership");
        fixture.Socket.Push(Frame("test-video", "room:message", "{\"ver\":2,\"percents\":[90]}"));
        fixture.Socket.Push(Frame("replacement-video", "room:message", "{\"ver\":2,\"percents\":[30]}"));
        await Until(() => frames.Any(x => x.Percent == 30));
        Assert(!frames.Any(x => x.Percent == 90), "removed room cannot drive output");
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
        var frames = new ConcurrentQueue<FeelConnectOutput>();
        client.OutputChanged += frames.Enqueue;
        await client.PairAsync("synthetic-startup-code", Array.Empty<string>());
        await Until(() => fixture.Joined.Contains("startup-video") && !fixture.Joined.Contains("test-video"));
        Assert(!frames.Any(x => x.Percent > 0), "early cues ignored during presence registration");
        await Task.Delay(50);
        fixture.Socket.Push(Frame("startup-video", "room:message", "{\"ver\":2,\"percents\":[40]}"));
        await Until(() => frames.Any(x => x.Percent == 40));
        Assert(frames.Any(x => x.Percent == 40), "room creation during presence is retained and joined");
    }

    static string Frame(string room, string type, string data) => "42/pubnub,[\"message\",{\"room\":" + JsonSerializer.Serialize(room) + ",\"message_type\":" + JsonSerializer.Serialize(type) + ",\"data\":" + data + "}]";
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    sealed class ServiceFixture : HttpMessageHandler
    {
        public MemorySocket Socket;
        public bool RejectAuth, RejectRoom, DelayAuth, Authenticated, ChangeRoomDuringPresence;
        public int ConnectionCount, PresenceOnline = -1;
        public long? TokenExpiry;
        public string[] PresenceNames = Array.Empty<string>();
        public string VideoRoom = "test-video";
        public HashSet<string> Joined = new(StringComparer.Ordinal);
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
            socket.Push("43/pubnub," + id.Value + "[{\"ok\":" + (RejectRoom ? "false" : "true") + "}]");
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert(request.RequestUri.Scheme == "https" && request.RequestUri.Host == "api.feel-app.com", "credentials restricted to API host");
            string path = request.RequestUri.AbsolutePath;
            if (path.Contains("/auth/", StringComparison.Ordinal) || path.Contains("/authconncode/", StringComparison.Ordinal))
            {
                AuthEntered.TrySetResult();
                if (DelayAuth) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (RejectAuth) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sensitive upstream diagnostics") };
                string fec = "synthetic-fec-token";
                if (TokenExpiry.HasValue) fec = "fixture." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + TokenExpiry.Value + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
                return Json("{\"access_token\":\"synthetic-access-token\",\"fec_token\":" + JsonSerializer.Serialize(fec) + "}");
            }
            if (path.Contains("/realtime/", StringComparison.Ordinal)) return Json("{\"pubnub_channel\":\"test-website\",\"partner_website\":\"Fixture\"}");
            if (path.Contains("/myrooms/", StringComparison.Ordinal)) return Json("{\"rooms\":[{\"pubnub_channel\":" + JsonSerializer.Serialize(VideoRoom) + "}]}");
            if (path.Contains("/presence2/", StringComparison.Ordinal))
            {
                Assert(request.Method == HttpMethod.Put, "presence uses PUT");
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                PresenceOnline = body.RootElement.GetProperty("online").GetInt32();
                PresenceNames = body.RootElement.GetProperty("devices").EnumerateArray().Select(x => x.GetString()).ToArray();
                if (PresenceOnline == 1 && ChangeRoomDuringPresence)
                {
                    VideoRoom = "startup-video";
                    Socket.Push(Frame("test-website", "website:rooms_changed", "{}"));
                    Socket.Push(Frame("test-website", "website:device_speed", "{\"strength\":100}"));
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
            if (fragment)
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
