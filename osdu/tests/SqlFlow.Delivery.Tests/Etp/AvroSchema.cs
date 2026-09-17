using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Engine.Protocols.Etp;

namespace SqlFlow.Delivery.Tests.Etp;

/// <summary>
/// One Avro type as the pinned ETP protocol declares it (osdu/specs/reservoir-ddms/etp-1.2.avpr), read at test time.
/// This model and the codec over it are deliberately independent of the module's own ETP records: the records carry
/// the schema's field order in code, this carries it as data, and <c>EtpSchemaTests</c> proves the two agree.
/// </summary>
internal abstract record AvroSchema;

internal sealed record AvroPrimitive(string Name) : AvroSchema;

internal sealed record AvroField(string Name, AvroSchema Type);

internal sealed record AvroRecord(string FullName, IReadOnlyList<AvroField> Fields) : AvroSchema;

internal sealed record AvroEnum(string FullName, IReadOnlyList<string> Symbols) : AvroSchema;

internal sealed record AvroFixed(string FullName, int Size) : AvroSchema;

internal sealed record AvroArray(AvroSchema Items) : AvroSchema;

internal sealed record AvroMap(AvroSchema Values) : AvroSchema;

internal sealed record AvroUnion(IReadOnlyList<AvroSchema> Branches) : AvroSchema;

/// <summary>The pinned ETP protocol, parsed once: every named type by its full name.</summary>
internal sealed class AvroProtocol
{
    private static readonly Lazy<AvroProtocol> Pinned = new(() => Parse(
        File.ReadAllText(Path.Combine(OsduContracts.Root, "reservoir-ddms", "etp-1.2.avpr"))));

    private readonly Dictionary<string, JsonElement> _declared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AvroSchema> _resolved = new(StringComparer.Ordinal);

    private AvroProtocol()
    {
    }

    public static AvroProtocol Etp12 => Pinned.Value;

    /// <summary>Every named type the protocol declares, in the order it declares them.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    public AvroSchema Named(string fullName)
    {
        if (_resolved.TryGetValue(fullName, out var known))
        {
            return known;
        }

        if (!_declared.TryGetValue(fullName, out var declaration))
        {
            throw new InvalidOperationException($"The pinned ETP protocol declares no type named {fullName}.");
        }

        var schema = Build(declaration, Namespace(fullName));
        _resolved[fullName] = schema;
        return schema;
    }

    private static AvroProtocol Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();
        var protocol = new AvroProtocol();
        var fallback = root.GetProperty("namespace").GetString()!;
        var names = new List<string>();
        foreach (var type in root.GetProperty("types").EnumerateArray())
        {
            var space = type.TryGetProperty("namespace", out var declared) ? declared.GetString()! : fallback;
            var name = $"{space}.{type.GetProperty("name").GetString()}";
            protocol._declared[name] = type;
            names.Add(name);
        }

        protocol.Names = names;
        return protocol;
    }

    private static string Namespace(string fullName) => fullName[..fullName.LastIndexOf('.')];

    private AvroSchema Build(JsonElement type, string enclosing)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            var name = type.GetString()!;
            return name switch
            {
                "null" or "boolean" or "int" or "long" or "float" or "double" or "bytes" or "string" => new AvroPrimitive(name),
                _ => Named(name.Contains('.', StringComparison.Ordinal) ? name : $"{enclosing}.{name}"),
            };
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return new AvroUnion([.. type.EnumerateArray().Select(branch => Build(branch, enclosing))]);
        }

        var kind = type.GetProperty("type").GetString()!;
        switch (kind)
        {
            case "array":
                return new AvroArray(Build(type.GetProperty("items"), enclosing));
            case "map":
                return new AvroMap(Build(type.GetProperty("values"), enclosing));
            case "record":
            {
                var space = type.TryGetProperty("namespace", out var declared) ? declared.GetString()! : enclosing;
                var fields = new List<AvroField>();
                foreach (var field in type.GetProperty("fields").EnumerateArray())
                {
                    fields.Add(new AvroField(field.GetProperty("name").GetString()!, Build(field.GetProperty("type"), space)));
                }

                return new AvroRecord($"{space}.{type.GetProperty("name").GetString()}", fields);
            }

            case "enum":
            {
                var space = type.TryGetProperty("namespace", out var declared) ? declared.GetString()! : enclosing;
                return new AvroEnum(
                    $"{space}.{type.GetProperty("name").GetString()}",
                    [.. type.GetProperty("symbols").EnumerateArray().Select(symbol => symbol.GetString()!)]);
            }

            case "fixed":
            {
                var space = type.TryGetProperty("namespace", out var declared) ? declared.GetString()! : enclosing;
                return new AvroFixed($"{space}.{type.GetProperty("name").GetString()}", type.GetProperty("size").GetInt32());
            }

            default:
                return new AvroPrimitive(kind);
        }
    }
}

/// <summary>
/// A value tree of an Avro type: a record is its field values in order, an array a list, a map a list of pairs, and a
/// union the branch it took with that branch's value. It is what the schema-driven codec reads and writes.
/// </summary>
internal sealed record AvroUnionValue(int Branch, object? Value);

/// <summary>
/// Reads and writes Avro binary from the schema alone, as data rather than as code. It is the independent half of the
/// conformance test: the module's records must produce and accept exactly what this produces and accepts.
/// </summary>
internal static class AvroCodec
{
    private const string Alphabet = "abcXYZ019_-/:()' éΩ中🚀";

    /// <summary>A value of <paramref name="schema"/> built from a seeded source, so every run exercises the same tree.</summary>
    public static object? Sample(AvroSchema schema, Random random, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(random);
        switch (schema)
        {
            case AvroPrimitive primitive:
                return primitive.Name switch
                {
                    "null" => null,
                    "boolean" => random.Next(2) == 1,
                    "int" => random.Next(2) == 0 ? random.Next(-70000, 70000) : random.Next(int.MinValue, int.MaxValue),
                    "long" => random.NextInt64(long.MinValue, long.MaxValue),
                    "float" => (float)((random.NextDouble() - 0.5) * 1e6),
                    "double" => (random.NextDouble() - 0.5) * 1e12,
                    "bytes" => Bytes(random, random.Next(0, 9)),
                    _ => Text(random),
                };
            case AvroFixed @fixed:
                return Bytes(random, @fixed.Size);
            case AvroEnum @enum:
                return random.Next(@enum.Symbols.Count);
            case AvroRecord record:
            {
                var values = new object?[record.Fields.Count];
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    values[i] = Sample(record.Fields[i].Type, random, depth + 1);
                }

                return values;
            }

            case AvroArray array:
            {
                var items = new List<object?>();
                for (var i = 0; i < Count(random, depth); i++)
                {
                    items.Add(Sample(array.Items, random, depth + 1));
                }

                return items;
            }

            case AvroMap map:
            {
                var entries = new List<KeyValuePair<string, object?>>();
                for (var i = 0; i < Count(random, depth); i++)
                {
                    entries.Add(new KeyValuePair<string, object?>(
                        $"{Text(random)}{i.ToString(CultureInfo.InvariantCulture)}", Sample(map.Values, random, depth + 1)));
                }

                return entries;
            }

            case AvroUnion union:
            {
                var branch = random.Next(union.Branches.Count);
                return new AvroUnionValue(branch, Sample(union.Branches[branch], random, depth + 1));
            }

            default:
                throw new InvalidOperationException($"No sample for {schema}.");
        }
    }

    public static void Write(AvroSchema schema, object? value, EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(writer);
        switch (schema)
        {
            case AvroPrimitive primitive:
                switch (primitive.Name)
                {
                    case "null": break;  // An Avro null is no bytes at all.
                    case "boolean": writer.WriteBoolean((bool)value!); break;
                    case "int": writer.WriteInt((int)value!); break;
                    case "long": writer.WriteLong((long)value!); break;
                    case "float": writer.WriteFloat((float)value!); break;
                    case "double": writer.WriteDouble((double)value!); break;
                    case "bytes": writer.WriteBytes((byte[])value!); break;
                    default: writer.WriteString((string)value!); break;
                }

                break;
            case AvroFixed:
                writer.WriteFixed((byte[])value!);
                break;
            case AvroEnum:
                writer.WriteEnum((int)value!);
                break;
            case AvroRecord record:
            {
                var values = (object?[])value!;
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    Write(record.Fields[i].Type, values[i], writer);
                }

                break;
            }

            case AvroArray array:
            {
                var items = (List<object?>)value!;
                writer.WriteBlockHeader(items.Count);
                foreach (var item in items)
                {
                    Write(array.Items, item, writer);
                }

                writer.WriteBlockEnd();
                break;
            }

            case AvroMap map:
            {
                var entries = (List<KeyValuePair<string, object?>>)value!;
                writer.WriteBlockHeader(entries.Count);
                foreach (var (key, entry) in entries)
                {
                    writer.WriteString(key);
                    Write(map.Values, entry, writer);
                }

                writer.WriteBlockEnd();
                break;
            }

            case AvroUnion union:
            {
                var taken = (AvroUnionValue)value!;
                writer.WriteUnion(taken.Branch);
                Write(union.Branches[taken.Branch], taken.Value, writer);
                break;
            }

            default:
                throw new InvalidOperationException($"Cannot write {schema}.");
        }
    }

    public static object? Read(AvroSchema schema, ref EtpReader reader)
    {
        ArgumentNullException.ThrowIfNull(schema);
        switch (schema)
        {
            case AvroPrimitive primitive:
                return primitive.Name switch
                {
                    "null" => null,
                    "boolean" => reader.ReadBoolean(),
                    "int" => reader.ReadInt(),
                    "long" => reader.ReadLong(),
                    "float" => reader.ReadFloat(),
                    "double" => reader.ReadDouble(),
                    "bytes" => reader.ReadBytes().ToArray(),
                    _ => reader.ReadString(),
                };
            case AvroFixed @fixed:
                return reader.ReadFixed(@fixed.Size).ToArray();
            case AvroEnum @enum:
                return reader.ReadEnum(@enum.Symbols.Count);
            case AvroRecord record:
            {
                var values = new object?[record.Fields.Count];
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    values[i] = Read(record.Fields[i].Type, ref reader);
                }

                return values;
            }

            case AvroArray array:
            {
                var items = new List<object?>();
                for (var count = reader.ReadBlockCount(); count != 0; count = reader.ReadBlockCount())
                {
                    for (var i = 0; i < count; i++)
                    {
                        items.Add(Read(array.Items, ref reader));
                    }
                }

                return items;
            }

            case AvroMap map:
            {
                var entries = new List<KeyValuePair<string, object?>>();
                for (var count = reader.ReadBlockCount(); count != 0; count = reader.ReadBlockCount())
                {
                    for (var i = 0; i < count; i++)
                    {
                        var key = reader.ReadString();
                        entries.Add(new KeyValuePair<string, object?>(key, Read(map.Values, ref reader)));
                    }
                }

                return entries;
            }

            case AvroUnion union:
            {
                var branch = reader.ReadUnion(union.Branches.Count);
                return new AvroUnionValue(branch, Read(union.Branches[branch], ref reader));
            }

            default:
                throw new InvalidOperationException($"Cannot read {schema}.");
        }
    }

    /// <summary>
    /// Whether two value trees of one schema carry the same content. A map's entries are compared by key, because the
    /// order a map's entries go on the wire in is not part of what it means.
    /// </summary>
    public static bool Same(AvroSchema schema, object? left, object? right)
    {
        ArgumentNullException.ThrowIfNull(schema);
        switch (schema)
        {
            case AvroPrimitive { Name: "bytes" }:
            case AvroFixed:
                return ((byte[])left!).AsSpan().SequenceEqual((byte[])right!);
            case AvroPrimitive:
            case AvroEnum:
                return Equals(left, right);
            case AvroRecord record:
            {
                var a = (object?[])left!;
                var b = (object?[])right!;
                for (var i = 0; i < record.Fields.Count; i++)
                {
                    if (!Same(record.Fields[i].Type, a[i], b[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            case AvroArray array:
            {
                var a = (List<object?>)left!;
                var b = (List<object?>)right!;
                if (a.Count != b.Count)
                {
                    return false;
                }

                for (var i = 0; i < a.Count; i++)
                {
                    if (!Same(array.Items, a[i], b[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            case AvroMap map:
            {
                var a = (List<KeyValuePair<string, object?>>)left!;
                var b = (List<KeyValuePair<string, object?>>)right!;
                if (a.Count != b.Count)
                {
                    return false;
                }

                var byKey = b.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                return a.All(entry => byKey.TryGetValue(entry.Key, out var other) && Same(map.Values, entry.Value, other));
            }

            case AvroUnion union:
            {
                var a = (AvroUnionValue)left!;
                var b = (AvroUnionValue)right!;
                return a.Branch == b.Branch && Same(union.Branches[a.Branch], a.Value, b.Value);
            }

            default:
                throw new InvalidOperationException($"Cannot compare {schema}.");
        }
    }

    /// <summary>Entries enough to exercise a block, and none once nesting is deep enough to stay small.</summary>
    private static int Count(Random random, int depth) => depth >= 3 ? random.Next(0, 2) : random.Next(0, 4);

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Text(Random random)
    {
        var length = random.Next(0, 9);
        var text = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var at = random.Next(Alphabet.Length);
            if (char.IsHighSurrogate(Alphabet[at]))
            {
                text.Append(Alphabet[at]).Append(Alphabet[at + 1]);
            }
            else if (char.IsLowSurrogate(Alphabet[at]))
            {
                text.Append(Alphabet[at - 1]).Append(Alphabet[at]);
            }
            else
            {
                text.Append(Alphabet[at]);
            }
        }

        return text.ToString();
    }
}
