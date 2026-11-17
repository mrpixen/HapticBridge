using System;
using System.Linq;
using System.Text.Json;
using LovenseConnector;

// Synthetic percentage batches only. No service requests or physical output.
internal static class FeelConnectV2DiagnosticsTests
{
    static int assertions;
    public static void Main()
    {
        BatchMeaningAndUnchangedOutput();
        InvalidBatchIsAtomic();
        AuthorizationAndLifetime();
        Console.WriteLine("FeelConnect V2 diagnostics: " + assertions + " assertions passed (offline).");
    }

    static void Assert(bool condition, string label)
    { assertions++; if (!condition) throw new Exception("V2 diagnostics: " + label); }
    static FeelConnectProtocol Decoder()
    { var protocol = new FeelConnectProtocol(); protocol.SetRooms(new[] { "synthetic-video" }); return protocol; }
    static FeelConnectOutput Accept(FeelConnectProtocol protocol, string samples, long now = 100, string room = "synthetic-video", int version = 2)
    {
        using var envelope = JsonDocument.Parse("{\"room\":" + JsonSerializer.Serialize(room) +
            ",\"publisher\":\"synthetic-private-producer\",\"message_type\":\"room:message\",\"data\":{\"ver\":" + version +
            ",\"percents\":" + samples + "}}");
        return protocol.Accept(envelope.RootElement, now, 1700000000000);
    }

    static void BatchMeaningAndUnchangedOutput()
    {
        var protocol = Decoder();
        var output = Accept(protocol, "[0,0,0]");
        var zeros = protocol.V2Diagnostics;
        Assert(output.IsStop && output.Percent == 0, "all-zero batch remains a stop");
        Assert(zeros.AcceptedBatches == 1 && zeros.Samples == 3 && zeros.PositiveSamples == 0 && zeros.MixedBatches == 0,
            "all-zero traffic is distinguishable from absent traffic");
        Assert(zeros.LastBatchSamples == 3 && zeros.LastBatchMaximum == 0 && zeros.LastBatchFinal == 0, "all-zero last batch summary");

        output = Accept(protocol, "[25,0]", 200);
        var mixed = protocol.V2Diagnostics;
        Assert(output.IsStop && output.Percent == 0 && protocol.Percent == 0, "positive-then-zero does not change final-value output policy");
        Assert(mixed.AcceptedBatches == 2 && mixed.Samples == 5 && mixed.PositiveSamples == 1 && mixed.MixedBatches == 1,
            "positive sample discarded by final-value output is counted");
        Assert(mixed.LastBatchSamples == 2 && mixed.LastBatchMaximum == 25 && mixed.LastBatchFinal == 0, "mixed last batch retains numeric peak and final");
        Assert(zeros.AcceptedBatches == 1 && zeros.LastBatchSamples == 3, "previous diagnostic snapshot remains immutable");

        Accept(protocol, "[0,100,0]", 300);
        Assert(protocol.V2Diagnostics.MixedBatches == 2 && protocol.V2Diagnostics.LastBatchMaximum == 100 && protocol.Percent == 0,
            "zero-positive-zero peak is visible without synthesizing a pulse");
        output = Accept(protocol, "[25,\"50\",50.5]", 400);
        var positive = protocol.V2Diagnostics;
        Assert(output.Percent == 50.5 && !output.IsStop, "valid decimal and numeric-string sample handling stays unchanged");
        Assert(positive.AcceptedBatches == 4 && positive.Samples == 11 && positive.PositiveSamples == 5 && positive.MixedBatches == 2,
            "positive-only batch does not count as mixed");
        Assert(positive.LastBatchSamples == 3 && positive.LastBatchMaximum == 50.5 && positive.LastBatchFinal == 50.5, "latest positive batch replaces last summary");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(positive));
        Assert(json.RootElement.EnumerateObject().Count() == 7 && json.RootElement.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Number),
            "diagnostic serialization contains numeric aggregates only");
    }

    static void InvalidBatchIsAtomic()
    {
        var protocol = Decoder(); Accept(protocol, "[60]", 100);
        var before = protocol.V2Diagnostics;
        string oversized = "[" + string.Join(',', Enumerable.Repeat("10", 1001)) + "]";
        foreach (string invalid in new[] { "[0,100,101]", "[100,-1]", "[100,null]", "[100,\"NaN\"]", "[100,\"Infinity\"]",
            "[100,true]", "[100,{}]", "[100,[]]", "[]", "{}", "null", oversized })
        {
            Assert(Accept(protocol, invalid, 500) == null, "invalid batch rejected: " + invalid.Substring(0, Math.Min(20, invalid.Length)));
            Assert(protocol.V2Diagnostics == before && protocol.Percent == 60 && protocol.LastInputMs == 100,
                "invalid suffix/shape cannot partially change diagnostics, output, or lease");
        }
        Assert(protocol.Tick(5099) == null && protocol.Tick(5100).IsStop, "invalid batches cannot extend prior output timeout");
        Assert(protocol.V2Diagnostics == before, "timeout does not erase useful accepted-input diagnostics");
        Accept(protocol, "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]", 6000);
        Assert(protocol.V2Diagnostics.AcceptedBatches == 2 && protocol.V2Diagnostics.Samples == 1001 &&
            protocol.V2Diagnostics.PositiveSamples == 1001 && protocol.V2Diagnostics.LastBatchSamples == 1000,
            "largest permitted batch is counted once and completely");
    }

    static void AuthorizationAndLifetime()
    {
        var protocol = Decoder(); var empty = protocol.V2Diagnostics;
        Assert(Accept(protocol, "[100,0]", room: "untrusted-video") == null && protocol.V2Diagnostics == empty,
            "well-formed unauthorized room cannot populate diagnostics");
        Assert(Accept(protocol, "[100,0]", version: 9) == null && protocol.V2Diagnostics == empty,
            "unsupported version cannot populate V2 diagnostics");
        Accept(protocol, "[50,0]"); var accepted = protocol.V2Diagnostics;
        protocol.Reset(); protocol.SetRooms(new[] { "replacement-video" });
        Assert(Accept(protocol, "[100,0]") == null && protocol.V2Diagnostics == accepted,
            "removed room cannot change retained session diagnostics");
        Accept(protocol, "[40]", room: "replacement-video");
        Assert(protocol.V2Diagnostics.AcceptedBatches == 2 && protocol.V2Diagnostics.LastBatchFinal == 40,
            "room refresh retains counters and accepts the new authorized room");
        Assert(Decoder().V2Diagnostics == new FeelConnectV2Diagnostics(), "new pairing decoder starts with empty diagnostics");
    }
}
