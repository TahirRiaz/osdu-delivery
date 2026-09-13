namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// Where versioned reference snapshots (the metadata cache) live. Reads are what <c>plan</c> and <c>run</c> need;
/// writes are the cache capture's job. Implementations are file-shaped (a local directory or a blob prefix). Templates
/// live in the catalog instead (docs/delivery/mapping-templates.md).
/// </summary>
public interface ISnapshotStore
{
    /// <summary>The version the <c>pinned</c> reference setting resolves to, or null when no snapshot exists.</summary>
    Task<string?> CurrentReferenceVersionAsync(CancellationToken ct = default);

    Task<ReferenceSnapshot?> LoadReferencesAsync(string version, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListReferenceVersionsAsync(CancellationToken ct = default);

    Task SaveReferencesAsync(ReferenceSnapshot snapshot, bool makeCurrent, CancellationToken ct = default);
}
