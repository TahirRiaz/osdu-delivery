using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A worker's writes, for the tests that set a record's state by hand: what the worker appends under the lease that holds
/// the record (a lease of the test's own when none does), applied at once, as a checkpoint of that lease applies it. The
/// lease itself stays as it is, so the test still decides how it ends.
/// </summary>
internal static class LedgerDeliveries
{
    /// <summary>Settles one record as <paramref name="completion"/> says.</summary>
    public static Task CompleteAsync(this ILedger ledger, Guid flowId, RecordCompletion completion)
        => ledger.CompleteManyAsync(flowId, [completion]);

    /// <summary>Settles records as <paramref name="completions"/> say, each under the lease that holds it.</summary>
    public static async Task CompleteManyAsync(this ILedger ledger, Guid flowId, IReadOnlyList<RecordCompletion> completions)
    {
        var records = await ledger.GetRecordsAsync(flowId, completions.Select(c => c.DeliveryKey));
        foreach (var lease in completions.GroupBy(c => records.GetValueOrDefault(c.DeliveryKey)?.LeaseOwner ?? UnleasedToken()))
        {
            await ledger.AppendAsync(flowId, lease.Key, new LeaseAppend([], lease.ToList()));
            await ledger.CheckpointLeaseAsync(lease.Key, lease.Max(c => c.Attempt.CompletedUtc));
        }
    }

    /// <summary>Keeps a step for the record's next try, as a worker's step report does.</summary>
    public static async Task SaveStepAsync(this ILedger ledger, Guid flowId, DeliveryKey key, Guid? submissionId, string documentRef, string stepJson, DateTime atUtc)
    {
        var token = (await ledger.GetRecordAsync(flowId, key))?.LeaseOwner ?? UnleasedToken();
        await ledger.AppendAsync(flowId, token, new LeaseAppend([new RecordStep(key, submissionId, documentRef, stepJson, atUtc)], []));
        await ledger.CheckpointLeaseAsync(token, atUtc);
    }

    /// <summary>A token no lease holds: what a test writes under for a record no lease holds.</summary>
    private static string UnleasedToken() => "tests/" + Guid.NewGuid().ToString("N");
}
