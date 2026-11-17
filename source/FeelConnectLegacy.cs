using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LovenseConnector
{
    public sealed record FeelConnectRouteOccupancyReport(
        [property: JsonPropertyName("websiteChannels")] int WebsiteChannels,
        [property: JsonPropertyName("websiteOccupancy")] int WebsiteOccupancy,
        [property: JsonPropertyName("videoChannels")] int VideoChannels,
        [property: JsonPropertyName("videoOccupancy")] int VideoOccupancy,
        [property: JsonPropertyName("complete")] bool Complete);

    /// <summary>Receive-only PubNub compatibility for the legacy Feel website SDK.</summary>
    internal sealed class FeelConnectLegacy : IAsyncDisposable
    {
        // Public production subscribe routing identifier, published in the first (production) AppsSettings
        // block at https://api.feel-app.com/static/feeljs/1.2.1/feel.min.js . This is not an account credential.
        // No publish key, partner key or secret key is used. Channel scope comes only from QR-authenticated API metadata.
        internal const string ProductionSubscribeKey = "sub-c-296aa490-f663-11e5-ba5f-0619f8945a4f";
        // The same official 1.2.1 CDN bundle finally exports a staging AppsSettings object using this public
        // subscribe identifier. It is used ONLY for an explicit occupancy read, never for receiving or presence.
        // Provenance: https://api.feel-app.com/static/feeljs/1.2.1/feel.min.js (final exported AppsSettings object).
        internal const string ReferenceSubscribeKey = "sub-c-3f203b78-e5d9-11e5-a027-0619f8945a4f";
        readonly HttpClient http;
        readonly string subscribeKey;
        readonly int messageHandlerVersion;
        readonly string uuid = "mobile-client-hapticbridge-" + Guid.NewGuid().ToString("N");
        readonly Action<JsonElement, int> receive;
        readonly Action<string> disconnected;
        readonly object sync = new();
        readonly CancellationTokenSource stop;
        readonly CancellationToken ownerCancellation;
        readonly SemaphoreSlim presenceGate = new(1, 1);
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource subscriptionPrimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource activeRequest;
        string[] channels = Array.Empty<string>();
        int version;
        int readySteps;
        bool disposed;
        readonly Dictionary<string, ulong> watermarks = new(StringComparer.Ordinal);
        long rawFrames, droppedShape, droppedScope, droppedType, droppedTime, droppedStale, droppedReplay;
        internal readonly record struct DiagnosticSnapshot(long RawFrames, long DroppedShape, long DroppedScope,
            long DroppedType, long DroppedTime, long DroppedStale, long DroppedReplay);
        internal DiagnosticSnapshot Diagnostics => new(Interlocked.Read(ref rawFrames), Interlocked.Read(ref droppedShape),
            Interlocked.Read(ref droppedScope), Interlocked.Read(ref droppedType), Interlocked.Read(ref droppedTime),
            Interlocked.Read(ref droppedStale), Interlocked.Read(ref droppedReplay));
        public Task Ready => ready.Task;
        public Task Worker { get; private set; }
        public int Generation => Volatile.Read(ref version);

        internal FeelConnectLegacy(HttpClient http, IEnumerable<string> authorizedChannels, Action<JsonElement, int> receive,
            CancellationToken cancellationToken, string subscribeKey = ProductionSubscribeKey, Action<string> disconnected = null,
            int messageHandlerVersion = 3)
        {
            if (messageHandlerVersion != 2 && messageHandlerVersion != 3)
                throw new ArgumentOutOfRangeException(nameof(messageHandlerVersion));
            this.http = http; this.receive = receive; this.subscribeKey = subscribeKey;
            this.messageHandlerVersion = messageHandlerVersion;
            this.disconnected = disconnected;
            ownerCancellation = cancellationToken;
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetChannels(authorizedChannels);
        }

        public void Start() { if (Worker != null) throw new InvalidOperationException(); Worker = Task.Run(RunAsync); }
        public void SetChannels(IEnumerable<string> authorizedChannels)
        {
            var updated = authorizedChannels?.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
            if (updated.Length == 0 || updated.Length > 129 || updated.Any(x => !ValidChannel(x)))
                throw new InvalidOperationException("The website returned unsupported legacy channel names.");
            lock (sync)
            {
                if (channels.SequenceEqual(updated, StringComparer.Ordinal)) return;
                channels = updated; version++; activeRequest?.Cancel();
            }
        }

        static bool ValidChannel(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
            !value.Any(c => char.IsControl(c) || c == ',' || c == '*' || c == '?' || c == '#' || c == '/') &&
            !value.EndsWith("-pnpres", StringComparison.Ordinal);

        async Task RunAsync()
        {
            Task heartbeat = null, subscription = null;
            try
            {
                subscription = SubscribeLoopAsync();
                heartbeat = HeartbeatLoopAsync();
                Task completed = await Task.WhenAny(subscription, heartbeat).ConfigureAwait(false);
                try { await completed.ConfigureAwait(false); }
                finally { stop.Cancel(); }
                await Task.WhenAll(subscription, heartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (disposed || ownerCancellation.IsCancellationRequested)
            {
                ready.TrySetCanceled();
                throw;
            }
            catch (Exception)
            {
                try { disconnected?.Invoke("The legacy video connection ended. Pair again to reconnect."); } catch { }
                ready.TrySetException(new InvalidOperationException("The legacy video connection could not be established."));
                throw new InvalidOperationException("The legacy video connection ended.");
            }
            finally
            {
                stop.Cancel();
                if (heartbeat != null) try { await heartbeat.ConfigureAwait(false); } catch { }
                if (subscription != null) try { await subscription.ConfigureAwait(false); } catch { }
                string[] leaving; lock (sync) leaving = channels;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await PresenceAsync(leaving, "leave", deadline.Token).ConfigureAwait(false); } catch { }
            }
        }

        async Task SubscribeLoopAsync()
        {
            string cursor = "0", region = "";
            string restoreCursor = "";
            int currentVersion = -1;
            string[] previous = Array.Empty<string>();
            bool first = true;
            while (!stop.IsCancellationRequested)
            {
                string[] selected; int snapshot;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                lock (sync) { selected = channels; snapshot = version; activeRequest = request; }
                try
                {
                    if (snapshot != currentVersion)
                    {
                        string[] removed = previous.Except(selected, StringComparer.Ordinal).ToArray();
                        if (removed.Length > 0) await PresenceAsync(removed, "leave", request.Token).ConfigureAwait(false);
                        if (cursor != "0") restoreCursor = cursor;
                        cursor = "0"; region = ""; currentVersion = snapshot; previous = selected;
                    }
                    // Presence streams are virtual suffixes of precisely the same authorized channels.
                    string subscribeChannels = string.Join(",", selected.Concat(selected.Select(x => x + "-pnpres")).Select(Uri.EscapeDataString));
                    string uri = "https://ps.pndsn.com/v2/subscribe/" + Uri.EscapeDataString(subscribeKey) + "/" + subscribeChannels +
                        "/0?uuid=" + Uri.EscapeDataString(uuid) + "&tt=" + cursor + (region.Length == 0 ? "" : "&tr=" + region) +
                        "&heartbeat=90&state=" + Uri.EscapeDataString(State(selected));
                    using var body = await GetAsync(uri, 330, request.Token).ConfigureAwait(false);
                    JsonElement data = body.RootElement;
                    if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("t", out var position) ||
                        !position.TryGetProperty("t", out var token) || !DecimalToken(token, out string next) ||
                        !position.TryGetProperty("r", out var area) || !area.TryGetInt32(out int nextRegion) || nextRegion < 0 ||
                        !data.TryGetProperty("m", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > 1000)
                        throw new InvalidOperationException("The legacy service returned an unsupported subscription response.");
                    // Prime the new subscription, then retain the prior cursor once so a first play sent while
                    // the website created its room is not lost. Freshness limits and watermarks reject old replay.
                    if (restoreCursor.Length > 0)
                    { cursor = restoreCursor; region = nextRegion.ToString(CultureInfo.InvariantCulture); restoreCursor = ""; }
                    else { cursor = next; region = nextRegion.ToString(CultureInfo.InvariantCulture); }
                    foreach (var message in messages.EnumerateArray())
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        FeelConnectCounters.Increment(ref rawFrames);
                        JsonElement? envelope = Normalize(message, selected, out DropReason reason);
                        if (!envelope.HasValue) { CountDrop(reason); continue; }
                        lock (sync)
                        {
                            if (snapshot != version || stop.IsCancellationRequested) break;
                            if (!AcceptWatermark(envelope.Value)) { FeelConnectCounters.Increment(ref droppedReplay); continue; }
                        }
                        // Do not hold the transport lock while calling its owner. The owner checks the generation
                        // under its own lock, so removed channels cannot emit late while avoiding lock inversion.
                        receive(envelope.Value, snapshot);
                    }
                    if (first)
                    {
                        lock (sync)
                        {
                            if (snapshot == version && !stop.IsCancellationRequested)
                            {
                                first = false;
                                subscriptionPrimed.TrySetResult();
                                if (Interlocked.Increment(ref readySteps) == 2) ready.TrySetResult();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    lock (sync) if (snapshot == version) throw;
                }
                finally { lock (sync) if (ReferenceEquals(activeRequest, request)) activeRequest = null; }
            }
        }

        async Task HeartbeatLoopAsync()
        {
            // A heartbeat can wake the website and trigger its first cue. Establish a subscribe cursor first.
            await subscriptionPrimed.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            bool first = true;
            while (!stop.IsCancellationRequested)
            {
                string[] selected; lock (sync) selected = channels;
                await PresenceAsync(selected, "heartbeat", stop.Token).ConfigureAwait(false);
                if (first) { first = false; if (Interlocked.Increment(ref readySteps) == 2) ready.TrySetResult(); }
                await Task.Delay(TimeSpan.FromSeconds(20), stop.Token).ConfigureAwait(false);
            }
        }

        async Task PresenceAsync(string[] selected, string action, CancellationToken cancellationToken)
        {
            if (selected.Length == 0) return;
            await presenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // An old in-flight heartbeat must finish before removed rooms leave; queued old heartbeats are discarded.
                if (action == "heartbeat") lock (sync) if (!selected.SequenceEqual(channels, StringComparer.Ordinal)) return;
                string uri = "https://ps.pndsn.com/v2/presence/sub-key/" + Uri.EscapeDataString(subscribeKey) + "/channel/" +
                    string.Join(",", selected.Select(Uri.EscapeDataString)) + "/" + action + "?uuid=" + Uri.EscapeDataString(uuid);
                if (action == "heartbeat") uri += "&heartbeat=90&state=" + Uri.EscapeDataString(State(selected));
                using var body = await GetAsync(uri, 12, cancellationToken).ConfigureAwait(false);
                if (body.RootElement.ValueKind != JsonValueKind.Object ||
                    (body.RootElement.TryGetProperty("status", out var status) && status.TryGetInt32(out int code) && code != 200) ||
                    (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True))
                    throw new InvalidOperationException("The legacy presence request was rejected.");
            }
            finally { presenceGate.Release(); }
        }

        string State(string[] selected) => JsonSerializer.Serialize(selected.ToDictionary(x => x, _ => new { messageHandlerVersion }, StringComparer.Ordinal));

        internal async Task<FeelConnectPresenceReport> InspectPresenceAsync(string websiteChannel, CancellationToken cancellationToken = default)
        {
            string[] selected; int snapshot;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                stop.Token.ThrowIfCancellationRequested();
                selected = channels; snapshot = version;
                if (!selected.Contains(websiteChannel, StringComparer.Ordinal))
                    throw new InvalidOperationException("The current website channel is unavailable.");
            }
            using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
            string uri = "https://ps.pndsn.com/v2/presence/sub-key/" + Uri.EscapeDataString(ProductionSubscribeKey) + "/channel/" +
                string.Join(",", selected.Select(Uri.EscapeDataString)) + "?uuid=" + Uri.EscapeDataString(uuid) +
                "&disable_uuids=0&state=1&limit=1000";
            using var body = await GetAsync(uri, 12, request.Token).ConfigureAwait(false);
            FeelConnectPresenceReport report = SummarizePresence(body.RootElement, selected, websiteChannel, uuid);
            lock (sync)
            {
                request.Token.ThrowIfCancellationRequested();
                if (snapshot != version) throw new InvalidOperationException("The website channels changed. Check the connection again.");
            }
            return report;
        }

        internal async Task<FeelConnectRouteOccupancyReport> InspectReferenceOccupancyAsync(string websiteChannel, CancellationToken cancellationToken = default)
        {
            string[] selected; int snapshot;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                stop.Token.ThrowIfCancellationRequested();
                selected = channels; snapshot = version;
                if (!selected.Contains(websiteChannel, StringComparer.Ordinal))
                    throw new InvalidOperationException("The current website channel is unavailable.");
            }
            using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
            string uri = "https://ps.pndsn.com/v2/presence/sub-key/" + Uri.EscapeDataString(ReferenceSubscribeKey) + "/channel/" +
                string.Join(",", selected.Select(Uri.EscapeDataString)) + "?uuid=" + Uri.EscapeDataString(uuid) +
                "&disable_uuids=1&state=0&limit=1000";
            using var body = await GetAsync(uri, 12, request.Token).ConfigureAwait(false);
            var report = SummarizeReferenceOccupancy(body.RootElement, selected, websiteChannel);
            lock (sync)
            {
                request.Token.ThrowIfCancellationRequested();
                if (snapshot != version) throw new InvalidOperationException("The website channels changed. Check the connection again.");
            }
            return report;
        }

        internal static FeelConnectRouteOccupancyReport SummarizeReferenceOccupancy(JsonElement response, string[] selected, string websiteChannel)
        {
            const string invalid = "The presence service returned an unsupported response.";
            if (response.ValueKind != JsonValueKind.Object ||
                (response.TryGetProperty("status", out var status) && (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code != 200)) ||
                (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True))
                throw new InvalidOperationException(invalid);
            JsonElement payload = response.TryGetProperty("payload", out var nested) ? nested : response;
            if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(invalid);
            bool hasMap = payload.TryGetProperty("channels", out var map) && map.ValueKind == JsonValueKind.Object;
            if (!hasMap && selected.Length != 1) throw new InvalidOperationException(invalid);
            int websiteChannels = 0, websiteOccupancy = 0, videoChannels = 0, videoOccupancy = 0; bool complete = true;
            foreach (string channel in selected)
            {
                bool website = channel == websiteChannel;
                if (website) websiteChannels++; else videoChannels++;
                JsonElement entry = payload;
                if (hasMap && !map.TryGetProperty(channel, out entry)) { complete = false; continue; }
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("occupancy", out var occupancy) ||
                    occupancy.ValueKind != JsonValueKind.Number || !occupancy.TryGetInt32(out int count) || count < 0 || count > 1_000_000)
                    throw new InvalidOperationException(invalid);
                if (website) websiteOccupancy += count; else videoOccupancy += count;
            }
            return new FeelConnectRouteOccupancyReport(websiteChannels, websiteOccupancy, videoChannels, videoOccupancy, complete);
        }

        // Remote UUIDs, channel names and state are consumed only in memory and never enter the returned report.
        internal static FeelConnectPresenceReport SummarizePresence(JsonElement response, string[] selected, string websiteChannel, string ownUuid)
        {
            const string invalid = "The presence service returned an unsupported response.";
            if (response.ValueKind != JsonValueKind.Object ||
                (response.TryGetProperty("status", out var status) && (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out int code) || code != 200)) ||
                (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True))
                throw new InvalidOperationException(invalid);
            JsonElement payload = response.TryGetProperty("payload", out var nested) ? nested : response;
            if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(invalid);
            bool hasMap = payload.TryGetProperty("channels", out var map) && map.ValueKind == JsonValueKind.Object;
            if (!hasMap && selected.Length != 1) throw new InvalidOperationException(invalid);
            var website = new PresenceTotals(); var video = new PresenceTotals(); bool complete = true;
            foreach (string channel in selected)
            {
                var totals = channel == websiteChannel ? website : video;
                totals.Channels++;
                JsonElement entry = payload;
                if (hasMap && !map.TryGetProperty(channel, out entry)) { complete = false; continue; }
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("occupancy", out var occupancy) ||
                    occupancy.ValueKind != JsonValueKind.Number || !occupancy.TryGetInt32(out int count) || count < 0 || count > 1_000_000 ||
                    !entry.TryGetProperty("uuids", out var occupants) || occupants.ValueKind != JsonValueKind.Array || occupants.GetArrayLength() > 1000)
                    throw new InvalidOperationException(invalid);
                totals.TotalOccupancy += count;
                var seen = new HashSet<string>(StringComparer.Ordinal); bool self = false, version3 = false;
                foreach (var occupant in occupants.EnumerateArray())
                {
                    string id = occupant.ValueKind == JsonValueKind.String ? occupant.GetString() : FeelConnectProtocol.String(occupant, "uuid");
                    if (string.IsNullOrEmpty(id) || id.Length > 512 || !seen.Add(id)) throw new InvalidOperationException(invalid);
                    if (id == ownUuid)
                    {
                        self = true;
                        version3 = occupant.ValueKind == JsonValueKind.Object && occupant.TryGetProperty("state", out var state) &&
                            FeelConnectProtocol.TryNumber(state, "messageHandlerVersion", out double version) && version == 3;
                    }
                    else if (id.StartsWith("jsapi-watch-", StringComparison.Ordinal)) totals.WatchPeers++;
                    else if (id.StartsWith("jsapi-lib-", StringComparison.Ordinal)) totals.PlayerPeers++;
                    else totals.OtherPeers++;
                }
                if (seen.Count > count) throw new InvalidOperationException(invalid);
                if (seen.Count != count) complete = false;
                if (self) totals.SelfPresentChannels++;
                if (version3) totals.SelfVersion3Channels++;
            }
            return new FeelConnectPresenceReport(website.Report(), video.Report(), complete);
        }

        sealed class PresenceTotals
        {
            public int Channels, SelfPresentChannels, SelfVersion3Channels, TotalOccupancy, WatchPeers, PlayerPeers, OtherPeers;
            public FeelConnectPresenceRole Report() => new(Channels, SelfPresentChannels, SelfVersion3Channels, TotalOccupancy, WatchPeers, PlayerPeers, OtherPeers);
        }

        async Task<JsonDocument> GetAsync(string uri, int seconds, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The legacy service rejected or could not complete the connection.");
            const int limit = 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Legacy response size exceeded its limit.");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > limit) throw new InvalidOperationException("Legacy response size exceeded its limit.");
                bytes.Write(buffer, 0, count);
            }
            try { return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new InvalidOperationException("The legacy service returned invalid data."); }
        }

        internal static bool DecimalToken(JsonElement token, out string value)
        {
            value = token.ValueKind == JsonValueKind.String ? token.GetString() : token.ValueKind == JsonValueKind.Number ? token.GetRawText() : "";
            return value.Length > 0 && value.Length <= 20 && value.All(char.IsAsciiDigit) && ulong.TryParse(value, out _);
        }

        internal enum DropReason { None, Shape, Scope, Type, Time, Stale }
        void CountDrop(DropReason reason)
        {
            switch (reason)
            {
                case DropReason.Shape: FeelConnectCounters.Increment(ref droppedShape); break;
                case DropReason.Scope: FeelConnectCounters.Increment(ref droppedScope); break;
                case DropReason.Type: FeelConnectCounters.Increment(ref droppedType); break;
                case DropReason.Time: FeelConnectCounters.Increment(ref droppedTime); break;
                case DropReason.Stale: FeelConnectCounters.Increment(ref droppedStale); break;
            }
        }
        internal static JsonElement? Normalize(JsonElement message, IEnumerable<string> authorizedChannels, long? nowMs = null)
            => Normalize(message, authorizedChannels, out _, nowMs);

        internal static JsonElement? Normalize(JsonElement message, IEnumerable<string> authorizedChannels, out DropReason reason, long? nowMs = null)
        {
            reason = DropReason.Shape;
            if (message.ValueKind != JsonValueKind.Object || !FeelConnectProtocol.TryString(message, "c", out string channel) ||
                !message.TryGetProperty("d", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            bool presence = channel.EndsWith("-pnpres", StringComparison.Ordinal);
            if (presence) channel = channel.Substring(0, channel.Length - 7);
            if (!authorizedChannels.Contains(channel, StringComparer.Ordinal)) { reason = DropReason.Scope; return null; }
            if (message.TryGetProperty("e", out var eventType) && (!eventType.TryGetInt32(out int kind) || kind > 1 || kind < 0)) { reason = DropReason.Type; return null; }
            long receivedAt = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long time = receivedAt;
            string exact = "";
            if (message.TryGetProperty("p", out var published) && published.ValueKind == JsonValueKind.Object &&
                published.TryGetProperty("t", out var rawTime) && DecimalToken(rawTime, out exact))
            {
                ulong milliseconds = ulong.Parse(exact, CultureInfo.InvariantCulture) / 10000;
                if (milliseconds <= long.MaxValue) time = (long)milliseconds;
            }
            else if (!presence) { reason = DropReason.Time; return null; }
            bool timed = FeelConnectProtocol.TryNumber(data, "ver", out double version) && version == 3 && FeelConnectProtocol.String(data, "type") == "play";
            if (receivedAt - time > (timed ? 60000 : 5000) || time - receivedAt > 60000) { reason = DropReason.Stale; return null; }
            string type = presence ? "system:presence" : FeelConnectProtocol.String(data, "message_type");
            string publisher = FeelConnectProtocol.String(message, "i");
            if (publisher.Length == 0) publisher = FeelConnectProtocol.String(data, "src");
            reason = DropReason.None;
            return JsonSerializer.SerializeToElement(new {
                room = channel, message_type = type, data = data.Clone(),
                publisher, serverTime = Math.Max(time, receivedAt), legacyTimetoken = exact
            });
        }

        bool AcceptWatermark(JsonElement envelope)
        {
            if (FeelConnectProtocol.String(envelope, "message_type") == "system:presence") return true;
            string room = FeelConnectProtocol.String(envelope, "room"), publisher = FeelConnectProtocol.String(envelope, "publisher");
            string stamp = FeelConnectProtocol.String(envelope, "legacyTimetoken");
            if (!ulong.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out ulong time)) return false;
            string key = room + "\0" + publisher;
            if (watermarks.TryGetValue(key, out ulong previous) && time <= previous) return false;
            if (!watermarks.ContainsKey(key) && watermarks.Count >= 1024)
                throw new InvalidOperationException("Too many publishers in the video session.");
            watermarks[key] = time; return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return; disposed = true; stop.Cancel();
            lock (sync) activeRequest?.Cancel();
            if (Worker != null) try { await Worker.ConfigureAwait(false); } catch { }
            stop.Dispose();
            presenceGate.Dispose();
        }
    }
}
