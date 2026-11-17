using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

internal static class JsonCompatibilityTests
{
    public static int Main()
    {
        var json = new JavaScriptSerializer();
        var body = json.Deserialize<Dictionary<string, object>>("{\"level\":20,\"outputs\":[{\"channel\":2,\"level\":0}],\"enabled\":true}");
        if (!(body["level"] is int n && n == 20) || !(body["enabled"] is bool b && b)) throw new Exception("API scalar types changed.");
        var nested = (Dictionary<string, object>)((object[])body["outputs"])[0];
        if (!(nested["channel"] is int c && c == 2)) throw new Exception("API nested objects changed.");
        var invalid = json.Deserialize<Dictionary<string, object>>("{\"level\":1.25,\"large\":2147483648}");
        if (invalid["level"] is int || invalid["large"] is int) throw new Exception("Out-of-contract numbers became valid integers.");
        var roundTrip = json.Deserialize<Dictionary<string, object>>(json.Serialize(body));
        if (!(roundTrip["outputs"] is object[] a && a.Length == 1)) throw new Exception("Nested round trip failed.");
        Console.WriteLine("PASS JSON compatibility preserves API integer validation and nested payloads");
        return 0;
    }
}
