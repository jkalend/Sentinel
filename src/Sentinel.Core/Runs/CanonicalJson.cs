using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Core.Runs;

/// <summary>
/// Small RFC 8785-style canonical JSON writer for run and export manifests.
/// Objects are written with ordinal-sorted property names and no insignificant
/// whitespace. Values used by the manifest are strings, integers, booleans,
/// arrays, and objects.
/// </summary>
public static class CanonicalJson
{
    public static string Serialize<T>(T value) =>
        Serialize(JsonSerializer.SerializeToNode(value) ?? throw new ArgumentNullException(nameof(value)));

    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash<T>(T value) => HashText(Serialize(value));

    public static string HashText(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Serialize(JsonNode.Parse(document.RootElement.GetRawText()));
    }

    public static JsonNode Parse(string json) =>
        JsonNode.Parse(json) ?? throw new JsonException("JSON value is null.");

    public static bool SemanticallyEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var property in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var item in value)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteValue(writer, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON node type {node.GetType().Name}.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        if (value.TryGetValue<bool>(out var boolean))
        {
            writer.WriteBooleanValue(boolean);
            return;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            writer.WriteNumberValue(intValue);
            return;
        }

        if (value.TryGetValue<long>(out var integer))
        {
            writer.WriteNumberValue(integer);
            return;
        }

        if (value.TryGetValue<ulong>(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
            return;
        }

        if (value.TryGetValue<double>(out var number))
        {
            writer.WriteNumberValue(number);
            return;
        }

        if (value.TryGetValue<decimal>(out var decimalNumber))
        {
            writer.WriteNumberValue(decimalNumber);
            return;
        }

        if (value.TryGetValue<string>(out var text))
        {
            writer.WriteStringValue(text);
            return;
        }

        throw new InvalidOperationException("Unsupported canonical JSON scalar.");
    }
}
