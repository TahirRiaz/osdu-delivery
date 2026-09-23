using System.Runtime.CompilerServices;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>What looking a value up in a replace table found.</summary>
internal enum ReplaceLookupKind
{
    /// <summary>The table lists the value: <see cref="ReplaceLookup.Value"/> is what it becomes, null for no value.</summary>
    Found,

    /// <summary>The table does not list the value; the replace's <c>otherwise</c> decides.</summary>
    NotListed,

    /// <summary>Several rows answer to the value and they give different values, so none of them is taken.</summary>
    Ambiguous,
}

/// <summary>
/// The outcome of one replace lookup: what the value becomes, and the rows that decided it, so a replace reading a cached
/// table can record every row a render depended on.
/// </summary>
internal readonly record struct ReplaceLookup(ReplaceLookupKind Kind, ReferenceValue? Value, IReadOnlyList<ReferenceItem> Rows)
{
    public static ReplaceLookup NotListed { get; } = new(ReplaceLookupKind.NotListed, null, []);
}

/// <summary>
/// The one way a replace looks a value up, whether its table is written in the mapping or read from the cache. A table is a
/// <see cref="ReferenceType"/> and a value is matched by the cache's own rules (<see cref="ReferenceType.Find"/>): an exact
/// match wins, case is ignored only when that finds one row, and a value several rows answer to selects none of them,
/// unless every one of those rows gives the same value, in which case which of them was meant makes no difference.
/// </summary>
internal static class ReplaceTables
{
    /// <summary>The field an inline table's incoming values are held under.</summary>
    public const string KeyField = "key";

    /// <summary>The field an inline table's replacements are held under; a pair replacing with no value holds none.</summary>
    public const string ValueField = "value";

    // An inline table is built once per parsed modifier, not per row: one table serves every render of a mapping, and a
    // table no longer referenced by any mapping goes with it.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string?>, ReferenceType> Inline = [];

    /// <summary>The lookup table of a replace written in the mapping: one row per pair, keyed by the incoming value.</summary>
    public static ReferenceType Of(IReadOnlyDictionary<string, string?> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return Inline.GetValue(replacements, Build);
    }

    /// <summary>
    /// Looks <paramref name="value"/> up in <paramref name="table"/>: matched on <paramref name="matchField"/>, and replaced
    /// by what the matched row holds at <paramref name="valueField"/>.
    /// </summary>
    public static ReplaceLookup Find(ReferenceType table, string matchField, string valueField, string value)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(matchField);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueField);
        ArgumentNullException.ThrowIfNull(value);
        var found = table.Find(matchField, value);
        if (found.Item is { } row)
        {
            return new ReplaceLookup(ReplaceLookupKind.Found, table.Value(row, valueField), [row]);
        }

        if (!found.IsCaseAmbiguous)
        {
            return ReplaceLookup.NotListed;
        }

        var values = found.CaseVariants.Select(candidate => table.Value(candidate, valueField)).ToList();
        var distinct = values.Select(Canonical).Distinct(StringComparer.Ordinal).Count();
        return distinct == 1
            ? new ReplaceLookup(ReplaceLookupKind.Found, values[0], found.CaseVariants)
            : new ReplaceLookup(ReplaceLookupKind.Ambiguous, null, found.CaseVariants);
    }

    /// <summary>A value as text two rows can be compared by; no value compares as itself and never equals a text.</summary>
    private static string Canonical(ReferenceValue? value) => value is null ? "\u0000none" : CanonicalJson.ToString(value.Node);

    private static ReferenceType Build(IReadOnlyDictionary<string, string?> replacements)
    {
        var rows = new List<ReferenceItem>(replacements.Count);
        foreach (var (from, to) in replacements)
        {
            var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [KeyField] = ReferenceValue.Of(from) };
            if (to is not null)
            {
                fields[ValueField] = ReferenceValue.Of(to);
            }

            rows.Add(new ReferenceItem(from, fields));
        }

        return new ReferenceType("replace", LookupEntityType("replace"), rows);
    }

    /// <summary>The entity type a table that holds no OSDU records is kept under: no OSDU group is called lookup.</summary>
    public static string LookupEntityType(string name) => ReferenceType.LookupEntityTypePrefix + name;
}
