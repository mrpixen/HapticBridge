using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LovenseConnector;

internal static class LegacyPresenceTests
{
    static int checks;
    static void Require(bool condition, string description)
    { if (!condition) throw new Exception("Legacy presence: " + description); checks++; }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static readonly string[] Channels = { "fixture-website", "fixture-video" };

    static async Task Main()
    {
        Summaries();
        await StartupOrder(false);
        await StartupOrder(true);
        await CancelBeforePriming();
        await ScopedInspection();
        await ChangedScope();
        await CancelInspection();
        await ReferenceOccupancy();
        Console.WriteLine("Legacy presence: " + checks + " offline checks passed.");
    }

    static void Summaries()
    {
        using var data = JsonDocument.Parse("""
            {"status":200,"payload":{"channels":{
              "fixture-website":{"occupancy":4,"uuids":[
                {"uuid":"private-self","state":{"messageHandlerVersion":3,"secret":"private-state"}},
                {"uuid":"jsapi-watch-private-browser"},"jsapi-lib-private-player","private-other"]},
              "fixture-video":{"occupancy":2,"uuids":[{"uuid":"private-self","state":{"messageHandlerVersion":3}},"jsapi-lib-private-player"]},
              "private-unrelated":{"occupancy":1,"uuids":["private-unrelated-peer"]}
            }}}
            """);
        var report = FeelConnectLegacy.SummarizePresence(data.RootElement, Channels, Channels[0], "private-self");
        Require(report.Complete, "full authorized response is complete");
        Require(report.Website.Channels == 1 && report.Website.SelfPresentChannels == 1 && report.Website.SelfVersion3Channels == 1,
            "website confirms own presence and timed-cue capability");
        Require(report.Website.TotalOccupancy == 4 && report.Website.WatchPeers == 1 && report.Website.PlayerPeers == 1 && report.Website.OtherPeers == 1,
            "website peer roles counted without retaining identities");
        Require(report.Video.Channels == 1 && report.Video.SelfPresentChannels == 1 && report.Video.PlayerPeers == 1 && report.Video.TotalOccupancy == 2,
            "video roles stay separate and unrelated channels ignored");
        string json = JsonSerializer.Serialize(report);
        Require(!json.Contains("private", StringComparison.Ordinal) && !json.Contains("fixture", StringComparison.Ordinal) &&
            !json.Contains("uuid", StringComparison.OrdinalIgnoreCase) && !json.Contains("secret", StringComparison.Ordinal), "report contains no remote identifiers or state");

        using var flat = JsonDocument.Parse("{\"status\":200,\"occupancy\":1,\"uuids\":[\"private-self\"]}");
        var single = FeelConnectLegacy.SummarizePresence(flat.RootElement, new[] { Channels[0] }, Channels[0], "private-self");
        Require(single.Complete && single.Website.SelfPresentChannels == 1 && single.Website.SelfVersion3Channels == 0 && single.Video.Channels == 0,
            "flat single-channel response supported without inventing capability state");
        using var partial = JsonDocument.Parse("{\"payload\":{\"channels\":{\"fixture-website\":{\"occupancy\":2,\"uuids\":[\"private-self\"]}}}}");
        var incomplete = FeelConnectLegacy.SummarizePresence(partial.RootElement, Channels, Channels[0], "private-self");
        Require(!incomplete.Complete && incomplete.Video.Channels == 1, "missing or truncated occupants cannot claim complete absence");
        foreach (string malformed in new[] {
            "{\"status\":403,\"message\":\"private-secret\"}",
            "{\"occupancy\":-1,\"uuids\":[]}",
            "{\"occupancy\":1,\"uuids\":[\"private-a\",\"private-b\"]}",
            "{\"occupancy\":2,\"uuids\":[\"private-a\",\"private-a\"]}",
            "{\"occupancy\":0,\"uuids\":{\"private-secret\":1}}" })
        {
            using var invalid = JsonDocument.Parse(malformed); bool rejected = false;
            try { FeelConnectLegacy.SummarizePresence(invalid.RootElement, new[] { Channels[0] }, Channels[0], "private-self"); }
            catch (InvalidOperationException error) { rejected = !error.Message.Contains("private", StringComparison.Ordinal); }
            Require(rejected, "malformed response rejected with fixed error text");
        }
    }

    static async Task StartupOrder(bool reject)
    {
        using var fixture = new Fixture { RejectPrime = reject };
        using var http = new HttpClient(fixture) { Timeout = Timeout.InfiniteTimeSpan };
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => { }, CancellationToken.None);
        client.Start();
        await fixture.PrimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30);
        Require(fixture.Heartbeats == 0 && !client.Ready.IsCompleted, "heartbeat cannot announce before initial cursor");
        fixture.ReleasePrime.TrySetResult();
        if (reject)
        {
            bool failed = false;
            try { await client.Ready.WaitAsync(TimeSpan.FromSeconds(3)); } catch (InvalidOperationException) { failed = true; }
            Require(failed && fixture.Heartbeats == 0, "failed subscription never starts heartbeat or becomes ready");
        }
        else
        {
            await client.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            Require(fixture.Heartbeats == 1 && fixture.PrimeCompleted, "readiness follows both priming and heartbeat");
        }
        await client.DisposeAsync();
    }

    static async Task CancelBeforePriming()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture); using var stop = new CancellationTokenSource();
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => { }, stop.Token);
        client.Start(); await fixture.PrimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel(); await client.DisposeAsync();
        Require(client.Ready.IsCanceled && fixture.Heartbeats == 0, "cancellation drains blocked priming and heartbeat wait");
    }

    static async Task ScopedInspection()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => throw new Exception("Inspection emitted a cue."), CancellationToken.None);
        var report = await client.InspectPresenceAsync(Channels[0]);
        var uri = fixture.Requests.Single();
        Require(uri.Scheme == "https" && uri.Host == "ps.pndsn.com" && uri.AbsolutePath.Contains(FeelConnectLegacy.ProductionSubscribeKey, StringComparison.Ordinal),
            "inspection fixes production host and subscribe identifier");
        Require(uri.AbsolutePath.EndsWith("/channel/fixture-video,fixture-website", StringComparison.Ordinal) &&
            !uri.Query.Contains("channel-group", StringComparison.Ordinal) && !uri.AbsolutePath.Contains("subscribe/", StringComparison.Ordinal),
            "inspection reads precisely authorized channels without subscription or enumeration");
        Require(uri.Query.Contains("state=1", StringComparison.Ordinal) && uri.Query.Contains("limit=1000", StringComparison.Ordinal) &&
            uri.Query.Contains("uuid=mobile-client-hapticbridge-", StringComparison.Ordinal), "bounded inspection uses current receiver identity");
        Require(report.Complete && report.Website.SelfPresentChannels == 1 && report.Video.SelfVersion3Channels == 1 && fixture.Heartbeats == 0,
            "read-only inspection reports own presence without sending heartbeat");
        bool rejected = false;
        try { await client.InspectPresenceAsync("unrelated-channel"); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && fixture.Requests.Count == 1, "unauthorized website role rejected before network access");
    }

    static async Task ChangedScope()
    {
        using var fixture = new Fixture { HoldInspection = true }; using var http = new HttpClient(fixture);
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => { }, CancellationToken.None);
        Task<FeelConnectPresenceReport> pending = client.InspectPresenceAsync(Channels[0]);
        await fixture.InspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        client.SetChannels(new[] { Channels[0], "replacement-video" }); fixture.ReleaseInspection.TrySetResult();
        bool rejected = false;
        try { await pending; } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "result from superseded authorization snapshot is rejected");
    }

    static async Task CancelInspection()
    {
        using var fixture = new Fixture { HoldInspection = true }; using var http = new HttpClient(fixture);
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => { }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = client.InspectPresenceAsync(Channels[0], cancellation.Token);
        await fixture.InspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        bool cancelled = false;
        try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        fixture.HoldInspection = false;
        var report = await client.InspectPresenceAsync(Channels[0]);
        Require(cancelled && report.Complete, "cancelled inspection does not cancel the receiver session");
    }

    static async Task ReferenceOccupancy()
    {
        using var fixture = new Fixture(); using var http = new HttpClient(fixture);
        await using var client = new FeelConnectLegacy(http, Channels, (_, _) => throw new Exception("Occupancy inspection emitted a cue."), CancellationToken.None);
        var report = await client.InspectReferenceOccupancyAsync(Channels[0]);
        Uri request = fixture.Requests.Single();
        Require(request.Scheme == "https" && request.Host == "ps.pndsn.com" &&
            request.AbsolutePath.Contains("/sub-key/" + FeelConnectLegacy.ReferenceSubscribeKey + "/channel/fixture-video,fixture-website", StringComparison.Ordinal),
            "reference check uses one fixed published namespace and exact authorized channels");
        Require(request.Query.Contains("disable_uuids=1&state=0&limit=1000", StringComparison.Ordinal) &&
            request.Query.Contains("uuid=mobile-client-hapticbridge-", StringComparison.Ordinal) && !request.Query.Contains("auth=", StringComparison.Ordinal),
            "reference check requests occupancy only with existing receiver identity");
        Require(report.Complete && report.WebsiteChannels == 1 && report.WebsiteOccupancy == 2 && report.VideoChannels == 1 && report.VideoOccupancy == 3,
            "reference report contains fixed role counts");
        string json = JsonSerializer.Serialize(report);
        Require(!json.Contains("private", StringComparison.Ordinal) && !json.Contains("fixture", StringComparison.Ordinal) &&
            !json.Contains("uuid", StringComparison.OrdinalIgnoreCase) && fixture.Heartbeats == 0 && fixture.Requests.Count == 1,
            "reference read leaks no remote text and causes no control or presence traffic");
        using var partial = JsonDocument.Parse("{\"status\":200,\"payload\":{\"channels\":{\"fixture-website\":{\"occupancy\":0}}}}");
        var incomplete = FeelConnectLegacy.SummarizeReferenceOccupancy(partial.RootElement, Channels, Channels[0]);
        Require(!incomplete.Complete && incomplete.WebsiteOccupancy == 0, "missing reference channel is marked incomplete");
        using var invalid = JsonDocument.Parse("{\"occupancy\":-1,\"message\":\"private-error\"}");
        bool rejected = false;
        try { FeelConnectLegacy.SummarizeReferenceOccupancy(invalid.RootElement, new[] { Channels[0] }, Channels[0]); }
        catch (InvalidOperationException error) { rejected = !error.Message.Contains("private", StringComparison.Ordinal); }
        Require(rejected, "reference malformed occupancy returns fixed error text");

        fixture.HoldInspection = true;
        var pending = client.InspectReferenceOccupancyAsync(Channels[0]);
        client.SetChannels(new[] { Channels[0], "replacement-video" }); fixture.ReleaseInspection.TrySetResult();
        rejected = false;
        try { await pending; } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "reference result cannot survive channel authorization replacement");
    }

    sealed class Fixture : HttpMessageHandler
    {
        public readonly ConcurrentQueue<Uri> Requests = new();
        public readonly TaskCompletionSource PrimeEntered = Signal(), ReleasePrime = Signal(), InspectionEntered = Signal(), ReleaseInspection = Signal();
        public bool RejectPrime, PrimeCompleted, HoldInspection;
        public int Heartbeats; int subscriptions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri; Requests.Enqueue(uri);
            if (uri.AbsolutePath.Contains("/v2/subscribe/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref subscriptions) > 1) await Task.Delay(Timeout.Infinite, cancellationToken);
                PrimeEntered.TrySetResult(); await ReleasePrime.Task.WaitAsync(cancellationToken);
                if (RejectPrime) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                PrimeCompleted = true;
                return Json("{\"t\":{\"t\":\"20000000000000017\",\"r\":1},\"m\":[]}");
            }
            if (uri.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            { Interlocked.Increment(ref Heartbeats); return Json("{\"status\":200}"); }
            if (uri.AbsolutePath.EndsWith("/leave", StringComparison.Ordinal)) return Json("{\"status\":200}");
            InspectionEntered.TrySetResult();
            if (HoldInspection) await ReleaseInspection.Task.WaitAsync(cancellationToken);
            if (uri.Query.Contains("disable_uuids=1", StringComparison.Ordinal))
                return Json("{\"status\":200,\"message\":\"private-message\",\"payload\":{\"channels\":{\"fixture-website\":{\"occupancy\":2},\"fixture-video\":{\"occupancy\":3},\"private-unrelated\":{\"occupancy\":99}}}}");
            string own = Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(x => x.StartsWith("uuid=", StringComparison.Ordinal)).Substring(5));
            return Json(JsonSerializer.Serialize(new { status = 200, payload = new { channels = Channels.ToDictionary(x => x,
                _ => new { occupancy = 1, uuids = new[] { new { uuid = own, state = new { messageHandlerVersion = 3 } } } }) } }));
        }
        static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
}
