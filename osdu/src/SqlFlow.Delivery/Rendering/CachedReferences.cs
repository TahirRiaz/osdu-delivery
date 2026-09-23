using System.Text.RegularExpressions;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>An OSDU record id a cached field holds: the id without its version, and the entity type it names a record of.</summary>
internal readonly record struct CachedReference(string Id, string EntityType);

/// <summary>
/// OSDU record ids held in cached fields, for a relationship to be written from: OSDU's own translations
/// (<c>ExternalUnitOfMeasure.UnitOfMeasureID</c>, <c>ExternalReferenceValueMapping.SimpleMap.ReferenceValueID</c>) cache
/// the id of the platform record a value stands for, and a mapping writes it as the reference. The render and the gate
/// read an id the same way, and look for the record it names in the same types.
/// </summary>
internal static partial class CachedReferences
{
    /// <summary>
    /// The record <paramref name="text"/> names, or null when it is not an OSDU record id: <c>partition:group--Entity:code</c>,
    /// with or without a version after a last colon.
    /// </summary>
    public static CachedReference? Parse(string? text)
    {
        var match = text is null ? null : RecordId().Match(text.Trim());
        return match is { Success: true } ? new CachedReference(match.Groups["id"].Value, match.Groups["entity"].Value) : null;
    }

    /// <summary>
    /// An id as a relationship is written: with the colon that separates a version, which a reference to the latest version
    /// leaves empty, added when the cached value leaves it out; an id naming a version is written as it is.
    /// </summary>
    public static string Written(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return Parse(trimmed) is { } reference && string.Equals(reference.Id, trimmed, StringComparison.Ordinal) ? trimmed + ":" : trimmed;
    }

    /// <summary>The types of the cache version that hold records of <paramref name="entityType"/>: where a record it names is looked for.</summary>
    public static IReadOnlyList<ReferenceType> Holding(ReferenceSnapshot references, string entityType)
    {
        ArgumentNullException.ThrowIfNull(references);
        return references.Types.Where(t => string.Equals(t.EntityType, entityType, StringComparison.Ordinal)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The type and record of <paramref name="holding"/> that <paramref name="reference"/> names, or null when none of them
    /// holds it.
    /// </summary>
    public static (ReferenceType Type, ReferenceItem Item)? Find(IReadOnlyList<ReferenceType> holding, CachedReference reference)
    {
        ArgumentNullException.ThrowIfNull(holding);
        foreach (var type in holding)
        {
            if (type.ById(reference.Id) is { } item)
            {
                return (type, item);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(?<id>[\w\-\.]+:(?<entity>[\w\-\.]+--[\w\-\.]+):[\w\-\.\%]+):?[0-9]*$")]
    private static partial Regex RecordId();
}
