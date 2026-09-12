using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Drops;

/// <summary>The type names an inline column can take: the manifest's names, so the written drop declares them as they are.</summary>
public static class InlineColumnTypes
{
    public const string Text = "string";

    public const string Whole = "long";

    public const string Real = "double";

    public const string Flag = "boolean";

    /// <summary>The CLR type the parquet writer stores a column of <paramref name="type"/> as.</summary>
    public static Type ClrType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            Whole => typeof(long),
            Real => typeof(double),
            Flag => typeof(bool),
            Text => typeof(string),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "An inline column is string, long, double or boolean."),
        };
    }
}

/// <summary>One column of an inline scope: its name as the submission first spelled it, and its type across every row.</summary>
public sealed record InlineColumn(string Name, string Type);

/// <summary>One record of an inline submission: its root row and the rows of each child scope, values as JSON scalars.</summary>
public sealed record InlineRecord(
    IReadOnlyDictionary<string, object?> Row,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Scopes);

/// <summary>
/// The records a source sends in a submission request instead of a drop (design.md section 3.4). Each record has the
/// shape of a mapping fixture, <c>{ "record": { column: value }, "scopes": { scope: [ { column: value } ] } }</c>, and its
/// values are JSON scalars: string, number, boolean or null. A collection is a child scope, never a nested value.
/// Parsing checks the shape and the ceilings, gives each column one type across every row of its scope, and produces the
/// canonical form the ledger stores and hashes: the same records sent with their keys in another order or with other
/// whitespace are the same submission. A message names the record, the scope and the column it is about, never a value.
/// </summary>
public sealed partial class InlineRecords
{
    /// <summary>Records one submission carries. A larger set is a drop.</summary>
    public const int MaxRecords = 1000;

    /// <summary>Child-scope rows across every record of one submission.</summary>
    public const int MaxChildRows = 100_000;

    /// <summary>Columns one scope carries.</summary>
    public const int MaxColumns = 500;

    /// <summary>Child scopes one submission carries.</summary>
    public const int MaxScopes = 32;

    public const int MaxNameLength = 128;

    /// <summary>The canonical form's size ceiling, in UTF-8 bytes: what the ledger stores for one submission.</summary>
    public const int MaxContentBytes = 8 * 1024 * 1024;

    /// <summary>The largest request body the submission route reads: the content ceiling with room for whitespace and the other fields.</summary>
    public const long MaxRequestBytes = 16L * 1024 * 1024;

    public const string RecordProperty = "record";

    public const string ScopesProperty = "scopes";

    private InlineRecords(
        IReadOnlyList<InlineRecord> records,
        IReadOnlyList<InlineColumn> rootColumns,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<InlineColumn>>> scopeColumns,
        long childRows,
        string json,
        int bytes,
        string hash)
    {
        Records = records;
        RootColumns = rootColumns;
        ScopeColumns = scopeColumns;
        ChildRowCount = childRows;
        Json = json;
        ContentBytes = bytes;
        ContentHash = hash;
    }

    public IReadOnlyList<InlineRecord> Records { get; }

    /// <summary>The root rows' columns, in the order the records first name them.</summary>
    public IReadOnlyList<InlineColumn> RootColumns { get; }

    /// <summary>Each child scope the records carry, in the order they first name it, with its columns.</summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<InlineColumn>>> ScopeColumns { get; }

    public long ChildRowCount { get; }

    /// <summary>The canonical form: records in the order sent, keys sorted, numbers in round-trip form.</summary>
    public string Json { get; }

    public int ContentBytes { get; }

    /// <summary>SHA-256 of <see cref="Json"/>.</summary>
    public string ContentHash { get; }

    /// <summary>Parses the stored canonical form (or any JSON text holding the records array).</summary>
    public static InlineRecords Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FlowValidationException($"records are not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    public static InlineRecords Parse(JsonElement records)
    {
        if (records.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("records", "must be a JSON array of records, each { \"record\": { ... }, \"scopes\": { ... } }.");
        }

        var count = records.GetArrayLength();
        if (count == 0)
        {
            throw Invalid("records", "is empty; a submission carries at least one record.");
        }

        if (count > MaxRecords)
        {
            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"holds {count} records; one submission carries at most {MaxRecords}. Split it, or deliver the set as a drop."));
        }

        var root = new ColumnSet(DropManifest.RootScope);
        var scopes = new Dictionary<string, ColumnSet>(StringComparer.OrdinalIgnoreCase);
        var scopeOrder = new List<ColumnSet>();
        var parsed = new List<InlineRecord>(count);
        long childRows = 0;
        var index = 0;
        foreach (var item in records.EnumerateArray())
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"records[{index}]");
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(at, "must be an object with a \"record\" and, optionally, \"scopes\".");
            }

            JsonElement? rowElement = null;
            JsonElement? scopesElement = null;
            foreach (var property in item.EnumerateObject())
            {
                switch (property.Name)
                {
                    case RecordProperty when rowElement is null:
                        rowElement = property.Value;
                        break;
                    case ScopesProperty when scopesElement is null:
                        scopesElement = property.Value;
                        break;
                    case RecordProperty or ScopesProperty:
                        throw Invalid(at, $"names \"{property.Name}\" twice.");
                    default:
                        throw Invalid(at, "has a key other than \"record\" and \"scopes\"; a record carries only those two.");
                }
            }

            if (rowElement is not { } rowValue)
            {
                throw Invalid(at, "has no \"record\"; the root row is required.");
            }

            var row = ReadRow(rowValue, at + "." + RecordProperty, root, allowDeliveryKey: true);
            if (row.Count == 0)
            {
                throw Invalid(at + "." + RecordProperty, "has no columns.");
            }

            var recordScopes = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
            if (scopesElement is { ValueKind: not JsonValueKind.Null } scopesValue)
            {
                var scopesAt = at + "." + ScopesProperty;
                if (scopesValue.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid(scopesAt, "must be an object of scope names to arrays of rows.");
                }

                foreach (var scope in scopesValue.EnumerateObject())
                {
                    if (!ScopeName().IsMatch(scope.Name) || scope.Name.Equals(DropManifest.RootScope, StringComparison.OrdinalIgnoreCase))
                    {
                        throw Invalid(scopesAt, $"names a scope that is not an identifier of at most {MaxNameLength} characters (letters, digits, '_' and '-'), or is the root scope's name '{DropManifest.RootScope}'.");
                    }

                    var scopeAt = scopesAt + "." + scope.Name;
                    if (!scopes.TryGetValue(scope.Name, out var set))
                    {
                        if (scopes.Count >= MaxScopes)
                        {
                            throw Invalid(scopeAt, string.Create(CultureInfo.InvariantCulture, $"would be child scope {MaxScopes + 1}; a submission carries at most {MaxScopes}."));
                        }

                        set = new ColumnSet(scope.Name);
                        scopes[scope.Name] = set;
                        scopeOrder.Add(set);
                    }

                    if (recordScopes.ContainsKey(set.Scope))
                    {
                        throw Invalid(scopeAt, "is named twice in the record (scope names are compared without case).");
                    }

                    if (scope.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw Invalid(scopeAt, "must be an array of rows.");
                    }

                    var rows = new List<IReadOnlyDictionary<string, object?>>(scope.Value.GetArrayLength());
                    foreach (var child in scope.Value.EnumerateArray())
                    {
                        if (++childRows > MaxChildRows)
                        {
                            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"hold more than {MaxChildRows} child rows; one submission carries at most that many. Split it, or deliver the set as a drop."));
                        }

                        rows.Add(ReadRow(child, string.Create(CultureInfo.InvariantCulture, $"{scopeAt}[{rows.Count}]"), set, allowDeliveryKey: false));
                    }

                    recordScopes[set.Scope] = rows;
                }
            }

            parsed.Add(new InlineRecord(row, recordScopes));
            index++;
        }

        var bytes = Canonical(parsed);
        if (bytes.Length > MaxContentBytes)
        {
            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"take {bytes.Length} bytes in canonical form; one submission carries at most {MaxContentBytes}. Split it, or deliver the set as a drop."));
        }

        return new InlineRecords(
            parsed,
            root.Columns(),
            scopeOrder.Select(s => new KeyValuePair<string, IReadOnlyList<InlineColumn>>(s.Scope, s.Columns())).ToList(),
            childRows,
            System.Text.Encoding.UTF8.GetString(bytes),
            bytes.Length,
            Hashing.ContentHash.Of(bytes));
    }

    private static Dictionary<string, object?> ReadRow(JsonElement element, string at, ColumnSet columns, bool allowDeliveryKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(at, "must be an object of column names to values.");
        }

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            if (name.Length == 0 || name.Length > MaxNameLength || name.Trim().Length != name.Length || name.Any(char.IsControl))
            {
                throw Invalid(at, $"has a column name that is empty, longer than {MaxNameLength} characters, padded with spaces, or holds control characters.");
            }

            var columnAt = at + "." + name;
            if (row.ContainsKey(name))
            {
                throw Invalid(columnAt, "is named twice in the row (column names are compared without case).");
            }

            if (name.Equals(DropReader.DeliveryKeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                if (!allowDeliveryKey)
                {
                    throw Invalid(columnAt, $"is reserved in a child row: the row belongs to the record it is nested under, and the written drop joins it by '{DropReader.DeliveryKeyColumn}'.");
                }

                if (property.Value.ValueKind != JsonValueKind.String || !Guid.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, out _))
                {
                    throw Invalid(columnAt, "must be the record's delivery key as a UUID string when it is sent (docs/delivery/drop-contract.md).");
                }
            }

            var value = Scalar(property.Value, columnAt);
            row[columns.Observe(name, value, columnAt)] = value;
        }

        return row;
    }

    private static object? Scalar(JsonElement value, string at) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => Number(value, at),
        JsonValueKind.Object or JsonValueKind.Array => throw Invalid(at, "is a nested value; a value is a string, a number, a boolean or null, and a collection is sent as a child scope under \"scopes\"."),
        _ => throw Invalid(at, "is not a JSON value."),
    };

    private static object Number(JsonElement value, string at)
    {
        if (value.TryGetInt64(out var whole))
        {
            return whole;
        }

        if (value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            throw Invalid(at, "is an integer outside the 64-bit range; send it as a string.");
        }

        if (value.TryGetDouble(out var real) && double.IsFinite(real))
        {
            return real;
        }

        throw Invalid(at, "is a number outside the range of a double; send it as a string.");
    }

    /// <summary>Records in the order sent; within each, the root row, then the scopes by name; every row's columns sorted by name.</summary>
    private static byte[] Canonical(IReadOnlyList<InlineRecord> records)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var record in records)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(RecordProperty);
                WriteRow(writer, record.Row);
                if (record.Scopes.Count > 0)
                {
                    writer.WritePropertyName(ScopesProperty);
                    writer.WriteStartObject();
                    foreach (var (name, rows) in record.Scopes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(name);
                        writer.WriteStartArray();
                        foreach (var row in rows)
                        {
                            WriteRow(writer, row);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return buffer.ToArray();
    }

    private static void WriteRow(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> row)
    {
        writer.WriteStartObject();
        foreach (var (name, value) in row.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case long whole:
                    writer.WriteNumberValue(whole);
                    break;
                case double real:
                    // A real number keeps a fraction or an exponent in its text, so reading the stored form back gives the
                    // column the type it was accepted with: 1000.0 stays a double rather than turning into a long.
                    var literal = real.ToString("R", CultureInfo.InvariantCulture);
                    writer.WriteRawValue(literal.AsSpan().IndexOfAny('.', 'E', 'e') < 0 ? literal + ".0" : literal, skipInputValidation: true);
                    break;
                default:
                    throw new InvalidOperationException($"An inline value is a string, long, double, boolean or null; {value.GetType().Name} is not.");
            }
        }

        writer.WriteEndObject();
    }

    private static FlowValidationException Invalid(string at, string message) => new($"{at} {message}");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex ScopeName();

    /// <summary>The columns of one scope as rows name them: the first spelling of each name, and one type across every row.</summary>
    private sealed class ColumnSet
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _names = [];
        private readonly List<string?> _types = [];
        private readonly List<string> _typedAt = [];

        public ColumnSet(string scope)
        {
            Scope = scope;
        }

        public string Scope { get; }

        /// <summary>Takes a value into its column and returns the column's name as first spelled.</summary>
        public string Observe(string name, object? value, string at)
        {
            if (!_index.TryGetValue(name, out var i))
            {
                if (_names.Count >= MaxColumns)
                {
                    throw Invalid(at, string.Create(CultureInfo.InvariantCulture, $"would be column {MaxColumns + 1} of scope '{Scope}'; a scope carries at most {MaxColumns}."));
                }

                i = _names.Count;
                _index[name] = i;
                _names.Add(name);
                _types.Add(null);
                _typedAt.Add(at);
            }

            if (value is not null)
            {
                var type = TypeOf(value);
                var current = _types[i];
                if (current is null)
                {
                    _types[i] = type;
                    _typedAt[i] = at;
                }
                else if (!current.Equals(type, StringComparison.Ordinal))
                {
                    if (current is InlineColumnTypes.Whole or InlineColumnTypes.Real && type is InlineColumnTypes.Whole or InlineColumnTypes.Real)
                    {
                        _types[i] = InlineColumnTypes.Real;
                    }
                    else
                    {
                        throw Invalid(at, $"is a {Describe(type)}, but {_typedAt[i]} is a {Describe(current)}; a column holds one type in every row of its scope.");
                    }
                }
            }

            return _names[i];
        }

        public IReadOnlyList<InlineColumn> Columns()
            => _names.Select((name, i) => new InlineColumn(name, _types[i] ?? InlineColumnTypes.Text)).ToList();

        private static string TypeOf(object value) => value switch
        {
            string => InlineColumnTypes.Text,
            bool => InlineColumnTypes.Flag,
            long => InlineColumnTypes.Whole,
            _ => InlineColumnTypes.Real,
        };

        private static string Describe(string type) => type switch
        {
            InlineColumnTypes.Text => "string",
            InlineColumnTypes.Flag => "boolean",
            _ => "number",
        };
    }
}
