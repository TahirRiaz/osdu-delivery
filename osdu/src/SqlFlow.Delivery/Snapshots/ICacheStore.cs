namespace SqlFlow.Delivery.Snapshots;

/// <summary>Who and what wrote a cache version, kept on the version so a cached value can be traced to the capture that produced it.</summary>
/// <param name="RunId">The platform run that captured the version; null for an import from files.</param>
/// <param name="CapturedBy">Who asked: the run's trigger (manual:&lt;user&gt;, schedule:&lt;name&gt;), or cli:&lt;user&gt;.</param>
/// <param name="Origin">Where the content came from: the OSDU endpoint reference searched, or the directory imported.</param>
public sealed record CacheCapture(Guid? RunId, string CapturedBy, string Origin);

/// <summary>One type a cache version holds, and how many records of it.</summary>
public sealed record CacheVersionType(string Name, string EntityType, long Items);

/// <summary>
/// A version of a partition's cache as its row describes it, without its records: <c>Scope</c> is the partition whose cache
/// the version belongs to, and <c>FlowName</c> the cache flow whose capture or import wrote it. <c>SystemProperties</c> are
/// the partition's own settings the capture found, kept apart from the types.
/// </summary>
public sealed record CacheVersionInfo(
    string Scope,
    string Version,
    int Sequence,
    DateTime CapturedUtc,
    bool Current,
    string? PreviousVersion,
    Guid? RunId,
    string CapturedBy,
    string Origin,
    string FlowName,
    long Items,
    IReadOnlyList<CacheVersionType> Types,
    IReadOnlyList<SystemProperty> SystemProperties);

/// <summary>What merging a capture into a partition's cache did.</summary>
/// <param name="Snapshot">The version written, or the current version when the merge changed no cached content.</param>
/// <param name="Previous">The version that was current before the merge; null for the partition's first version.</param>
/// <param name="Written">False when the merge changed no cached content, so no version was written.</param>
public sealed record CacheWrite(ReferenceSnapshot Snapshot, ReferenceSnapshot? Previous, bool Written);

/// <summary>
/// Where every cache lives (design.md section 6.2): the catalog, one cache per OSDU data partition. Every cache flow that
/// searches a partition merges its captures into that partition's cache, and every delivery flow that delivers to the
/// partition renders against it. Versions form one line per partition, the newest always current, and a version is never
/// rewritten.
/// </summary>
public interface ICacheStore
{
    /// <summary>The newest version of the partition's cache, or null when it holds none.</summary>
    Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default);

    /// <summary>One version of the partition's cache with every record it holds, or null when there is no such version.</summary>
    Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default);

    /// <summary>Every version of the partition's cache, newest first.</summary>
    Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// One version of the partition's cache as its row describes it, without reading a record: <paramref name="version"/>,
    /// or the current one when it is null. Null when there is no such version.
    /// </summary>
    Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default);

    /// <summary>What the synced cache flows declare the partition's cache holds; empty when no flow for it is synced.</summary>
    Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// Merges one cache flow's capture into the partition's cache (<see cref="CacheMerge"/>) and writes the result as the
    /// next version, which becomes current, unless the cached content did not change. Either way the flow's membership of
    /// the captured types becomes what it captured. <paramref name="readings"/> are what the platform's services said about
    /// the partition's system properties during the capture; none keeps the current ones, as an import does. Fails without
    /// writing anything when another write of the partition wins.
    /// </summary>
    Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default);
}
