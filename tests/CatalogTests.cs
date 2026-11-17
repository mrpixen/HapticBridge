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
        Require(catalog.Count == 43, "Complete downloaded model catalog parses");
    }
}
