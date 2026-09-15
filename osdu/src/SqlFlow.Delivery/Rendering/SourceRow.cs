using System.Globalization;

namespace SqlFlow.Delivery.Rendering;

/// <summary>One row of one scope (the record table or a child dataset): column name to a scalar (string, long, double, decimal, bool, DateTimeOffset, Guid) or null.</summary>
public sealed class SourceRow
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    public SourceRow(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values is Dictionary<string, object?> d && ReferenceEquals(d.Comparer, StringComparer.OrdinalIgnoreCase)
            ? values
            : new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    public static SourceRow Empty { get; } = new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    public IEnumerable<string> Columns => _values.Keys;

    public bool Has(string column) => _values.ContainsKey(column);

    public object? Get(string column) => _values.TryGetValue(column, out var v) ? v : null;

    /// <summary>The value as an invariant string, or null when absent or null. Doubles use round-trip formatting.</summary>
    public string? GetString(string column) => Stringify(Get(column));

    public static string? Stringify(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => NumberValues.Text(f),
        decimal m => NumberValues.Text(m),
        DateTimeOffset dto => Json.CanonicalJson.FormatDateTime(dto),
        DateTime dt => Json.CanonicalJson.FormatDateTime(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static SourceRow FromStrings(IReadOnlyDictionary<string, string?> values)
        => new(values.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Where a record's row came from, as SQLFlow's system columns on the ingestion table say: the file the pre-ingestion flow
/// landed it from (<c>FileName_DW</c>), its row in that file (<c>RowNumber_DW</c>) and when the ingestion flow last updated it
/// (<c>UpdatedDate_DW</c>). Each part is null when the table does not carry it or the flow opts out of it. It is what traces a
/// delivered version, and every attempt of it, back to the exact row of the exact file.
/// </summary>
public readonly record struct SourceOrigin(string? FileName, long? RowNumber, DateTime? UpdatedUtc);

/// <summary>
/// One deliverable as read from the ingestion tables: the record row plus the rows of each child dataset. The renderer's
/// contract is "produce a document from a row scope" (design.md section 15).
/// </summary>
public sealed record SourceRecord
{
    public required SourceRow Row { get; init; }

    /// <summary>The file, row and update time of the record row; empty for a record not read from ingestion tables (a fixture).</summary>
    public SourceOrigin Origin { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<SourceRow>> Scopes { get; init; }
        = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The version the rows carry: the ingestion fingerprint, completed with the business version by the planner.</summary>
    public Planning.SourceVersion Version { get; init; }

    /// <summary>The record's key tuple as the source read it, a JSON array of strings in the flow's key order.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>When the ingestion table marked the record row deleted, or null for a live row.</summary>
    public DateTime? DeletedUtc { get; init; }

    public IReadOnlyList<SourceRow> ScopeRows(string scope)
        => Scopes.TryGetValue(scope, out var rows) ? rows : [];
}
