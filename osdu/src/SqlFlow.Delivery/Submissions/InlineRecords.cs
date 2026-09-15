using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Submissions;

/// <summary>The type names an inline column can take, which the landing file writers type their columns by.</summary>
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

/// <summary>One column of an inline dataset: its name as the submission first spelled it, and its type across every row.</summary>
public sealed record InlineColumn(string Name, string Type);

/// <summary>
/// Where one record's payload files already are, and what says whether they changed. The location is a folder the node
/// opens with its own identity when the run delivers: nothing is uploaded and nothing is copied (docs/stage4-design.md
/// section 4.1). The hash is the payload's content hash for a flow that decides payload changes by hash; a flow that
/// takes the files' modified times as the watermark needs none.
/// </summary>
public sealed record InlineFile(string Location, string? Hash);

/// <summary>One record of an inline submission: its dataset row, the rows of each child dataset, and where its files are.</summary>
public sealed record InlineRecord(
    IReadOnlyDictionary<string, object?> Row,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Datasets,
    IReadOnlyDictionary<string, InlineFile> Files);

/// <summary>
/// The records a source sends in a submission request (docs/stage4-design.md section 4). Each record has the
/// shape of a mapping fixture, <c>{ "record": { column: value }, "datasets": { child: [ { column: value } ] } }</c>: the
/// dataset row a mapping reads as <c>dataset.column</c>, and the rows of each child dataset it reads as
/// <c>dataset.child.column</c>. Values are JSON scalars: string, number, boolean or null. A collection is a child dataset,
/// never a nested value. Parsing checks the shape and the ceilings, gives each column one type across every row of its
/// dataset, and produces the canonical form the ledger stores and hashes: the same records sent with their keys in another
/// order or with other whitespace are the same submission. A message names the record, the child dataset and the column
/// it is about, never a value.
/// </summary>
public sealed partial class InlineRecords
{
    /// <summary>Records one submission carries. A larger set is split into several submissions, or landed as files for the pre flow.</summary>
    public const int MaxRecords = 1000;

    /// <summary>Child dataset rows across every record of one submission.</summary>
    public const int MaxChildRows = 100_000;

    /// <summary>Columns the rows of one dataset carry.</summary>
    public const int MaxColumns = 500;

    /// <summary>Child datasets one submission carries.</summary>
    public const int MaxDatasets = 32;

    public const int MaxNameLength = 128;

    /// <summary>The canonical form's size ceiling, in UTF-8 bytes: what the ledger stores for one submission.</summary>
    public const int MaxContentBytes = 8 * 1024 * 1024;

    /// <summary>The largest request body the submission route reads: the content ceiling with room for whitespace and the other fields.</summary>
    public const long MaxRequestBytes = 16L * 1024 * 1024;

    /// <summary>Payload sets one submission points at.</summary>
    public const int MaxFileSets = 8;

    public const int MaxLocationLength = 2000;

    public const int MaxHashLength = 200;

    public const string RecordProperty = "record";

    public const string DatasetsProperty = "datasets";

    public const string FilesProperty = "files";

    /// <summary>The column a record may state its delivery key in; reserved everywhere else.</summary>
    public const string DeliveryKeyColumn = "deliveryKey";

    private static readonly IReadOnlyDictionary<string, InlineFile> NoFiles = new Dictionary<string, InlineFile>(StringComparer.OrdinalIgnoreCase);

    private InlineRecords(
        IReadOnlyList<InlineRecord> records,
        IReadOnlyList<InlineColumn> rootColumns,
        IReadOnlyList<KeyValuePair<string, IReadOnlyList<InlineColumn>>> datasetColumns,
        IReadOnlyList<string> fileSets,
        long childRows,
        string json,
        int bytes,
        string hash)
    {
        Records = records;
        RootColumns = rootColumns;
        DatasetColumns = datasetColumns;
        FileSets = fileSets;
        ChildRowCount = childRows;
        Json = json;
        ContentBytes = bytes;
        ContentHash = hash;
    }

    public IReadOnlyList<InlineRecord> Records { get; }

    /// <summary>The dataset rows' columns, in the order the records first name them.</summary>
    public IReadOnlyList<InlineColumn> RootColumns { get; }

    /// <summary>Each child dataset the records carry, in the order they first name it, with its columns.</summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<InlineColumn>>> DatasetColumns { get; }

    /// <summary>The payload sets the records point at, in the order they first name one.</summary>
    public IReadOnlyList<string> FileSets { get; }

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
            throw Invalid("records", "must be a JSON array of records, each { \"record\": { ... }, \"datasets\": { ... } }.");
        }

        var count = records.GetArrayLength();
        if (count == 0)
        {
            throw Invalid("records", "is empty; a submission carries at least one record.");
        }

        if (count > MaxRecords)
        {
            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"holds {count} records; one submission carries at most {MaxRecords}. Split it into several submissions, or land the files for the pre-ingestion flow."));
        }

        var root = new ColumnSet(SourceDatasets.Record);
        var datasets = new Dictionary<string, ColumnSet>(StringComparer.OrdinalIgnoreCase);
        var datasetOrder = new List<ColumnSet>();
        var fileSets = new List<string>();
        var parsed = new List<InlineRecord>(count);
        long childRows = 0;
        var index = 0;
        foreach (var item in records.EnumerateArray())
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"records[{index}]");
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(at, "must be an object with a \"record\" and, optionally, \"datasets\" and \"files\".");
            }

            JsonElement? rowElement = null;
            JsonElement? datasetsElement = null;
            JsonElement? filesElement = null;
            foreach (var property in item.EnumerateObject())
            {
                switch (property.Name)
                {
                    case RecordProperty when rowElement is null:
                        rowElement = property.Value;
                        break;
                    case DatasetsProperty when datasetsElement is null:
                        datasetsElement = property.Value;
                        break;
                    case FilesProperty when filesElement is null:
                        filesElement = property.Value;
                        break;
                    case RecordProperty or DatasetsProperty or FilesProperty:
                        throw Invalid(at, $"names \"{property.Name}\" twice.");
                    default:
                        throw Invalid(at, "has a key other than \"record\", \"datasets\" and \"files\"; a record carries only those three.");
                }
            }

            if (rowElement is not { } rowValue)
            {
                throw Invalid(at, "has no \"record\"; the dataset row is required.");
            }

            var row = ReadRow(rowValue, at + "." + RecordProperty, root);
            if (row.Count == 0)
            {
                throw Invalid(at + "." + RecordProperty, "has no columns.");
            }

            var recordDatasets = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
            if (datasetsElement is { ValueKind: not JsonValueKind.Null } datasetsValue)
            {
                var datasetsAt = at + "." + DatasetsProperty;
                if (datasetsValue.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid(datasetsAt, "must be an object of child dataset names to arrays of rows.");
                }

                foreach (var dataset in datasetsValue.EnumerateObject())
                {
                    if (!Identifier().IsMatch(dataset.Name) || dataset.Name.Equals(SourceDatasets.Record, StringComparison.OrdinalIgnoreCase))
                    {
                        throw Invalid(datasetsAt, $"names a child dataset that is not an identifier of at most {MaxNameLength} characters (letters, digits, '_' and '-'), or is '{SourceDatasets.Record}', which names the dataset row itself.");
                    }

                    var datasetAt = datasetsAt + "." + dataset.Name;
                    if (!datasets.TryGetValue(dataset.Name, out var set))
                    {
                        if (datasets.Count >= MaxDatasets)
                        {
                            throw Invalid(datasetAt, string.Create(CultureInfo.InvariantCulture, $"would be child dataset {MaxDatasets + 1}; a submission carries at most {MaxDatasets}."));
                        }

                        set = new ColumnSet(dataset.Name);
                        datasets[dataset.Name] = set;
                        datasetOrder.Add(set);
                    }

                    if (recordDatasets.ContainsKey(set.Dataset))
                    {
                        throw Invalid(datasetAt, "is named twice in the record (child dataset names are compared without case).");
                    }

                    if (dataset.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw Invalid(datasetAt, "must be an array of rows.");
                    }

                    var rows = new List<IReadOnlyDictionary<string, object?>>(dataset.Value.GetArrayLength());
                    foreach (var child in dataset.Value.EnumerateArray())
                    {
                        if (++childRows > MaxChildRows)
                        {
                            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"hold more than {MaxChildRows} child rows; one submission carries at most that many. Split it into several submissions, or land the files for the pre-ingestion flow."));
                        }

                        rows.Add(ReadRow(child, string.Create(CultureInfo.InvariantCulture, $"{datasetAt}[{rows.Count}]"), set));
                    }

                    recordDatasets[set.Dataset] = rows;
                }
            }

            parsed.Add(new InlineRecord(row, recordDatasets, ReadFiles(filesElement, at, fileSets)));
            index++;
        }

        var bytes = Canonical(parsed);
        if (bytes.Length > MaxContentBytes)
        {
            throw Invalid("records", string.Create(CultureInfo.InvariantCulture, $"take {bytes.Length} bytes in canonical form; one submission carries at most {MaxContentBytes}. Split it into several submissions, or land the files for the pre-ingestion flow."));
        }

        return new InlineRecords(
            parsed,
            root.Columns(),
            datasetOrder.Select(s => new KeyValuePair<string, IReadOnlyList<InlineColumn>>(s.Dataset, s.Columns())).ToList(),
            fileSets,
            childRows,
            System.Text.Encoding.UTF8.GetString(bytes),
            bytes.Length,
            Hashing.ContentHash.Of(bytes));
    }

    private static Dictionary<string, object?> ReadRow(JsonElement element, string at, ColumnSet columns)
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

            var value = Scalar(property.Value, columnAt);

            // The delivery key is derived from the mapping's dataset key, never sent as a column. A record may state the
            // key it expects, which is checked against the derived one; a child row has no key of its own to state.
            if (name.Equals(DeliveryKeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                if (!columns.Dataset.Equals(SourceDatasets.Record, StringComparison.Ordinal))
                {
                    throw Invalid(columnAt, $"is reserved in a child row: a child row belongs to the record its parent names, so it carries no delivery key of its own.");
                }

                if (value is not string text || !Guid.TryParse(text, CultureInfo.InvariantCulture, out _))
                {
                    throw Invalid(columnAt, "must be the record's delivery key as a UUID, which is checked against the key the mapping derives; omit it to take the derived key.");
                }
            }
            row[columns.Observe(name, value, columnAt)] = value;
        }

        return row;
    }

    /// <summary>
    /// Where a record's files are: <c>{ "curves": "abfss://..." }</c>, or <c>{ "curves": { "location": ..., "hash": ... } }</c>
    /// when the flow decides payload changes by content hash. Nothing is uploaded; the node opens the location when the
    /// run delivers, so what is checked here is the shape, and the flow's own roots are checked when the request is accepted.
    /// </summary>
    private static IReadOnlyDictionary<string, InlineFile> ReadFiles(JsonElement? element, string at, List<string> fileSets)
    {
        if (element is not { ValueKind: not JsonValueKind.Null } value)
        {
            return NoFiles;
        }

        var filesAt = at + "." + FilesProperty;
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(filesAt, "must be an object of payload names to where that payload's files are.");
        }

        var files = new Dictionary<string, InlineFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!Identifier().IsMatch(property.Name))
            {
                throw Invalid(filesAt, $"names a payload that is not an identifier of at most {MaxNameLength} characters (letters, digits, '_' and '-').");
            }

            var setAt = filesAt + "." + property.Name;
            if (files.ContainsKey(property.Name))
            {
                throw Invalid(setAt, "is named twice in the record (payload names are compared without case).");
            }

            string? location;
            string? hash = null;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    location = property.Value.GetString();
                    break;
                case JsonValueKind.Object:
                    location = null;
                    foreach (var field in property.Value.EnumerateObject())
                    {
                        switch (field.Name)
                        {
                            case "location" when location is null:
                                location = field.Value.ValueKind == JsonValueKind.String
                                    ? field.Value.GetString()
                                    : throw Invalid(setAt + ".location", "must be the folder or glob the payload's files are in.");
                                break;
                            case "hash" when hash is null:
                                hash = field.Value.ValueKind == JsonValueKind.String
                                    ? field.Value.GetString()
                                    : throw Invalid(setAt + ".hash", "must be the payload's content hash as a string.");
                                break;
                            default:
                                throw Invalid(setAt, "has a key other than \"location\" and \"hash\".");
                        }
                    }

                    break;
                default:
                    throw Invalid(setAt, "must be where the payload's files are, as a location or as { \"location\": ..., \"hash\": ... }.");
            }

            if (string.IsNullOrWhiteSpace(location) || location.Length > MaxLocationLength || location.Any(char.IsControl))
            {
                throw Invalid(setAt, $"must name where the payload's files are: 1 to {MaxLocationLength} characters without control characters.");
            }

            if (hash is not null && (hash.Length == 0 || hash.Length > MaxHashLength || hash.Any(char.IsControl)))
            {
                throw Invalid(setAt + ".hash", $"must be 1 to {MaxHashLength} characters without control characters.");
            }

            if (!fileSets.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (fileSets.Count >= MaxFileSets)
                {
                    throw Invalid(setAt, string.Create(CultureInfo.InvariantCulture, $"names payload {MaxFileSets + 1}; a submission carries at most {MaxFileSets} payloads."));
                }

                fileSets.Add(property.Name);
            }

            files[fileSets.First(s => s.Equals(property.Name, StringComparison.OrdinalIgnoreCase))] = new InlineFile(location.Trim(), hash);
        }

        return files;
    }

    private static object? Scalar(JsonElement value, string at) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => Number(value, at),
        JsonValueKind.Object or JsonValueKind.Array => throw Invalid(at, "is a nested value; a value is a string, a number, a boolean or null, and a collection is sent as a child dataset under \"datasets\"."),
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

    /// <summary>Records in the order sent; within each, the dataset row, then the child datasets by name; every row's columns sorted by name.</summary>
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
                if (record.Datasets.Count > 0)
                {
                    writer.WritePropertyName(DatasetsProperty);
                    writer.WriteStartObject();
                    foreach (var (name, rows) in record.Datasets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
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

                if (record.Files.Count > 0)
                {
                    writer.WritePropertyName(FilesProperty);
                    writer.WriteStartObject();
                    foreach (var (name, file) in record.Files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(name);
                        writer.WriteStartObject();
                        writer.WriteString("location", file.Location);
                        if (file.Hash is { } hash)
                        {
                            writer.WriteString("hash", hash);
                        }

                        writer.WriteEndObject();
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
    private static partial Regex Identifier();

    /// <summary>The columns of one dataset as its rows name them: the first spelling of each name, and one type across every row.</summary>
    private sealed class ColumnSet
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _names = [];
        private readonly List<string?> _types = [];
        private readonly List<string> _typedAt = [];

        public ColumnSet(string dataset)
        {
            Dataset = dataset;
        }

        /// <summary>The child dataset's name, or <see cref="SourceDatasets.Record"/> for the dataset row.</summary>
        public string Dataset { get; }

        /// <summary>How messages name the dataset.</summary>
        private string What => Dataset.Equals(SourceDatasets.Record, StringComparison.Ordinal) ? "the dataset row" : $"child dataset '{Dataset}'";

        /// <summary>Takes a value into its column and returns the column's name as first spelled.</summary>
        public string Observe(string name, object? value, string at)
        {
            if (!_index.TryGetValue(name, out var i))
            {
                if (_names.Count >= MaxColumns)
                {
                    throw Invalid(at, string.Create(CultureInfo.InvariantCulture, $"would be column {MaxColumns + 1} of {What}; the rows of one dataset carry at most {MaxColumns} columns."));
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
                        throw Invalid(at, $"is a {Describe(type)}, but {_typedAt[i]} is a {Describe(current)}; a column holds one type in every row of its dataset.");
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
