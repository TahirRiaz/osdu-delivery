using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>What every shape of the ddms route works with: the target, the flow's options and routing, the log and the clock.</summary>
/// <param name="Client">The target, under the flow's auth and headers.</param>
/// <param name="Options">The flow's protocol options.</param>
/// <param name="Routing">Where each record goes, every registration the flow names read.</param>
/// <param name="Logger">The protocol's log.</param>
/// <param name="RequestBodyCeiling">The target's declared request body ceiling (<c>reliability.maxRequestBodyBytes</c>); 0 when none is declared.</param>
/// <param name="Time">The clock.</param>
internal sealed record DdmsShapeContext(OsduHttpClient Client, ProtocolOptions Options, DdmsRouting Routing, ILogger Logger, long RequestBodyCeiling, TimeProvider Time);

/// <summary>
/// One call pattern of the ddms route (docs/interfaces-design.md section 5.4): how a record and the data its DDMS keeps
/// for it are checked, written, read back, verified and removed, as the DDMS's integration brief describes the service.
/// The route picks the shape of the DDMS serving each record's entity type; a flow that names its own paths reaches a
/// facade of the Wellbore DDMS v3.
/// </summary>
internal interface IDdmsShape
{
    /// <summary>
    /// Everything the shape checks before its first request for <paramref name="work"/>: a record the DDMS would refuse,
    /// and bulk data it could not take, are held here (<see cref="RecordHeldException"/>), so a composed route that sends
    /// something else first holds them before that too. Returns what <see cref="SendAsync"/> uses.
    /// </summary>
    Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct);

    /// <summary>
    /// Writes what <see cref="PrepareAsync"/> checked: the record when the work delivers it, then the data the DDMS keeps
    /// for it. <paramref name="work"/> may differ from the prepared work only in what a composed route learned since (its
    /// dataset list, the version its manifest wrote).
    /// </summary>
    Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct);

    /// <summary>Reads the record back and compares its version with the one the ledger holds.</summary>
    Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct);

    /// <summary>The record as the DDMS holds it, or null when it holds none.</summary>
    Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct);

    /// <summary>Removes the record, and what the DDMS created for it that the ledger names, to the extent the scope asks.</summary>
    Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct);

    /// <summary>
    /// Carries the link a record keeps to the data its DDMS holds (the Wellbore DDMS's bulk link, RAFS's content
    /// datasets) from <paramref name="stored"/> into <paramref name="document"/>, which rewrites the record past the DDMS
    /// (a manifest). Returns false when the document rendered a link of its own that differs from the stored one: the
    /// DDMS manages it, so the document's is replaced.
    /// </summary>
    bool CarryLink(JsonObject? stored, JsonObject document);
}

/// <summary>Values the ddms route's shapes read from a record and its target state.</summary>
internal static class DdmsShapeValues
{
    /// <summary>The cache directive a read sends to a DDMS that caches its answers (RAFS, osdu/specs/rafs-ddms/INTEGRATION.md section 1.5).</summary>
    public static readonly IReadOnlyDictionary<string, string> NoStore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cache-Control"] = "no-store" };

    /// <summary>The ids a target state keeps under <paramref name="key"/>, comma-separated.</summary>
    public static IReadOnlyList<string> Ids(IReadOnlyDictionary<string, string>? targetState, string key)
        => targetState is not null && targetState.TryGetValue(key, out var joined) && joined.Length > 0
            ? joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The partition part of an OSDU record id (<c>{partition}:{entityType}:{key}</c>).</summary>
    public static string? Namespace(string targetId)
    {
        var colon = targetId.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? targetId[..colon] : null;
    }

    /// <summary>The part of an OSDU record id after its entity type: everything after the second colon.</summary>
    public static string? EntityId(string targetId)
    {
        var first = targetId.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : targetId.IndexOf(':', first + 1);
        return second < 0 || second == targetId.Length - 1 ? null : targetId[(second + 1)..];
    }

    /// <summary>A long list for a step value or a message: the first entries, and how many more there are.</summary>
    public static string Bounded(IReadOnlyCollection<string> values, int shown = 20)
        => values.Count <= shown
            ? string.Join(", ", values)
            : string.Join(", ", values.Take(shown)) + string.Create(System.Globalization.CultureInfo.InvariantCulture, $" and {values.Count - shown} more");
}
