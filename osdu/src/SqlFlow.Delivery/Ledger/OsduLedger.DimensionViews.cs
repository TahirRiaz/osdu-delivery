using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The views dimension flows declare over their tables (docs/dimension-plan.md, Views): written, checked, dropped and read
/// through <see cref="SqlServerDimensionViewStore"/>. A view spans the partitions its tables hold, so its record belongs to
/// no ledger; each check is kept in the partition of the build that made it.
/// </summary>
public sealed partial class OsduLedger
{
    public async Task<DimensionViewsWritten> WriteDimensionViewsAsync(DimensionViewWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var partition = await WritePartitionAsync(write.FlowLedgerId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerDimensionViewStore.WriteAsync(db, write, partition, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionViewState>> ListDimensionViewsAsync(string? flowName, CancellationToken ct = default)
    {
        await using var db = Open();
        return await SqlServerDimensionViewStore.ListAsync(db, string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<DimensionViewDetail?> GetDimensionViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.GetAsync(db, name.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DimensionViewsReadingAsync(string table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using var db = Open();
        return await SqlServerDimensionViewStore.ReadingAsync(db, table.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<DimensionViewRemoved?> RemoveDimensionViewAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        return await SqlServerDimensionViewStore.RemoveAsync(db, name.Trim(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DimensionViewProbe>> ProbeDimensionViewsAsync(string flowName, IReadOnlyList<DimensionViewSpec> views, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(views);
        if (views.Count == 0)
        {
            return [];
        }

        await using var db = Open();
        return await SqlServerDimensionViewStore.ProbeAsync(db, flowName, views, ct).ConfigureAwait(false);
    }

    public async Task<DatabaseIdentity> DatabaseIdentityAsync(CancellationToken ct = default)
    {
        await using var db = Open();
        return await SqlServerDimensionViewStore.IdentityAsync(db, ct).ConfigureAwait(false);
    }
}
