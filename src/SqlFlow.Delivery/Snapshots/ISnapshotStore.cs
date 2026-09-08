namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// Where versioned schema and reference snapshots live. Reads are what <c>validate</c>, <c>plan</c> and <c>run</c>
/// need; writes are the <c>snapshot</c> verb's job. Implementations are file-shaped (a local directory or a blob
/// prefix) so plan works offline (design.md section 11).
/// </summary>
public interface ISnapshotStore
{
    Task<SchemaSnapshot?> LoadSchemaAsync(string kind, CancellationToken ct = default);

    Task SaveSchemaAsync(SchemaSnapshot schema, CancellationToken ct = default);

    /// <summary>The version the <c>pinned</c> reference setting resolves to, or null when no snapshot exists.</summary>
    Task<string?> CurrentReferenceVersionAsync(CancellationToken ct = default);

    Task<ReferenceSnapshot?> LoadReferencesAsync(string version, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListReferenceVersionsAsync(CancellationToken ct = default);

    Task SaveReferencesAsync(ReferenceSnapshot snapshot, bool makeCurrent, CancellationToken ct = default);
}

/// <summary>Resolves a flow's render inputs into a pinned <see cref="RenderContext"/> and the snapshots behind it.</summary>
public sealed record ResolvedRender(RenderContext Context, SchemaSnapshot Schema, ReferenceSnapshot References);
