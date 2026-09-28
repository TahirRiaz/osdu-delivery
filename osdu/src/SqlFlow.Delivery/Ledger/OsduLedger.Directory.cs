using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Ledger;

// The ledger's directory (docs/ledger.md, Partitions): the partitions the ledger keys by, each with its number, and every
// ledger with the partition it belongs to. Every ledger table starts its key with the partition's number, so every read and
// write of a ledger names it: the directory resolves it once per ledger, and this process keeps it, since a ledger's
// partition never changes once it has one.
public sealed partial class OsduLedger
{
    private const short Unassigned = DeliveryModel.UnassignedPartition;

    /// <summary>The widest partition number the ledger keys by: <c>smallint</c>.</summary>
    private const int MaxPartitionNumber = short.MaxValue;

    /// <summary>
    /// The ledger tables a ledger's rows are kept in, whose rows an adoption moves into the ledger's partition. Each has an
    /// index leading with the partition and the ledger identity, so a ledger's rows are one range of it.
    /// </summary>
    private static readonly string[] LedgerTables =
        ["Record", "RecordIdentity", "Attempt", "Submission", "WorkBatch", "Lease", "RecordEvent", "SourceWatermark", "Activity", "Retrieval"];

    /// <summary>The statement that moves a slice of an unassigned ledger's rows of each table into its partition.</summary>
    private static readonly string[] AdoptStatements = LedgerTables
        .Select(table => "UPDATE TOP (@slice) [osdu].[" + table + "] SET [PartitionId] = @partition WHERE [PartitionId] = @unassigned AND [FlowId] = @flow;")
        .ToArray();

    /// <summary>
    /// Ledger identities this process has resolved to their partition's number. An unassigned ledger is not kept: its next
    /// run adopts it, perhaps in another process, and it is read again until then.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, short> _ledgerPartitions = new();

    /// <summary>Partition numbers this process has resolved, by data-partition-id, compared regardless of case.</summary>
    private readonly ConcurrentDictionary<string, short> _partitionIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Partition names by number, for the rows the ledger reads back.</summary>
    private readonly ConcurrentDictionary<short, string> _partitionNames = new();

    public async Task<LedgerEntry> RegisterLedgerAsync(LedgerEntry ledger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var partition = RequirePartitionName(ledger);
        if (string.IsNullOrWhiteSpace(ledger.Kind) || string.IsNullOrWhiteSpace(ledger.FlowName) || string.IsNullOrWhiteSpace(ledger.LedgerName))
        {
            throw new ArgumentException($"Ledger {ledger.FlowId:D} is registered with its kind, its flow and its name.", nameof(ledger));
        }

        var partitionId = await EnsurePartitionIdAsync(partition, ct).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            var existing = await ReadAsync(db => db.DeliveryLedgers.AsNoTracking().FirstOrDefaultAsync(l => l.FlowId == ledger.FlowId, ct), ct).ConfigureAwait(false);
            if (existing is null)
            {
                if (await TryAddLedgerAsync(ledger, partitionId, ct).ConfigureAwait(false))
                {
                    _ledgerPartitions[ledger.FlowId] = partitionId;
                    return await EntryAsync(ledger.FlowId, ct).ConfigureAwait(false);
                }

                // Another registration of the same ledger inserted it first: read what it wrote and compare.
                continue;
            }

            if (existing.PartitionId == partitionId)
            {
                _ledgerPartitions[ledger.FlowId] = partitionId;
                return await ToEntryAsync(existing, ct).ConfigureAwait(false);
            }

            if (existing.PartitionId != Unassigned)
            {
                throw new DeliveryException(
                    $"Ledger '{existing.LedgerName}' ({existing.FlowId:D}) belongs to partition '{await PartitionNameAsync(existing.PartitionId, ct).ConfigureAwait(false)}', and this run delivers to '{partition}'. "
                    + "A ledger keeps the records of one partition: the ids they were delivered as, and every record a later run compares with, are that partition's. "
                    + "A flow whose data-partition-id header resolves to another partition here than where its ledger was written, or whose keepLedger names another partition than the one that kept the ledger, meets this. "
                    + "Point the header at the ledger's partition, or name the partitions the flow serves under partitions so each keeps a ledger of its own. Nothing ran.");
            }

            if (attempt > 3)
            {
                throw new DeliveryException(
                    $"Ledger '{existing.LedgerName}' ({existing.FlowId:D}) could not be placed in partition '{partition}': another registration kept changing it. Nothing ran; run again.");
            }

            await AdoptAsync(existing, partitionId, partition, ct).ConfigureAwait(false);
        }
    }

    public async Task<LedgerEntry?> GetLedgerAsync(Guid flowId, CancellationToken ct = default)
    {
        var row = await ReadAsync(db => db.DeliveryLedgers.AsNoTracking().FirstOrDefaultAsync(l => l.FlowId == flowId, ct), ct).ConfigureAwait(false);
        return row is null ? null : await ToEntryAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LedgerEntry>> ListLedgersAsync(string? partition, CancellationToken ct = default)
    {
        List<DeliveryLedger> rows;
        if (string.IsNullOrWhiteSpace(partition))
        {
            rows = await ReadAsync(db => db.DeliveryLedgers.AsNoTracking().OrderBy(l => l.PartitionId).ThenBy(l => l.LedgerName).ToListAsync(ct), ct).ConfigureAwait(false);
        }
        else if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is { } id)
        {
            rows = await ReadAsync(db => db.DeliveryLedgers.AsNoTracking().Where(l => l.PartitionId == id).OrderBy(l => l.LedgerName).ToListAsync(ct), ct).ConfigureAwait(false);
        }
        else
        {
            return [];
        }

        var entries = new List<LedgerEntry>(rows.Count);
        foreach (var row in rows)
        {
            entries.Add(await ToEntryAsync(row, ct).ConfigureAwait(false));
        }

        return entries;
    }

    /// <summary>
    /// The partition number <paramref name="flowId"/>'s rows are kept under, for a read: null when no run has registered the
    /// ledger, so there is nothing of it to read; <see cref="Unassigned"/> for a ledger not yet placed.
    /// </summary>
    private async ValueTask<short?> PartitionOfAsync(Guid flowId, CancellationToken ct)
    {
        if (_ledgerPartitions.TryGetValue(flowId, out var known))
        {
            return known;
        }

        var row = await ReadAsync(db => db.DeliveryLedgers.AsNoTracking().Where(l => l.FlowId == flowId).Select(l => (short?)l.PartitionId).FirstOrDefaultAsync(ct), ct).ConfigureAwait(false);
        if (row is { } found && found != Unassigned)
        {
            _ledgerPartitions[flowId] = found;
        }

        return row;
    }

    /// <summary>The partition number every row a write adds to <paramref name="flowId"/>'s ledger is keyed by.</summary>
    /// <exception cref="DeliveryException">No run has registered the ledger.</exception>
    private async ValueTask<short> WritePartitionAsync(Guid flowId, CancellationToken ct)
        => await PartitionOfAsync(flowId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Ledger {flowId:D} is not in the ledger's directory, so nothing of it can be written: a run registers the ledger of the partition it delivers to before it writes a row of it (docs/ledger.md, Partitions).");

    /// <summary>The number of the partition named <paramref name="partition"/>, or null when no ledger has been kept under it.</summary>
    private async ValueTask<short?> PartitionIdOfAsync(string partition, CancellationToken ct)
    {
        var name = partition.Trim();
        if (_partitionIds.TryGetValue(name, out var known))
        {
            return known;
        }

        var row = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Where(p => p.Name == name).Select(p => new { p.PartitionId, p.Name }).FirstOrDefaultAsync(ct), ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        _partitionIds[row.Name] = row.PartitionId;
        _partitionNames[row.PartitionId] = row.Name;
        return row.PartitionId;
    }

    /// <summary>The number of the partition named <paramref name="partition"/>, numbering it when it has none yet.</summary>
    private async Task<short> EnsurePartitionIdAsync(string partition, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is { } known)
            {
                return known;
            }

            await using var db = Open();
            db.DeliveryLedgerPartitions.Add(new DeliveryLedgerPartition { Name = partition, CreatedUtc = Now });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                // Numbered by another registration meanwhile: read its number.
                continue;
            }
            catch (DbUpdateException ex) when (IsOverflow(ex))
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"The ledger keys by at most {MaxPartitionNumber:N0} partitions, and partition '{partition}' would be one more."), ex);
            }
        }
    }

    /// <summary>The data-partition-id of partition number <paramref name="partitionId"/>, or null for an unassigned ledger.</summary>
    private async ValueTask<string?> PartitionNameAsync(short partitionId, CancellationToken ct)
    {
        if (partitionId == Unassigned)
        {
            return null;
        }

        if (_partitionNames.TryGetValue(partitionId, out var known))
        {
            return known;
        }

        // The table holds one row per partition the ledger has kept, so every name is read at once.
        var rows = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => new { p.PartitionId, p.Name }).ToListAsync(ct), ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _partitionNames[row.PartitionId] = row.Name;
            _partitionIds[row.Name] = row.PartitionId;
        }

        return _partitionNames.TryGetValue(partitionId, out var name)
            ? name
            : throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Partition number {partitionId} is not in the ledger's directory, which numbers every partition a ledger row names."));
    }

    /// <summary>The data-partition-ids of the partition numbers <paramref name="partitionIds"/>, for rows read across ledgers.</summary>
    private async Task<IReadOnlyDictionary<short, string?>> PartitionNamesAsync(IEnumerable<short> partitionIds, CancellationToken ct)
    {
        var names = new Dictionary<short, string?>();
        foreach (var id in partitionIds.Distinct())
        {
            names[id] = await PartitionNameAsync(id, ct).ConfigureAwait(false);
        }

        return names;
    }

    private async Task<bool> TryAddLedgerAsync(LedgerEntry ledger, short partitionId, CancellationToken ct)
    {
        await using var db = Open();
        db.DeliveryLedgers.Add(new DeliveryLedger
        {
            PartitionId = partitionId,
            FlowId = ledger.FlowId,
            Kind = Truncate(ledger.Kind.Trim(), 16)!,
            FlowName = Truncate(ledger.FlowName, DeliveryLedger.MaxFlowNameLength)!,
            Interface = Truncate(ledger.Interface, DeliveryInterface.MaxInterfaceLength) ?? string.Empty,
            LedgerName = Truncate(ledger.LedgerName, 200)!,
            RegisteredUtc = Now,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Places a ledger the upgrade to partition keys could not place into <paramref name="partition"/>: every row of it, a
    /// slice of each table at a time, and then its directory row. Refused when its records were delivered to another
    /// partition, since placing them here would make them another partition's records. Two adoptions of one ledger at once
    /// move each row once, and the one that places the directory row second finds it placed.
    /// </summary>
    private async Task AdoptAsync(DeliveryLedger ledger, short partitionId, string partition, CancellationToken ct)
    {
        var delivered = await DeliveredPartitionsAsync(ledger.FlowId, ct).ConfigureAwait(false);
        var elsewhere = delivered.Where(d => !string.Equals(d.Partition, partition, StringComparison.OrdinalIgnoreCase)).ToList();
        if (elsewhere.Count > 0)
        {
            throw new DeliveryException(
                $"Ledger '{ledger.LedgerName}' ({ledger.FlowId:D}) is not yet placed in a partition, and it holds "
                + string.Join(", ", elsewhere.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Records:N0} record(s) delivered to '{d.Partition}'")))
                + $", so it cannot become partition '{partition}''s ledger: a ledger keeps the records of one partition. "
                + "Deliver those records again from a ledger of their own partition (name the partitions under partitions, with keepLedger on the one they went to), or run this flow in that partition. Nothing ran.");
        }

        await using var db = Open();
        foreach (var statement in AdoptStatements)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var moved = await RetryDeadlockAsync(
                    () => db.Database.ExecuteSqlRawAsync(
                        statement,
                        [
                            new SqlParameter("@slice", SqlDbType.Int) { Value = WriteSlice },
                            new SqlParameter("@partition", SqlDbType.SmallInt) { Value = partitionId },
                            new SqlParameter("@unassigned", SqlDbType.SmallInt) { Value = Unassigned },
                            new SqlParameter("@flow", SqlDbType.UniqueIdentifier) { Value = ledger.FlowId },
                        ],
                        ct),
                    ct).ConfigureAwait(false);
                if (moved < WriteSlice)
                {
                    break;
                }
            }
        }

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE [osdu].[Ledger] SET [PartitionId] = @partition WHERE [FlowId] = @flow AND [PartitionId] = @unassigned;",
            [
                new SqlParameter("@partition", SqlDbType.SmallInt) { Value = partitionId },
                new SqlParameter("@flow", SqlDbType.UniqueIdentifier) { Value = ledger.FlowId },
                new SqlParameter("@unassigned", SqlDbType.SmallInt) { Value = Unassigned },
            ],
            ct).ConfigureAwait(false);
    }

    private async Task<LedgerEntry> EntryAsync(Guid flowId, CancellationToken ct)
        => await GetLedgerAsync(flowId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Ledger {flowId:D} was registered, and is not in the ledger's directory when read back.");

    private async Task<LedgerEntry> ToEntryAsync(DeliveryLedger row, CancellationToken ct) => new()
    {
        FlowId = row.FlowId,
        Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false),
        Kind = row.Kind,
        FlowName = row.FlowName,
        Interface = row.Interface,
        LedgerName = row.LedgerName,
        RegisteredUtc = DateTime.SpecifyKind(row.RegisteredUtc, DateTimeKind.Utc),
    };

    /// <summary>The data-partition-id a registration names, checked as a partition is everywhere.</summary>
    private static string RequirePartitionName(LedgerEntry ledger)
    {
        var name = ledger.Partition?.Trim();
        return !string.IsNullOrEmpty(name) && CacheScope.IsPartitionId(name)
            ? name
            : throw new DeliveryException(
                $"Ledger {ledger.FlowId:D} ({ledger.LedgerName}) is registered in '{ledger.Partition}', which is no data-partition-id: letters, digits, underscore, hyphen and dot, at most {CacheScope.MaxLength} characters. A ledger belongs to the partition a run delivers to, resolved.");
    }

    /// <summary>Whether a write failed on a unique key or index: another writer inserted the same row first.</summary>
    private static bool IsDuplicate(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a write failed because an identity ran past its type (error 8115, arithmetic overflow).</summary>
    private static bool IsOverflow(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == 8115))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A record's place in the record table's key order: its partition's number, its ledger identity and its delivery key.</summary>
public readonly record struct RecordCursor(short PartitionId, Guid FlowId, Guid DeliveryKey);
