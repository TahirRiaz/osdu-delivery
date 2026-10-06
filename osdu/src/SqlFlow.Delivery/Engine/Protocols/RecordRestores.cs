using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// How a storage record is written back as OSDU held it at an earlier version (a reversal, docs/reversal-plan.md): the
/// version read from storage, without the properties the service writes itself, as the document of an ordinary write
/// through the route's own record writer, so the batching, the per-record fallback on a refused batch and the reading of
/// the version written are the same as for a delivery. The version is written as it was, the data keys the flow carries
/// forward on a delivery included (<c>preserveDataKeys</c>): those keys describe that version (a Wellbore DDMS record's
/// <c>ExtensionProperties.wdms.bulkURI</c> names its bulk data), so carrying the latest version's would keep what is being
/// put back. Only the keys another system writes on its own (External Data Services' run state) are carried from the latest
/// version, since a reversal of this flow's delivery is not that system's to undo.
/// </summary>
internal static class RecordRestores
{
    /// <summary>The options a restore's writer takes: the route's own, without the flow's keys a delivery carries forward.</summary>
    public static ProtocolOptions WriterOptions(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { PreserveDataKeys = [], SkipDuplicates = false };
    }

    /// <summary>
    /// The data keys a restore carries from the latest version into <paramref name="document"/>: the keys another system
    /// writes on records of its type, and none of the flow's own.
    /// </summary>
    public static IReadOnlyList<string> CarriedKeys(JsonObject document) => OwnedContent.PreservedKeys(new ProtocolOptions(), document);

    /// <summary>
    /// The properties the storage service writes on every version and refuses to be given (openapi storage v2, Record:
    /// <c>version</c>, <c>createUser</c>, <c>createTime</c>, <c>modifyUser</c> and <c>modifyTime</c> are read-only).
    /// </summary>
    private static readonly string[] SystemProperties = ["version", "createUser", "createTime", "modifyUser", "modifyTime"];

    /// <summary>
    /// The document that writes <paramref name="restore"/>'s stored version back: that version without its system
    /// properties. Its id must be the record's, since a version of another record is never written over this one.
    /// </summary>
    /// <exception cref="DeliveryException">The stored version names another id, or none.</exception>
    public static JsonObject DocumentOf(VersionRestore restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        var document = (JsonObject)restore.Stored.DeepClone();
        var id = document["id"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (!string.Equals(id, restore.TargetId, StringComparison.Ordinal))
        {
            throw new DeliveryException($"The version {restore.Version} read for {restore.TargetId} names the record '{id ?? "(none)"}', so it is not written back over it.");
        }

        foreach (var property in SystemProperties)
        {
            document.Remove(property);
        }

        return document;
    }

    /// <summary>
    /// The writes of <paramref name="restores"/> through <paramref name="writer"/>, the route's storage record writer, a
    /// batch to a request as the writer batches: each record's document is its stored version, and the version it replaces
    /// is the one OSDU holds now.
    /// </summary>
    public static async Task<IReadOnlyList<RestoreResult>> WriteAsync(OsduRecordProtocol writer, IReadOnlyList<VersionRestore> restores, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(restores);
        var results = new RestoreResult?[restores.Count];
        var works = new List<(int Index, DeliveryWork Work)>(restores.Count);
        for (var i = 0; i < restores.Count; i++)
        {
            var restore = restores[i];
            try
            {
                works.Add((i, new DeliveryWork
                {
                    Key = restore.Key,
                    TargetId = restore.TargetId,
                    Document = DocumentOf(restore),
                    DeliverMetadata = true,
                    DeliverPayload = false,
                    ExistingVersion = restore.Latest,
                    TargetState = restore.TargetState ?? new Dictionary<string, string>(StringComparer.Ordinal),
                }));
            }
            catch (DeliveryException ex)
            {
                results[i] = new RestoreResult(restore, null, null, ex);
            }
        }

        if (works.Count > 0)
        {
            var outcomes = await writer.DeliverBatchAsync(works.Select(w => w.Work).ToList(), ct).ConfigureAwait(false);
            for (var j = 0; j < works.Count; j++)
            {
                var (index, _) = works[j];
                var outcome = outcomes[j];
                results[index] = outcome.Failure is { } failure
                    ? new RestoreResult(restores[index], null, null, failure)
                    : outcome.TargetVersion is { } version
                        ? new RestoreResult(restores[index], version, outcome.Returned, null)
                        : new RestoreResult(restores[index], null, outcome.Returned, new DeliveryException($"The write of {restores[index].TargetId} answered without the version it wrote."));
            }
        }

        return results.Select(r => r!).ToList();
    }
}
