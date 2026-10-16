using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a text read for an attribute or an element's field is kept as, by its <see cref="DimensionValueKeep"/>.</summary>
public static class DimensionKeeping
{
    /// <summary>
    /// <paramref name="text"/> as it is kept, trimmed: as a value shows it, cut at <paramref name="longest"/>; or as a key or a
    /// record's id, left out when longer, since a key cut joins to nothing. Null when nothing is kept.
    /// </summary>
    /// <returns>The value kept, whether it was cut, and whether it was left out for its length.</returns>
    public static (string? Value, bool Cut, bool TooLong) Keep(string text, DimensionValueKeep keep, int longest)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var kept = keep switch
        {
            DimensionValueKeep.Key => trimmed,
            DimensionValueKeep.Id => TargetId.IsRecordReference(trimmed) ? TargetId.WithoutVersion(trimmed) : trimmed,
            _ => DimensionLabeler.DisplayOf(trimmed).Trim(),
        };

        if (kept.Length == 0)
        {
            return (null, false, false);
        }

        if (kept.Length <= longest)
        {
            return (kept, false, false);
        }

        return keep == DimensionValueKeep.Value ? (kept[..longest], true, false) : (null, false, true);
    }

    /// <summary>How a document writes <paramref name="keep"/>.</summary>
    public static string Named(DimensionValueKeep keep) => keep switch
    {
        DimensionValueKeep.Key => "key",
        DimensionValueKeep.Id => "id",
        _ => "value",
    };

    /// <summary>The keep a document names, ignoring case; null when it names none of them.</summary>
    public static DimensionValueKeep? Parse(string? named) => named?.Trim().ToLowerInvariant() switch
    {
        "value" => DimensionValueKeep.Value,
        "key" => DimensionValueKeep.Key,
        "id" => DimensionValueKeep.Id,
        _ => null,
    };
}
