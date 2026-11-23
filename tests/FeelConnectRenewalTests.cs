using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LovenseConnector;

// Synthetic credentials and controllable clock/transports; never contacts a service or hardware.
internal static class FeelConnectRenewalTests
{
    static int assertions;
    public static async Task Main()
    {
        TokenMetadata();
        await RotationPreservesWebsiteAndDropsInterimCues();
        await BoundedRetriesAndHardExpiry();
        await UnchangedAndIncompleteTokens();
        await RejectedRenewal(false); await RejectedRenewal(true);
        await RejectedRoom();
        await ExpiryBeforeNewRoomAcknowledgement();
        await CancelPendingExchange();
        Console.WriteLine("FeelConnect renewal: " + assertions + " assertions passed (offline).");
    }
    static void Assert(bool value, string label)
    { assertions++; if (!value) throw new Exception("Renewal: " + label); }
    static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    static async Task RenewalFinished(FeelConnectClient client, Fixture fixture, int requestCount)
    {
        await Until(() => Volatile.Read(ref fixture.AuthRequests) == requestCount);
        // The HTTP request begins before its failure schedules the next retry. Advancing
        // the fake clock at request entry races that scheduling, especially on busy CI.
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        object sync = typeof(FeelConnectClient).GetField("sync", hidden).GetValue(client);
        Task renewal;
        lock(sync)
        {
            object session = typeof(FeelConnectClient).GetField("current", hidden).GetValue(client);
            renewal = (Task)session.GetType().GetField("RenewalTask").GetValue(session);
        }
        if(renewal==null)throw new Exception("Renewal attempt was not scheduled.");
        await renewal.WaitAsync(TimeSpan.FromSeconds(5));
    }
    static string Token(object payload) => "synthetic." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    static void TokenMetadata()
    {
        var token = FeelConnectClient.JwtRenewal(Token(new { iat = 100, exp = 201 }));
        Assert(token.HasValue && token.Value.ExpiresAt.ToUnixTimeSeconds() == 201 &&
            token.Value.RenewAt == DateTimeOffset.FromUnixTimeMilliseconds(150500), "renewal occurs halfway through token lifetime");
        foreach (var payload in new object[] { new { exp = 200 }, new { iat = 100 }, new { iat = 201, exp = 200 },
            new { iat = 200, exp = 200 }, new { iat = "NaN", exp = "Infinity" }, new { iat = 0, exp = 1e30 }, new { iat = 1.5, exp = 200 } })
            Assert(FeelConnectClient.JwtRenewal(Token(payload)) == null, "incomplete/unordered/out-of-range metadata cannot schedule renewal");
        Assert(FeelConnectClient.JwtRenewal("not-a-jwt") == null, "opaque tokens retain existing expiry-only behavior");
    }

    static async Task RotationPreservesWebsiteAndDropsInterimCues()
    {
        using var f = new Fixture { HoldReplacementJoin = true }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, true, () => f.Now);
        var output = new ConcurrentQueue<FeelConnectOutput>(); client.OutputChanged += output.Enqueue;
        await client.PairAsync("feelapp://connect?code=synthetic-code", new[] { "Synthetic device" });
        var old = f.Sockets.Single();
        old.Push("42/pubnub," + JsonSerializer.Serialize(new object[] { "message", new {
            room = "synthetic-video", message_type = "room:message", publisher = "synthetic-publisher",
            data = new { ver = 3, type = "play", subtitles = new[] { new { time = 0, subtitle = 2 }, new { time = 300, subtitle = 4 } } }
        } }));
        await Until(() => client.CurrentPercent == 50);
        long accepted = client.Diagnostics.AcceptedCues;
        old.DeliverOneAfterCancellation = true;
        f.Advance(41);
        await old.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(client.Diagnostics.Phase == "Refreshing" && client.Diagnostics.Connected && client.CurrentPercent == 0,
            "internal renewal clears old output while retaining explicit connection enablement");
        f.Legacy(85); await Until(() => client.Diagnostics.LegacyMessages > 0);
        old.DelayedPacket.TrySetResult(Frame(90));
        await Until(() => f.PendingJoin != null);
        Assert(f.Sockets.Count == 2 && old.Disposed, "old receive generation drains before replacement is installed");
        var replacement = f.Sockets.Last();
        long fec = client.Diagnostics.FecMessages;
        f.Fec(replacement, 75); await Until(() => client.Diagnostics.FecMessages > fec);
        Assert(client.Diagnostics.AcceptedCues == accepted && client.CurrentPercent == 0, "old-generation, legacy-during-rotation and pre-ACK FEC cues cannot enter decoder");
        int outputMarker = output.Count;
        f.ReleaseJoin();
        await Until(() => client.Diagnostics.Phase == "Connected");
        await Task.Delay(400);
        Assert(client.CurrentPercent == 0 && !output.Skip(outputMarker).Any(x => x.Percent > 0), "successful renewal never replays prior or interim output");
        Assert(f.AuthOperations.SequenceEqual(new[] { "authconncode", "authconncode" }), "connection-code renewal retains the original exchange route");
        Assert(f.RealtimeRequests == 1 && f.RoomRequests == 1 && f.OnlinePresence == 1 && f.OfflinePresence == 0,
            "renewal preserves website identity, room discovery and HTTP presence");
        Assert(f.LegacyInitialSubscriptions == 1 && f.LegacyUuids.Distinct().Count() == 1, "PubNub subscription cursor and receiver UUID survive FEC rotation");
        Assert(f.AuthenticatedTokens.SequenceEqual(new[] { f.InitialToken, f.ReplacementToken }), "replacement socket authenticates using only the renewed FEC token");
        Assert(f.MetadataCount == 2, "replacement video join announces metadata after acknowledgement");
        f.Fec(replacement, 35); await Until(() => client.CurrentPercent == 35);
        f.Legacy(45); await Until(() => client.CurrentPercent == 45);
        Assert(client.CurrentPercent == 45, "fresh cues work on both preserved legacy and replacement FEC transports");
        f.Advance(70); await Task.Delay(100);
        Assert(client.Diagnostics.Connected && f.AuthRequests == 2, "successfully authenticated later expiry outlives the original token without immediate renewal loop");
        await client.UpdateDevicesAsync(new[] { "Changed synthetic device" });
        Assert(f.PresenceCredentials.All(value => value == "original-access"), "device updates retain original website access token");
        await client.DisconnectAsync(); int exchanges = f.AuthRequests;
        f.Advance(10000); await Task.Delay(100);
        Assert(f.AuthRequests == exchanges && f.OfflinePresence == 1, "disconnect stops renewal and reports offline only once");
    }

    static async Task BoundedRetriesAndHardExpiry()
    {
        using var f = new Fixture { InitialLifetime = 1000, ExchangeStatus = HttpStatusCode.ServiceUnavailable, ExchangeResponseDelayMs = 150 }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, true, () => f.Now);
        await client.PairAsync("synthetic-qr", Array.Empty<string>());
        f.Fec(f.Sockets.Single(), 50); await Until(() => client.CurrentPercent == 50);
        f.Advance(41); await RenewalFinished(client, f, 2);
        await Task.Delay(100); Assert(f.AuthRequests == 2 && f.Sockets.Count == 1 && client.CurrentPercent == 50,
            "temporary exchange failure retains usable old transport and avoids hot retry");
        f.Advance(29); await Task.Delay(100); Assert(f.AuthRequests == 2, "retry does not run before minimum spacing");
        f.Advance(2); await RenewalFinished(client, f, 3);
        f.Advance(31); await RenewalFinished(client, f, 4);
        f.Advance(100); await Task.Delay(100);
        Assert(f.AuthRequests == 4 && client.Diagnostics.Connected, "renewal retry budget is bounded at three exchanges");
        f.Advance(1000); await Until(() => client.Diagnostics.Phase == "Expired");
        Assert(client.CurrentPercent == 0 && !client.Diagnostics.Connected, "failed renewal never extends original hard expiry");
    }

    static async Task UnchangedAndIncompleteTokens()
    {
        using var f = new Fixture { ReturnUnchanged = true }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, utcNow: () => f.Now);
        await client.PairAsync("synthetic-same-token", Array.Empty<string>());
        f.Advance(41); await Until(() => f.AuthRequests == 2); await Task.Delay(100);
        Assert(f.Sockets.Count == 1 && f.AuthRequests == 2, "unchanged token does not rotate or reschedule immediately");
        await client.DisconnectAsync();
        using var noIssued = new Fixture { IncludeIssuedAt = false }; using var otherHttp = new HttpClient(noIssued);
        await using var other = new FeelConnectClient(otherHttp, noIssued.Connect, utcNow: () => noIssued.Now);
        await other.PairAsync("synthetic-no-issued", Array.Empty<string>());
        noIssued.Advance(80); await Task.Delay(100);
        Assert(noIssued.AuthRequests == 1 && other.Diagnostics.Connected, "expiry-only token does not invent a renewal schedule");
        noIssued.Advance(30); await Until(() => other.Diagnostics.Phase == "Expired");
        Assert(!other.Diagnostics.Connected, "expiry-only token still enforces its known deadline after clock advancement");
    }

    static async Task RejectedRenewal(bool socketRejection)
    {
        using var f = new Fixture { ExchangeStatus = socketRejection ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
            RejectReplacementAuthentication = socketRejection }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, utcNow: () => f.Now);
        await client.PairAsync("synthetic-rejected-renewal", Array.Empty<string>());
        f.Fec(f.Sockets.Single(), 60); await Until(() => client.CurrentPercent == 60);
        f.Advance(41); await Until(() => client.Diagnostics.Phase == "Rejected");
        Assert(!client.Diagnostics.Connected && client.CurrentPercent == 0, "rejected " + (socketRejection ? "replacement namespace" : "exchange") + " stops the session");
        f.Advance(300); await Task.Delay(100);
        Assert(f.AuthRequests == 2, "rejected renewal is not silently retried");
    }

    static async Task RejectedRoom()
    {
        using var f = new Fixture { RejectReplacementJoin = true }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, utcNow: () => f.Now);
        await client.PairAsync("synthetic-room-rejected", Array.Empty<string>());
        f.Advance(41); await Until(() => client.Diagnostics.Phase == "Rejected");
        Assert(client.CurrentPercent == 0 && !client.Diagnostics.Connected && f.MetadataCount == 1, "failed replacement room ACK cannot extend trust or announce readiness");
    }

    static async Task ExpiryBeforeNewRoomAcknowledgement()
    {
        using var f = new Fixture { HoldReplacementJoin = true }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, utcNow: () => f.Now);
        await client.PairAsync("synthetic-expiry-in-join", Array.Empty<string>());
        f.Advance(41); await Until(() => f.PendingJoin != null);
        f.Advance(60); await Until(() => client.Diagnostics.Phase == "Expired");
        f.ReleaseJoin(); await Task.Delay(100);
        Assert(client.CurrentPercent == 0 && !client.Diagnostics.Connected, "late successful replacement ACK cannot revive the expired original session");
    }

    static async Task CancelPendingExchange()
    {
        using var f = new Fixture { HoldExchange = true }; using var http = new HttpClient(f);
        await using var client = new FeelConnectClient(http, f.Connect, utcNow: () => f.Now);
        await client.PairAsync("synthetic-cancel-renewal", Array.Empty<string>());
        f.Advance(41); await f.ExchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!client.Diagnostics.Connected && f.Sockets.Count == 1 && f.OfflinePresence == 1, "disconnect cancels outstanding renewal without opening another socket");
    }

    static string Frame(double level) => "42/pubnub," + JsonSerializer.Serialize(new object[] { "message", new {
        room = "synthetic-video", message_type = "room:message", publisher = "synthetic-publisher", data = new { ver = 2, percents = new[] { level } }
    } });

    sealed class Fixture : HttpMessageHandler
    {
        readonly DateTimeOffset basis = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        long seconds; long legacySequence;
        readonly Channel<string> legacy = Channel.CreateUnbounded<string>();
        public readonly ConcurrentQueue<MemorySocket> Sockets = new();
        public readonly ConcurrentQueue<string> AuthOperations = new(), AuthenticatedTokens = new(), PresenceCredentials = new(), LegacyUuids = new();
        public readonly TaskCompletionSource ExchangeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Tuple<MemorySocket, int> PendingJoin;
        public int AuthRequests, RealtimeRequests, RoomRequests, OnlinePresence, OfflinePresence, MetadataCount, LegacyInitialSubscriptions;
        public int InitialLifetime = 100;
        public int ExchangeResponseDelayMs;
        public bool IncludeIssuedAt = true, HoldReplacementJoin, RejectReplacementJoin, RejectReplacementAuthentication, HoldExchange, ReturnUnchanged;
        public HttpStatusCode ExchangeStatus = HttpStatusCode.OK;
        public DateTimeOffset Now => basis.AddSeconds(Interlocked.Read(ref seconds));
        public string InitialToken => IncludeIssuedAt ? Token(new { iat = basis.ToUnixTimeSeconds() + 80 - InitialLifetime, exp = basis.ToUnixTimeSeconds() + InitialLifetime }) : Token(new { exp = basis.ToUnixTimeSeconds() + InitialLifetime });
        public string ReplacementToken => Token(new { iat = basis.ToUnixTimeSeconds() + 40, exp = basis.ToUnixTimeSeconds() + 1000 });
        public void Advance(int bySeconds) => Interlocked.Add(ref seconds, bySeconds);
        public void Fec(MemorySocket socket, double level) => socket.Push(Frame(level));
        public void Legacy(double level)
        {
            long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + Interlocked.Increment(ref legacySequence);
            legacy.Writer.TryWrite(JsonSerializer.Serialize(new { c = "synthetic-video", d = new { ver = 2, percents = new[] { level } }, i = "synthetic-producer", p = new { t = stamp.ToString() } }));
        }
        public Task<WebSocket> Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); int index = Sockets.Count;
            var socket = new MemorySocket((self, packet) => Sent(self, packet, index)); Sockets.Enqueue(socket);
            socket.Push("0{\"sid\":\"synthetic-engine\",\"pingInterval\":25000,\"pingTimeout\":20000}");
            return Task.FromResult<WebSocket>(socket);
        }
        void Sent(MemorySocket socket, string packet, int index)
        {
            if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal))
            {
                using var auth = JsonDocument.Parse(packet.Substring(10)); AuthenticatedTokens.Enqueue(auth.RootElement.GetProperty("token").GetString());
                socket.Push(index > 0 && RejectReplacementAuthentication ? "44/pubnub,{}" : "40/pubnub,{\"sid\":\"synthetic-namespace\"}"); return;
            }
            if (!FeelConnectClient.TrySocketPacket(packet, out int type, out int? id, out string json) || type != 2) return;
            using var doc = JsonDocument.Parse(json); string action = doc.RootElement[0].GetString();
            if (action == "message") { Interlocked.Increment(ref MetadataCount); return; }
            bool video = doc.RootElement[1].GetProperty("room_name").GetString() == "synthetic-video";
            if (index > 0 && video && HoldReplacementJoin) { PendingJoin = Tuple.Create(socket, id.Value); return; }
            socket.Push("43/pubnub," + id + (index > 0 && video && RejectReplacementJoin ? "[{\"ok\":false}]" : "[{\"ok\":true}]"));
        }
        public void ReleaseJoin()
        { var pending = PendingJoin; PendingJoin = null; pending?.Item1.Push("43/pubnub," + pending.Item2 + "[{\"ok\":true}]"); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri.AbsolutePath;
            if (request.RequestUri.Host == "ps.pndsn.com")
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
                if (query.TryGetValue("uuid", out string uuid)) LegacyUuids.Enqueue(uuid);
                if (path.EndsWith("/heartbeat", StringComparison.Ordinal) || path.EndsWith("/leave", StringComparison.Ordinal)) return Json("{\"status\":200}");
                bool first = request.RequestUri.Query.Contains("&tt=0&", StringComparison.Ordinal);
                if (first) Interlocked.Increment(ref LegacyInitialSubscriptions);
                string message = first ? "" : await legacy.Reader.ReadAsync(token);
                return Json("{\"t\":{\"t\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + "\",\"r\":1},\"m\":[" + message + "]}");
            }
            if (request.RequestUri.Host != "api.feel-app.com") throw new Exception("Unexpected synthetic HTTP host");
            string operation = path.Split('/')[3];
            if (operation == "auth" || operation == "authconncode")
            {
                int count = Interlocked.Increment(ref AuthRequests); AuthOperations.Enqueue(operation);
                if (count > 1)
                {
                    ExchangeEntered.TrySetResult();
                    if (HoldExchange) await Task.Delay(Timeout.Infinite, token);
                    if (ExchangeResponseDelayMs > 0) await Task.Delay(ExchangeResponseDelayMs, token);
                    if (ExchangeStatus != HttpStatusCode.OK) return new HttpResponseMessage(ExchangeStatus);
                }
                return Json(JsonSerializer.Serialize(new { access_token = count == 1 ? "original-access" : "replacement-access-must-be-ignored", fec_token = count == 1 || ReturnUnchanged ? InitialToken : ReplacementToken }));
            }
            if (operation == "realtime") { Interlocked.Increment(ref RealtimeRequests); return Json("{\"pubnub_channel\":\"synthetic-website\"}"); }
            if (operation == "myrooms") { Interlocked.Increment(ref RoomRequests); return Json("{\"rooms\":[{\"pubnub_channel\":\"synthetic-video\"}]}"); }
            if (operation == "firmware") return Json("{\"FC3_APP_SOCKETIO_TIPS_WEBSITES_CHANNELS_code\":1}");
            if (operation == "presence2")
            {
                PresenceCredentials.Enqueue(path.Split('/').Last());
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                if (body.RootElement.GetProperty("online").GetInt32() == 1) Interlocked.Increment(ref OnlinePresence); else Interlocked.Increment(ref OfflinePresence);
                return Json("{}");
            }
            throw new Exception("Unexpected synthetic operation");
        }
        static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    sealed class MemorySocket : WebSocket
    {
        readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
        readonly CancellationTokenSource aborted = new();
        readonly Action<MemorySocket, string> sent;
        WebSocketState state = WebSocketState.Open;
        public bool Disposed, DeliverOneAfterCancellation;
        public readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> DelayedPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemorySocket(Action<MemorySocket, string> sent) { this.sent = sent; }
        public void Push(string text) => inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; aborted.Cancel(); }
        public override void Dispose() { Disposed = true; Abort(); DelayedPacket.TrySetCanceled(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            byte[] packet;
            try { packet = await inbound.Reader.ReadAsync(linked.Token); }
            catch (OperationCanceledException) when (DeliverOneAfterCancellation)
            { DeliverOneAfterCancellation = false; CancellationObserved.TrySetResult(); packet = Encoding.UTF8.GetBytes(await DelayedPacket.Task); }
            packet.CopyTo(buffer.AsSpan()); return new WebSocketReceiveResult(packet.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType kind, bool endOfMessage, CancellationToken token)
        { token.ThrowIfCancellationRequested(); sent(this, Encoding.UTF8.GetString(buffer)); return Task.CompletedTask; }
    }
}
