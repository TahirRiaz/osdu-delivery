namespace SqlFlow.Delivery.Model;

/// <summary>
/// The entries of a pipeline's cache step (osdu/docs/reference/flow/delivery.md, cache): <c>auto</c>, which the step works
/// out from the pipeline itself, or the entries it names. An entry is a type of the partition's cache by its name
/// (<c>UnitOfMeasure</c>), or an OSDU entity type (<c>reference-data--UnitOfMeasure</c>), which stands for every type of the
/// partition's cache holding records of it, whichever cache flow declares them.
/// </summary>
public sealed record CacheStepEntries
{
    /// <summary>The most entries one step names.</summary>
    public const int MaxNamed = 64;

    /// <summary>The longest entry.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The word that has the step work its entries out.</summary>
    public const string AutoWord = "auto";

    /// <summary>A step that brings nothing up to date.</summary>
    public static CacheStepEntries None { get; } = new();

    /// <summary>True for <c>auto</c>.</summary>
    public bool Auto { get; init; }

    /// <summary>The entries named, in document order, each once; empty for <c>auto</c> and for no step.</summary>
    public IReadOnlyList<string> Named { get; init; } = [];

    /// <summary>True when the step brings nothing up to date.</summary>
    public bool IsEmpty => !Auto && Named.Count == 0;

    /// <summary>True when <paramref name="entry"/> names an OSDU entity type (<c>group--Entity</c>) rather than a cache type.</summary>
    public static bool IsEntityType(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var separator = entry.IndexOf("--", StringComparison.Ordinal);
        return separator > 0 && separator < entry.Length - 2;
    }

    /// <summary>
    /// True when <paramref name="entry"/> can name a cache type or an entity type: not empty, at most
    /// <see cref="MaxNameLength"/> characters, and without white space, wildcards, <c>|</c>, <c>:</c> or control characters.
    /// </summary>
    public static bool IsEntry(string? entry)
        => !string.IsNullOrEmpty(entry)
            && entry.Length <= MaxNameLength
            && !entry.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '*' or '|' or ':');

    /// <summary>The step as a document writes it.</summary>
    public override string ToString() => Auto ? AutoWord : string.Join(", ", Named);
}

/// <summary>
/// What a pipeline brings up to date in its partition's cache around its run (osdu/docs/reference/flow/delivery.md, cache):
/// the types it needs current before it renders, and the types the records it delivers go into as it ends.
/// </summary>
public sealed record FlowCacheSteps
{
    /// <summary>The types brought up to date from OSDU before the run renders.</summary>
    public CacheStepEntries Before { get; init; } = CacheStepEntries.None;

    /// <summary>The types the records the run lands go into as it ends.</summary>
    public CacheStepEntries After { get; init; } = CacheStepEntries.None;

    /// <summary>True when the pipeline names no cache step.</summary>
    public bool IsEmpty => Before.IsEmpty && After.IsEmpty;
}
