namespace SqlFlow.Delivery.Snapshots;

/// <summary>How a render depended on one cached value.</summary>
public enum CacheUsageKind
{
    /// <summary>The value the source was matched against: if it disappears, the record stops resolving.</summary>
    Match,

    /// <summary>A value read out of the cached record and written into the document: if it changes, the document changes.</summary>
    Value,

    /// <summary>
    /// A path read out of the cached record that held no value, so the document was built without one: a dictionary entry
    /// replacing a spelling with no value, say. Its value is empty. If the path gains a value, the document changes.
    /// </summary>
    Empty,

    /// <summary>
    /// A key looked up in a lookup table that listed no row under it, so the document was built without what a row would
    /// have given: a spelling a dictionary does not list yet. The item is the key as <see cref="CacheUsage.ListingKey"/>
    /// folds it, the path the field a row would have given, and the value the key as it was looked up. If the table
    /// comes to list the key, the document changes.
    /// </summary>
    Unlisted,

    /// <summary>
    /// An id a node built and wrote although the cache held records of its entity type and not this one, because the node
    /// lets an unverified reference go out (<c>$unverified</c>). The item is the record id, the path <c>id</c> and the value
    /// the id as written. If a later version holds the record, the reference is verified, and the record is built again.
    /// </summary>
    Unverified,

    /// <summary>
    /// The rows a <c>$findAll</c> found by one key: every record of the type whose field holds the key, whatever case either
    /// is written in. The item is the key as <see cref="CacheUsage.ListingKey"/> folds it, the path the field compared, and
    /// the value the ids of the rows found, in ordinal order and joined by <see cref="CacheUsage.ListedSeparator"/>, empty
    /// when none was. It is recorded whether or not a row was found, so a later version that lists another row under the
    /// key, drops one, or lists the first, reaches every record built without it.
    /// </summary>
    Listed,
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

    /// <summary>
    /// A key as an <see cref="CacheUsageKind.Unlisted"/> usage holds it: without case, since a table matches a key without
    /// case when only one row answers, so a row added under any spelling of it may be the one a later render finds.
    /// </summary>
    public static string ListingKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Trim().ToUpperInvariant();
    }

    /// <summary>What separates the ids a <see cref="CacheUsageKind.Listed"/> usage holds.</summary>
    public const string ListedSeparator = ", ";

    /// <summary>
    /// The usage a <c>$findAll</c> records for one key: the rows it found under <paramref name="field"/>, by id in ordinal
    /// order, however many (none included).
    /// </summary>
    public static CacheUsage Listing(string typeName, string field, string key, IEnumerable<string> ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ids);
        return new CacheUsage(typeName, ListingKey(key), field, ListedIds(ids), CacheUsageKind.Listed);
    }

    /// <summary>The ids a listing holds, as <see cref="Listing"/> writes them: distinct, in ordinal order, joined.</summary>
    public static string ListedIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return string.Join(ListedSeparator, ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }
}
