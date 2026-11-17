using System;
using System.Collections.Concurrent;
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

// Synthetic HTTP and socket fixtures only. No real pairing, browser, network or hardware.
internal static class FeelConnectRoomDiscoveryTests
{
    static int assertions;
    public static async Task Main()
    {
        await RoomCreatedAfterInitialLookup();
        await AbsentRoomDoesNotPollForever();
        await ExistingRoomNeedsNoRecovery();
        await CancelPendingDiscovery();
        Console.WriteLine("FeelConnect room discovery: " + assertions + " assertions passed (offline).");
    }

    static void Check(bool condition, string label)
    { assertions++; if (!condition) throw new Exception("Room discovery: " + label); }
    static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    static async Task RoomCreatedAfterInitialLookup()
    {
        using var fixture = new Fixture { CreateRoomWhenOnline = true };
        using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-room-after-presence", Array.Empty<string>());
        await Until(() => client.Diagnostics.VideoRooms == 1 && fixture.MetadataCount == 1);
        Check(fixture.InitialLookupBeforePresence, "initial room lookup can precede online registration");
        Check(fixture.RoomRequests == 2 && fixture.RecoveryAfterReady,
            "one post-ready lookup discovers the room created by presence without a rooms_changed event");
        Check(fixture.Joined.Contains("synthetic-video") && client.Diagnostics.Connected,
            "newly authorized video room is acknowledged and announced");
        Check(client.Diagnostics.CandidateCues == 0 && client.CurrentPercent == 0,
            "room recovery alone never manufactures playback or output");
    }

    static async Task AbsentRoomDoesNotPollForever()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-no-video-room", Array.Empty<string>());
        await Until(() => fixture.RoomRequests == 2);
        await Task.Delay(1600);
        Check(fixture.RoomRequests == 2 && client.Diagnostics.Connected && client.Diagnostics.VideoRooms == 0,
            "missing room receives one recovery lookup and then remains connected without repeated polling");
        Check(fixture.MetadataCount == 0 && client.Diagnostics.CandidateCues == 0,
            "absent room does not receive fabricated announcements or cues");
        fixture.RoomAvailable = true; fixture.NotifyRoomChange();
        await Until(() => client.Diagnostics.VideoRooms == 1 && fixture.MetadataCount == 1);
        Check(fixture.RoomRequests == 3, "a later authenticated room-change event still discovers a room after recovery is exhausted");
    }

    static async Task ExistingRoomNeedsNoRecovery()
    {
        using var fixture = new Fixture { RoomAvailable = true }; using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-existing-room", Array.Empty<string>());
        await Task.Delay(1500);
        Check(fixture.RoomRequests == 1 && fixture.MetadataCount == 1,
            "a session that already has a video room does not do speculative recovery lookups");
        Check(client.Diagnostics.VideoRooms == 1 && client.Diagnostics.Connected,
            "existing authorized room stays paired without a recovery reset");
    }

    static async Task CancelPendingDiscovery()
    {
        using var fixture = new Fixture { HoldRecovery = true }; using var http = new HttpClient(fixture);
        await using var client = new FeelConnectClient(http, fixture.Connect, true);
        await client.PairAsync("synthetic-cancel-room-recovery", Array.Empty<string>());
        await fixture.RecoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(fixture.RecoveryCanceled && !client.Diagnostics.Connected,
            "disconnect cancels and awaits an in-flight recovery request");
        Check(fixture.MetadataCount == 0 && !fixture.Joined.Contains("synthetic-video"),
            "canceled discovery cannot join or announce a late room");
        int count = fixture.RoomRequests; await Task.Delay(200);
        Check(fixture.RoomRequests == count, "disconnected session cannot schedule another room lookup");
        fixture.HoldRecovery = false; fixture.RoomAvailable = true;
        await client.PairAsync("synthetic-replacement-pairing", Array.Empty<string>());
        Check(client.Diagnostics.Connected && client.Diagnostics.VideoRooms == 1 && fixture.MetadataCount == 1,
            "a replacement pairing joins only its fresh authorized room after old discovery is canceled");
        await Task.Delay(1200);
        Check(fixture.RoomRequests == count + 1 && fixture.MetadataCount == 1,
            "old recovery cannot apply stale rooms or send metadata into the replacement pairing");
    }

    sealed class Fixture : HttpMessageHandler
    {
        public volatile bool RoomAvailable, CreateRoomWhenOnline, HoldRecovery, RecoveryCanceled;
        public bool InitialLookupBeforePresence, RecoveryAfterReady;
        public readonly ConcurrentBag<string> Joined = new();
        public readonly TaskCompletionSource RecoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RoomRequests => Volatile.Read(ref roomRequests);
        public int MetadataCount => Volatile.Read(ref metadataCount);
        int roomRequests, metadataCount, presenceOnline, legacyHeartbeat;
        MemorySocket socket;

        public Task<WebSocket> Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); socket = new MemorySocket(Sent);
            socket.Push("0{\"sid\":\"synthetic-engine\",\"pingInterval\":25000,\"pingTimeout\":20000}");
            return Task.FromResult<WebSocket>(socket);
        }
        public void NotifyRoomChange() => socket.Push("42/pubnub," + JsonSerializer.Serialize(new object[] {
            "message", new { room = "synthetic-website", message_type = "website:rooms_changed", data = new { } }
        }));
        Task Sent(string packet, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal))
            { socket.Push("40/pubnub,{\"sid\":\"synthetic-namespace\"}"); return Task.CompletedTask; }
            if (!FeelConnectClient.TrySocketPacket(packet, out int type, out int? id, out string json) || type != 2) return Task.CompletedTask;
            using var payload = JsonDocument.Parse(json); string action = payload.RootElement[0].GetString(); var body = payload.RootElement[1];
            if (action == "message")
            {
                if (body.GetProperty("room").GetString() != "synthetic-video" ||
                    body.GetProperty("message_type").GetString() != "webshare:session_metadata")
                    throw new Exception("Unexpected synthetic metadata scope");
                Interlocked.Increment(ref metadataCount); return Task.CompletedTask;
            }
            if (!id.HasValue) throw new Exception("Synthetic join requires acknowledgement");
            if (action == "room:join") Joined.Add(body.GetProperty("room_name").GetString());
            socket.Push("43/pubnub," + id + "[{\"ok\":true}]"); return Task.CompletedTask;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri.AbsolutePath;
            if (request.RequestUri.Host == "ps.pndsn.com")
            {
                if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                { Interlocked.Increment(ref legacyHeartbeat); return Json("{\"status\":200}"); }
                if (path.EndsWith("/leave", StringComparison.Ordinal)) return Json("{\"status\":200}");
                if (!request.RequestUri.Query.Contains("&tt=0&", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, token);
                return Json("{\"t\":{\"t\":\"17000000000000000\",\"r\":1},\"m\":[]}");
            }
            if (request.RequestUri.Host != "api.feel-app.com") throw new Exception("Unexpected synthetic host");
            if (path.Contains("/auth/", StringComparison.Ordinal)) return Json("{\"access_token\":\"synthetic-access\",\"fec_token\":\"synthetic-fec\"}");
            if (path.Contains("/realtime/", StringComparison.Ordinal)) return Json("{\"pubnub_channel\":\"synthetic-website\"}");
            if (path.EndsWith("/firmware", StringComparison.Ordinal)) return Json("{\"FC3_APP_SOCKETIO_TIPS_WEBSITES_CHANNELS_code\":1}");
            if (path.Contains("/myrooms/", StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref roomRequests);
                if (count == 1) InitialLookupBeforePresence = Volatile.Read(ref presenceOnline) == 0;
                else
                {
                    RecoveryAfterReady = Volatile.Read(ref presenceOnline) > 0 && Volatile.Read(ref legacyHeartbeat) > 0;
                    if (HoldRecovery)
                    {
                        RecoveryEntered.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) { RecoveryCanceled = true; throw; }
                    }
                }
                return Json(RoomAvailable ? "{\"rooms\":[{\"pubnub_channel\":\"synthetic-video\"}]}" : "{\"rooms\":[]}");
            }
            if (path.Contains("/presence2/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                if (body.RootElement.GetProperty("online").GetInt32() == 1)
                { Interlocked.Increment(ref presenceOnline); if (CreateRoomWhenOnline) RoomAvailable = true; }
                return Json("{}");
            }
            throw new Exception("Unexpected synthetic operation");
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
            if (packet.Length > buffer.Count) throw new Exception("Synthetic packet exceeded buffer");
            packet.CopyTo(buffer.Array, buffer.Offset); return new WebSocketReceiveResult(packet.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType kind, bool endOfMessage, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return send(Encoding.UTF8.GetString(buffer), token); }
    }
}
