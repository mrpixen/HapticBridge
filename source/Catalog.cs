using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LovenseConnector
{
    public sealed class ModelInfo
    {
        public string type { get; set; }
        public string name { get; set; }
        public string[] symbols { get; set; }
        public string[] features { get; set; }
    }

    public sealed class ModelMatch
    {
        public string Name;
        public string[] Features;
        public bool Ambiguous;
    }

    public sealed class Catalog
    {
        readonly ModelInfo[] models;
        public int Count { get { return models.Length; } }
        public Catalog(string path) : this(File.ReadAllText(path), true) { }
        private Catalog(string content,bool fromJson)
        {
            models = new JavaScriptSerializer().Deserialize<ModelInfo[]>(content);
            if (models == null || models.Length == 0) throw new InvalidDataException("The model catalog is empty.");
        }
        public static Catalog FromJson(string content) { return new Catalog(content,true); }
        public static int[] VibrationChannels(ModelMatch model)
        {
            if(model.Features==null || !model.Features.Contains("Vibration")) return new int[0];
            var numbered=Enumerable.Range(1,3).Where(c=>model.Features.Contains("Vibration motor "+c)).ToArray();
            return numbered.Length>0?numbered:new[]{0};
        }
        public ModelMatch Match(string advertisedName, string symbol)
        {
            string shortName = Regex.Replace(advertisedName ?? "", "^(LVS-|LOVE-)", "", RegexOptions.IgnoreCase);
            // Newer names end in two firmware digits, e.g. Edge204 = Edge2 + 04.
            string product = Regex.Replace(shortName, @"\d{2}$", "");
            var named = models.Where(m => product.StartsWith(m.type, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.type.Length).FirstOrDefault();
            if (named != null)
            {
                return new ModelMatch { Name = Regex.Replace(product, @"([a-zA-Z])(\d)", "$1 $2"), Features = named.features, Ambiguous = false };
            }
            var candidates = models.Where(m => m.symbols.Any(s => String.Equals(s, symbol, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length == 0)
                return new ModelMatch { Name = advertisedName, Features = new string[0], Ambiguous = true };
            // Shared symbols are not enough to identify a generation. Only show common features.
            var features = candidates[0].features.AsEnumerable();
            foreach (var candidate in candidates.Skip(1)) features = features.Intersect(candidate.features);
            return new ModelMatch { Name = candidates[0].name + (candidates.Length > 1 ? " family" : ""), Features = features.ToArray(), Ambiguous = candidates.Length > 1 };
        }
    }
}
