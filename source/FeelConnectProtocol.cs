using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LovenseConnector
{
    public enum FeelConnectPlaybackMode { Timed, Live }
    public sealed record FeelConnectOutput(double Percent, string Kind, bool IsStop);
    public sealed record FeelConnectStatus(string Phase, string Message, bool Connected, long MessagesReceived);
    public sealed record FeelConnectPresenceRole(
        [property: JsonPropertyName("channels")] int Channels,
        [property: JsonPropertyName("selfPresentChannels")] int SelfPresentChannels,
        [property: JsonPropertyName("selfVersion3Channels")] int SelfVersion3Channels,
        [property: JsonPropertyName("totalOccupancy")] int TotalOccupancy,
        [property: JsonPropertyName("watchPeers")] int WatchPeers,
        [property: JsonPropertyName("playerPeers")] int PlayerPeers,
        [property: JsonPropertyName("otherPeers")] int OtherPeers);
    public sealed record FeelConnectPresenceReport(
        [property: JsonPropertyName("website")] FeelConnectPresenceRole Website,
        [property: JsonPropertyName("video")] FeelConnectPresenceRole Video,
        [property: JsonPropertyName("complete")] bool Complete);
    public sealed record FeelConnectDiagnostics(
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("connected")] bool Connected,
        [property: JsonPropertyName("videoRooms")] int VideoRooms,
        [property: JsonPropertyName("fecMessages")] long FecMessages,
        [property: JsonPropertyName("legacyMessages")] long LegacyMessages,
        [property: JsonPropertyName("candidateCues")] long CandidateCues,
        [property: JsonPropertyName("acceptedCues")] long AcceptedCues,
        [property: JsonPropertyName("unrecognizedMessages")] long UnrecognizedMessages,
        [property: JsonPropertyName("lastCueKind")] string LastCueKind,
        [property: JsonPropertyName("lastCueAt")] DateTimeOffset? LastCueAt)
    {
        [JsonPropertyName("playbackMode")] public string PlaybackMode { get; init; } = "timed";
        [JsonPropertyName("transport")] public FeelConnectTransportDiagnostics Transport { get; init; } = new();
        [JsonPropertyName("v2")] public FeelConnectV2Diagnostics V2 { get; init; } = new();
    }
    /// <summary>Validated V2 input statistics for one pairing session; no payloads or source identifiers.</summary>
    public sealed record FeelConnectV2Diagnostics
    {
        [JsonPropertyName("acceptedBatches")] public long AcceptedBatches { get; init; }
        [JsonPropertyName("samples")] public long Samples { get; init; }
        [JsonPropertyName("positiveSamples")] public long PositiveSamples { get; init; }
        [JsonPropertyName("mixedBatches")] public long MixedBatches { get; init; }
        [JsonPropertyName("lastBatchSamples")] public int LastBatchSamples { get; init; }
        [JsonPropertyName("lastBatchMaximum")] public double LastBatchMaximum { get; init; }
        [JsonPropertyName("lastBatchFinal")] public double LastBatchFinal { get; init; }
    }
    public sealed record FeelConnectTransportDiagnostics
    {
        [JsonPropertyName("legacyRawFrames")] public long LegacyRawFrames { get; init; }
        [JsonPropertyName("legacyDroppedShape")] public long LegacyDroppedShape { get; init; }
        [JsonPropertyName("legacyDroppedScope")] public long LegacyDroppedScope { get; init; }
        [JsonPropertyName("legacyDroppedType")] public long LegacyDroppedType { get; init; }
        [JsonPropertyName("legacyDroppedTime")] public long LegacyDroppedTime { get; init; }
        [JsonPropertyName("legacyDroppedStale")] public long LegacyDroppedStale { get; init; }
        [JsonPropertyName("legacyDroppedReplay")] public long LegacyDroppedReplay { get; init; }
        [JsonPropertyName("fecOtherEvents")] public long FecOtherEvents { get; init; }
        [JsonPropertyName("fecControlEvents")] public long FecControlEvents { get; init; }
        [JsonPropertyName("fecSessionEvents")] public long FecSessionEvents { get; init; }
        [JsonPropertyName("fecUnknownEvents")] public long FecUnknownEvents { get; init; }
        [JsonPropertyName("sessionMetadataMessages")] public long SessionMetadataMessages { get; init; }
        [JsonPropertyName("unknownObjectDataMessages")] public long UnknownObjectDataMessages { get; init; }
        [JsonPropertyName("unknownNonObjectDataMessages")] public long UnknownNonObjectDataMessages { get; init; }
        [JsonPropertyName("unknownCueShapeMessages")] public long UnknownCueShapeMessages { get; init; }
        [JsonPropertyName("unknownMissingDataMessages")] public long UnknownMissingDataMessages { get; init; }
        [JsonPropertyName("unknownNullDataMessages")] public long UnknownNullDataMessages { get; init; }
        [JsonPropertyName("unknownStringDataMessages")] public long UnknownStringDataMessages { get; init; }
        [JsonPropertyName("unknownArrayDataMessages")] public long UnknownArrayDataMessages { get; init; }
        [JsonPropertyName("unknownScalarDataMessages")] public long UnknownScalarDataMessages { get; init; }
        [JsonPropertyName("unknownEnvelopeCueShapeMessages")] public long UnknownEnvelopeCueShapeMessages { get; init; }
        [JsonPropertyName("unknownJsonObjectDataMessages")] public long UnknownJsonObjectDataMessages { get; init; }
        [JsonPropertyName("unknownJsonCueShapeMessages")] public long UnknownJsonCueShapeMessages { get; init; }
        [JsonPropertyName("unknownNonObjectEnvelopeMessages")] public long UnknownNonObjectEnvelopeMessages { get; init; }
        [JsonPropertyName("unknownJsonObjectEnvelopeMessages")] public long UnknownJsonObjectEnvelopeMessages { get; init; }
    }
    internal static class FeelConnectCounters
    {
        internal static void Increment(ref long counter)
        {
            long previous;
            do
            {
                previous = System.Threading.Interlocked.Read(ref counter);
                if (previous >= 1_000_000_000) return;
            } while (System.Threading.Interlocked.CompareExchange(ref counter, previous + 1, previous) != previous);
        }
    }
    public sealed record FeelConnectPairing(string Value, bool IsConnectionCode)
    {
        public static FeelConnectPairing Parse(string input)
        {
            input = (input ?? "").Trim();
            if (input.Length > 8192 || input.Length == 0) throw new ArgumentException("Paste a FeelConnect pairing value or scan its QR code.");
            bool code = false;
            if (input.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "feelapp" && uri.Scheme != "feelvr") ||
                    (uri.Host != "authorize" && uri.Host != "connect"))
                    throw new ArgumentException("This is not a FeelConnect pairing code.");
                code = uri.Host == "connect";
                string key = code ? "code" : "token";
                var values = uri.Query.TrimStart('?').Split('&')
                    .Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0] == key).ToArray();
                if (values.Length != 1) throw new ArgumentException("The pairing link has no unique pairing value.");
                input = Uri.UnescapeDataString(values[0][1]);
            }
            if (input.Length < 4 || input.Length > 8192 || input.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                throw new ArgumentException("The pairing value is incomplete or invalid.");
            return new FeelConnectPairing(input, code);
        }
        // Avoid putting a credential into debugger displays, exceptions or diagnostic output.
        public override string ToString() => IsConnectionCode ? "FeelConnect connection code" : "FeelConnect QR token";
    }

    /// <summary>Bounded, transport-independent decoder. Call using one monotonically increasing clock.</summary>
    public sealed class FeelConnectProtocol
    {
        const int MaxCues = 10000;
        const long MaxTimelineMs = 2 * 60 * 60 * 1000;
        readonly HashSet<string> rooms = new(StringComparer.Ordinal);
        readonly HashSet<string> seenTipIds = new(StringComparer.Ordinal);
        readonly Queue<string> tipIds = new();
        List<Cue> timeline = new();
        int cursor;
        long endAt;
        double percent;
        string activePublisher = "", activeRoom = "", activeKind = "Stopped";
        public long LastInputMs { get; private set; }
        public FeelConnectV2Diagnostics V2Diagnostics { get; private set; } = new();
        public bool RoomsChanged { get; private set; }
        public int PendingCues => timeline.Count - cursor;
        public double Percent => percent;
        public string TargetUser { get; set; } = "";
        // A missing stop cannot leave an immediate command active indefinitely. This is a timeout, not an intensity cap.
        public int ImmediateTimeoutMs { get; set; } = 5000;

        readonly record struct Cue(long At, double Percent);

        public void SetRooms(IEnumerable<string> allowedRooms)
        {
            rooms.Clear();
            foreach (string room in allowedRooms ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(room) && room.Length <= 512) rooms.Add(room);
            if (activeRoom.Length != 0 && !rooms.Contains(activeRoom)) Reset("Video room removed");
        }

        public FeelConnectOutput Reset(string reason = "Stopped")
        {
            timeline.Clear(); cursor = 0; endAt = 0; activePublisher = ""; activeRoom = "";
            percent = 0; activeKind = reason;
            return new FeelConnectOutput(0, reason, true);
        }

        /// <summary>Returns null for traffic that does not change output, including unrecognized messages.</summary>
        public FeelConnectOutput Accept(JsonElement envelope, long nowMs, long utcNowMs)
        {
            RoomsChanged = false;
            if (envelope.ValueKind != JsonValueKind.Object || !TryString(envelope, "room", out string room) || !rooms.Contains(room)) return null;
            if (!envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            string messageType = String(envelope, "message_type");
            string what = String(data, "what");
            string publisher = String(envelope, "publisher");
            if (publisher.Length == 0) publisher = String(data, "src");
            if (messageType == "website:rooms_changed" || what == "rooms_changed") { RoomsChanged = true; return null; }
            if (messageType == "system:presence")
            {
                string action = String(data, "action");
                string departed = String(data, "uuid");
                if (departed.Length == 0) departed = String(data, "publisher");
                if ((action == "leave" || action == "timeout" || action == "empty") && room == activeRoom &&
                    (action == "empty" || (activePublisher.Length != 0 && departed == activePublisher)))
                    return Reset("Publisher disconnected");
                return null;
            }
            if (messageType == "website:device_speed" || what == "device_speed")
            {
                if (!TryNumber(data, "strength", out double value) || !ValidPercent(value)) return null;
                LastInputMs = nowMs;
                return Immediate(value, nowMs, room, publisher, "Website intensity");
            }
            if (messageType == "device:position" && what == "device_percent")
            {
                string target = String(data, "target");
                if (data.TryGetProperty("target", out _) && (target.Length == 0 || TargetUser.Length == 0 || target != TargetUser)) return null;
                if (!TryNumber(data, "payload", out double value) || !ValidPercent(value)) return null;
                LastInputMs = nowMs;
                return Immediate(value, nowMs, room, publisher, "Video intensity");
            }
            if (messageType == "website:tip" || what == "tip")
            {
                var result = Tip(data, nowMs, room, publisher);
                if (result != null) LastInputMs = nowMs;
                return result;
            }
            if (!TryNumber(data, "ver", out double version)) return null;
            if (version == 2)
            {
                if (!data.TryGetProperty("percents", out var values) || values.ValueKind != JsonValueKind.Array ||
                    values.GetArrayLength() == 0 || values.GetArrayLength() > 1000) return null;
                double last = 0, maximum = 0;
                int count = values.GetArrayLength(), positive = 0;
                foreach (var item in values.EnumerateArray())
                {
                    if (!TryNumber(item, out last) || !ValidPercent(last)) return null;
                    if (last > 0) positive++;
                    maximum = Math.Max(maximum, last);
                }
                // Commit only after the entire authorized batch validates. A trailing invalid value
                // must not change statistics, just as it must not change output or its timeout.
                var priorV2 = V2Diagnostics;
                V2Diagnostics = new FeelConnectV2Diagnostics
                {
                    AcceptedBatches = Math.Min(1_000_000_000, priorV2.AcceptedBatches + 1),
                    Samples = Math.Min(1_000_000_000, priorV2.Samples + count),
                    PositiveSamples = Math.Min(1_000_000_000, priorV2.PositiveSamples + positive),
                    MixedBatches = Math.Min(1_000_000_000, priorV2.MixedBatches + (positive > 0 && positive < count ? 1 : 0)),
                    LastBatchSamples = count, LastBatchMaximum = maximum, LastBatchFinal = last
                };
                // The reference receiver sends every array value to the same devices; these are not motor channels.
                // With no timestamps, the most recent value is the meaningful value at BLE dispatch time.
                LastInputMs = nowMs;
                return Immediate(last, nowMs, room, publisher, "Video intensity v2");
            }
            if (version != 3) return null;
            string type = String(data, "type");
            if (type == "stop") { LastInputMs = nowMs; return Reset("Video stopped"); }
            if (type != "play" || !data.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array) return null;
            if (subtitles.GetArrayLength() == 0) { LastInputMs = nowMs; return Reset("Empty video cue batch"); }
            if (subtitles.GetArrayLength() > MaxCues) return null;
            double latency = 0;
            if (TryNumber(data, "serverTime", out double sentAt))
            {
                double receivedAt = TryNumber(envelope, "serverTime", out double serverAt) ? serverAt : utcNowMs;
                latency = receivedAt - sentAt;
                // Old queued playback is not replayed after a stalled connection. Small negative clock skew is harmless.
                if (!double.IsFinite(latency) || latency > 60000 || latency < -60000) return null;
                latency = Math.Max(0, latency);
            }
            var parsed = new List<Cue>();
            double first = double.NaN, previous = -1;
            foreach (var cue in subtitles.EnumerateArray())
            {
                if (!TryNumber(cue, "time", out double time) || !TryNumber(cue, "subtitle", out double intensity) ||
                    time < 0 || time < previous || intensity < 0 || intensity > 4) return null;
                if (double.IsNaN(first)) first = time;
                if (time - first > MaxTimelineMs || time > 1e12) return null;
                long at = nowMs + (long)Math.Round(time - first - latency);
                // The app's generic device path scales subtitle units by 25 before writing a percentage.
                parsed.Add(new Cue(at, intensity * 25));
                previous = time;
            }
            timeline = parsed; cursor = 0; activeRoom = room; activePublisher = publisher; activeKind = "Video cues v3";
            // The published SDK sends 70-second windows every 60 seconds. A sparse window must hold its
            // final value until the next window, rather than ending as soon as its last change is reached.
            endAt = Math.Max(parsed[^1].At + 1500, nowMs + 75000 - (long)Math.Round(latency));
            LastInputMs = nowMs;
            // Every new batch replaces pending cues, including a seek to an earlier playback position.
            return Tick(nowMs) ?? new FeelConnectOutput(percent, activeKind, percent == 0);
        }

        FeelConnectOutput Immediate(double value, long now, string room, string publisher, string kind)
        {
            timeline.Clear(); cursor = 0; activeRoom = room; activePublisher = publisher; activeKind = kind;
            endAt = now + Math.Clamp(ImmediateTimeoutMs, 500, 60000);
            percent = value;
            if (value == 0) return Reset(kind + " stopped");
            return new FeelConnectOutput(value, kind, false);
        }

        public FeelConnectOutput Tick(long nowMs)
        {
            if (endAt != 0 && nowMs >= endAt) return Reset("Cue timeout");
            Cue? latest = null;
            while (cursor < timeline.Count && timeline[cursor].At <= nowMs) latest = timeline[cursor++];
            if (latest is null) return null;
            percent = latest.Value.Percent;
            return new FeelConnectOutput(percent, activeKind, percent == 0);
        }

        FeelConnectOutput Tip(JsonElement data, long now, string room, string publisher)
        {
            if (!TryNumber(data, "strength", out double strength) || !ValidPercent(strength) ||
                !TryNumber(data, "duration", out double seconds) || seconds < 0 || seconds > 3600) return null;
            string id = String(data, "id");
            if (id.Length == 0 && data.TryGetProperty("id", out var numberId) && numberId.ValueKind == JsonValueKind.Number) id = numberId.GetRawText();
            string dedupe = room + ":" + id;
            if (id.Length > 0 && seenTipIds.Contains(dedupe)) return null;
            var parsed = new List<Cue>();
            long duration = (long)Math.Round(seconds * 1000);
            if (duration == 0 || strength == 0) return Reset("Tip stopped");
            if (data.TryGetProperty("pattern", out var pattern) && pattern.ValueKind != JsonValueKind.Null &&
                !(pattern.ValueKind == JsonValueKind.String && pattern.GetString().Length == 0))
            {
                JsonDocument document = null;
                try
                {
                    if (pattern.ValueKind == JsonValueKind.String)
                    {
                        string text = pattern.GetString();
                        if (text.Length > 65536) return null;
                        document = JsonDocument.Parse(text.Replace("&#34;", "\"", StringComparison.Ordinal), new JsonDocumentOptions { MaxDepth = 8 });
                        pattern = document.RootElement;
                    }
                    if (pattern.ValueKind != JsonValueKind.Object) return null;
                    var cycle = new SortedDictionary<long, double>();
                    foreach (var item in pattern.EnumerateObject())
                    {
                        if (!double.TryParse(item.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out double at) ||
                            !double.IsFinite(at) || at < 0 || at > 3600 || !TryNumber(item.Value, out double value) || !ValidPercent(value)) return null;
                        long ms = (long)Math.Round(at * 1000);
                        if (cycle.ContainsKey(ms) || cycle.Count >= 1000) return null;
                        cycle.Add(ms, Math.Round(value * strength / 100, MidpointRounding.AwayFromZero));
                    }
                    if (cycle.Count == 0) return null;
                    long period = cycle.Last().Key;
                    if (cycle.Count > 1 && period < 100) return null;
                    for (long offset = 0; offset < duration; offset += cycle.Count > 1 ? period : duration)
                    {
                        foreach (var point in cycle)
                        {
                            if (offset + point.Key >= duration - 100) break;
                            if (parsed.Count >= MaxCues) return null;
                            parsed.Add(new Cue(now + offset + point.Key, point.Value));
                        }
                        if (cycle.Count == 1) break;
                    }
                }
                catch (JsonException) { return null; }
                finally { document?.Dispose(); }
            }
            else parsed.Add(new Cue(now, strength));
            parsed.Add(new Cue(now + duration, 0));
            if (id.Length > 0)
            {
                seenTipIds.Add(dedupe); tipIds.Enqueue(dedupe);
                while (tipIds.Count > 128) seenTipIds.Remove(tipIds.Dequeue());
            }
            timeline = parsed; cursor = 0; endAt = now + duration + 1; percent = 0;
            activeRoom = room; activePublisher = publisher; activeKind = "Timed tip";
            return Tick(now) ?? new FeelConnectOutput(percent, activeKind, percent == 0);
        }

        internal static bool ValidPercent(double value) => double.IsFinite(value) && value >= 0 && value <= 100;
        internal static string String(JsonElement element, string name) => TryString(element, name, out string value) ? value : "";
        internal static bool TryString(JsonElement element, string name, out string value)
        {
            value = "";
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var found) || found.ValueKind != JsonValueKind.String) return false;
            value = found.GetString() ?? ""; return true;
        }
        internal static bool TryNumber(JsonElement element, string name, out double value)
        {
            value = 0;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found) && TryNumber(found, out value);
        }
        internal static bool TryNumber(JsonElement element, out double value)
        {
            value = 0;
            if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value) && double.IsFinite(value);
            return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
    }
}
