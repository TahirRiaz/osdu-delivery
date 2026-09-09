namespace SqlFlow.Delivery.Snapshots;

/// <summary>How a render depended on one cached value.</summary>
public enum CacheUsageKind
{
    /// <summary>The value the source was matched against: if it disappears, the record stops resolving.</summary>
    Match,

    /// <summary>A value read out of the cached record and written into the document: if it changes, the document changes.</summary>
    Value,
}

/// <summary>
/// One dependency of a rendered record on the cache: which cached record it used, which path it read, and what
/// that path held at the time. Recorded per delivered record so a later cache version can say exactly which
/// records its changes touch, without re-rendering the estate and without guessing (design.md section 6.2).
/// </summary>
public sealed record CacheUsage(string TypeName, string ItemId, string Path, string Value, CacheUsageKind Kind)
{
    /// <summary>The value truncated for a ledger column and for display; the hash covers the whole of it.</summary>
    public const int MaxValueLength = 400;

    public string Display => Value.Length <= MaxValueLength ? Value : Value[..MaxValueLength];

    public string ValueHash => Hashing.ContentHash.Of(Value);
}
