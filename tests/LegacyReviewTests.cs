using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

internal static class LegacyReviewTests
{
    static int checks;
    static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception("Legacy review: " + label);
        checks++;
    }

    static JsonElement Wire(string channel, object payload, long time, string publisher = "fixture-publisher", int eventType = 0) =>
        JsonSerializer.SerializeToElement(new { c = channel, d = payload, i = publisher, e = eventType,
            p = new { t = checked(time * 10000 + 17).ToString(CultureInfo.InvariantCulture) } });

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static async Task Main()
    {
        const long now = 2000000000000;
        var allowed = new[] { "fixture-website", "fixture-video" };
        var immediate = new { ver = 2, percents = new[] { 25, 75 }, src = "fixture-source" };

        var parsed = FeelConnectLegacy.Normalize(Wire("fixture-video", immediate, now, ""), allowed, now);
        Require(parsed.HasValue, "authorized video message accepted");
        Require(parsed.Value.GetProperty("room").GetString() == "fixture-video", "exact authorized channel retained");
        Require(parsed.Value.GetProperty("publisher").GetString() == "fixture-source", "legacy src supplies missing publisher");
        Require(parsed.Value.GetProperty("legacyTimetoken").GetString() == "20000000000000017", "publish cursor survives beyond double precision");
        Require(!FeelConnectLegacy.Normalize(Wire("unrelated-room", immediate, now), allowed, now).HasValue, "unrelated channel rejected");
        Require(!FeelConnectLegacy.Normalize(Wire("fixture-video-extra", immediate, now), allowed, now).HasValue, "channel prefix does not grant access");
        Require(!FeelConnectLegacy.Normalize(Wire("fixture-video", immediate, now, eventType: 4), allowed, now).HasValue, "file events cannot become actuator messages");
        Require(!FeelConnectLegacy.Normalize(Wire("fixture-video", immediate, now - 120000), allowed, now).HasValue, "stale immediate message rejected");

        var presence = FeelConnectLegacy.Normalize(Wire("fixture-video-pnpres", new { action = "leave", uuid = "fixture-source" }, now), allowed, now);
        Require(presence.HasValue && presence.Value.GetProperty("message_type").GetString() == "system:presence", "derived presence channel is normalized");
        Require(presence.Value.GetProperty("room").GetString() == "fixture-video", "presence maps back to the authorized base room");
        Require(!FeelConnectLegacy.Normalize(Wire("unrelated-room-pnpres", new { action = "leave", uuid = "fixture-source" }, now), allowed, now).HasValue,
            "presence suffix does not authorize another channel");

        foreach (string raw in new[] { "\"20000000000000017\"", "20000000000000017" })
        {
            using var document = JsonDocument.Parse(raw);
            Require(FeelConnectLegacy.DecimalToken(document.RootElement, out string value) && value == "20000000000000017", "exact string and integer cursor supported");
        }
        foreach (string raw in new[] { "null", "true", "\"1e17\"", "1e17", "-1", "\"1.5\"", "\"18446744073709551616\"" })
        {
            using var document = JsonDocument.Parse(raw);
            Require(!FeelConnectLegacy.DecimalToken(document.RootElement, out _), "invalid cursor rejected");
        }

        using var placeholderHttp = new HttpClient(new FixtureHandler());
        foreach (string channel in new[] { "*", "fixture.*", "fixture,other", "fixture/other", "fixture-pnpres", "" })
        {
            bool rejected = false;
            try { _ = new FeelConnectLegacy(placeholderHttp, new[] { channel }, (_, _) => { }, CancellationToken.None, "sub-c-fixture"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "non-exact or virtual subscription input rejected");
        }

        await ReadinessAndCleanup();
        await RoomReplacement();
        await NewerStopWinsOverReplay();
        await FailureStopsBeforeLeaveFinishes();
        Console.WriteLine("Legacy review: " + checks + " independent offline checks passed.");
    }

    static async Task ReadinessAndCleanup()
    {
        using var fixture = new FixtureHandler { HoldHeartbeat = true };
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        int callbacks = 0;
        await using var client = new FeelConnectLegacy(http, new[] { "fixture-website" }, (_, _) => Interlocked.Increment(ref callbacks), CancellationToken.None, "sub-c-fixture");
        client.Start();
        await fixture.PollWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!client.Ready.IsCompleted, "subscribe alone cannot claim presence readiness");
        fixture.ReleaseHeartbeat.TrySetResult();
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        var heartbeat = fixture.Requests.First(u => u.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal));
        string query = Uri.UnescapeDataString(heartbeat.Query);
        Require(query.Contains("uuid=mobile-client-", StringComparison.Ordinal), "legacy website sees the required client prefix");
        Require(query.Contains("messageHandlerVersion", StringComparison.Ordinal) && query.Contains(":3", StringComparison.Ordinal), "presence advertises timed-cue support");
        Require(query.Contains("heartbeat=90", StringComparison.Ordinal), "presence lifetime is bounded");
        Require(fixture.Requests.All(u => u.Scheme == "https" && u.Host == "ps.pndsn.com"), "every transport request uses the fixed TLS host");
        Require(fixture.Requests.All(u => !u.AbsolutePath.Contains("/publish/", StringComparison.Ordinal) && !u.Query.Contains("auth=", StringComparison.Ordinal)),
            "transport never publishes or borrows a partner credential");
        await client.DisposeAsync();
        Require(fixture.CancelledPolls > 0, "disconnect cancels its pending long poll");
        Require(fixture.Requests.Any(u => u.AbsolutePath.EndsWith("/leave", StringComparison.Ordinal)), "disconnect announces presence leave");
        Require(callbacks == 0, "empty subscription never creates output");
    }

    static async Task RoomReplacement()
    {
        using var fixture = new FixtureHandler();
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        await using var client = new FeelConnectLegacy(http, new[] { "fixture-website", "fixture-old" }, (_, _) => { }, CancellationToken.None, "sub-c-fixture");
        client.Start();
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.PollWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int generation = client.Generation;
        client.SetChannels(new[] { "fixture-website", "fixture-new" });
        await fixture.Reconfigured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(client.Generation != generation, "room replacement invalidates old callback generations");
        Require(fixture.CancelledPolls > 0, "room replacement cancels the old subscription");
        Require(fixture.Requests.Any(u => u.AbsolutePath.Contains("/fixture-old/leave", StringComparison.Ordinal)), "removed room receives a leave");
        var newRequest = fixture.Requests.Last(u => u.AbsolutePath.Contains("/v2/subscribe/", StringComparison.Ordinal));
        Require(newRequest.AbsolutePath.Contains("fixture-new", StringComparison.Ordinal) && !newRequest.AbsolutePath.Contains("fixture-old", StringComparison.Ordinal),
            "replacement polls contain only the new authorization snapshot");
        Require(newRequest.Query.Contains("tt=" + fixture.InitialCursor, StringComparison.Ordinal),
            "new room catches the fresh subscription gap using the previous exact cursor");
        Require(newRequest.Query.Contains("tr=2", StringComparison.Ordinal),
            "restored cursor uses the latest subscription response region");
    }

    static async Task NewerStopWinsOverReplay()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var fixture = new FixtureHandler
        {
            InitialMessages = new[] {
                Wire("fixture-video", new { ver = 3, type = "stop" }, now),
                Wire("fixture-video", new { ver = 2, percents = new[] { 80 } }, now - 1),
                Wire("fixture-video", new { ver = 3, type = "stop" }, now),
            }
        };
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        var received = new ConcurrentQueue<JsonElement>();
        await using var client = new FeelConnectLegacy(http, new[] { "fixture-video" }, (value, _) => received.Enqueue(value.Clone()), CancellationToken.None, "sub-c-fixture");
        client.Start();
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Require(received.Count == 1, "older play and duplicate stop are dropped after a newer stop");
        Require(received.TryPeek(out var only) && only.GetProperty("data").GetProperty("type").GetString() == "stop",
            "a delayed replay cannot replace the latest stop");
    }

    static async Task FailureStopsBeforeLeaveFinishes()
    {
        using var fixture = new FixtureHandler { HoldLeave = true };
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        var disconnected = Signal();
        await using var client = new FeelConnectLegacy(http, new[] { "fixture-video" }, (_, _) => { }, CancellationToken.None, "sub-c-fixture", _ => disconnected.TrySetResult());
        client.Start();
        await client.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.PollWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.FailPoll.TrySetResult();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Leaving.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!client.Worker.IsCompleted, "output failure is signaled before slow leave cleanup completes");
        fixture.ReleaseLeave.TrySetResult();
        await client.DisposeAsync();
    }

    sealed class FixtureHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<Uri> Requests = new();
        public readonly TaskCompletionSource ReleaseHeartbeat = Signal(), PollWaiting = Signal(), Reconfigured = Signal(), FailPoll = Signal(), Leaving = Signal(), ReleaseLeave = Signal();
        public bool HoldHeartbeat, HoldLeave;
        public readonly string InitialCursor = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 10000 + 17).ToString(CultureInfo.InvariantCulture);
        public JsonElement[] InitialMessages = Array.Empty<JsonElement>();
        public int CancelledPolls;
        int polls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri);
            string path = request.RequestUri.AbsolutePath;
            if (path.Contains("/v2/subscribe/", StringComparison.Ordinal))
            {
                int number = Interlocked.Increment(ref polls);
                if (number == 1 || path.Contains("fixture-new", StringComparison.Ordinal) && number == 3)
                {
                    string cursor = number == 1 ? InitialCursor : (ulong.Parse(InitialCursor, CultureInfo.InvariantCulture) + 20000).ToString(CultureInfo.InvariantCulture);
                    return Json(new { t = new { t = cursor, r = number == 1 ? 1 : 2 }, m = number == 1 ? InitialMessages : Array.Empty<JsonElement>() });
                }
                if (number >= 4 && path.Contains("fixture-new", StringComparison.Ordinal)) Reconfigured.TrySetResult();
                PollWaiting.TrySetResult();
                try { await FailPoll.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref CancelledPolls); throw; }
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                if (HoldHeartbeat) await ReleaseHeartbeat.Task.WaitAsync(cancellationToken);
                return Json(new { status = 200 });
            }
            if (path.EndsWith("/leave", StringComparison.Ordinal))
            {
                Leaving.TrySetResult();
                if (HoldLeave) await ReleaseLeave.Task.WaitAsync(cancellationToken);
                return Json(new { status = 200 });
            }
            throw new InvalidOperationException("Unexpected transport request in offline fixture.");
        }

        static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }
}
