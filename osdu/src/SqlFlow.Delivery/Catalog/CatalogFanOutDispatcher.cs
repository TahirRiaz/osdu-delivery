using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine.FanOut;

namespace SqlFlow.Delivery.Catalog;

/// <summary>The fan-out dispatcher over the catalog's run queue: members are ordinary queued runs of the root's pipeline.</summary>
public sealed class CatalogFanOutDispatcher : IFanOutDispatcher
{
    private readonly Func<CatalogDbContext>? _contexts;
    private readonly TimeProvider _time;

    public CatalogFanOutDispatcher(Func<CatalogDbContext>? contexts, TimeProvider? time = null)
    {
        _contexts = contexts;
        _time = time ?? TimeProvider.System;
    }

    public bool Available => _contexts is not null;

    public async Task<FanOutHandle> EnqueueAsync(Guid rootRunId, string operation, IReadOnlyList<RunParameters> members, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        await using var db = Open();
        var result = await RunQueueStore.EnqueueFanOutAsync(db, rootRunId, operation, members, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return new FanOutHandle(result.GroupId, result.RunIds);
    }

    public async Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await using var db = Open();
        var rows = await db.Runs.AsNoTracking()
            .Where(r => r.GroupId == handle.GroupId)
            .OrderBy(r => r.FanOutSlot)
            .Select(r => new { r.RunId, r.FanOutSlot, r.Status, r.Error, r.ResultJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return new FanOutState(rows.Select(r => new FanOutMemberState(r.RunId, r.FanOutSlot ?? 0, r.Status, r.Error, r.ResultJson)).ToList());
    }

    public async Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await using var db = Open();
        await RunQueueStore.CancelGroupAsync(db, handle.GroupId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
    }

    private CatalogDbContext Open()
        => (_contexts ?? throw new DeliveryException("This host has no catalog connection, so a run cannot fan out."))();
}
