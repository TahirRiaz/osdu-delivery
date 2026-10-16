using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Which OSDU records the storage service holds, asked in the batched read every route verifies through (openapi storage
/// v2, <c>POST /query/records</c>, <see cref="OsduRecordProtocol.MaxVerifyBatch"/> ids a request, headers only).
/// </summary>
public static class StoragePresence
{
    /// <summary>The ids among <paramref name="ids"/> storage holds, compared exactly.</summary>
    public static async Task<IReadOnlySet<string>> PresentAsync(OsduHttpClient client, string batchPath, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPath);
        ArgumentNullException.ThrowIfNull(ids);
        var requests = ids.Distinct(StringComparer.Ordinal).Select(id => new VerifyRequest(id, null)).ToList();
        if (requests.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var verified = await RecordWriter.VerifyBatchAsync(client, batchPath, requests, ct).ConfigureAwait(false);
        return requests
            .Where((request, index) => verified[index].Outcome == VerifyOutcome.Match)
            .Select(request => request.TargetId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
