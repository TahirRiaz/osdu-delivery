using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Json;

/// <summary>
/// Byte-stable JSON serialisation (design.md section 6.2). Object keys are sorted ordinally, numbers are written in
/// their shortest round-trip invariant form, nulls are omitted from objects, and no whitespace is emitted. Two
/// renders of the same logical document produce identical bytes, which is what makes the content hash meaningful.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialises a node to canonical UTF-8 bytes.</summary>
    public static byte[] ToBytes(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, node);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Serialises a node to a canonical string.</summary>
    public static string ToString(JsonNode? node) => Encoding.UTF8.GetString(ToBytes(node));

    /// <summary>Parses arbitrary JSON text and returns its canonical form.</summary>
    public static string Canonicalize(string json) => ToString(JsonNode.Parse(json));

    /// <summary>Indented rendering of the canonical form, for diffs and plan output.</summary>
    public static string Pretty(JsonNode? node) => Normalize(node)?.ToJsonString(PrettyOptions) ?? "null";

    /// <summary>
    /// Returns a canonical copy of the node: sorted keys, nulls removed from objects, numbers normalised. Arrays keep
    /// their order because array order is semantically significant in OSDU documents (curves, for example).
    /// </summary>
    public static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        if (kv.Value is null)
                        {
                            continue;
                        }

                        var child = Normalize(kv.Value);
                        if (child is not null)
                        {
                            result[kv.Key] = child;
                        }
                    }

                    return result;
                }
            case JsonArray arr:
                {
                    var result = new JsonArray();
                    foreach (var item in arr)
                    {
                        // Null array elements are kept: removing them would shift positions.
                        result.Add(item is null ? null : Normalize(item));
                    }

                    return result;
                }
            case JsonValue value:
                return NormalizeValue(value);
            default:
                return node.DeepClone();
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Value is null)
                    {
                        continue;
                    }

                    writer.WritePropertyName(kv.Key);
                    Write(writer, kv.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray arr:
                writer.WriteStartArray();
                foreach (var item in arr)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteValue(writer, value);
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        var normalized = NormalizeValue(value);
        if (normalized.TryGetValue<string>(out var s))
        {
            writer.WriteStringValue(s);
        }
        else if (normalized.TryGetValue<bool>(out var b))
        {
            writer.WriteBooleanValue(b);
        }
        else if (normalized.TryGetValue<long>(out var l))
        {
            writer.WriteNumberValue(l);
        }
        else if (normalized.TryGetValue<double>(out var d))
        {
            writer.WriteRawValue(FormatDouble(d), skipInputValidation: true);
        }
        else
        {
            normalized.WriteTo(writer);
        }
    }

    /// <summary>
    /// Collapses the many ways a scalar can arrive (int, long, decimal, float, double, JsonElement) into one of
    /// string, bool, long or double, so that 1, 1.0 and 1e0 all canonicalise identically.
    /// </summary>
    private static JsonValue NormalizeValue(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => JsonValue.Create(element.GetString()!),
                JsonValueKind.True => JsonValue.Create(true),
                JsonValueKind.False => JsonValue.Create(false),
                JsonValueKind.Number => NormalizeNumber(element),
                _ => value,
            };
        }

        if (value.TryGetValue<string>(out var s))
        {
            return JsonValue.Create(s);
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return JsonValue.Create(b);
        }

        if (value.TryGetValue<DateTime>(out var dt))
        {
            return JsonValue.Create(FormatDateTime(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)));
        }

        if (value.TryGetValue<DateTimeOffset>(out var dto))
        {
            return JsonValue.Create(FormatDateTime(dto));
        }

        if (value.TryGetValue<Guid>(out var g))
        {
            return JsonValue.Create(g.ToString("D"));
        }

        var underlying = value.GetValue<object>();
        return underlying switch
        {
            byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(underlying, CultureInfo.InvariantCulture)),
            ulong ul when ul <= long.MaxValue => JsonValue.Create((long)ul),
            ulong ul => JsonValue.Create((double)ul),
            float f => IntegralOrDouble(f),
            double d => IntegralOrDouble(d),
            decimal m => IntegralOrDouble((double)m),
            _ => value,
        };
    }

    private static JsonValue NormalizeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var l))
        {
            return JsonValue.Create(l);
        }

        return IntegralOrDouble(element.GetDouble());
    }

    private static JsonValue IntegralOrDouble(double d)
    {
        if (double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < 9007199254740992d)
        {
            return JsonValue.Create((long)d);
        }

        return JsonValue.Create(d);
    }

    /// <summary>RFC 3339 UTC with fractional seconds only when present.</summary>
    public static string FormatDateTime(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>Shortest round-trip representation, invariant culture.</summary>
    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new InvalidOperationException($"Non-finite number {d} cannot be represented in a canonical JSON document.");
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
