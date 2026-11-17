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
using System.Security.Cryptography;
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
        readonly bool enableLegacy;
        readonly Func<CancellationToken, Task<WebSocket>> socketFactory;
        readonly Func<DateTimeOffset> utcNow = () => DateTimeOffset.UtcNow;
        readonly SemaphoreSlim lifecycle = new(1, 1);
        readonly object sync = new();
        Session current;
        long lifecycleGeneration;
        bool disposed;
        DateTimeOffset? lastFrameAt;
        FeelConnectStatus lastStatus = new("Disconnected", "Disconnected", false, 0);
        public event Action<FeelConnectStatus> StatusChanged;
        public event Action<FeelConnectOutput> OutputChanged;
        public DateTimeOffset? LastFrameAt { get { lock (sync) return lastFrameAt; } }
        /// <summary>Server-provided website label for the local UI only; not included in numeric diagnostics.</summary>
        public string WebsiteName
        {
            get { lock (sync) return current != null && lastStatus.Connected && !current.Stop.IsCancellationRequested ? current.WebsiteName : ""; }
        }
        public FeelConnectDiagnostics Diagnostics
        {
            get
            {
                lock (sync)
                {
                    var session = current;
                    var legacy = session?.Legacy?.Diagnostics ?? new FeelConnectLegacy.DiagnosticSnapshot();
                    return new FeelConnectDiagnostics(lastStatus.Phase, lastStatus.Connected, session?.VideoRooms ?? 0,
                        session?.FecMessages ?? 0, session?.LegacyMessages ?? 0, session?.CandidateCues ?? 0,
                        session?.AcceptedCues ?? 0, session?.UnrecognizedMessages ?? 0, session?.LastCueKind ?? "", lastFrameAt)
                    {
                        PlaybackMode = session?.PlaybackMode == FeelConnectPlaybackMode.Live ? "live" : "timed",
                        V2 = session?.Protocol.V2Diagnostics ?? new FeelConnectV2Diagnostics(),
                        Transport = new FeelConnectTransportDiagnostics
                        {
                            LegacyRawFrames = legacy.RawFrames, LegacyDroppedShape = legacy.DroppedShape,
                            LegacyDroppedScope = legacy.DroppedScope, LegacyDroppedType = legacy.DroppedType,
                            LegacyDroppedTime = legacy.DroppedTime, LegacyDroppedStale = legacy.DroppedStale,
                            LegacyDroppedReplay = legacy.DroppedReplay, FecOtherEvents = session?.FecOtherEvents ?? 0,
                            FecControlEvents = session?.FecControlEvents ?? 0, FecSessionEvents = session?.FecSessionEvents ?? 0,
                            FecUnknownEvents = session?.FecUnknownEvents ?? 0, SessionMetadataMessages = session?.SessionMetadataMessages ?? 0,
                            UnknownObjectDataMessages = session?.UnknownObjectDataMessages ?? 0,
                            UnknownNonObjectDataMessages = session?.UnknownNonObjectDataMessages ?? 0,
                            UnknownCueShapeMessages = session?.UnknownCueShapeMessages ?? 0,
                            UnknownMissingDataMessages = session?.UnknownMissingDataMessages ?? 0,
                            UnknownNullDataMessages = session?.UnknownNullDataMessages ?? 0,
                            UnknownStringDataMessages = session?.UnknownStringDataMessages ?? 0,
                            UnknownArrayDataMessages = session?.UnknownArrayDataMessages ?? 0,
                            UnknownScalarDataMessages = session?.UnknownScalarDataMessages ?? 0,
                            UnknownEnvelopeCueShapeMessages = session?.UnknownEnvelopeCueShapeMessages ?? 0,
                            UnknownJsonObjectDataMessages = session?.UnknownJsonObjectDataMessages ?? 0,
                            UnknownJsonCueShapeMessages = session?.UnknownJsonCueShapeMessages ?? 0,
                            UnknownNonObjectEnvelopeMessages = session?.UnknownNonObjectEnvelopeMessages ?? 0,
                            UnknownJsonObjectEnvelopeMessages = session?.UnknownJsonObjectEnvelopeMessages ?? 0
                        }
                    };
                }
            }
        }
        /// <summary>A fresh transport snapshot for explicit output arming; never restarts or extends a cue.</summary>
        public double CurrentPercent
        {
            get
            {
                lock (sync)
                {
                    Session session = current;
                    if (session == null || !session.Accepting || session.Stop.IsCancellationRequested) return 0;
                    if (session.ExpiresAt.HasValue && utcNow() >= session.ExpiresAt) return 0;
                    var output = session.Protocol.Tick(session.Clock.ElapsedMilliseconds);
                    if (output != null) EmitOutputLocked(session, output);
                    return session.Protocol.Percent;
                }
            }
        }

        public FeelConnectClient()
        {
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            ownHttp = true;
            enableLegacy = true;
            socketFactory = ConnectSocketAsync;
        }

        // Offline fixtures supply both transports. Production callers cannot redirect credentials to a custom host.
        internal FeelConnectClient(HttpClient http, Func<CancellationToken, Task<WebSocket>> socketFactory, bool enableLegacy = false,
            Func<DateTimeOffset> utcNow = null)
        {
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
            this.enableLegacy = enableLegacy;
            if (utcNow != null) this.utcNow = utcNow;
            if (enableLegacy) http.Timeout = Timeout.InfiniteTimeSpan;
        }

        public Task PairAsync(string pairingValue, IEnumerable<string> deviceNames, CancellationToken cancellationToken = default,
            FeelConnectPlaybackMode playbackMode = FeelConnectPlaybackMode.Timed)
        {
            ValidatePlaybackMode(playbackMode);
            var pairing = FeelConnectPairing.Parse(pairingValue);
            string[] names = CleanDeviceNames(deviceNames);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                long request = ++lifecycleGeneration;
                CancelCurrent("Pairing replaced");
                return PairCoreAsync(pairing, names, playbackMode, cancellationToken, request);
            }
        }

        /// <summary>Reuses only this active session's in-memory pairing value. A fresh receiver announces the selected format.</summary>
        public Task ReconnectAsync(FeelConnectPlaybackMode mode, CancellationToken cancellationToken = default)
        {
            ValidatePlaybackMode(mode);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current == null || !lastStatus.Connected || current.Stop.IsCancellationRequested || current.Pairing == null)
                    throw new InvalidOperationException("Pair a video session before changing its playback mode.");
                var pairing = current.Pairing;
                string[] names = current.DeviceNames.ToArray();
                long request = ++lifecycleGeneration;
                CancelCurrent("Playback mode changed");
                return PairCoreAsync(pairing, names, mode, cancellationToken, request);
            }
        }

        static void ValidatePlaybackMode(FeelConnectPlaybackMode mode)
        {
            if (mode != FeelConnectPlaybackMode.Timed && mode != FeelConnectPlaybackMode.Live)
                throw new ArgumentOutOfRangeException(nameof(mode), "Choose timed cues or live levels.");
        }

        async Task PairCoreAsync(FeelConnectPairing pairing, string[] names, FeelConnectPlaybackMode mode,
            CancellationToken cancellationToken, long request)
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            Session session;
            try
            {
                lock (sync) EnsureLifecycleRequest(request);
                await RemoveCurrentAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    EnsureLifecycleRequest(request);
                    session = new Session(pairing, names, mode, cancellationToken);
                    current = session; lastFrameAt = null;
                    session.Worker = Task.Run(() => RunAsync(session));
                }
            }
            finally { lifecycle.Release(); }
            // The caller sees successful pairing only after namespace authentication, every room ACK and presence registration.
            await session.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        void EnsureLifecycleRequest(long request)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (request != lifecycleGeneration) throw new OperationCanceledException("A newer connection action replaced this request.");
        }

        static string[] CleanDeviceNames(IEnumerable<string> names) => (names ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => new string(n.Trim().Where(c => !char.IsControl(c)).Take(80).ToArray()))
            .Where(n => n.Length != 0).Distinct(StringComparer.Ordinal).Take(32).ToArray();

        /// <summary>Updates existing device registration without changing output ownership.</summary>
        public async Task UpdateDevicesAsync(IEnumerable<string> deviceNames, CancellationToken cancellationToken = default)
        {
            string[] names = CleanDeviceNames(deviceNames);
            Session session;
            CancellationToken lifetime;
            lock (sync)
            {
                session = current;
                if (session == null || !lastStatus.Connected || session.Stop.IsCancellationRequested) return;
                if (SameDeviceNames(names, session.DeviceNames)) return;
                lifetime = session.Stop.Token;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            await PresenceAsync(session, true, cancellation.Token, names).ConfigureAwait(false);
        }

        static bool SameDeviceNames(string[] left, string[] right) => left.Length == right.Length &&
            new HashSet<string>(left, StringComparer.Ordinal).SetEquals(right);

        /// <summary>Read-only occupancy counts for the current QR-authorized channels.</summary>
        public async Task<FeelConnectPresenceReport> CheckPresenceAsync(CancellationToken cancellationToken = default)
        {
            Session session;
            CancellationToken lifetime;
            lock (sync)
            {
                session = current;
                if (session == null || !lastStatus.Connected || session.Stop.IsCancellationRequested || session.Legacy == null)
                    throw new InvalidOperationException("Pair a video session before checking its website connection.");
                lifetime = session.Stop.Token;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            var report = await session.Legacy.InspectPresenceAsync(session.WebsiteRoom, cancellation.Token).ConfigureAwait(false);
            lock (sync) EnsureCurrent(session);
            return report;
        }

        /// <summary>One read-only check of the legacy SDK's fixed published route; never changes the active transport.</summary>
        public async Task<FeelConnectRouteOccupancyReport> CheckReferenceRouteAsync(CancellationToken cancellationToken = default)
        {
            Session session;
            CancellationToken lifetime;
            lock (sync)
            {
                session = current;
                if (session == null || !lastStatus.Connected || session.Stop.IsCancellationRequested || session.Legacy == null)
                    throw new InvalidOperationException("Pair a video session before checking the old SDK route.");
                lifetime = session.Stop.Token;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            var report = await session.Legacy.InspectReferenceOccupancyAsync(session.WebsiteRoom, cancellation.Token).ConfigureAwait(false);
            lock (sync) EnsureCurrent(session);
            return report;
        }

        public async Task DisconnectAsync()
        {
            long request;
            lock (sync) { request = ++lifecycleGeneration; CancelCurrent("Disconnected"); }
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (sync) if (request != lifecycleGeneration) return;
                await RemoveCurrentAsync().ConfigureAwait(false);
            }
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
            old.FecStop.Dispose(); old.Stop.Dispose(); old.SendLock.Dispose();
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
                session.RenewAt = JwtRenewal(session.FecToken)?.RenewAt;
                if (session.ExpiresAt.HasValue && session.ExpiresAt <= utcNow())
                    throw new ReceiverException("Expired", "This pairing session has expired. Generate a new QR code.");
                var realtimeTask = RequestAsync("realtime", session.AccessToken, null, session.Stop.Token);
                var roomsTask = RequestAsync("myrooms", session.AccessToken, null, session.Stop.Token);
                await Task.WhenAll(realtimeTask, roomsTask).ConfigureAwait(false);
                using (var realtime = await realtimeTask.ConfigureAwait(false))
                {
                    session.WebsiteRoom = RequiredString(realtime.RootElement, "pubnub_channel", 512);
                    session.WebsiteName = CleanWebsiteName(FeelConnectProtocol.String(realtime.RootElement, "partner_website"));
                    session.Protocol.TargetUser = FeelConnectProtocol.String(realtime.RootElement, "external_user_id");
                }
                string[] rooms;
                using (var roomData = await roomsTask.ConfigureAwait(false)) rooms = ReadRooms(roomData.RootElement, session.WebsiteRoom);
                if (enableLegacy)
                {
                    using var configuration = await RequestAsync("firmware", "", null, session.Stop.Token).ConfigureAwait(false);
                    session.UseFecWebsite = ReadTransportFlag(configuration.RootElement, "FC3_APP_SOCKETIO_TIPS_WEBSITES_CHANNELS_code");
                }
                PublishStatus(session, "Connecting", "Connecting to the live cue service…", false);
                using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token))
                {
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    session.Socket = await socketFactory(connectDeadline.Token).ConfigureAwait(false);
                    await HandshakeAsync(session, session.Socket, session.FecToken, connectDeadline.Token).ConfigureAwait(false);
                }
                session.ReceiveTask = ReceiveLoopAsync(session, session.Socket, session.FecGeneration, session.FecStop.Token);
                await JoinRoomsAsync(session, rooms).ConfigureAwait(false);
                session.PresenceRegistered = true;
                await PresenceAsync(session, true, session.Stop.Token).ConfigureAwait(false);
                if (enableLegacy)
                {
                    PublishStatus(session, "Connecting", "Enabling the website’s legacy video connection…", false);
                    session.Legacy = new FeelConnectLegacy(http, rooms,
                        (envelope, generation) => HandleEnvelope(session, envelope, false, generation), session.Stop.Token,
                        disconnected: reason => LegacyFailed(session, reason),
                        messageHandlerVersion: session.PlaybackMode == FeelConnectPlaybackMode.Live ? 2 : 3);
                    session.Legacy.Start();
                    await session.Legacy.Ready.WaitAsync(TimeSpan.FromSeconds(20), session.Stop.Token).ConfigureAwait(false);
                }
                lock (sync)
                {
                    EnsureCurrent(session);
                    session.Accepting = true;
                    session.Starting = false;
                    // Online registration can create the first room without a room-change event.
                    // One bounded post-ready discovery closes that race; it is not periodic polling.
                    if (session.VideoRooms == 0) session.RefreshRequested = true;
                    EmitStatusLocked(session, "Connected", "Connected. Waiting for video cues.", true);
                    EmitBufferedLocked(session);
                }
                session.Ready.TrySetResult();
                session.TimerTask = TimerLoopAsync(session);
                session.MetadataTask = MetadataLoopAsync(session, session.Socket, session.FecGeneration, session.FecStop.Token);
                Task completed = await Task.WhenAny(session.Legacy == null ? new[] { session.TimerTask } :
                    new[] { session.TimerTask, session.Legacy.Worker }).ConfigureAwait(false);
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
                // A renewal may still own an old transport. Let it finish cancellation before
                // taking the final worker/socket snapshot for cleanup.
                if (session.RenewalTask != null) try { await session.RenewalTask.ConfigureAwait(false); } catch { }
                foreach (var ack in session.Acks.Values) ack.TrySetCanceled();
                foreach (Task task in new[] { session.ReceiveTask, session.TimerTask, session.RefreshTask, session.MetadataTask })
                    if (task != null) try { await task.ConfigureAwait(false); } catch { }
                if (session.Legacy != null) await session.Legacy.DisposeAsync().ConfigureAwait(false);
                if (session.PresenceRegistered && session.AccessToken.Length != 0)
                {
                    using var offlineDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await PresenceAsync(session, false, offlineDeadline.Token).ConfigureAwait(false); } catch { }
                }
                session.Socket?.Dispose(); session.Socket = null;
                session.AccessToken = ""; session.FecToken = ""; session.Pairing = null;
            }
        }

        async Task HandshakeAsync(Session session, WebSocket socket, string fecToken, CancellationToken cancellationToken)
        {
            string packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (packet.Length < 2 || packet[0] != '0') throw new ReceiverException("Unsupported", "The server did not provide a compatible live connection.");
            using (var open = Parse(packet.AsMemory(1)))
            {
                if (!FeelConnectProtocol.TryNumber(open.RootElement, "pingInterval", out double interval) ||
                    !FeelConnectProtocol.TryNumber(open.RootElement, "pingTimeout", out double timeout) ||
                    interval < 100 || timeout < 100 || interval + timeout > 180000)
                    throw new ReceiverException("Unsupported", "The live server returned an invalid heartbeat.");
                session.HeartbeatMs = (int)(interval + timeout + 1000);
            }
            await SendAsync(session, socket, "40/pubnub," + JsonSerializer.Serialize(new { token = fecToken }), cancellationToken).ConfigureAwait(false);
            while (true)
            {
                packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
                if (packet.StartsWith('2')) { await SendAsync(session, socket, "3" + packet.Substring(1), cancellationToken).ConfigureAwait(false); continue; }
                if (packet.StartsWith("40/pubnub,", StringComparison.Ordinal)) return;
                if (packet.StartsWith("44/pubnub,", StringComparison.Ordinal))
                    throw new ReceiverException("Rejected", "The live service rejected this pairing. Generate a new QR code.");
                throw new ReceiverException("Unsupported", "The live server returned an unsupported handshake.");
            }
        }

        async Task ReceiveLoopAsync(Session session, WebSocket socket, int generation, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat.CancelAfter(session.HeartbeatMs);
                string packet = await ReceiveTextAsync(socket, heartbeat.Token).ConfigureAwait(false);
                lock (sync) EnsureFecCurrent(session, generation);
                if (packet.StartsWith('2')) { await SendAsync(session, socket, "3" + packet.Substring(1), cancellationToken).ConfigureAwait(false); continue; }
                if (packet == "1" || packet.StartsWith("41/pubnub", StringComparison.Ordinal))
                    throw new ReceiverException("Disconnected", "The video connection closed. Pair again to reconnect.");
                if (packet.StartsWith("44/pubnub", StringComparison.Ordinal))
                    throw new ReceiverException("Expired", "The pairing session ended. Generate a new QR code.");
                if (!TrySocketPacket(packet, out int type, out int? ackId, out string json)) continue;
                if (type == 3 && ackId.HasValue)
                {
                    TaskCompletionSource<bool> ack;
                    lock (sync)
                    {
                        EnsureFecCurrent(session, generation);
                        if (!session.Acks.TryRemove(ackId.Value, out ack)) continue;
                    }
                    using var data = Parse(json.AsMemory());
                    bool ok = data.RootElement.ValueKind == JsonValueKind.Array && data.RootElement.GetArrayLength() > 0 &&
                        data.RootElement[0].ValueKind == JsonValueKind.Object && data.RootElement[0].TryGetProperty("ok", out var value) && value.ValueKind == JsonValueKind.True;
                    ack.TrySetResult(ok); continue;
                }
                if (type != 2) continue;
                using (var data = Parse(json.AsMemory()))
                {
                    var root = data.RootElement;
                    if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 || root[0].ValueKind != JsonValueKind.String || root[0].GetString() != "message")
                    { lock (sync) { EnsureFecCurrent(session, generation); CountOtherFecEvent(session, root); } continue; }
                    HandleEnvelope(session, root[1], true, fecGeneration: generation);
                }
            }
        }

        void HandleEnvelope(Session session, JsonElement envelope, bool fromFec, int legacyGeneration = 0, int fecGeneration = 0)
        {
            lock (sync)
            {
                EnsureCurrent(session);
                if (fromFec && fecGeneration != session.FecGeneration) return;
                if (!fromFec && session.Legacy?.Generation != legacyGeneration) return;
                session.Messages++;
                if (fromFec) session.FecMessages++; else session.LegacyMessages++;
                string room = FeelConnectProtocol.String(envelope, "room");
                string kind = FeelConnectProtocol.String(envelope, "message_type");
                bool website = room == session.WebsiteRoom;
                bool roomChange = website && (kind == "website:rooms_changed" ||
                    (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("data", out var data) && FeelConnectProtocol.String(data, "what") == "rooms_changed"));
                // Online presence can cause the website to create a video room before its HTTP acknowledgement.
                if (roomChange) session.RefreshRequested = true;
                bool presence = kind == "system:presence";
                if (fromFec && presence && !website && session.AllowedRooms.Contains(room) && session.Joined.Contains(room) &&
                    envelope.TryGetProperty("data", out var presenceData) && FeelConnectProtocol.String(presenceData, "action") == "join")
                    session.PendingMetadata.Add(room);
                bool candidate = IsCueCandidate(envelope, kind);
                if (candidate) session.CandidateCues++;
                else if (!presence && !roomChange)
                {
                    session.UnrecognizedMessages++;
                    CountUnknownMessage(session, envelope, kind);
                }
                if (!session.AllowedRooms.Contains(room)) return;
                // Website controls migrate independently. Authorized video rooms can deliver their
                // versioned cues over either transport, including while website controls use FEC.
                bool modern = kind == "device:position" || kind == "room:play" || kind == "room:stop";
                if (!presence && !roomChange && website && ((modern && !fromFec) || (!modern && fromFec != session.UseFecWebsite))) return;
                if (!session.Accepting)
                {
                    // The first legacy subscribe response can already contain fresh cues. Decode them on their
                    // original monotonic clock, but do not emit until all pairing acknowledgements have succeeded.
                    if ((session.Starting || session.Buffering) && !roomChange)
                    {
                        var startup = session.Protocol.Accept(envelope, session.Clock.ElapsedMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        if (startup != null)
                        {
                            session.StartupOutput = startup; session.BufferedRoom = room;
                            if (candidate) RecordCueLocked(session, startup);
                        }
                    }
                    return;
                }
                long now = session.Clock.ElapsedMilliseconds;
                string fingerprint = website ? null : VideoPlayFingerprint(envelope);
                PruneVideoReceipts(session, now);
                if (fingerprint != null && session.VideoReceipts.TryGetValue(fingerprint, out var prior) && prior.FromFec != fromFec) return;
                FeelConnectOutput output = session.Protocol.Accept(envelope, now, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (output != null)
                {
                    // Rejected and startup-buffered messages never enter the replay cache. Stops are
                    // always decoded, while accepted play receipts survive a stop to reject a late mirror.
                    if (fingerprint != null && !session.VideoReceipts.ContainsKey(fingerprint))
                    {
                        session.VideoReceipts.Add(fingerprint, new VideoReceipt(now, fromFec));
                        session.VideoReceiptOrder.Enqueue(fingerprint);
                        PruneVideoReceipts(session, now);
                    }
                    if (candidate) RecordCueLocked(session, output);
                    EmitOutputLocked(session, output);
                }
                EmitStatusLocked(session, "Connected", output?.Kind ?? "Listening for video cues.", true);
            }
        }

        static string VideoPlayFingerprint(JsonElement envelope)
        {
            if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !FeelConnectProtocol.TryNumber(data, "ver", out double version) || version != 3 ||
                FeelConnectProtocol.String(data, "type") != "play" ||
                !FeelConnectProtocol.TryNumber(data, "serverTime", out double stamp) ||
                !data.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array ||
                subtitles.GetArrayLength() == 0 || subtitles.GetArrayLength() > 10000) return null;
            // A producer identity and timestamp distinguish a mirrored batch from an intentional replay.
            // Prefer its in-payload identity, which remains stable when transport publisher IDs differ.
            string publisher = FeelConnectProtocol.String(data, "src");
            if (publisher.Length == 0) publisher = FeelConnectProtocol.String(envelope, "publisher");
            if (publisher.Length == 0) return null;
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(FeelConnectProtocol.String(envelope, "room"));
                writer.WriteStringValue(publisher);
                writer.WriteNumberValue(stamp);
                foreach (var cue in subtitles.EnumerateArray())
                {
                    if (!FeelConnectProtocol.TryNumber(cue, "time", out double time) ||
                        !FeelConnectProtocol.TryNumber(cue, "subtitle", out double value)) return null;
                    writer.WriteStartArray(); writer.WriteNumberValue(time); writer.WriteNumberValue(value); writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        }

        static void PruneVideoReceipts(Session session, long now)
        {
            while (session.VideoReceiptOrder.TryPeek(out string key) &&
                (session.VideoReceipts.Count > 512 || now - session.VideoReceipts[key].At > 60000))
            {
                session.VideoReceiptOrder.Dequeue(); session.VideoReceipts.Remove(key);
            }
        }

        static bool IsCueCandidate(JsonElement envelope, string kind)
        {
            if (kind == "website:device_speed" || kind == "website:tip" || kind == "device:position" || kind == "room:play" || kind == "room:stop") return true;
            if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("data", out var data)) return false;
            string what = FeelConnectProtocol.String(data, "what");
            return what == "device_speed" || what == "tip" || what == "device_percent" || FeelConnectProtocol.TryNumber(data, "ver", out _);
        }

        static string CleanWebsiteName(string value)
        {
            var text = new StringBuilder();
            foreach (var rune in (value ?? "").EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format) continue;
                if (text.Length + rune.Utf16SequenceLength > 80) break;
                text.Append(rune.ToString());
            }
            return text.ToString().Trim();
        }

        void CountOtherFecEvent(Session session, JsonElement packet)
        {
            lock (sync)
            {
                EnsureCurrent(session);
                FeelConnectCounters.Increment(ref session.FecOtherEvents);
                string name = packet.ValueKind == JsonValueKind.Array && packet.GetArrayLength() > 0 && packet[0].ValueKind == JsonValueKind.String
                    ? packet[0].GetString() : "";
                if (name == "device:position" || name == "room:play" || name == "room:stop" || name == "room:message" || name == "website:device_speed" || name == "website:tip")
                    FeelConnectCounters.Increment(ref session.FecControlEvents);
                else if (name == "system:presence" || name == "website:rooms_changed" || IsSessionMetadata(name))
                    FeelConnectCounters.Increment(ref session.FecSessionEvents);
                else FeelConnectCounters.Increment(ref session.FecUnknownEvents);
            }
        }
        static bool IsSessionMetadata(string name) => name == "user-authentication" || name == "user-devices-changed" ||
            name == "webshare:session_metadata" || name == "webshare:session_update" || name == "webshare:presence" || name == "website:devices_changed";

        static void CountUnknownMessage(Session session, JsonElement envelope, string kind)
        {
            // Fixed structural counts only. Never retain or expose arbitrary event names, keys or data values.
            if (envelope.ValueKind != JsonValueKind.Object)
            {
                FeelConnectCounters.Increment(ref session.UnknownNonObjectEnvelopeMessages);
                CountJsonStringShape(session, envelope, true);
            }
            if (HasCueShape(envelope)) FeelConnectCounters.Increment(ref session.UnknownEnvelopeCueShapeMessages);
            bool hasData = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("data", out _);
            JsonElement data = hasData ? envelope.GetProperty("data") : default;
            if (IsSessionMetadata(kind) || IsSessionMetadata(FeelConnectProtocol.String(data, "what")))
                FeelConnectCounters.Increment(ref session.SessionMetadataMessages);
            if (data.ValueKind != JsonValueKind.Object)
            {
                FeelConnectCounters.Increment(ref session.UnknownNonObjectDataMessages);
                if (!hasData) FeelConnectCounters.Increment(ref session.UnknownMissingDataMessages);
                else if (data.ValueKind == JsonValueKind.Null) FeelConnectCounters.Increment(ref session.UnknownNullDataMessages);
                else if (data.ValueKind == JsonValueKind.String)
                {
                    FeelConnectCounters.Increment(ref session.UnknownStringDataMessages);
                    CountJsonStringShape(session, data, false);
                }
                else if (data.ValueKind == JsonValueKind.Array) FeelConnectCounters.Increment(ref session.UnknownArrayDataMessages);
                else FeelConnectCounters.Increment(ref session.UnknownScalarDataMessages);
                return;
            }
            FeelConnectCounters.Increment(ref session.UnknownObjectDataMessages);
            if (HasCueShape(data)) FeelConnectCounters.Increment(ref session.UnknownCueShapeMessages);
        }
        static bool HasCueShape(JsonElement data) => data.ValueKind == JsonValueKind.Object &&
            (data.TryGetProperty("ver", out _) || data.TryGetProperty("percents", out _) || data.TryGetProperty("values", out _) ||
                data.TryGetProperty("subtitles", out _) || data.TryGetProperty("strength", out _) || data.TryGetProperty("payload", out _));

        static void CountJsonStringShape(Session session, JsonElement value, bool envelope)
        {
            if (value.ValueKind != JsonValueKind.String) return;
            string text = value.GetString();
            if (text.Length > 16384 || !text.AsSpan().TrimStart().StartsWith("{")) return;
            try
            {
                using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                if (parsed.RootElement.ValueKind != JsonValueKind.Object) return;
                if (envelope) FeelConnectCounters.Increment(ref session.UnknownJsonObjectEnvelopeMessages);
                else FeelConnectCounters.Increment(ref session.UnknownJsonObjectDataMessages);
                if (HasCueShape(parsed.RootElement) || (parsed.RootElement.TryGetProperty("data", out var nested) && HasCueShape(nested)))
                    FeelConnectCounters.Increment(ref session.UnknownJsonCueShapeMessages);
            }
            catch (JsonException) { }
        }

        void RecordCueLocked(Session session, FeelConnectOutput output)
        {
            session.AcceptedCues++; session.LastCueKind = output.Kind; lastFrameAt = DateTimeOffset.UtcNow;
        }
        void EmitBufferedLocked(Session session)
        {
            if (session.StartupOutput != null)
                EmitOutputLocked(session, session.StartupOutput.Samples.Count > 0
                    ? session.Protocol.Reset("Waiting for fresh video cues")
                    : session.Protocol.Tick(session.Clock.ElapsedMilliseconds) ?? session.StartupOutput);
            session.StartupOutput = null;
            session.BufferedRoom = "";
        }

        void LegacyFailed(Session session, string reason)
        {
            lock (sync)
            {
                if (!ReferenceEquals(current, session) || session.Stop.IsCancellationRequested) return;
                session.Accepting = false;
                EmitOutputLocked(session, session.Protocol.Reset(reason));
                EmitStatusLocked(session, "Disconnected", reason, false);
                session.Stop.Cancel(); session.Socket?.Abort();
            }
        }

        async Task TimerLoopAsync(Session session)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            while (await timer.WaitForNextTickAsync(session.Stop.Token).ConfigureAwait(false))
            {
                Task renewal; Task[] fecWorkers;
                lock (sync)
                {
                    EnsureCurrent(session); EnsureUnexpired(session);
                    renewal = session.RenewalTask;
                    fecWorkers = session.RotatingFec ? Array.Empty<Task>() : new[] { session.ReceiveTask, session.MetadataTask };
                }
                if (renewal?.IsCompleted == true) await renewal.ConfigureAwait(false);
                foreach (var worker in fecWorkers)
                    if (worker?.IsCompleted == true)
                    {
                        await worker.ConfigureAwait(false);
                        throw new ReceiverException("Disconnected", "The video connection closed. Pair again to reconnect.");
                    }
                lock (sync)
                {
                    EnsureCurrent(session);
                    if (session.Accepting)
                    {
                        var output = session.Protocol.Tick(session.Clock.ElapsedMilliseconds);
                        if (output != null) EmitOutputLocked(session, output);
                    }
                    bool renewalRunning = session.RenewalTask != null && !session.RenewalTask.IsCompleted;
                    bool refreshRunning = session.RefreshTask != null && !session.RefreshTask.IsCompleted;
                    if (!renewalRunning && !refreshRunning && session.RenewAt.HasValue && utcNow() >= session.RenewAt)
                    {
                        session.RenewalTask = RenewFecAsync(session);
                        renewalRunning = true;
                    }
                    if (!renewalRunning && session.RefreshRequested && !refreshRunning &&
                        session.Clock.ElapsedMilliseconds - session.LastRefreshMs >= 1000)
                    {
                        session.RefreshRequested = false; session.LastRefreshMs = session.Clock.ElapsedMilliseconds;
                        session.Accepting = false;
                        session.Buffering = true; session.StartupOutput = null; session.BufferedRoom = "";
                        EmitOutputLocked(session, session.Protocol.Reset("Refreshing video rooms"));
                        // A room update keeps the same authenticated connection. Clear old cues while
                        // preserving the consumer's explicit enabled-output choice; failures still disconnect.
                        EmitStatusLocked(session, "Refreshing", "Updating the video rooms…", true);
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
                EnsureCurrent(session); session.Accepting = true; session.Buffering = false;
                EmitStatusLocked(session, "Connected", "Video connection updated. Waiting for fresh cues.", true);
                EmitBufferedLocked(session);
            }
        }

        async Task RenewFecAsync(Session session)
        {
            string replacement;
            lock (sync) { EnsureCurrent(session); session.RenewalAttempts++; }
            try
            {
                using var auth = await RequestAsync(session.Pairing.IsConnectionCode ? "authconncode" : "auth",
                    session.Pairing.Value, null, session.Stop.Token).ConfigureAwait(false);
                // Guest renewal changes only the FEC token. The website access token and legacy
                // receiver identity remain those of the original acknowledged pairing.
                replacement = RequiredString(auth.RootElement, "fec_token", 16384);
            }
            catch (ReceiverException ex) when (ex.Phase != "Rejected") { ScheduleRenewalRetry(session); return; }
            catch (HttpRequestException) { ScheduleRenewalRetry(session); return; }
            catch (OperationCanceledException) when (!session.Stop.IsCancellationRequested) { ScheduleRenewalRetry(session); return; }

            var metadata = JwtRenewal(replacement);
            lock (sync)
            {
                EnsureCurrent(session);
                if (replacement == session.FecToken || metadata == null || metadata.Value.ExpiresAt <= utcNow() ||
                    (session.ExpiresAt.HasValue && metadata.Value.ExpiresAt <= session.ExpiresAt.Value))
                { ScheduleRenewalRetry(session); return; }
            }

            WebSocket oldSocket; CancellationTokenSource oldLifetime; Task oldReceive, oldMetadata;
            int generation; string[] rooms;
            lock (sync)
            {
                EnsureCurrent(session); EnsureUnexpired(session);
                session.RotatingFec = true; session.Accepting = false;
                session.Buffering = false; session.StartupOutput = null; session.BufferedRoom = "";
                oldSocket = session.Socket; oldLifetime = session.FecStop;
                oldReceive = session.ReceiveTask; oldMetadata = session.MetadataTask;
                generation = ++session.FecGeneration;
                rooms = session.AllowedRooms.ToArray();
                oldLifetime.Cancel(); oldSocket?.Abort();
                foreach (var ack in session.Acks.Values) ack.TrySetCanceled();
                session.Acks.Clear();
                EmitOutputLocked(session, session.Protocol.Reset("Renewing video connection"));
                // Connected remains true only for this internal refresh; the consumer retains its
                // explicit enablement while the decoder drops, rather than buffers, interim cues.
                EmitStatusLocked(session, "Refreshing", "Renewing the video connection…", true);
            }
            foreach (var worker in new[] { oldReceive, oldMetadata })
                if (worker != null) try { await worker.ConfigureAwait(false); } catch { }
            lock (sync)
            {
                session.FecStop = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token);
                session.Socket = null; session.ReceiveTask = null; session.MetadataTask = null;
                session.Joined.Clear(); session.PendingMetadata.Clear(); session.MetadataSentAt.Clear();
            }
            oldSocket?.Dispose(); oldLifetime.Dispose();
            lock (sync) { EnsureCurrent(session); EnsureUnexpired(session); }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.FecStop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var socket = await socketFactory(deadline.Token).ConfigureAwait(false);
                try
                {
                    lock (sync) { EnsureFecCurrent(session, generation); EnsureUnexpired(session); session.Socket = socket; }
                    await HandshakeAsync(session, socket, replacement, deadline.Token).ConfigureAwait(false);
                }
                catch { socket.Dispose(); throw; }
            }
            lock (sync)
            {
                EnsureFecCurrent(session, generation); EnsureUnexpired(session);
                session.ReceiveTask = ReceiveLoopAsync(session, session.Socket, generation, session.FecStop.Token);
            }
            await JoinRoomsAsync(session, rooms, updateLegacy: false).ConfigureAwait(false);
            lock (sync)
            {
                EnsureFecCurrent(session, generation); EnsureUnexpired(session);
                // Local JWT metadata never extends authorization before authentication and every
                // room acknowledgement succeed, and cannot revive an expired previous session.
                session.FecToken = replacement; session.ExpiresAt = metadata.Value.ExpiresAt;
                session.RenewAt = metadata.Value.RenewAt > utcNow().AddSeconds(30) ? metadata.Value.RenewAt : utcNow().AddSeconds(30);
                session.RenewalAttempts = 0;
                session.MetadataTask = MetadataLoopAsync(session, session.Socket, generation, session.FecStop.Token);
                session.RotatingFec = false; session.Accepting = true;
                EmitStatusLocked(session, "Connected", "Video connection renewed. Waiting for fresh cues.", true);
            }
        }

        void ScheduleRenewalRetry(Session session)
        {
            lock (sync)
            {
                EnsureCurrent(session); EnsureUnexpired(session);
                // A stale/unchanged response must not create a midpoint retry loop. After three
                // failed exchanges the original token simply reaches its existing hard expiry.
                session.RenewAt = session.RenewalAttempts < 3 ? utcNow().AddSeconds(30) : null;
            }
        }

        void EnsureUnexpired(Session session)
        {
            if (session.ExpiresAt.HasValue && utcNow() >= session.ExpiresAt.Value)
                throw new ReceiverException("Expired", "The pairing session expired. Generate a new QR code.");
        }

        async Task JoinRoomsAsync(Session session, string[] rooms, bool updateLegacy = true)
        {
            // Authenticated API metadata establishes the allowlist before a newly joined room can send its first cue.
            lock (sync)
            {
                EnsureCurrent(session);
                session.AllowedRooms.Clear(); session.AllowedRooms.UnionWith(rooms);
                session.PendingMetadata.IntersectWith(session.AllowedRooms);
                foreach (string removed in session.MetadataSentAt.Keys.Except(rooms, StringComparer.Ordinal).ToArray())
                    session.MetadataSentAt.Remove(removed);
                session.Protocol.SetRooms(rooms);
                if (session.StartupOutput != null && !rooms.Contains(session.BufferedRoom, StringComparer.Ordinal))
                { session.StartupOutput = null; session.BufferedRoom = ""; }
                session.VideoRooms = rooms.Count(x => x != session.WebsiteRoom);
            }
            // Acknowledgements are required: receiving a WebSocket upgrade alone is not a paired website.
            string[] additions, removals;
            lock (sync)
            {
                additions = rooms.Except(session.Joined, StringComparer.Ordinal).ToArray();
                removals = session.Joined.Except(rooms, StringComparer.Ordinal).ToArray();
            }
            foreach (string room in additions)
            {
                await RoomActionAsync(session, "room:join", room).ConfigureAwait(false);
                lock (sync) { EnsureCurrent(session); session.Joined.Add(room); }
                if (room != session.WebsiteRoom) await SendRoomMetadataAsync(session, room).ConfigureAwait(false);
            }
            foreach (string removed in removals)
            {
                await RoomActionAsync(session, "room:leave", removed).ConfigureAwait(false);
                lock (sync) { EnsureCurrent(session); session.Joined.Remove(removed); }
            }
            if (updateLegacy) session.Legacy?.SetChannels(rooms);
        }

        async Task MetadataLoopAsync(Session session, WebSocket socket, int generation, CancellationToken cancellationToken)
        {
            // A slow metadata send must not stall cue scheduling or its output timeout.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await FlushMetadataAsync(session, socket, generation, cancellationToken).ConfigureAwait(false);
        }

        async Task FlushMetadataAsync(Session session, WebSocket socket, int generation, CancellationToken cancellationToken)
        {
            string[] pending;
            lock (sync)
            {
                EnsureFecCurrent(session, generation);
                long now = session.Clock.ElapsedMilliseconds;
                pending = session.PendingMetadata.Where(room => !session.MetadataSentAt.TryGetValue(room, out long sent) || now - sent >= 250).ToArray();
            }
            foreach (string room in pending) await SendRoomMetadataAsync(session, room, socket, generation, cancellationToken).ConfigureAwait(false);
        }

        async Task SendRoomMetadataAsync(Session session, string room)
        {
            WebSocket socket; int generation; CancellationToken lifetime;
            lock (sync) { EnsureCurrent(session); socket = session.Socket; generation = session.FecGeneration; lifetime = session.FecStop.Token; }
            await SendRoomMetadataAsync(session, room, socket, generation, lifetime).ConfigureAwait(false);
        }

        async Task SendRoomMetadataAsync(Session session, string room, WebSocket socket, int generation, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await session.SendLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                string[] names;
                lock (sync)
                {
                    EnsureFecCurrent(session, generation);
                    session.PendingMetadata.Remove(room);
                    if (room == session.WebsiteRoom || !session.AllowedRooms.Contains(room) || !session.Joined.Contains(room)) return;
                    names = session.DeviceNames.ToArray();
                    session.MetadataSentAt[room] = session.Clock.ElapsedMilliseconds;
                }
                string packet = "42/pubnub," + JsonSerializer.Serialize(new object[] { "message", new {
                    room, message_type = "webshare:session_metadata", data = new { room, devices = names }
                } });
                await socket.SendAsync(Utf8.GetBytes(packet).AsMemory(), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
            }
            finally { session.SendLock.Release(); }
        }

        async Task RoomActionAsync(Session session, string action, string room)
        {
            WebSocket socket; int generation; CancellationToken lifetime;
            lock (sync) { EnsureCurrent(session); socket = session.Socket; generation = session.FecGeneration; lifetime = session.FecStop.Token; }
            int id = Interlocked.Increment(ref session.NextAck);
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Acks[id] = ack;
            try
            {
                await SendAsync(session, socket, "42/pubnub," + id.ToString(CultureInfo.InvariantCulture) +
                    JsonSerializer.Serialize(new object[] { action, new { room_name = room } }), lifetime).ConfigureAwait(false);
                if (!await ack.Task.WaitAsync(TimeSpan.FromSeconds(8), lifetime).ConfigureAwait(false))
                    throw new ReceiverException("Rejected", "The website did not authorize its video room. Generate a fresh QR code.");
                lock (sync) EnsureFecCurrent(session, generation);
            }
            catch (TimeoutException) { throw new ReceiverException("Timeout", "The website did not confirm its video room. Pair again to retry."); }
            finally { session.Acks.TryRemove(id, out _); }
        }

        async Task PresenceAsync(Session session, bool online, CancellationToken cancellationToken, string[] updatedNames = null)
        {
            await session.PresenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string[] names;
                lock (sync)
                {
                    if (online) EnsureCurrent(session);
                    if (updatedNames != null && SameDeviceNames(updatedNames, session.DeviceNames)) return;
                    names = updatedNames ?? session.DeviceNames;
                }
                using var result = await RequestAsync("presence2", session.AccessToken,
                    new { online = online ? 1 : 0, devices = names }, cancellationToken).ConfigureAwait(false);
                if (online && updatedNames != null)
                {
                    lock (sync) { EnsureCurrent(session); session.DeviceNames = updatedNames; }
                }
            }
            finally { session.PresenceLock.Release(); }
        }

        async Task<JsonDocument> RequestAsync(string operation, string credential, object body, CancellationToken cancellationToken)
        {
            // HttpClient's headers-only completion timeout does not cover a response body that stalls mid-stream.
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            cancellationToken = requestDeadline.Token;
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Put,
                new Uri(ApiRoot, operation + (credential.Length == 0 ? "" : "/" + Uri.EscapeDataString(credential))));
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

        static bool ReadTransportFlag(JsonElement configuration, string property)
        {
            if (FeelConnectProtocol.TryNumber(configuration, property, out double value) && (value == 0 || value == 1)) return value == 1;
            throw new ReceiverException("Unsupported", "The service returned an unsupported video transport configuration.");
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

        internal static (DateTimeOffset ExpiresAt, DateTimeOffset RenewAt)? JwtRenewal(string token)
        {
            try
            {
                string[] pieces = token.Split('.'); if (pieces.Length != 3) return null;
                string payload = pieces[1].Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                using var data = JsonDocument.Parse(Convert.FromBase64String(payload), new JsonDocumentOptions { MaxDepth = 8 });
                if (!FeelConnectProtocol.TryNumber(data.RootElement, "iat", out double issued) ||
                    !FeelConnectProtocol.TryNumber(data.RootElement, "exp", out double expiry) || issued >= expiry ||
                    issued != Math.Truncate(issued) || expiry != Math.Truncate(expiry)) return null;
                var start = DateTimeOffset.FromUnixTimeSeconds(checked((long)issued));
                var end = DateTimeOffset.FromUnixTimeSeconds(checked((long)expiry));
                return (end, start.AddTicks((end - start).Ticks / 2));
            }
            catch { return null; }
        }

        static async Task<WebSocket> ConnectSocketAsync(CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            try { await socket.ConnectAsync(SocketUri, cancellationToken).ConfigureAwait(false); return socket; }
            catch { socket.Dispose(); throw; }
        }

        async Task SendAsync(Session session, WebSocket socket, string packet, CancellationToken cancellationToken)
        {
            byte[] bytes = Utf8.GetBytes(packet);
            await session.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
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
        void EnsureFecCurrent(Session session, int generation)
        {
            EnsureCurrent(session);
            if (session.FecGeneration != generation || session.FecStop.IsCancellationRequested) throw new OperationCanceledException();
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
            lastStatus = new FeelConnectStatus(phase, message, connected, session.Messages);
            try { StatusChanged?.Invoke(lastStatus); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return; disposed = true;
            await DisconnectAsync().ConfigureAwait(false);
            if (ownHttp) http.Dispose();
        }

        readonly record struct VideoReceipt(long At, bool FromFec);

        sealed class Session
        {
            public FeelConnectPairing Pairing;
            public readonly FeelConnectPlaybackMode PlaybackMode;
            public string[] DeviceNames;
            public readonly SemaphoreSlim PresenceLock = new(1, 1);
            public readonly CancellationTokenSource Stop;
            public CancellationTokenSource FecStop;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public readonly FeelConnectProtocol Protocol = new();
            public readonly SemaphoreSlim SendLock = new(1, 1);
            public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> Acks = new();
            public readonly HashSet<string> Joined = new(StringComparer.Ordinal);
            public readonly HashSet<string> AllowedRooms = new(StringComparer.Ordinal);
            public readonly HashSet<string> PendingMetadata = new(StringComparer.Ordinal);
            public readonly Dictionary<string, long> MetadataSentAt = new(StringComparer.Ordinal);
            public readonly Dictionary<string, VideoReceipt> VideoReceipts = new(StringComparer.Ordinal);
            public readonly Queue<string> VideoReceiptOrder = new();
            public string AccessToken = "", FecToken = "", WebsiteRoom = "";
            public WebSocket Socket;
            public FeelConnectLegacy Legacy;
            public Task Worker, ReceiveTask, TimerTask, RefreshTask, MetadataTask, RenewalTask;
            public DateTimeOffset? ExpiresAt, RenewAt;
            public int FecGeneration, RenewalAttempts;
            public bool RotatingFec;
            public bool Accepting, PresenceRegistered, RefreshRequested;
            public bool UseFecWebsite = true;
            public bool Starting = true, Buffering;
            public FeelConnectOutput StartupOutput;
            public string BufferedRoom = "";
            public string WebsiteName = "";
            public int VideoRooms;
            public long FecMessages, LegacyMessages, CandidateCues, AcceptedCues, UnrecognizedMessages;
            public long FecOtherEvents, FecControlEvents, FecSessionEvents, FecUnknownEvents;
            public long SessionMetadataMessages, UnknownObjectDataMessages, UnknownNonObjectDataMessages, UnknownCueShapeMessages;
            public long UnknownMissingDataMessages, UnknownNullDataMessages, UnknownStringDataMessages, UnknownArrayDataMessages, UnknownScalarDataMessages;
            public long UnknownEnvelopeCueShapeMessages, UnknownJsonObjectDataMessages, UnknownJsonCueShapeMessages;
            public long UnknownNonObjectEnvelopeMessages, UnknownJsonObjectEnvelopeMessages;
            public string LastCueKind = "";
            public long Messages, LastRefreshMs;
            public int NextAck, HeartbeatMs = 45000;
            public Session(FeelConnectPairing pairing, string[] names, FeelConnectPlaybackMode playbackMode, CancellationToken cancellationToken)
            {
                Pairing = pairing; DeviceNames = names; PlaybackMode = playbackMode;
                Stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                FecStop = CancellationTokenSource.CreateLinkedTokenSource(Stop.Token);
            }
        }

        sealed class ReceiverException : Exception
        {
            public string Phase { get; }
            public string SafeMessage { get; }
            public ReceiverException(string phase, string message) : base(message) { Phase = phase; SafeMessage = message; }
        }
    }
}
