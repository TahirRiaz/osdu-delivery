using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;

namespace SqlFlow.Sources.Json;

/// <summary>
/// Flattens one JSON record into one or more ordered (column, value) rows by walking it against a
/// <see cref="JsonFlattenConfig"/>. A direct adaptation of the delta-forge recursive flattener: depth
/// guard, then exclude / json-string / scalar / object / array handling. Every value is emitted as a raw
/// string (numbers keep their original token, objects/arrays kept whole are compacted) so the downstream
/// type-inference step owns all typing.
///
/// Arrays whose path is an explode target produce one row per element, cross-producting with any other
/// exploded array in the same record. <see cref="Flatten"/> is the single-row schema view (every column
/// the record can produce, no row multiplication); <see cref="FlattenRows"/> is the full data view.
/// </summary>
public sealed class JsonPathFlattener
{
    /// <summary>Safety bound on the cross-product size for one record, to fail loudly instead of exhausting memory.</summary>
    public const int MaxRowsPerRecord = 1_000_000;

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private readonly JsonFlattenConfig _config;

    public JsonPathFlattener(JsonFlattenConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// The schema view of a record: a single row carrying every column the record can produce, including
    /// the fields of exploded arrays (visited without multiplying rows). Used to discover a file's columns.
    /// When two paths resolve to the same column name the last value wins.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Flatten(JsonElement record)
    {
        var rows = new List<OrderedRow> { new() };
        FlattenInto(record, "$", 0, rows, multiply: false);
        return rows[0].ToPairs();
    }

    /// <summary>
    /// The data view of a record: the actual output rows. With no explode paths this is always one row;
    /// each exploded array multiplies the rows by its element count (cross-product across sibling arrays).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string?>>> FlattenRows(JsonElement record)
    {
        var rows = new List<OrderedRow> { new() };
        FlattenInto(record, "$", 0, rows, multiply: true);

        var result = new List<IReadOnlyList<KeyValuePair<string, string?>>>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(row.ToPairs());
        }

        return result;
    }

    private void FlattenInto(JsonElement value, string path, int depth, List<OrderedRow> rows, bool multiply)
    {
        // Excluded subtrees are dropped entirely (no column), matching the configurator's "remove" intent.
        if (_config.IsExcluded(path))
        {
            return;
        }

        // An aliased path feeds a shared schema-evolution column; coalesce so a missing/null version-specific
        // path never nulls a value another path already supplied.
        var aliased = _config.IsAliased(path);

        // A json-string path, or anything past the depth limit, is captured whole as one JSON column.
        if (_config.IsJsonString(path) || depth > _config.MaxDepth)
        {
            SetAll(rows, _config.ColumnName(path), AsJsonString(value), aliased);
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                SetAll(rows, _config.ColumnName(path), null, aliased);
                break;

            case JsonValueKind.True:
                SetAll(rows, _config.ColumnName(path), "true", aliased);
                break;

            case JsonValueKind.False:
                SetAll(rows, _config.ColumnName(path), "false", aliased);
                break;

            case JsonValueKind.Number:
                // The raw token preserves precision and any leading/trailing significance that a parse to
                // double would lose; inference later decides int vs. decimal.
                SetAll(rows, _config.ColumnName(path), value.GetRawText(), aliased);
                break;

            case JsonValueKind.String:
                SetAll(rows, _config.ColumnName(path), value.GetString(), aliased);
                break;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
                    if (_config.IsIncluded(childPath))
                    {
                        FlattenInto(property.Value, childPath, depth + 1, rows, multiply);
                    }
                }

                break;

            case JsonValueKind.Array:
                if (_config.ShouldExplode(path))
                {
                    if (multiply)
                    {
                        ExplodeCrossProduct(value, path, depth, rows);
                    }
                    else
                    {
                        ExplodeSchema(value, path, depth, rows);
                    }
                }
                else
                {
                    HandleArray(value, path, depth, rows, multiply, aliased);
                }

                break;

            default:
                SetAll(rows, _config.ColumnName(path), AsJsonString(value), aliased);
                break;
        }
    }

    /// <summary>
    /// Schema view of an exploded array: recurse every element into the current row(s) without multiplying,
    /// so the union of element fields (even across heterogeneous elements) is captured as columns.
    /// </summary>
    private void ExplodeSchema(JsonElement array, string path, int depth, List<OrderedRow> rows)
    {
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            FlattenInto(element, $"{path}[{index}]", depth + 1, rows, multiply: false);
            index++;
        }
    }

    /// <summary>
    /// Data view of an exploded array: one output row per element. Existing rows are cloned per element and
    /// the element flattened into the clones, so several exploded arrays in one record cross-product. An
    /// empty array keeps the existing rows (left-join semantics: the parent is preserved, element columns
    /// stay null) rather than dropping the record.
    /// </summary>
    private void ExplodeCrossProduct(JsonElement array, string path, int depth, List<OrderedRow> rows)
    {
        if (array.GetArrayLength() == 0)
        {
            return;
        }

        var original = new List<OrderedRow>(rows);
        rows.Clear();

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var clones = new List<OrderedRow>(original.Count);
            foreach (var row in original)
            {
                clones.Add(row.Clone());
            }

            FlattenInto(element, $"{path}[{index}]", depth + 1, clones, multiply: true);
            rows.AddRange(clones);
            index++;

            if (rows.Count > MaxRowsPerRecord)
            {
                throw new SqlFlowException(
                    $"Exploding '{path}' produced more than {MaxRowsPerRecord} rows for a single record. "
                    + "Narrow the explode paths or pre-split the data.");
            }
        }
    }

    private void HandleArray(JsonElement array, string path, int depth, List<OrderedRow> rows, bool multiply, bool aliased)
    {
        switch (_config.ArrayHandling)
        {
            case JsonArrayHandling.FirstElement:
                if (array.GetArrayLength() > 0)
                {
                    FlattenInto(array[0], path, depth + 1, rows, multiply);
                }
                else
                {
                    SetAll(rows, _config.ColumnName(path), null, aliased);
                }

                break;

            case JsonArrayHandling.Join:
                SetAll(rows, _config.ColumnName(path), JoinScalars(array, _config.JoinSeparator), aliased);
                break;

            case JsonArrayHandling.Count:
                SetAll(rows, _config.ColumnName(path), array.GetArrayLength().ToString(CultureInfo.InvariantCulture), aliased);
                break;

            case JsonArrayHandling.Skip:
                break;

            // ToJson, and the unreachable Explode-default (an explode array never reaches here), keep the
            // array whole as one JSON-string column.
            default:
                SetAll(rows, _config.ColumnName(path), AsJsonString(array), aliased);
                break;
        }
    }

    private static void SetAll(List<OrderedRow> rows, string name, string? value, bool aliased = false)
    {
        foreach (var row in rows)
        {
            if (aliased)
            {
                row.SetCoalesce(name, value);
            }
            else
            {
                row.Set(name, value);
            }
        }
    }

    private static string JoinScalars(JsonElement array, string separator)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var element in array.EnumerateArray())
        {
            if (!first)
            {
                sb.Append(separator);
            }

            first = false;
            sb.Append(ScalarText(element));
        }

        return sb.ToString();
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => AsJsonString(element),
    };

    /// <summary>Compact JSON text for a value kept whole (excluded, json-string, or past max depth).</summary>
    private static string AsJsonString(JsonElement value) => JsonSerializer.Serialize(value, CompactJson);

    /// <summary>
    /// An insertion-ordered, name-keyed accumulator with last-write-wins on duplicate names. Column names
    /// are compared case-insensitively, matching SQL Server's default collation and the shared pipeline's
    /// column union, so two source keys that differ only in case (e.g. <c>Id</c> and <c>id</c>) deterministically
    /// fold onto one column instead of being silently misassigned downstream.
    /// </summary>
    private sealed class OrderedRow
    {
        private readonly List<string> _order;
        private readonly Dictionary<string, string?> _values;

        public OrderedRow()
        {
            _order = [];
            _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        private OrderedRow(List<string> order, Dictionary<string, string?> values)
        {
            _order = order;
            _values = values;
        }

        public void Set(string name, string? value)
        {
            if (_values.TryAdd(name, value))
            {
                _order.Add(name);
            }
            else
            {
                _values[name] = value;
            }
        }

        /// <summary>
        /// Sets a value only if it improves the column: a new column is added, and an existing null is
        /// upgraded to a non-null, but an existing non-null is never overwritten (so a later missing/null
        /// alias source path cannot blank out a value another path already supplied).
        /// </summary>
        public void SetCoalesce(string name, string? value)
        {
            if (_values.TryGetValue(name, out var existing))
            {
                if (existing is null && value is not null)
                {
                    _values[name] = value;
                }
            }
            else
            {
                _values[name] = value;
                _order.Add(name);
            }
        }

        public OrderedRow Clone() => new([.. _order], new Dictionary<string, string?>(_values, StringComparer.OrdinalIgnoreCase));

        public IReadOnlyList<KeyValuePair<string, string?>> ToPairs()
        {
            var pairs = new List<KeyValuePair<string, string?>>(_order.Count);
            foreach (var name in _order)
            {
                pairs.Add(new KeyValuePair<string, string?>(name, _values[name]));
            }

            return pairs;
        }
    }
}
