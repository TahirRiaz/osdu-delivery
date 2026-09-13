namespace SqlFlow.Delivery.Snapshots;

/// <summary>Who and what wrote a cache version, kept on the version so a cached value can be traced to the capture that produced it.</summary>
/// <param name="RunId">The platform run that captured the version; null for an import from files.</param>
/// <param name="CapturedBy">Who asked: the run's trigger (manual:&lt;user&gt;, schedule:&lt;name&gt;), or cli:&lt;user&gt;.</param>
/// <param name="Origin">Where the content came from: the OSDU endpoint reference searched, or the directory imported.</param>
public sealed record CacheCapture(Guid? RunId, string CapturedBy, string Origin);

/// <summary>One type a cache version holds, and how many records of it.</summary>
public sealed record CacheVersionType(string Name, string EntityType, long Items);

/// <summary>A version of a cache as its row describes it, without its records.</summary>
public sealed record CacheVersionInfo(
    string CacheName,
    string Version,
    int Sequence,
    DateTime CapturedUtc,
    bool Current,
    string? PreviousVersion,
    Guid? RunId,
    string CapturedBy,
    string Origin,
    long Items,
    IReadOnlyList<CacheVersionType> Types);

/// <summary>
/// Where the versions of every cache live (design.md section 6.2): the catalog. A cache is named by the cache flow that
/// captures it. Reads are what a render needs; writes are a refresh's. A version is never rewritten.
/// </summary>
public interface ICacheStore
{
    /// <summary>The version delivery flows render against unless they pin another, or null when the cache holds none.</summary>
    Task<string?> CurrentVersionAsync(string cache, CancellationToken ct = default);

    /// <summary>One version of a cache with every record it holds, or null when the cache holds no such version.</summary>
    Task<ReferenceSnapshot?> LoadAsync(string cache, string version, CancellationToken ct = default);

    /// <summary>Every version of a cache, newest first.</summary>
    Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string cache, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="snapshot"/> as the next version of the cache and, with <paramref name="makeCurrent"/>, makes it
    /// the current one. Fails without writing anything when the version exists or another write of the same cache wins.
    /// </summary>
    Task<CacheVersionInfo> SaveAsync(string cache, ReferenceSnapshot snapshot, CacheCapture capture, bool makeCurrent, CancellationToken ct = default);
}
