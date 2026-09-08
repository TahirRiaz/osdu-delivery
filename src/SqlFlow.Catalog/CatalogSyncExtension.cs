namespace SqlFlow.Catalog;

/// <summary>What one sync extension did to its document family in one repository sync.</summary>
public sealed record CatalogSyncExtensionResult(int Added, int Updated, int Unchanged, int Removed, int Invalid)
{
    public static CatalogSyncExtensionResult Empty { get; } = new(0, 0, 0, 0, 0);

    public CatalogSyncExtensionResult Add(CatalogSyncExtensionResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CatalogSyncExtensionResult(
            Added + other.Added, Updated + other.Updated, Unchanged + other.Unchanged, Removed + other.Removed, Invalid + other.Invalid);
    }
}

/// <summary>
/// A document family a flow kind adds to the repository sync beyond flows and schedules: the delivery kind's
/// mapping documents and snapshot manifests, say. The sync calls every registered extension inside its
/// reconciliation transaction, after the pipelines and runs, with the repository's materialized root; the
/// extension reconciles its own catalog rows for that repository and reports the tally. Extensions are
/// registered in the host's composition root, so the catalog never depends on the kinds that exist.
/// </summary>
public interface ICatalogSyncExtension
{
    /// <param name="context">The sync's catalog context, inside its transaction.</param>
    /// <param name="repoId">The repository being synced.</param>
    /// <param name="root">The repository's materialized root directory.</param>
    /// <param name="nowUtc">The sync's timestamp.</param>
    /// <param name="warnings">Where a document-level problem is reported without failing the sync.</param>
    /// <param name="ct">Cancellation.</param>
    Task<CatalogSyncExtensionResult> SyncAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct);
}
