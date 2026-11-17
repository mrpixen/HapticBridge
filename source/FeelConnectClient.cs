using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LovenseConnector
{
    /// <summary>
    /// Account-free FeelConnect QR receiver. Credentials are held in memory for the session only.
    /// Callbacks run on background threads. The consumer owns motor routing and explicit output enablement.
    /// </summary>
    public sealed class FeelConnectClient : IAsyncDisposable
    {
        const int MaxPayload = 1024 * 1024;
        static readonly Uri ApiRoot = new("https://api.feel-app.com/internal-api/v1/");
        static readonly Uri SocketUri = new("wss://fec.feelme.com/socket.io/?EIO=4&transport=websocket");
        static readonly UTF8Encoding Utf8 = new(false, true);
        readonly HttpClient http;
        readonly bool ownHttp;
        readonly Func<CancellationToken, Task<WebSocket>> socketFactory;
        readonly SemaphoreSlim lifecycle = new(1, 1);
        readonly object sync = new();
        Session current;
        bool disposed;
        DateTimeOffset? lastFrameAt;
        public event Action<FeelConnectStatus> StatusChanged;
        public event Action<FeelConnectOutput> OutputChanged;
        public DateTimeOffset? LastFrameAt { get { lock (sync) return lastFrameAt; } }
        /// <summary>A fresh transport snapshot for explicit output arming; never restarts or extends a cue.</summary>
        public double CurrentPercent
        {
            get
            {
                lock (sync)
                {
                    Session session = current;
                    if (session == null || !session.Accepting || session.Stop.IsCancellationRequested) return 0;
                    if (session.ExpiresAt.HasValue && DateTimeOffset.UtcNow >= session.ExpiresAt) return 0;
                    var output = session.Protocol.Tick(session.Clock.ElapsedMilliseconds);
                    if (output != null) EmitOutputLocked(session, output);
                    return session.Protocol.Percent;
                }
            }
        }

        public FeelConnectClient()
        {
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            ownHttp = true;
            socketFactory = ConnectSocketAsync;
        }

        // Offline fixtures supply both transports. Production callers cannot redirect credentials to a custom host.
        internal FeelConnectClient(HttpClient http, Func<CancellationToken, Task<WebSocket>> socketFactory)
        {
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        }

        public async Task PairAsync(string pairingValue, IEnumerable<string> deviceNames, CancellationToken cancellationToken = default)
        {
            var pairing = FeelConnectPairing.Parse(pairingValue);
            string[] names = (deviceNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => new string(n.Trim().Where(c => !char.IsControl(c)).Take(80).ToArray()))
                .Where(n => n.Length != 0).Distinct(StringComparer.Ordinal).Take(32).ToArray();
            CancelCurrent("Pairing replaced");
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            Session session;
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                await RemoveCurrentAsync().ConfigureAwait(false);
                session = new Session(pairing, names, cancellationToken);
                lock (sync) { current = session; lastFrameAt = null; }
                session.Worker = Task.Run(() => RunAsync(session));
            }
            finally { lifecycle.Release(); }
            // The caller sees successful pairing only after namespace authentication, every room ACK and presence registration.
            await session.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task DisconnectAsync()
        {
            CancelCurrent("Disconnected");
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try { await RemoveCurrentAsync().ConfigureAwait(false); }
            finally { lifecycle.Release(); }
        }

        void CancelCurrent(string reason)
        {
            lock (sync)
            {
                if (current == null) return;
                current.Accepting = false;
                current.Stop.Cancel();
                current.Socket?.Abort();
                EmitOutputLocked(current, current.Protocol.Reset(reason));
                EmitStatusLocked(current, "Disconnected", reason, false);
            }
        }

        async Task RemoveCurrentAsync()
        {
            Session old;
            lock (sync) { old = current; }
            if (old == null) return;
            old.Stop.Cancel(); old.Socket?.Abort();
            if (old.Worker != null) await old.Worker.ConfigureAwait(false);
            lock (sync) { if (ReferenceEquals(current, old)) current = null; }
            old.Stop.Dispose(); old.SendLock.Dispose();
        }

        async Task RunAsync(Session session)
        {
            string phase = "Disconnected", message = "Disconnected";
            try
            {
                PublishStatus(session, "Pairing", "Exchanging the website pairing code…", false);
                using (JsonDocument auth = await RequestAsync(session.Pairing.IsConnectionCode ? "authconncode" : "auth", session.Pairing.Value, null, session.Stop.Token).ConfigureAwait(false))
                {
                    session.AccessToken = RequiredString(auth.RootElement, "access_token", 16384);
                    session.FecToken = RequiredString(auth.RootElement, "fec_token", 16384);
                }
                session.ExpiresAt = JwtExpiry(session.FecToken);
                if (session.ExpiresAt.HasValue && session.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new ReceiverException("Expired", "This pairing session has expired. Generate a new QR code.");
                var realtimeTask = RequestAsync("realtime", session.AccessToken, null, session.Stop.Token);
                var roomsTask = RequestAsync("myrooms", session.AccessToken, null, session.Stop.Token);
                await Task.WhenAll(realtimeTask, roomsTask).ConfigureAwait(false);
                using (var realtime = await realtimeTask.ConfigureAwait(false))
                    session.WebsiteRoom = RequiredString(realtime.RootElement, "pubnub_channel", 512);
                string[] rooms;
                using (var roomData = await roomsTask.ConfigureAwait(false)) rooms = ReadRooms(roomData.RootElement, session.WebsiteRoom);
                PublishStatus(session, "Connecting", "Connecting to the live cue service…", false);
                using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token))
                {
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    session.Socket = await socketFactory(connectDeadline.Token).ConfigureAwait(false);
                    await HandshakeAsync(session, connectDeadline.Token).ConfigureAwait(false);
                }
                session.ReceiveTask = ReceiveLoopAsync(session);
                await JoinRoomsAsync(session, rooms).ConfigureAwait(false);
                session.PresenceRegistered = true;
                await PresenceAsync(session, true, session.Stop.Token).ConfigureAwait(false);
                lock (sync)
                {
                    EnsureCurrent(session);
                    session.Accepting = true;
                    EmitStatusLocked(session, "Connected", "Connected. Waiting for video cues.", true);
                }
                session.Ready.TrySetResult();
                session.TimerTask = TimerLoopAsync(session);
                Task completed = await Task.WhenAny(session.ReceiveTask, session.TimerTask).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                throw new ReceiverException("Disconnected", "The video connection closed. Pair again to reconnect.");
            }
            catch (ReceiverException ex) { phase = ex.Phase; message = ex.SafeMessage; }
            catch (OperationCanceledException) { message = session.Stop.IsCancellationRequested ? "Disconnected" : "Connection timed out. Pair again to retry."; }
            catch (Exception) { phase = "Error"; message = "The connection could not be completed. Check the network and generate a fresh QR code."; }
            finally
            {
                session.Stop.Cancel(); session.Socket?.Abort();
                lock (sync)
                {
                    if (ReferenceEquals(current, session))
                    {
                        session.Accepting = false;
                        EmitOutputLocked(session, session.Protocol.Reset(message));
                        EmitStatusLocked(session, phase, message, false);
                    }
                }
                session.Ready.TrySetException(new InvalidOperationException(message));
                foreach (var ack in session.Acks.Values) ack.TrySetCanceled();
                foreach (Task task in new[] { session.ReceiveTask, session.TimerTask, session.RefreshTask })
                    if (task != null) try { await task.ConfigureAwait(false); } catch { }
                if (session.PresenceRegistered && session.AccessToken.Length != 0)
                {
                    using var offlineDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await PresenceAsync(session, false, offlineDeadline.Token).ConfigureAwait(false); } catch { }
                }
                session.Socket?.Dispose(); session.Socket = null;
                session.AccessToken = ""; session.FecToken = ""; session.Pairing = null;
            }
        }

        async Task HandshakeAsync(Session session, CancellationToken cancellationToken)
        {
            string packet = await ReceiveTextAsync(session.Socket, cancellationToken).ConfigureAwait(false);
            if (packet.Length < 2 || packet[0] != '0') throw new ReceiverException("Unsupported", "The server did not provide a compatible live connection.");
            using (var open = Parse(packet.AsMemory(1)))
            {
                if (!FeelConnectProtocol.TryNumber(open.RootElement, "pingInterval", out double interval) ||
                    !FeelConnectProtocol.TryNumber(open.RootElement, "pingTimeout", out double timeout) ||
                    interval < 100 || timeout < 100 || interval + timeout > 180000)
                    throw new ReceiverException("Unsupported", "The live server returned an invalid heartbeat.");
                session.HeartbeatMs = (int)(interval + timeout + 1000);
            }
            await SendAsync(session, "40/pubnub," + JsonSerializer.Serialize(new { token = session.FecToken }), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                packet = await ReceiveTextAsync(session.Socket, cancellationToken).ConfigureAwait(false);
                if (packet.StartsWith('2')) { await SendAsync(session, "3" + packet.Substring(1), cancellationToken).ConfigureAwait(false); continue; }
                if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal)) return;
                if (packet.StartsWith("44/pubnub,", StringComparison.Ordinal))
                    throw new ReceiverException("Rejected", "The live service rejected this pairing. Generate a new QR code.");
                throw new ReceiverException("Unsupported", "The live server returned an unsupported handshake.");
            }
        }

        async Task ReceiveLoopAsync(Session session)
        {
            while (!session.Stop.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
                heartbeat.CancelAfter(session.HeartbeatMs);
                string packet = await ReceiveTextAsync(session.Socket, heartbeat.Token).ConfigureAwait(false);
                if (packet.StartsWith('2')) { await SendAsync(session, "3" + packet.Substring(1), session.Stop.Token).ConfigureAwait(false); continue; }
                if (packet == "1" || packet.StartsWith("41/pubnub", StringComparison.Ordinal))
                    throw new ReceiverException("Disconnected", "The video connection closed. Pair again to reconnect.");
                if (packet.StartsWith("44/pubnub", StringComparison.Ordinal))
                    throw new ReceiverException("Expired", "The pairing session ended. Generate a new QR code.");
                if (!TrySocketPacket(packet, out int type, out int? ackId, out string json)) continue;
                if (type == 3 && ackId.HasValue)
                {
                    if (!session.Acks.TryRemove(ackId.Value, out var ack)) continue;
                    using var data = Parse(json.AsMemory());
                    bool ok = data.RootElement.ValueKind == JsonValueKind.Array && data.RootElement.GetArrayLength() > 0 &&
                        data.RootElement[0].ValueKind == JsonValueKind.Object && data.RootElement[0].TryGetProperty("ok", out var value) && value.ValueKind == JsonValueKind.True;
                    ack.TrySetResult(ok); continue;
                }
                if (type != 2) continue;
                using (var data = Parse(json.AsMemory()))
                {
                    var root = data.RootElement;
                    if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 || root[0].ValueKind != JsonValueKind.String || root[0].GetString() != "message") continue;
                    lock (sync)
                    {
                        EnsureCurrent(session);
                        session.Messages++;
                        if (!session.Accepting)
                        {
                            // A website can create its video room as soon as it sees online presence, before the
                            // HTTP acknowledgement returns. Retain that notification while discarding early cues.
                            var envelope = root[1];
                            if (FeelConnectProtocol.String(envelope, "room") == session.WebsiteRoom &&
                                (FeelConnectProtocol.String(envelope, "message_type") == "website:rooms_changed" ||
                                (envelope.TryGetProperty("data", out var earlyData) && FeelConnectProtocol.String(earlyData, "what") == "rooms_changed")))
                                session.RefreshRequested = true;
                            continue;
                        }
                        FeelConnectOutput output = session.Protocol.Accept(root[1], session.Clock.ElapsedMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        if (output != null) { lastFrameAt = DateTimeOffset.UtcNow; EmitOutputLocked(session, output); }
                        if (session.Protocol.RoomsChanged) session.RefreshRequested = true;
                        EmitStatusLocked(session, "Connected", output?.Kind ?? "Listening for video cues.", true);
                    }
                }
            }
        }

        async Task TimerLoopAsync(Session session)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            while (await timer.WaitForNextTickAsync(session.Stop.Token).ConfigureAwait(false))
            {
                if (session.ExpiresAt.HasValue && DateTimeOffset.UtcNow >= session.ExpiresAt)
                    throw new ReceiverException("Expired", "The pairing session expired. Generate a new QR code.");
                lock (sync)
                {
                    EnsureCurrent(session);
                    if (session.Accepting)
                    {
                        var output = session.Protocol.Tick(session.Clock.ElapsedMilliseconds);
                        if (output != null) EmitOutputLocked(session, output);
                    }
                    if (session.RefreshRequested && (session.RefreshTask == null || session.RefreshTask.IsCompleted) &&
                        session.Clock.ElapsedMilliseconds - session.LastRefreshMs >= 1000)
                    {
                        session.RefreshRequested = false; session.LastRefreshMs = session.Clock.ElapsedMilliseconds;
                        session.Accepting = false;
                        EmitOutputLocked(session, session.Protocol.Reset("Refreshing video rooms"));
                        EmitStatusLocked(session, "Connecting", "Updating the website connection…", false);
                        session.RefreshTask = RefreshRoomsAsync(session);
                    }
                }
                if (session.RefreshTask?.IsFaulted == true) await session.RefreshTask.ConfigureAwait(false);
            }
        }

        async Task RefreshRoomsAsync(Session session)
        {
            using var data = await RequestAsync("myrooms", session.AccessToken, null, session.Stop.Token).ConfigureAwait(false);
            string[] rooms = ReadRooms(data.RootElement, session.WebsiteRoom);
            await JoinRoomsAsync(session, rooms).ConfigureAwait(false);
            lock (sync)
            {
                EnsureCurrent(session); session.Accepting = true;
                EmitStatusLocked(session, "Connected", "Video connection updated. Waiting for fresh cues.", true);
            }
        }

        async Task JoinRoomsAsync(Session session, string[] rooms)
        {
            // Acknowledgements are required: receiving a WebSocket upgrade alone is not a paired website.
            foreach (string room in rooms.Except(session.Joined, StringComparer.Ordinal))
            {
                await RoomActionAsync(session, "room:join", room).ConfigureAwait(false);
                session.Joined.Add(room);
            }
            foreach (string removed in session.Joined.Except(rooms, StringComparer.Ordinal).ToArray())
            {
                await RoomActionAsync(session, "room:leave", removed).ConfigureAwait(false);
                session.Joined.Remove(removed);
            }
            lock (sync) { EnsureCurrent(session); session.Protocol.SetRooms(rooms); }
        }

        async Task RoomActionAsync(Session session, string action, string room)
        {
            int id = Interlocked.Increment(ref session.NextAck);
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Acks[id] = ack;
            try
            {
                await SendAsync(session, "42/pubnub," + id.ToString(CultureInfo.InvariantCulture) +
                    JsonSerializer.Serialize(new object[] { action, new { room_name = room } }), session.Stop.Token).ConfigureAwait(false);
                if (!await ack.Task.WaitAsync(TimeSpan.FromSeconds(8), session.Stop.Token).ConfigureAwait(false))
                    throw new ReceiverException("Rejected", "The website did not authorize its video room. Generate a fresh QR code.");
            }
            catch (TimeoutException) { throw new ReceiverException("Timeout", "The website did not confirm its video room. Pair again to retry."); }
            finally { session.Acks.TryRemove(id, out _); }
        }

        async Task PresenceAsync(Session session, bool online, CancellationToken cancellationToken)
        {
            using var result = await RequestAsync("presence2", session.AccessToken,
                new { online = online ? 1 : 0, devices = session.DeviceNames }, cancellationToken).ConfigureAwait(false);
        }

        async Task<JsonDocument> RequestAsync(string operation, string credential, object body, CancellationToken cancellationToken)
        {
            // HttpClient's headers-only completion timeout does not cover a response body that stalls mid-stream.
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            cancellationToken = requestDeadline.Token;
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Put,
                new Uri(ApiRoot, operation + "/" + Uri.EscapeDataString(credential)));
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new ReceiverException("Rejected", "The pairing code was rejected or expired. Generate a new QR code.");
            if (!response.IsSuccessStatusCode)
                throw new ReceiverException("Error", "The FeelConnect service could not complete this request. Try a fresh QR code.");
            if (response.Content.Headers.ContentLength > MaxPayload) throw new ReceiverException("Error", "The service response exceeded the supported size.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var result = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (result.Length + read > MaxPayload) throw new ReceiverException("Error", "The service response exceeded the supported size.");
                result.Write(buffer, 0, read);
            }
            // Presence requires an HTTP success acknowledgement; its response body is not a protocol input.
            if (body != null) return JsonDocument.Parse("{}");
            try { return JsonDocument.Parse(result.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new ReceiverException("Unsupported", "The service returned an unsupported response."); }
        }

        static string[] ReadRooms(JsonElement data, string websiteRoom)
        {
            var result = new HashSet<string>(StringComparer.Ordinal) { websiteRoom };
            if (!data.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array || rooms.GetArrayLength() > 128)
                throw new ReceiverException("Unsupported", "The website returned an unsupported room list.");
            foreach (var room in rooms.EnumerateArray()) result.Add(RequiredString(room, "pubnub_channel", 512));
            return result.ToArray();
        }

        static string RequiredString(JsonElement data, string property, int maxLength)
        {
            if (!FeelConnectProtocol.TryString(data, property, out string text) || string.IsNullOrWhiteSpace(text) || text.Length > maxLength || text.Any(char.IsControl))
                throw new ReceiverException("Unsupported", "The website returned incomplete pairing details.");
            return text;
        }

        static DateTimeOffset? JwtExpiry(string token)
        {
            try
            {
                string[] pieces = token.Split('.'); if (pieces.Length != 3) return null;
                string payload = pieces[1].Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                using var data = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 8 });
                if (FeelConnectProtocol.TryNumber(data.RootElement, "exp", out double expiry)) return DateTimeOffset.FromUnixTimeSeconds((long)expiry);
            }
            catch { }
            // Metadata only: authentication is performed by the server, never by trusting this local decode.
            return null;
        }

        static async Task<WebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            try { await socket.ConnectAsync(SocketUri, cancellationToken).ConfigureAwait(false); return socket; }
            catch { socket.Dispose(); throw; }
        }

        async Task SendAsync(Session session, string packet, CancellationToken cancellationToken)
        {
            byte[] bytes = Utf8.GetBytes(packet);
            await session.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await session.Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
            finally { session.SendLock.Release(); }
        }

        static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            using var content = new MemoryStream(); byte[] buffer = new byte[8192];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) throw new ReceiverException("Disconnected", "The video connection closed. Pair again to reconnect.");
                if (result.MessageType != WebSocketMessageType.Text) throw new ReceiverException("Unsupported", "Unsupported binary data was rejected.");
                if (content.Length + result.Count > MaxPayload) throw new ReceiverException("Unsupported", "An oversized video message was rejected.");
                content.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Utf8.GetString(content.ToArray());
            }
        }

        internal static bool TrySocketPacket(string packet, out int type, out int? ackId, out string json)
        {
            type = 0; ackId = null; json = "";
            if (packet.Length < 12 || packet[0] != '4' || packet[1] < '0' || packet[1] > '6' ||
                !packet.AsSpan(2).StartsWith("/pubnub,", StringComparison.Ordinal)) return false;
            type = packet[1] - '0'; int index = 10, begin = index;
            while (index < packet.Length && char.IsAsciiDigit(packet[index])) index++;
            if (index > begin)
            {
                if (!int.TryParse(packet.AsSpan(begin, index - begin), NumberStyles.None, CultureInfo.InvariantCulture, out int id)) return false;
                ackId = id;
            }
            if (index >= packet.Length || (packet[index] != '[' && packet[index] != '{')) return false;
            json = packet.Substring(index); return true;
        }

        static JsonDocument Parse(ReadOnlyMemory<char> data)
        {
            try { return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new ReceiverException("Unsupported", "An invalid video message was rejected."); }
        }

        void EnsureCurrent(Session session)
        {
            if (!ReferenceEquals(current, session) || session.Stop.IsCancellationRequested) throw new OperationCanceledException();
        }
        void PublishStatus(Session session, string phase, string message, bool connected)
        {
            lock (sync) { EnsureCurrent(session); EmitStatusLocked(session, phase, message, connected); }
        }
        void EmitOutputLocked(Session session, FeelConnectOutput output)
        {
            if (!ReferenceEquals(current, session)) return;
            try { OutputChanged?.Invoke(output); } catch { /* Consumer failure cannot break disconnect cleanup. */ }
        }
        void EmitStatusLocked(Session session, string phase, string message, bool connected)
        {
            if (!ReferenceEquals(current, session)) return;
            try { StatusChanged?.Invoke(new FeelConnectStatus(phase, message, connected, session.Messages)); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return; disposed = true;
            await DisconnectAsync().ConfigureAwait(false);
            if (ownHttp) http.Dispose();
        }

        sealed class Session
        {
            public FeelConnectPairing Pairing;
            public readonly string[] DeviceNames;
            public readonly CancellationTokenSource Stop;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public readonly FeelConnectProtocol Protocol = new();
            public readonly SemaphoreSlim SendLock = new(1, 1);
            public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> Acks = new();
            public readonly HashSet<string> Joined = new(StringComparer.Ordinal);
            public string AccessToken = "", FecToken = "", WebsiteRoom = "";
            public WebSocket Socket;
            public Task Worker, ReceiveTask, TimerTask, RefreshTask;
            public DateTimeOffset? ExpiresAt;
            public bool Accepting, PresenceRegistered, RefreshRequested;
            public long Messages, LastRefreshMs;
            public int NextAck, HeartbeatMs = 45000;
            public Session(FeelConnectPairing pairing, string[] names, CancellationToken cancellationToken)
            { Pairing = pairing; DeviceNames = names; Stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); }
        }

        sealed class ReceiverException : Exception
        {
            public string Phase { get; }
            public string SafeMessage { get; }
            public ReceiverException(string phase, string message) : base(message) { Phase = phase; SafeMessage = message; }
        }
    }
}
