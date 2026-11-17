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

// Synthetic transport fixtures only: no real pairing values, websites, devices or network access.
internal static class FeelConnectTransportCompatibilityTests
{
    static int assertions;
    public static async Task Main()
    {
        await MetadataScopeAndLifecycle();
        await FailedJoinDoesNotAnnounce();
        await MetadataDoesNotBlockPlayback();
        await MetadataFailureClosesSession();
        await MirroredVideoBatches();
        await RejectedAndBufferedBatchesDoNotPoisonCache();
        await V2DiagnosticsRespectClientAcceptance();
        Console.WriteLine("FeelConnect transport compatibility: " + assertions + " assertions passed (offline).");
    }

    static void Assert(bool condition, string label)
    { assertions++; if (!condition) throw new Exception("Transport compatibility: " + label); }
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    static object Play(long stamp, double first = 1, string source = "synthetic-producer", double last = 4) => new {
        ver = 3, type = "play", serverTime = stamp, src = source,
        subtitles = new[] { new { time = 0, subtitle = first }, new { time = 200, subtitle = last } }
    };

    static async Task MetadataScopeAndLifecycle()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-metadata-pairing", new[] { " Generic device\n", "Generic device" });
        Assert(fixture.Metadata.Count == 1, "join announces one video room only");
        var announced = fixture.Metadata.Single();
        Assert(announced.GetProperty("message_type").GetString() == "webshare:session_metadata", "exact metadata message type");
        Assert(announced.GetProperty("room").GetString() == "fixture-video" &&
            announced.GetProperty("data").GetProperty("room").GetString() == "fixture-video", "metadata targets its joined video room");
        Assert(announced.GetProperty("data").GetProperty("devices").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "Generic device" }), "metadata uses validated device names");
        Assert(fixture.VideoJoinedBeforeMetadata, "metadata follows successful video acknowledgement");
        int before = fixture.Metadata.Count;
        long messages = client.Diagnostics.FecMessages;
        fixture.Fec("fixture-website", "system:presence", new { action = "join" });
        fixture.Fec("foreign-room", "system:presence", new { action = "join" });
        fixture.Fec("fixture-video", "system:presence", new { action = "leave" });
        fixture.Legacy("fixture-video-pnpres", new { action = "join" });
        await Until(() => client.Diagnostics.FecMessages >= messages + 3 && client.Diagnostics.LegacyMessages > 0);
        await Task.Delay(300);
        Assert(fixture.Metadata.Count == before, "unrelated rooms/actions and legacy presence do not announce");
        await client.UpdateDevicesAsync(new[] { "Replacement device" });
        messages = client.Diagnostics.FecMessages;
        for (int i = 0; i < 100; i++) fixture.Fec("fixture-video", "system:presence", new { action = "join" });
        await Until(() => client.Diagnostics.FecMessages >= messages + 100);
        await Until(() => fixture.Metadata.Count > before);
        await Task.Delay(100);
        Assert(fixture.Metadata.Count == before + 1, "presence burst coalesces to one announcement");
        Assert(fixture.Metadata.Last().GetProperty("data").GetProperty("devices")[0].GetString() == "Replacement device", "peer announcement uses current names");
        fixture.VideoRoom = "replacement-video";
        fixture.Fec("fixture-website", "website:rooms_changed", new { });
        await Until(() => fixture.Metadata.Any(x => x.GetProperty("room").GetString() == "replacement-video"));
        before = fixture.Metadata.Count;
        fixture.Fec("fixture-video", "system:presence", new { action = "join" });
        await Task.Delay(300);
        Assert(fixture.Metadata.Count == before, "removed room cannot request metadata");
        await client.DisconnectAsync();
        fixture.Fec("replacement-video", "system:presence", new { action = "join" });
        await Task.Delay(100);
        Assert(fixture.Metadata.Count == before, "disconnected session cannot announce");
        await client.PairAsync("synthetic-second-pairing", Array.Empty<string>());
        Assert(fixture.Metadata.Last().GetProperty("data").GetProperty("devices").GetArrayLength() == 0, "new session does not retain previous device names");
    }

    static async Task FailedJoinDoesNotAnnounce()
    {
        using var fixture = new Fixture { RejectVideoJoin = true }; using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        bool rejected = false;
        try { await client.PairAsync("synthetic-rejected-pairing", Array.Empty<string>()); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && fixture.Metadata.IsEmpty, "rejected room emits no device announcement");
    }

    static async Task MetadataDoesNotBlockPlayback()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        var output = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += output.Enqueue;
        await client.PairAsync("synthetic-blocked-send", Array.Empty<string>());
        fixture.HoldMetadata = true;
        fixture.Fec("fixture-video", "system:presence", new { action = "join" });
        await fixture.MetadataSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Fec("fixture-video", "room:message", Play(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await Until(() => output.Any(frame => frame.Percent == 100));
        Assert(output.Last().Percent == 100, "metadata send cannot block scheduled cue progression");
        fixture.Fec("fixture-video", "room:message", new { ver = 3, type = "stop" });
        await Until(() => output.Last().IsStop);
        Assert(output.Last().Percent == 0, "metadata send cannot block incoming stop");
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!client.Diagnostics.Connected, "disconnect cancels a blocked metadata send");
    }

    static async Task MirroredVideoBatches()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-dual-transport", Array.Empty<string>());
        Assert(client.Diagnostics.Connected, "pairing does not require a VIDEO firmware flag");
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Play(stamp);
        long accepted = client.Diagnostics.AcceptedCues;
        fixture.Fec("fixture-video", "room:message", play);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 1);
        long legacy = client.Diagnostics.LegacyMessages;
        // Same semantic payload with different property order and transport publisher ID.
        using var reordered = JsonDocument.Parse("{\"subtitles\":[{\"subtitle\":1,\"time\":0},{\"subtitle\":4,\"time\":200}],\"src\":\"synthetic-producer\",\"serverTime\":" + stamp + ",\"type\":\"play\",\"ver\":3}");
        fixture.Legacy("fixture-video", reordered.RootElement);
        await Until(() => client.Diagnostics.LegacyMessages > legacy);
        Assert(client.Diagnostics.AcceptedCues == accepted + 1, "normalized PubNub mirror does not restart an FEC batch");
        await Until(() => client.CurrentPercent == 100);
        fixture.Fec("fixture-video", "room:message", new { ver = 3, type = "stop" });
        await Until(() => client.CurrentPercent == 0);
        accepted = client.Diagnostics.AcceptedCues;
        legacy = client.Diagnostics.LegacyMessages;
        fixture.Legacy("fixture-video", play);
        await Until(() => client.Diagnostics.LegacyMessages > legacy);
        Assert(client.CurrentPercent == 0 && client.Diagnostics.AcceptedCues == accepted, "late mirror cannot restart a stopped batch");

        var next = Play(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 2, last: 2);
        fixture.Legacy("fixture-video", next);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 1);
        long fec = client.Diagnostics.FecMessages;
        fixture.Fec("fixture-video", "room:play", next);
        await Until(() => client.Diagnostics.FecMessages > fec);
        Assert(client.CurrentPercent == 50 && client.Diagnostics.AcceptedCues == accepted + 1, "reverse-order mirror is suppressed; new producer timestamp is accepted");
        legacy = client.Diagnostics.LegacyMessages;
        fixture.Legacy("fixture-video", new { ver = 3, type = "stop", message_type = "room:stop" });
        await Until(() => client.Diagnostics.LegacyMessages > legacy);
        Assert(client.CurrentPercent == 0, "explicit stop remains effective over either video transport");

        accepted = client.Diagnostics.AcceptedCues;
        var changed = Play(stamp, 3, last: 3);
        fixture.Legacy("fixture-video", changed);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 1);
        Assert(client.CurrentPercent == 75, "changed payload with same timestamp is not conflated");
        var otherProducer = Play(stamp, 1, "another-synthetic-producer", 1);
        fixture.Legacy("fixture-video", otherProducer);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 2);
        Assert(client.CurrentPercent == 25, "distinct producer is not conflated");

        var untimed = new { ver = 3, type = "play", src = "synthetic-producer", subtitles = new[] { new { time = 0, subtitle = 2 } } };
        accepted = client.Diagnostics.AcceptedCues;
        fixture.Fec("fixture-video", "room:message", untimed);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 1);
        fixture.Legacy("fixture-video", untimed);
        await Until(() => client.Diagnostics.AcceptedCues == accepted + 2);
        Assert(client.Diagnostics.AcceptedCues == accepted + 2, "timestamp-less payloads are not heuristically deduplicated");
        string diagnostics = JsonSerializer.Serialize(client.Diagnostics);
        Assert(!diagnostics.Contains("synthetic-producer") && !diagnostics.Contains("fixture-video"), "diagnostics retain no source identity or room data");
    }

    static async Task MetadataFailureClosesSession()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect);
        await client.PairAsync("synthetic-failed-metadata", Array.Empty<string>());
        fixture.Fec("fixture-video", "room:message", Play(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 4));
        await Until(() => client.CurrentPercent == 100);
        fixture.FailMetadata = true;
        fixture.Fec("fixture-video", "system:presence", new { action = "join" });
        await Until(() => !client.Diagnostics.Connected);
        Assert(client.CurrentPercent == 0, "metadata send failure closes the session and stops output");
        int attempts = fixture.MetadataFailures;
        await Task.Delay(300);
        Assert(fixture.MetadataFailures == attempts && attempts == 1, "failed metadata is not retried in an infinite loop");
    }

    static async Task RejectedAndBufferedBatchesDoNotPoisonCache()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-rejected-cache", Array.Empty<string>());
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = Play(stamp, 3, last: 3);
        long fec = client.Diagnostics.FecMessages;
        fixture.Fec("fixture-video", "room:message", play, stamp + 120000);
        await Until(() => client.Diagnostics.FecMessages > fec);
        Assert(client.Diagnostics.AcceptedCues == 0, "decoder rejects excessive latency");
        fixture.Legacy("fixture-video", play);
        await Until(() => client.Diagnostics.AcceptedCues == 1);
        Assert(client.CurrentPercent == 75, "rejected FEC packet did not poison valid normalized legacy acceptance");

        await client.DisconnectAsync();
        fixture.StartupPlay = Play(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 2, last: 2);
        await client.PairAsync("synthetic-buffer-cache", Array.Empty<string>());
        await Until(() => client.Diagnostics.AcceptedCues == 1);
        fixture.Legacy("fixture-video", fixture.StartupPlay);
        await Until(() => client.Diagnostics.AcceptedCues == 2);
        Assert(client.CurrentPercent == 50, "startup-buffered packet does not enter armed duplicate cache");
    }

    static async Task V2DiagnosticsRespectClientAcceptance()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-v2-diagnostics", Array.Empty<string>());
        var empty = new FeelConnectV2Diagnostics();
        Assert(client.Diagnostics.V2 == empty, "client starts with empty V2 diagnostics");
        long fec = client.Diagnostics.FecMessages;
        fixture.Fec("foreign-room", "room:message", new { ver = 2, percents = new[] { 100, 0 } });
        fixture.Fec("fixture-video", "room:message", new { ver = 2, percents = new[] { 100, 101 } });
        await Until(() => client.Diagnostics.FecMessages >= fec + 2);
        Assert(client.Diagnostics.V2 == empty && client.Diagnostics.AcceptedCues == 0,
            "foreign and invalid FEC packets cannot populate accepted V2 diagnostics");
        long legacy = client.Diagnostics.LegacyMessages;
        fixture.Legacy("fixture-website", new { ver = 2, percents = new[] { 99, 0 } });
        await Until(() => client.Diagnostics.LegacyMessages > legacy);
        Assert(client.Diagnostics.V2 == empty, "website transport migration rejection occurs before V2 statistics");

        fixture.Fec("fixture-video", "room:message", new { ver = 2, percents = new[] { 0, 0 } });
        await Until(() => client.Diagnostics.V2.AcceptedBatches == 1);
        fixture.Legacy("fixture-video", new { ver = 2, percents = new[] { 25, 0 } });
        await Until(() => client.Diagnostics.V2.AcceptedBatches == 2);
        var mixed = client.Diagnostics.V2;
        Assert(mixed.Samples == 4 && mixed.PositiveSamples == 1 && mixed.MixedBatches == 1 &&
            mixed.LastBatchSamples == 2 && mixed.LastBatchMaximum == 25 && mixed.LastBatchFinal == 0 && client.CurrentPercent == 0,
            "client exposes valid batches from both authorized video transports without changing zero output");
        Assert(client.Diagnostics.AcceptedCues == mixed.AcceptedBatches, "V2 counts agree with accepted cue stage");
        string json = JsonSerializer.Serialize(client.Diagnostics);
        Assert(json.Contains("\"v2\":{") && !json.Contains("fixture-video") && !json.Contains("route-publisher") && !json.Contains("synthetic-v2"),
            "public diagnostic projection contains no pairing, room or producer identifiers");

        await client.DisconnectAsync();
        Assert(client.Diagnostics.V2 == empty, "removed session exposes empty V2 diagnostics");
        fixture.StartupPlay = new { ver = 2, percents = new[] { 80, 0 } };
        await client.PairAsync("synthetic-v2-buffered", Array.Empty<string>());
        Assert(client.Diagnostics.V2.AcceptedBatches == 1 && client.Diagnostics.AcceptedCues == 1 &&
            client.Diagnostics.V2.PositiveSamples == 1 && client.CurrentPercent == 0,
            "authorized startup-buffered acceptance is counted once in the new session");
    }

    sealed class Fixture : HttpMessageHandler
    {
        public MemorySocket Socket;
        public string VideoRoom = "fixture-video";
        public bool RejectVideoJoin, VideoJoinedBeforeMetadata = true, HoldMetadata, FailMetadata;
        public int MetadataFailures;
        public object StartupPlay;
        public readonly TaskCompletionSource MetadataSendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<JsonElement> Metadata = new();
        readonly HashSet<string> joined = new(StringComparer.Ordinal);
        readonly Channel<string> legacy = Channel.CreateUnbounded<string>();
        long sequence;
        public Task<WebSocket> Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); joined.Clear();
            Socket = new MemorySocket(Sent);
            Socket.Push("0{\"sid\":\"fixture-engine\",\"pingInterval\":25000,\"pingTimeout\":20000}");
            return Task.FromResult<WebSocket>(Socket);
        }
        public void Fec(string room, string kind, object data, long? serverTime = null)
        {
            Socket.Push("42/pubnub," + JsonSerializer.Serialize(new object[] { "message", new {
                room, message_type = kind, data, publisher = "fec-route-publisher",
                serverTime = serverTime ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            } }));
        }
        public void Legacy(string room, object data)
        {
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + Interlocked.Increment(ref sequence);
            legacy.Writer.TryWrite(JsonSerializer.Serialize(new { c = room, d = data, i = "pubnub-route-publisher", p = new { t = stamp.ToString() } }));
        }
        async Task Sent(string packet, CancellationToken token)
        {
            if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal)) { Socket.Push("40/pubnub,{\"sid\":\"fixture-namespace\"}"); return; }
            if (!FeelConnectClient.TrySocketPacket(packet, out int type, out int? id, out string json) || type != 2) return;
            using var payload = JsonDocument.Parse(json);
            string action = payload.RootElement[0].GetString();
            var body = payload.RootElement[1];
            if (action == "message")
            {
                if (FailMetadata) { Interlocked.Increment(ref MetadataFailures); throw new WebSocketException("Synthetic metadata failure"); }
                if (HoldMetadata) { MetadataSendEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                VideoJoinedBeforeMetadata &= joined.Contains(body.GetProperty("room").GetString());
                Metadata.Enqueue(body.Clone()); return;
            }
            if (!id.HasValue) throw new Exception("Fixture expected room acknowledgement ID");
            string room = body.GetProperty("room_name").GetString();
            bool ok = !(RejectVideoJoin && action == "room:join" && room != "fixture-website");
            if (ok) { if (action == "room:join") joined.Add(room); else joined.Remove(room); }
            Socket.Push("43/pubnub," + id + "[{\"ok\":" + (ok ? "true" : "false") + "}]");
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri.AbsolutePath;
            if (request.RequestUri.Host == "ps.pndsn.com")
            {
                if (path.EndsWith("/heartbeat", StringComparison.Ordinal) || path.EndsWith("/leave", StringComparison.Ordinal)) return Json("{\"status\":200}");
                string message = request.RequestUri.Query.Contains("&tt=0&", StringComparison.Ordinal) ? "" : await legacy.Reader.ReadAsync(token);
                return Json("{\"t\":{\"t\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + "\",\"r\":1},\"m\":[" + message + "]}");
            }
            if (request.RequestUri.Host != "api.feel-app.com") throw new Exception("Unexpected fixture host");
            if (path.Contains("/auth/", StringComparison.Ordinal)) return Json("{\"access_token\":\"synthetic-access\",\"fec_token\":\"synthetic-fec\"}");
            if (path.Contains("/realtime/", StringComparison.Ordinal)) return Json("{\"pubnub_channel\":\"fixture-website\"}");
            if (path.Contains("/myrooms/", StringComparison.Ordinal)) return Json(JsonSerializer.Serialize(new { rooms = new[] { new { pubnub_channel = VideoRoom } } }));
            if (path.EndsWith("/firmware", StringComparison.Ordinal)) return Json("{\"FC3_APP_SOCKETIO_TIPS_WEBSITES_CHANNELS_code\":1}");
            if (path.Contains("/presence2/", StringComparison.Ordinal))
            {
                if (StartupPlay != null)
                {
                    using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                    if (body.RootElement.GetProperty("online").GetInt32() == 1)
                    { Fec(VideoRoom, "room:message", StartupPlay); await Task.Delay(80, token); }
                }
                return Json("{}");
            }
            throw new Exception("Unexpected fixture operation");
        }
        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    sealed class MemorySocket : WebSocket
    {
        readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
        readonly CancellationTokenSource aborted = new();
        readonly Func<string, CancellationToken, Task> send;
        WebSocketState state = WebSocketState.Open;
        public MemorySocket(Func<string, CancellationToken, Task> send) { this.send = send; }
        public void Push(string packet) => inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(packet));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; aborted.Cancel(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            var packet = await inbound.Reader.ReadAsync(linked.Token);
            if (packet.Length > buffer.Count) throw new Exception("Fixture frame too large");
            packet.CopyTo(buffer.AsSpan()); return new WebSocketReceiveResult(packet.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType kind, bool endOfMessage, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return send(Encoding.UTF8.GetString(buffer), token); }
    }
}
