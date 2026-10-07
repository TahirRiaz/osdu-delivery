using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>
/// One record to write back as OSDU held it at an earlier version: the version to put back, the latest version OSDU holds
/// (whose keys another system owns are carried over), and the target state the ledger keeps for the record.
/// </summary>
public sealed record WriteBack(DeliveryKey Key, string TargetId, long Version, long? Latest, IReadOnlyDictionary<string, string>? TargetState);

/// <summary>How writing one record back ended.</summary>
public enum WriteBackOutcome
{
    /// <summary>The version was read and written back; <see cref="WriteBackResult.NewVersion"/> is the version OSDU gave the write.</summary>
    Restored,

    /// <summary>OSDU no longer holds the version (the record's earlier versions were purged), so nothing was written.</summary>
    VersionMissing,

    /// <summary>The version could not be read, so nothing was written.</summary>
    ReadFailed,

    /// <summary>The version was read, and its write was refused or failed.</summary>
    WriteFailed,
}

/// <summary>
/// What writing one record back came to: the version OSDU gave the write and what the write returned, or why it did not
/// land, as the service or the route said it (callers redact it before it is stored or shown).
/// </summary>
public sealed record WriteBackResult(WriteBack Request, WriteBackOutcome Outcome, long? NewVersion, IReadOnlyDictionary<string, string>? Returned, string? Failure);

/// <summary>
/// Writing records back as OSDU held them at an earlier version, and the reads that decide it: the one path a reversal
/// (<see cref="ReversalRunner"/>) and a step back to the version before the latest (<see cref="PreviousVersionRestore"/>) both
/// take, so the two ask OSDU the same way, write the same content and leave the same target state in the ledger.
/// </summary>
public static class VersionWriteBack
{
    /// <summary>
    /// What OSDU holds of each record, read in the route's batches, as many batches at once as <paramref name="concurrency"/>
    /// allows. Results align with <paramref name="requests"/>; a batch that could not be read answers each of its records
    /// with the redacted error.
    /// </summary>
    public static async Task<VerifyResult[]> VerifyAsync(IDeliveryProtocol protocol, IReadOnlyList<VerifyRequest> requests, int concurrency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(requests);
        var results = new VerifyResult[requests.Count];
        var chunks = requests.Select((r, i) => (Request: r, Index: i)).Chunk(Math.Max(1, protocol.MaxVerifyBatch)).ToList();
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct }, async (chunk, token) =>
        {
            try
            {
                var answered = await protocol.VerifyBatchAsync(chunk.Select(c => c.Request).ToList(), token).ConfigureAwait(false);
                for (var j = 0; j < chunk.Length; j++)
                {
                    results[chunk[j].Index] = answered[j];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var detail = HeaderRedaction.RedactMessage(ex.Message);
                foreach (var (_, index) in chunk)
                {
                    results[index] = new VerifyResult(VerifyOutcome.Error, null, detail);
                }
            }
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Reads the version each record is to get back, as many at once as <paramref name="concurrency"/> allows, and writes
    /// the versions read back in the route's batches. Results align with <paramref name="work"/>: a record is written only
    /// once its version was read, and a write the route refused for the whole batch is reported against each record.
    /// </summary>
    public static async Task<IReadOnlyList<WriteBackResult>> RunAsync(IDeliveryProtocol protocol, IReadOnlyList<WriteBack> work, int concurrency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(work);
        if (work.Count == 0)
        {
            return [];
        }

        var results = new WriteBackResult?[work.Count];
        var stored = new JsonObject?[work.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct }, async (index, token) =>
        {
            var w = work[index];
            try
            {
                var version = await protocol.ReadVersionAsync(w.TargetId, w.Version, token).ConfigureAwait(false);
                if (version is null)
                {
                    results[index] = new WriteBackResult(w, WriteBackOutcome.VersionMissing, null, null, null);
                }
                else
                {
                    stored[index] = version;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                results[index] = new WriteBackResult(w, WriteBackOutcome.ReadFailed, null, null, ex.Message);
            }
        }).ConfigureAwait(false);

        var ready = Enumerable.Range(0, work.Count).Where(i => stored[i] is not null).ToList();
        if (ready.Count > 0)
        {
            var restores = ready
                .Select(i => new VersionRestore(work[i].Key, work[i].TargetId, work[i].Version, stored[i]!, work[i].Latest, work[i].TargetState))
                .ToList();
            IReadOnlyList<RestoreResult> written;
            try
            {
                written = await protocol.RestoreBatchAsync(restores, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                written = restores.Select(r => new RestoreResult(r, null, null, ex)).ToList();
            }

            if (written.Count != restores.Count)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The route answered {written.Count} result(s) for {restores.Count} record(s) written back, so which records it wrote cannot be told."));
            }

            for (var j = 0; j < ready.Count; j++)
            {
                var result = written[j];
                results[ready[j]] = result.Succeeded
                    ? new WriteBackResult(work[ready[j]], WriteBackOutcome.Restored, result.NewVersion, result.Returned, null)
                    : new WriteBackResult(work[ready[j]], WriteBackOutcome.WriteFailed, null, null, result.Failure?.Message ?? "the route answered no version for the write");
            }
        }

        return results.Select(r => r!).ToList();
    }

    /// <summary>The target state a record written back holds: what it held, with what the write returned, at the new version.</summary>
    public static string? TargetStateAfter(string? targetStateJson, string targetId, long version, IReadOnlyDictionary<string, string>? returned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recordId"] = targetId,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (name, value) in returned ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            values[name] = value;
        }

        return JsonMerge.Merge(targetStateJson, JsonMerge.FromValues(values));
    }
}
