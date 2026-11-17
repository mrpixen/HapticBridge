using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

// Small compatibility surface for the original gateway, backed by the bundled .NET serializer.
// Preserves integer/array/dictionary values consumed by the local API validation.
namespace System.Web.Script.Serialization
{
    internal sealed class JavaScriptSerializer
    {
        static readonly JsonSerializerOptions Options = CreateOptions();
        static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true, MaxDepth = 64 };
            options.Converters.Add(new PlainObjectConverter());
            return options;
        }
        public string Serialize(object value) { return JsonSerializer.Serialize(value, Options); }
        public T Deserialize<T>(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length > 2097152) throw new ArgumentException("JSON input is too large.");
            return JsonSerializer.Deserialize<T>(value, Options);
        }
        sealed class PlainObjectConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Null: return null;
                    case JsonTokenType.True: return true;
                    case JsonTokenType.False: return false;
                    case JsonTokenType.String: return reader.GetString();
                    case JsonTokenType.Number:
                        if (reader.TryGetInt32(out var small)) return small;
                        if (reader.TryGetInt64(out var large)) return large;
                        return reader.GetDouble();
                    case JsonTokenType.StartArray:
                        var list = new List<object>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            list.Add(Read(ref reader, typeof(object), options));
                        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete JSON array.");
                        return list.ToArray();
                    case JsonTokenType.StartObject:
                        var map = new Dictionary<string, object>(StringComparer.Ordinal);
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name.");
                            string name = reader.GetString();
                            if (!reader.Read()) throw new JsonException("Missing property value.");
                            if (!map.TryAdd(name, Read(ref reader, typeof(object), options))) throw new JsonException("Duplicate property name.");
                        }
                        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Incomplete JSON object.");
                        return map;
                    default: throw new JsonException("Unsupported JSON value.");
                }
            }
            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value == null) { writer.WriteNullValue(); return; }
                if (value.GetType() == typeof(object)) { writer.WriteStartObject(); writer.WriteEndObject(); return; }
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
            }
        }
    }
}
