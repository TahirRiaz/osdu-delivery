using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The mappings the repository sync read into the module database, as the engine reads them where it has no repository tree:
// the explorer and an assertion flow judge a mapping's assertions on what OSDU holds (osdu/docs/reference/flow/mapping-assertions.md).
public sealed partial class OsduLedger
{
    /// <summary>The most synced mappings one reference is looked for among: one per repository that declares it.</summary>
    internal const int MaxSyncedMappings = 50;

    public Task<SyncedMapping?> SyncedMappingAsync(Guid mappingId, CancellationToken ct = default)
        => ReadAsync(
            async db =>
            {
                var row = await db.DeliveryMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, ct).ConfigureAwait(false);
                return row is null ? null : Synced(row);
            },
            ct);

    public Task<IReadOnlyList<SyncedMapping>> SyncedMappingsAsync(string reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var wanted = reference.Trim();
        return ReadAsync(
            async db => (IReadOnlyList<SyncedMapping>)(await db.DeliveryMappings.AsNoTracking()
                    .Where(m => m.Reference == wanted)
                    .OrderBy(m => m.RepoId).ThenBy(m => m.RelativePath)
                    .Take(MaxSyncedMappings)
                    .ToListAsync(ct).ConfigureAwait(false))
                .Select(Synced)
                .ToList(),
            ct);
    }

    private static SyncedMapping Synced(DeliveryMapping row)
        => new(row.Id, row.RepoId, row.Reference, row.Kind, row.RelativePath, row.ContentHash, row.Yaml, string.Equals(row.Status, "valid", StringComparison.Ordinal), row.Message);
}
