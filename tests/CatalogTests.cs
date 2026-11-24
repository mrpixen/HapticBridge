using System;
using System.Linq;
using LovenseConnector;
class CatalogTests
{
    static void Require(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Main(string[] args)
    {
        var catalog = new Catalog(args[0]);
        var edge = catalog.Match("LVS-Edge299", "p");
        Require(edge.Name == "Edge 2", "Product generation is preserved separately from firmware");
        Require(edge.Features.Contains("Vibration motor 1") && edge.Features.Contains("Vibration motor 2"), "Edge exposes its two motors");
        var gush = catalog.Match("LVS-Legacy99", "ed");
        Require(gush.Name == "Gush" && gush.Features.SequenceEqual(new[] { "Vibration" }), "Legacy advertisement resolves through model symbol");
        var lush = catalog.Match("LVS-Shared99", "s");
        Require(lush.Ambiguous && lush.Name == "Lush family", "Shared symbols do not invent a generation");
        Require(Catalog.VibrationChannels(lush).SequenceEqual(new[] { 0 }), "Shared single-motor families use the unnumbered channel");
        var namedLush = catalog.Match("LVS-Lush399", "s");
        Require(namedLush.Name == "Lush 3" && !namedLush.Ambiguous && Catalog.VibrationChannels(namedLush).SequenceEqual(new[] { 0 }), "Explicit single-motor generation exposes channel zero");
        var lapis = catalog.Match("LVS-Lapis99", "u");
        Require(lapis.Name == "Lapis" && Catalog.VibrationChannels(lapis).SequenceEqual(new[] { 1, 2, 3 }), "Triple-motor models expose each numbered channel");
        var osci = catalog.Match("LVS-Shared99", "oc");
        Require(osci.Ambiguous && !osci.Features.Contains("Oscillation"), "Ambiguous models expose only common capabilities");
        var namedOsci = catalog.Match("LVS-Osci399", "oc");
        Require(namedOsci.Features.Contains("Oscillation"), "Explicit model name selects generation-specific functions");
        var unknown = catalog.Match("LVS-Unknown99", "xy");
        Require(unknown.Features.Length == 0, "Unknown models do not inherit actuator capabilities");
        Require(Catalog.VibrationChannels(unknown).Length == 0, "Unknown devices expose no motor channels");
        var prefixOnly=catalog.Match("LVS-EdgePrototype99", "xy");
        Require(prefixOnly.Ambiguous&&prefixOnly.Features.Length==0, "A model-name prefix alone cannot grant actuator capabilities");
        var sharedPrefix=catalog.Match("LVS-Osci3Prototype99", "oc");
        Require(sharedPrefix.Ambiguous&&!sharedPrefix.Features.Contains("Oscillation"), "An unrecognized suffix falls back to common symbol capabilities");
        var mini=catalog.Match("LOVE-LushMini99", "s");
        Require(!mini.Ambiguous&&mini.Name=="LushMini", "Known alphabetic model suffixes still match their complete catalog entry");
        var lowercase=catalog.Match("lvs-edge299", "p");
        Require(!lowercase.Ambiguous&&Catalog.VibrationChannels(lowercase).SequenceEqual(new[]{1,2}), "Numeric generations and case-insensitive names remain supported");
        Require(catalog.Count == 43, "Complete downloaded model catalog parses");
    }
}
