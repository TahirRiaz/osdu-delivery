using System.Globalization;

namespace SqlFlow.Delivery.Rendering;

/// <summary>One row of one scope in the drop: column name to a scalar (string, long, double, decimal, bool, DateTimeOffset, Guid) or null.</summary>
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
/// One deliverable as read from the drop: the root row plus the rows of each child scope. The renderer's contract
/// is "produce a document from a row scope" (design.md section 15).
/// </summary>
public sealed record SourceRecord
{
    public required SourceRow Row { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<SourceRow>> Scopes { get; init; }
        = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The delivery key the drop claims for this record, when it carries one; cross-checked by the renderer.</summary>
    public Guid? DeclaredDeliveryKey { get; init; }

    public IReadOnlyList<SourceRow> ScopeRows(string scope)
        => Scopes.TryGetValue(scope, out var rows) ? rows : [];
}
