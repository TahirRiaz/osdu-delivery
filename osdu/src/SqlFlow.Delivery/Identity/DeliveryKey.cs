using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Identity;

/// <summary>
/// The deterministic surrogate for one source row (design.md section 5.2). Computed from the source key, never
/// assigned by a run. The idempotency token and the OSDU id derive from this one value, and the ledger keys a record by
/// it together with the flow that delivers it, so the same row read by several flows is one record per flow
/// (design.md section 5.4).
/// </summary>
public readonly record struct DeliveryKey(Guid Value)
{
    private static readonly Guid KeyNamespace = DeterministicGuid.Namespace("delivery-key");

    /// <summary>
    /// Derives the key from the source system name and the ordered natural-key values. Values are joined with a
    /// separator that cannot appear in a well-formed key, so ("a|b", "c") and ("a", "b|c") differ.
    /// </summary>
    public static DeliveryKey Derive(string sourceSystem, IReadOnlyList<string?> naturalKeyValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentNullException.ThrowIfNull(naturalKeyValues);
        if (naturalKeyValues.Count == 0)
        {
            throw new ArgumentException("A delivery key needs at least one natural key value.", nameof(naturalKeyValues));
        }

        return new DeliveryKey(DeterministicGuid.V5(KeyNamespace, SourceKey.Compose(sourceSystem, naturalKeyValues)));
    }

    public static DeliveryKey Parse(string text) => new(Guid.Parse(text, CultureInfo.InvariantCulture));

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>
/// The source system's own identity for the record, carried verbatim as provenance (design.md section 5.1) and
/// used as the input to <see cref="DeliveryKey"/>.
/// </summary>
public static class SourceKey
{
    /// <summary>ASCII unit separator (U+001F): cannot appear in a well-formed natural key value.</summary>
    public const char Separator = (char)0x1F;

    public static string Compose(string sourceSystem, IReadOnlyList<string?> values)
    {
        var sb = new StringBuilder(sourceSystem.Trim().ToLowerInvariant());
        foreach (var v in values)
        {
            sb.Append(Separator);
            sb.Append(v is null ? string.Empty : v.Trim());
        }

        return sb.ToString();
    }

    /// <summary>Human-readable form for logs and the ledger's <c>SourceKey</c> column.</summary>
    public static string Display(string sourceSystem, IReadOnlyList<string?> values)
        => sourceSystem + ":" + string.Join("/", values.Select(v => v is null ? "<null>" : v.Trim()));
}
