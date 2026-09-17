using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// The check <c>target.verifyReferences: storage</c> asks for before a record is sent (docs/interfaces-design.md section
/// 7). A record waits for the records the ledger holds and has not delivered; the ids no record of the ledger holds are
/// looked up here in OSDU's storage service, and a record referring to one storage does not hold is not sent: a source
/// that must not write dangling references holds it until the record it names exists.
/// </summary>
public sealed class ReferenceCheck
{
    /// <summary>How many of a record's missing references its hold names; the rest are counted.</summary>
    private const int Named = 5;

    private readonly ILedger _ledger;
    private readonly OsduHttpClient _client;
    private readonly string _batchPath;

    public ReferenceCheck(ILedger ledger, OsduHttpClient client, string batchPath)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPath);
        _ledger = ledger;
        _client = client;
        _batchPath = batchPath;
    }

    /// <summary>
    /// The references of <paramref name="records"/> that neither the ledger nor storage holds, by record; records whose
    /// references all resolve are left out. Throws when the ledger or storage cannot be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<DeliveryKey, IReadOnlyList<RecordReference>>> MissingAsync(IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ids = records.SelectMany(r => r.PendingReferences).Select(r => r.Id).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<DeliveryKey, IReadOnlyList<RecordReference>>();
        }

        var held = await _ledger.HeldIdsAsync(ids, ct).ConfigureAwait(false);
        var unknown = ids.Where(id => !held.Contains(id)).ToList();
        if (unknown.Count == 0)
        {
            return new Dictionary<DeliveryKey, IReadOnlyList<RecordReference>>();
        }

        var present = await StoragePresence.PresentAsync(_client, _batchPath, unknown, ct).ConfigureAwait(false);
        var absent = unknown.Where(id => !present.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var missing = new Dictionary<DeliveryKey, IReadOnlyList<RecordReference>>();
        foreach (var record in records)
        {
            var names = record.PendingReferences.Where(r => absent.Contains(r.Id)).ToList();
            if (names.Count > 0)
            {
                missing[record.DeliveryKey] = names;
            }
        }

        return missing;
    }

    /// <summary>Why a record with <paramref name="missing"/> references is held.</summary>
    public static string Describe(IReadOnlyList<RecordReference> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var named = string.Join(", ", missing.Take(Named).Select(r => $"{r.Id} ({r.Property})"));
        var more = missing.Count > Named ? $" and {missing.Count - Named} more" : string.Empty;
        return $"refers to {named}{more}, which neither the ledger nor OSDU's storage service holds; target.verifyReferences is storage, "
            + "so the record is not sent with a reference to nothing. Release it once the records it names exist.";
    }
}
