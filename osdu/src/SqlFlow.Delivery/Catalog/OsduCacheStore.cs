using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The cache store over the <c>osdu.CacheVersion</c>, <c>osdu.CacheItem</c> and <c>osdu.CacheMember</c>
/// tables, one cache per partition. A merge runs in one transaction: it reads the partition's current version and who holds
/// each record of the captured types, merges the capture in, and when the cached content moved writes the version row
/// first, which claims the partition's next sequence so a concurrent write fails instead of interleaving, then the records
/// that changed, arrived or left, then the membership and the current flag. A record whose values did not change is not
/// written again: its open range already covers the new version, however many flows captured it. The version is the whole
/// partition's cache and moves when anything in it does; each type it holds carries its own content hash, how it compares
/// with the version before and the version its content dates from, so a type that only rode along with another's change is
/// told apart, and its records are not even read. Versions never change once written, so the most recently loaded ones are
/// kept in memory. The partition's retention (<see cref="ApplyRetentionAsync"/>) prunes the records of the versions the
/// partition no longer needs, and keeps their rows; a pruned version is refused on every host, whatever it holds in memory.
/// </summary>
public sealed class OsduCacheStore : ICacheStore
{
    /// <summary>The width of a partition name and of a cache flow name in the catalog.</summary>
    public const int MaxNameLength = 200;

    private const int InsertChunk = 2_000;

    private const int UpdateChunk = 1_000;

    /// <summary>
    /// The most stored rows one statement of a retention removes. Each batch is its own short statement, so pruning years of
    /// history never holds a long lock on the rows every render of the partition reads, and never takes enough row locks for
    /// SQL Server to lock the whole table instead.
    /// </summary>
    private const int PruneChunk = 2_000;

    /// <summary>How many loaded versions stay in memory. A run renders against one; the GUI reads a handful.</summary>
    private const int RetainedVersions = 8;

    private readonly Func<OsduDbContext> _factory;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Scope, string Version, ReferenceSnapshot Snapshot)> _recent = new();

    public OsduCacheStore(Func<OsduDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        return await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope && v.Current)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        // The row is read whether or not the version is in memory: a version the retention pruned is refused alike on every
        // host, including one that loaded it before it was pruned.
        await using var db = _factory();
        var row = await db.DeliveryCacheVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Scope == scope && v.Version == version, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (row.PrunedUtc is { } pruned)
        {
            throw new CacheVersionPrunedException(scope, version, Utc(pruned));
        }

        if (Recent(scope, version) is { } known)
        {
            return known;
        }

        var snapshot = await ReadAsync(db, row, ct).ConfigureAwait(false);
        Remember(scope, version, snapshot);
        return snapshot;
    }

    public async Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        var rows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope)
            .OrderByDescending(v => v.Sequence)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(Info).ToList();
    }

    public async Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        await using var db = _factory();
        var rows = db.DeliveryCacheVersions.AsNoTracking().Where(v => v.Scope == scope);
        rows = version is null ? rows.Where(v => v.Current) : rows.Where(v => v.Version == version);
        var row = await rows.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : Info(row);
    }

    public async Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        return await DeclarationAsync(db, scope, ct).ConfigureAwait(false);
    }

    /// <summary>What the synced cache flows declare for a partition, read through an open context.</summary>
    public static async Task<CacheDeclaration> DeclarationAsync(OsduDbContext db, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var rows = await db.DeliveryCacheDefinitions.AsNoTracking()
            .Where(d => d.Scope == scope)
            .OrderBy(d => d.FlowName).ThenBy(d => d.Name).ThenBy(d => d.RepoId)
            .Select(d => new { d.FlowName, d.Name, d.EntityType, d.Kind, d.Query, d.FieldsJson, d.OnChange, d.Origin, d.KeyField, d.RetentionDays })
            .ToListAsync(ct).ConfigureAwait(false);

        // A flow is named once per catalog; the sync warns when two repositories declare the same one, and the first row wins here.
        var seen = new HashSet<(string Flow, string Type)>();
        var declarations = new List<CacheTypeDeclaration>(rows.Count);
        foreach (var row in rows)
        {
            if (!seen.Add((row.FlowName, row.Name.ToUpperInvariant())))
            {
                continue;
            }

            declarations.Add(new CacheTypeDeclaration(
                row.FlowName, row.Name, row.EntityType, row.Kind, string.IsNullOrWhiteSpace(row.Query) ? "*" : row.Query,
                ParseFields(row.FieldsJson, row.FlowName, row.Name),
                row.OnChange.Equals("approve", StringComparison.OrdinalIgnoreCase) ? CacheChangeMode.Approve : CacheChangeMode.Auto,
                CacheOrigins.Parse(row.Origin),
                row.KeyField,
                row.RetentionDays));
        }

        return new CacheDeclaration(scope, declarations);
    }

    public async Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(capture);
        if (scope.Length > MaxNameLength)
        {
            throw new DeliveryException($"A partition name is at most {MaxNameLength} characters; '{scope[..40]}...' is longer.");
        }

        if (flowName.Length > MaxNameLength)
        {
            throw new DeliveryException($"A cache flow name is at most {MaxNameLength} characters; '{flowName[..40]}...' is longer.");
        }

        if (captured.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != captured.Count)
        {
            throw new DeliveryException($"Cache flow '{flowName}' captured the same type name twice; nothing was written to the cache of partition '{scope}'.");
        }

        foreach (var type in captured)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (type.Items.FirstOrDefault(item => !ids.Add(item.Id)) is { } twice)
            {
                throw new DeliveryException(
                    $"Cache flow '{flowName}': type {type.Name} holds record {twice.Id} more than once, so the capture could not say which values it holds. Nothing was written to the cache of partition '{scope}'.");
            }

            // A lookup row is stored and matched under its key, so a key the catalog could not hold exactly once, or one a
            // match would never find, is refused here whatever produced it.
            if (type.IsLookup)
            {
                var bad = type.Items.Select(item => (item.Id, Problem: LookupKeys.Problem(item.Id))).Where(k => k.Problem is not null).Take(5).ToList();
                if (bad.Count > 0)
                {
                    throw new DeliveryException(
                        $"Cache flow '{flowName}': lookup table {type.Name} has keys the cache cannot hold: {string.Join("; ", bad.Select(k => k.Problem))}. Nothing was written to the cache of partition '{scope}'.");
                }
            }
        }

        var typeNames = captured.Select(t => t.Name).ToList();
        var label = CacheVersionLabel.Mint(capturedUtc);

        await using var db = _factory();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            var write = await strategy.ExecuteAsync(async () =>
            {
                // A retried attempt rebuilds everything it stages from the capture, never from what a failed attempt tracked.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Refreshes of one partition merge one after another: a second waits for the first to commit and then merges
                // onto the version it wrote, instead of both claiming the next version and one failing.
                await LockPartitionAsync(db, scope, flowName, ct).ConfigureAwait(false);

                var currentRow = await db.DeliveryCacheVersions.AsNoTracking()
                    .Where(v => v.Scope == scope && v.Current)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var current = currentRow is null ? null : Recent(scope, currentRow.Version) ?? await ReadAsync(db, currentRow, ct).ConfigureAwait(false);

                var memberRows = await db.DeliveryCacheMembers.AsNoTracking()
                    .Where(m => m.Scope == scope && typeNames.Contains(m.TypeName))
                    .Select(m => new { m.TypeName, m.RecordId, m.FlowName })
                    .ToListAsync(ct).ConfigureAwait(false);
                var members = new Dictionary<CacheMemberKey, IReadOnlySet<string>>(CacheMemberKey.Comparer);
                foreach (var held in memberRows.GroupBy(m => new CacheMemberKey(m.TypeName, m.RecordId), CacheMemberKey.Comparer))
                {
                    members[held.Key] = held.Select(m => m.FlowName).ToHashSet(StringComparer.Ordinal);
                }

                var declared = await db.DeliveryCacheDefinitions.AsNoTracking()
                    .Where(d => d.Scope == scope)
                    .Select(d => d.Name)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                var plan = CacheMerge.Apply(current, members, flowName, captured, declared, label, capturedUtc, readings);
                var hash = plan.Snapshot.ContentHash();
                if (current is not null && string.Equals(hash, current.ContentHash(), StringComparison.Ordinal))
                {
                    await WriteMembersAsync(db, scope, flowName, captured, plan, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return new CacheWrite(current, current, Written: false, CacheTypeChanges.None(current));
                }

                var sequence = (await db.DeliveryCacheVersions
                    .Where(v => v.Scope == scope)
                    .MaxAsync(v => (int?)v.Sequence, ct).ConfigureAwait(false) ?? 0) + 1;
                var version = await db.DeliveryCacheVersions.AnyAsync(v => v.Scope == scope && v.Version == label, ct).ConfigureAwait(false)
                    ? $"{label}-{sequence.ToString(CultureInfo.InvariantCulture)}"
                    : label;
                var snapshot = string.Equals(version, plan.Snapshot.Version, StringComparison.Ordinal)
                    ? plan.Snapshot
                    : new ReferenceSnapshot(version, capturedUtc, plan.Snapshot.Types, plan.Snapshot.SystemProperties);
                var types = snapshot.Types.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

                // The version moves because something in the partition's cache did; each type's own hash says whether it
                // was that type. Read before any record is written, since a version written before types were hashed is
                // dated from the ranges its records hold.
                var changes = CacheTypeChanges.Compare(current, snapshot);
                var since = await SinceAsync(db, currentRow, version, types, changes, ct).ConfigureAwait(false);

                // The row goes in first: the unique sequence is what a concurrent write of the same partition collides on.
                var row = new DeliveryCacheVersion
                {
                    Id = FlowIdentity.FromName($"delivery-cache-version/{scope}/{version}"),
                    Scope = scope,
                    FlowName = flowName,
                    Version = version,
                    Sequence = sequence,
                    CapturedUtc = capturedUtc.UtcDateTime,
                    ContentHash = hash,
                    PreviousVersion = currentRow?.Version,
                    Current = false,
                    RunId = capture.RunId,
                    CapturedBy = Clip(capture.CapturedBy, 200),
                    Origin = Clip(capture.Origin, 1000),
                    TypesJson = TypesJson(types, changes, since),
                    SystemPropertiesJson = SystemPropertiesJson(snapshot.SystemProperties),
                    Items = types.Sum(t => (long)t.Items.Count),
                };
                db.DeliveryCacheVersions.Add(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                await WriteItemsAsync(db, scope, sequence, types, changes, ct).ConfigureAwait(false);
                await WriteMembersAsync(db, scope, flowName, captured, plan, ct).ConfigureAwait(false);

                await db.DeliveryCacheVersions
                    .Where(v => v.Scope == scope && v.Current)
                    .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, false), ct).ConfigureAwait(false);
                await db.DeliveryCacheVersions
                    .Where(v => v.Id == row.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, true), ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new CacheWrite(snapshot, current, Written: true, changes);
            }).ConfigureAwait(false);

            if (write.Written)
            {
                Remember(scope, write.Snapshot.Version, write.Snapshot);
            }

            return write;
        }
        catch (DbUpdateException ex)
        {
            throw new DeliveryException(
                $"Cache flow '{flowName}' could not write a version of the cache of partition '{scope}' ({ex.GetBaseException().Message}). Nothing of this capture was kept; run the refresh again.",
                ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The pass takes no lock, and needs none. It records a version's change counts before it marks the version pruned, and
    /// both writes skip a row a concurrent pass already wrote, so two passes never record counts read after either removed a
    /// row. It removes only rows a version up to the current one at its start closed, so a row a concurrent merge writes or
    /// closes is never among them. Each step is repeatable: a pass cut short leaves versions marked and rows the next pass
    /// removes. It is housekeeping, so it runs at a low deadlock priority: when it meets a merge, a delivery or a reader of
    /// the cache in a deadlock, the database rolls back its statement rather than theirs, and the pass starts again.
    /// </remarks>
    public async Task<CacheRetentionOutcome> ApplyRetentionAsync(
        string scope, string flowName, int retentionDays, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, CacheRetention.MaxDays);

        var progress = new RetentionProgress();
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await RetainAsync(scope, flowName, retentionDays, now, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < RetentionAttempts && IsDeadlockVictim(ex))
            {
                // Waiting a moment, longer each time and never the same for two passes, keeps the pass from meeting the same
                // writer the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 100) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How many times a retention the database chose as a deadlock victim is started before the deadlock is reported.</summary>
    private const int RetentionAttempts = 5;

    /// <summary>The SQL Server error of a statement the database rolled back to end a deadlock.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Whether a statement failed because the database chose it as the victim of a deadlock, searched through the causes.</summary>
    private static bool IsDeadlockVictim(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => e.Number == DeadlockVictim))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a retention did across its attempts: an attempt the database rolled back keeps what it finished.</summary>
    private sealed class RetentionProgress
    {
        public List<string> Pruned { get; } = [];

        public long RowsRemoved { get; set; }
    }

    /// <summary>
    /// One attempt of a retention, on one connection at a low deadlock priority, restored before the connection goes back to
    /// the pool.
    /// </summary>
    private async Task<CacheRetentionOutcome> RetainAsync(
        string scope, string flowName, int retentionDays, DateTimeOffset now, RetentionProgress progress, CancellationToken ct)
    {
        await using var db = _factory();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY LOW;", ct).ConfigureAwait(false);
            return await RetainOnAsync(db, scope, flowName, retentionDays, now, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (db.Database.GetDbConnection().State == ConnectionState.Open)
            {
                await db.Database.ExecuteSqlRawAsync("SET DEADLOCK_PRIORITY NORMAL;", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The attempt's work on its connection: weigh the versions, record what the pruned ones changed, mark them, remove their rows.</summary>
    private async Task<CacheRetentionOutcome> RetainOnAsync(
        OsduDbContext db, string scope, string flowName, int retentionDays, DateTimeOffset now, RetentionProgress progress, CancellationToken ct)
    {
        var days = (await DeclarationAsync(db, scope, ct).ConfigureAwait(false)).RetentionFor(flowName, retentionDays);
        var rows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope)
            .Select(v => new { v.Sequence, v.Version, v.CapturedUtc, v.Current, v.PrunedUtc, Counted = v.ChangesJson != null })
            .ToListAsync(ct).ConfigureAwait(false);
        var held = rows.Count(v => v.PrunedUtc is null);
        if (await PinnedVersionsAsync(db, scope, ct).ConfigureAwait(false) is not { } pinned)
        {
            return new CacheRetentionOutcome(
                days, held, [.. progress.Pruned], progress.RowsRemoved,
                Deferred: "a delivery flow that may read this partition was synced before pinned cache versions were recorded, so the versions it pins are not known yet; the next repository sync records them, and the refresh after it prunes");
        }

        var plan = CacheRetention.Plan(
            rows.Select(v => new CacheVersionSpan(v.Sequence, v.Version, Utc(v.CapturedUtc), v.Current, v.PrunedUtc is not null)).ToList(),
            days, now.UtcDateTime, pinned);
        if (plan.Current == 0)
        {
            return new CacheRetentionOutcome(days, held, [.. progress.Pruned], progress.RowsRemoved);
        }

        // What each version changed is recorded before any row it was counted from goes, so the history reads the same after.
        var counted = rows.Where(v => v.Counted).Select(v => v.Sequence).ToHashSet();
        var uncounted = plan.Counted.Where(sequence => !counted.Contains(sequence)).ToList();
        if (uncounted.Count > 0)
        {
            var changes = await CacheVersions.CountChangesAsync(db, scope, uncounted, ct).ConfigureAwait(false);
            foreach (var sequence in uncounted)
            {
                var json = CacheVersions.ChangesJson(changes.GetValueOrDefault(sequence) ?? []);
                await db.DeliveryCacheVersions
                    .Where(v => v.Scope == scope && v.Sequence == sequence && v.ChangesJson == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(v => v.ChangesJson, json), ct).ConfigureAwait(false);
            }
        }

        var pruned = plan.Pruned.Select(v => v.Version).ToList();
        if (pruned.Count > 0)
        {
            var sequences = plan.Pruned.Select(v => v.Sequence).ToList();
            var at = now.UtcDateTime;
            await db.DeliveryCacheVersions
                .Where(v => v.Scope == scope && sequences.Contains(v.Sequence) && v.PrunedUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(v => v.PrunedUtc, at), ct).ConfigureAwait(false);
            progress.Pruned.AddRange(pruned);
            Forget(scope, pruned);
        }

        // Rows go whenever some version is pruned, newly or before: a pass cut short left rows the next one removes.
        if (progress.Pruned.Count > 0 || rows.Any(v => v.PrunedUtc is not null))
        {
            await RemoveUnkeptRowsAsync(db, scope, plan, progress, ct).ConfigureAwait(false);
        }

        return new CacheRetentionOutcome(days, plan.Kept.Count, [.. progress.Pruned], progress.RowsRemoved);
    }

    /// <summary>
    /// The versions of the partition's cache an active delivery flow pins (<c>render.cacheVersion</c>): one bound to the
    /// partition, and one whose partition is its header's, which may resolve to it. A label is a version of one partition's
    /// cache, so keeping another partition's version of the same label keeps more than needed and never less. Null when such
    /// a flow's interface was synced before pins were recorded, so what it pins is not known.
    /// </summary>
    private static async Task<IReadOnlySet<string>?> PinnedVersionsAsync(OsduDbContext db, string scope, CancellationToken ct)
    {
        var declared = await db.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.Active && (i.Partition == scope || i.Partition == string.Empty))
            .Select(i => i.CacheVersion)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (declared.Any(version => version is null))
        {
            return null;
        }

        return declared
            .Where(version => !string.Equals(version, FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase))
            .Select(version => version!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes, a batch at a time, every stored row of the partition that no kept version holds: a closed range, ended by the
    /// current version at the latest, that overlaps no run of kept versions. Each batch is its own statement, and counts in
    /// <paramref name="progress"/> as soon as it commits.
    /// </summary>
    private static async Task RemoveUnkeptRowsAsync(OsduDbContext db, string scope, CacheRetentionPlan plan, RetentionProgress progress, CancellationToken ct)
    {
        const string Sql = """
            DELETE TOP (@chunk) i
            FROM [osdu].[CacheItem] AS i
            WHERE i.[Scope] = @scope
              AND i.[ToSequence] IS NOT NULL
              AND i.[ToSequence] <= @current
              AND NOT EXISTS (
                  SELECT 1 FROM OPENJSON(@kept) WITH ([From] int '$.from', [To] int '$.to') AS k
                  WHERE i.[FromSequence] <= k.[To] AND i.[ToSequence] > k.[From]);
            """;
        var kept = new JsonArray(plan.KeptRuns().Select(run => (JsonNode)new JsonObject { ["from"] = run.From, ["to"] = run.To }).ToArray()).ToJsonString();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await db.Database.ExecuteSqlRawAsync(
                Sql,
                [
                    new SqlParameter("@chunk", SqlDbType.Int) { Value = PruneChunk },
                    new SqlParameter("@scope", SqlDbType.NVarChar, MaxNameLength) { Value = scope },
                    new SqlParameter("@current", SqlDbType.Int) { Value = plan.Current },
                    new SqlParameter("@kept", SqlDbType.NVarChar, -1) { Value = kept },
                ],
                ct).ConfigureAwait(false);
            progress.RowsRemoved += deleted;
            if (deleted < PruneChunk)
            {
                return;
            }
        }
    }

    /// <summary>How long a merge waits for another merge of the same partition to finish before it gives up.</summary>
    public static readonly TimeSpan PartitionLockWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Takes the partition's merge lock for the rest of the transaction (<c>sp_getapplock</c>, exclusive, owned by the
    /// transaction, released when it commits or rolls back). The capture a refresh searched or read is already in hand, so
    /// what waits is only another merge's database work.
    /// </summary>
    private static async Task LockPartitionAsync(OsduDbContext db, string scope, string flowName, CancellationToken ct)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PartitionLockWait + TimeSpan.FromSeconds(30));
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;",
                [
                    result,
                    new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = "osdu-cache:" + scope },
                    new SqlParameter("@timeout", SqlDbType.Int) { Value = (int)PartitionLockWait.TotalMilliseconds },
                ],
                ct).ConfigureAwait(false);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }

        // 0 and 1 grant the lock (at once, or after waiting); a negative answer is a timeout, a cancel, a deadlock or an error.
        if (result.Value is not int granted || granted < 0)
        {
            throw new DeliveryException(result.Value is -1
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Cache flow '{flowName}' waited {PartitionLockWait.TotalMinutes:0} minutes for another refresh of partition '{scope}' to finish merging, and it had not. Nothing of this capture was kept; run the refresh again once the other has finished.")
                : $"Cache flow '{flowName}' could not take the merge lock of partition '{scope}' (sp_getapplock answered {result.Value}). Nothing of this capture was kept; run the refresh again.");
        }
    }

    /// <summary>A version row as the store and the catalog readers describe it.</summary>
    public static CacheVersionInfo Info(DeliveryCacheVersion row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CacheVersionInfo(
            row.Scope, row.Version, row.Sequence, DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc), row.Current, row.PreviousVersion,
            row.RunId, row.CapturedBy, row.Origin, row.FlowName, row.Items, ParseTypes(row.TypesJson, row.Scope, row.Version),
            ParseSystemProperties(row.SystemPropertiesJson, row.Scope, row.Version),
            row.PrunedUtc is { } pruned ? Utc(pruned) : null);
    }

    /// <summary>The captured values of a record as the catalog stores them: names in ordinal order, each value as captured.</summary>
    public static string FieldsJson(ReferenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = new JsonObject();
        foreach (var (name, value) in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            fields[name] = value.Node.DeepClone();
        }

        return fields.ToJsonString();
    }

    /// <summary>A declaration's fields as the sync stores them: <c>[{ "path": "data.Code", "as": "Code" }]</c>.</summary>
    public static IReadOnlyList<ReferenceFieldSpec> ParseFields(string json, string flowName, string typeName)
    {
        try
        {
            var fields = new List<ReferenceFieldSpec>();
            foreach (var field in (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? []).OfType<JsonObject>())
            {
                if (field["path"] is not JsonValue path || !path.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var name = field["as"] is JsonValue alias && alias.TryGetValue<string>(out var given) && !string.IsNullOrWhiteSpace(given) ? given : null;
                fields.Add(new ReferenceFieldSpec(text, name));
            }

            return fields;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException(
                $"The catalog's declaration of {typeName} by cache flow '{flowName}' holds fields that are not valid JSON ({ex.Message}); sync the repository again.", ex);
        }
    }

    /// <summary>
    /// One version with every record whose range covers its sequence, checked against the hash it was written with and, for a
    /// version written since types were hashed, each type against its own: one pass over the records answers both, and a
    /// type that no longer matches is named.
    /// </summary>
    private static async Task<ReferenceSnapshot> ReadAsync(OsduDbContext db, DeliveryCacheVersion row, CancellationToken ct)
    {
        var scope = row.Scope;
        var sequence = row.Sequence;
        var items = await db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == scope && i.FromSequence <= sequence && (i.ToSequence == null || i.ToSequence > sequence))
            .Select(i => new { i.TypeName, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var byType = items.GroupBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // One pool for the version: a value many records hold (the field a thousand wellbores lie in) is kept once.
        var pool = new StringPool();

        // The version row lists every type, a type that holds no record included, so the loaded version holds exactly the
        // types the merge left and hashes as it did.
        var listed = ParseTypes(row.TypesJson, scope, row.Version);
        var types = listed
            .Select(type => new ReferenceType(
                type.Name,
                type.EntityType,
                (byType.GetValueOrDefault(type.Name) ?? [])
                    .OrderBy(i => i.RecordId, StringComparer.Ordinal)
                    .Select(i => new ReferenceItem(i.RecordId, Fields(i.FieldsJson, scope, row.Version, pool))),
                type.Key))
            .ToList();
        var snapshot = new ReferenceSnapshot(
            row.Version,
            new DateTimeOffset(DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc)),
            types,
            ParseSystemProperties(row.SystemPropertiesJson, scope, row.Version));
        var hashes = snapshot.Hashes();
        var altered = listed
            .Where(type => type.Hash is not null && !string.Equals(type.Hash, hashes.Of(type.Name), StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToList();
        if (!string.Equals(hashes.Content, row.ContentHash, StringComparison.Ordinal) || altered.Count > 0)
        {
            // A retention may have pruned the version while its records were read: that is what to say, not that they were altered.
            var pruned = await db.DeliveryCacheVersions.AsNoTracking()
                .Where(v => v.Id == row.Id)
                .Select(v => v.PrunedUtc)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (pruned is { } at)
            {
                throw new CacheVersionPrunedException(scope, row.Version, Utc(at));
            }

            var which = altered.Count == 0 ? string.Empty : $" (the records of {string.Join(", ", altered)} no longer match the hash the version recorded for them)";
            throw new DeliveryException(
                $"Version {row.Version} of the cache of partition '{scope}' does not match the content hash it was written with{which}: its records were altered after the version was written, so nothing renders against it.");
        }

        return snapshot;
    }

    /// <summary>
    /// Compares the new version against what the newest one holds, record by record over every type that arrived, changed or
    /// left: a record that changed ends its open range and begins another, one that arrived begins one, and one that left
    /// ends its range. A type whose content hash did not move is not read at all: the newest version was loaded from exactly
    /// its open ranges and checked against its hash, so they already cover the new version as they stand.
    /// </summary>
    private static async Task WriteItemsAsync(
        OsduDbContext db, string scope, int sequence, IReadOnlyList<ReferenceType> types, CacheTypeChanges changes, CancellationToken ct)
    {
        var moved = types.Where(type => changes.Of(type.Name) is not CacheTypeChange.Unchanged).ToList();
        var touched = moved.Select(type => type.Name).Concat(changes.Removed).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (touched.Count == 0)
        {
            // Only the partition's system properties moved.
            return;
        }

        // The type names as the open ranges spell them, matched here without regard to case whatever the database's collation,
        // so the query below finds a type's ranges however an earlier version spelled it.
        var spelled = (await db.DeliveryCacheItems.AsNoTracking()
                .Where(i => i.Scope == scope && i.ToSequence == null)
                .Select(i => i.TypeName)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(touched.Contains)
            .ToList();
        var open = await db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == scope && i.ToSequence == null && spelled.Contains(i.TypeName))
            .Select(i => new { i.ItemId, i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var held = open.ToDictionary(o => (Type: o.TypeName.ToUpperInvariant(), o.RecordId));

        var closing = new List<long>();
        var arriving = new List<DeliveryCacheItem>();
        var seen = new HashSet<(string Type, string RecordId)>();
        foreach (var type in moved)
        {
            foreach (var item in type.Items)
            {
                var key = (Type: type.Name.ToUpperInvariant(), item.Id);
                seen.Add(key);
                var fields = FieldsJson(item);
                if (held.TryGetValue(key, out var existing))
                {
                    if (string.Equals(existing.FieldsJson, fields, StringComparison.Ordinal)
                        && string.Equals(existing.EntityType, type.EntityType, StringComparison.Ordinal)
                        && string.Equals(existing.TypeName, type.Name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    closing.Add(existing.ItemId);
                }

                arriving.Add(new DeliveryCacheItem
                {
                    Scope = scope,
                    TypeName = type.Name,
                    EntityType = type.EntityType,
                    RecordId = item.Id,
                    FieldsJson = fields,
                    Terms = Terms(item),
                    FromSequence = sequence,
                });
            }
        }

        closing.AddRange(open.Where(o => !seen.Contains((o.TypeName.ToUpperInvariant(), o.RecordId))).Select(o => o.ItemId));
        foreach (var chunk in closing.Chunk(UpdateChunk))
        {
            var ids = chunk.ToList();
            await db.DeliveryCacheItems
                .Where(i => ids.Contains(i.ItemId))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.ToSequence, (int?)sequence), ct).ConfigureAwait(false);
        }

        foreach (var chunk in arriving.Chunk(InsertChunk))
        {
            db.DeliveryCacheItems.AddRange(chunk);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// The flow's membership of the captured types becomes exactly what it captured, under the names the cache holds the
    /// types by; a type the merge removed from the partition loses every flow's membership.
    /// </summary>
    private static async Task WriteMembersAsync(
        OsduDbContext db, string scope, string flowName, IReadOnlyList<ReferenceType> captured, CacheMergePlan plan, CancellationToken ct)
    {
        var typeNames = captured.Select(t => t.Name).ToList();
        await db.DeliveryCacheMembers
            .Where(m => m.Scope == scope && m.FlowName == flowName && typeNames.Contains(m.TypeName))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (plan.RemovedTypes.Count > 0)
        {
            var removed = plan.RemovedTypes.ToList();
            await db.DeliveryCacheMembers
                .Where(m => m.Scope == scope && removed.Contains(m.TypeName))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        var rows = captured.SelectMany(type =>
        {
            var name = plan.Snapshot.Type(type.Name)?.Name ?? type.Name;
            return type.Items.Select(item => new DeliveryCacheMember { Scope = scope, TypeName = name, RecordId = item.Id, FlowName = flowName });
        });
        foreach (var chunk in rows.Chunk(InsertChunk))
        {
            db.DeliveryCacheMembers.AddRange(chunk);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Every scalar the record holds, newline separated, so a text search over the cache is one predicate.</summary>
    private static string Terms(ReferenceItem item)
        => string.Join('\n', item.Fields.Values.SelectMany(v => v.Terms).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The version's types as its row lists them: each with its entity type, its record count and a lookup table's key, and
    /// its content hash, how it compares with the version before, and the version its content dates from.
    /// </summary>
    private static string TypesJson(IEnumerable<ReferenceType> types, CacheTypeChanges changes, IReadOnlyDictionary<string, string?> since)
    {
        var array = new JsonArray();
        foreach (var type in types)
        {
            var entry = new JsonObject { ["name"] = type.Name, ["entityType"] = type.EntityType, ["items"] = type.Items.Count };
            if (type.Key is not null)
            {
                entry["key"] = type.Key;
            }

            entry["hash"] = type.ContentHash();
            entry["change"] = CacheTypeChanges.Text(changes.Of(type.Name)
                ?? throw new InvalidOperationException($"The merge compared no type {type.Name} with the version before, and it writes one."));
            if (since.GetValueOrDefault(type.Name) is { } from)
            {
                entry["since"] = from;
            }

            array.Add(entry);
        }

        return array.ToJsonString();
    }

    /// <summary>
    /// The version each type of a new version dates from: the new version for a type that arrived or changed, and for one
    /// that did not, the version the current one says it dates from. A current version written before types were hashed
    /// says nothing, so the type is dated from what the partition's records and versions hold: the latest version at which
    /// a record of it began or ended its range, or at which the type came back into the cache, whichever is later. A type
    /// neither can date is left undated rather than guessed.
    /// </summary>
    private static async Task<Dictionary<string, string?>> SinceAsync(
        OsduDbContext db, DeliveryCacheVersion? currentRow, string version, IReadOnlyList<ReferenceType> types, CacheTypeChanges changes, CancellationToken ct)
    {
        var since = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var carried = currentRow is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : ParseTypes(currentRow.TypesJson, currentRow.Scope, currentRow.Version).ToDictionary(t => t.Name, t => t.Since, StringComparer.OrdinalIgnoreCase);
        var undated = new List<string>();
        foreach (var type in types)
        {
            if (changes.Of(type.Name) is not CacheTypeChange.Unchanged)
            {
                since[type.Name] = version;
            }
            else if (carried.GetValueOrDefault(type.Name) is { } known)
            {
                since[type.Name] = known;
            }
            else
            {
                undated.Add(type.Name);
            }
        }

        if (undated.Count == 0 || currentRow is null)
        {
            return since;
        }

        // Where each undated type's stay in the cache began: walking back from the current version, the oldest version of the
        // unbroken run that lists it. The walk stops as soon as every type's run has ended.
        var scope = currentRow.Scope;
        var labels = new Dictionary<int, string>();
        var began = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var running = new HashSet<string>(undated, StringComparer.OrdinalIgnoreCase);
        var versions = db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope && v.Sequence <= currentRow.Sequence)
            .OrderByDescending(v => v.Sequence)
            .Select(v => new { v.Sequence, v.Version, v.TypesJson })
            .AsAsyncEnumerable();
        await foreach (var row in versions.WithCancellation(ct).ConfigureAwait(false))
        {
            labels[row.Sequence] = row.Version;
            var names = ParseTypes(row.TypesJson, scope, row.Version).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in running.ToList())
            {
                if (names.Contains(name))
                {
                    began[name] = row.Sequence;
                }
                else
                {
                    running.Remove(name);
                }
            }

            if (running.Count == 0)
            {
                break;
            }
        }

        // The latest version at which a record of each undated type began or ended a range: a change of the type. The names
        // are taken as the ranges spell them, matched without regard to case whatever the database's collation.
        var wanted = new HashSet<string>(undated, StringComparer.OrdinalIgnoreCase);
        var spelled = (await db.DeliveryCacheItems.AsNoTracking()
                .Where(i => i.Scope == scope)
                .Select(i => i.TypeName)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(wanted.Contains)
            .ToList();
        var ranges = (await db.DeliveryCacheItems.AsNoTracking()
                .Where(i => i.Scope == scope && spelled.Contains(i.TypeName))
                .GroupBy(i => i.TypeName)
                .Select(g => new { Type = g.Key, Began = g.Max(i => i.FromSequence), Ended = g.Max(i => i.ToSequence) })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => Math.Max(r.Began, r.Ended ?? 0)), StringComparer.OrdinalIgnoreCase);

        foreach (var name in undated)
        {
            var at = Math.Max(began.GetValueOrDefault(name), ranges.GetValueOrDefault(name));
            since[name] = at > 0 && labels.TryGetValue(at, out var label) ? label : null;
        }

        return since;
    }

    /// <summary>A version's system properties as the row stores them, in the order they are hashed.</summary>
    private static string SystemPropertiesJson(IEnumerable<SystemProperty> properties)
    {
        var array = new JsonArray();
        foreach (var property in SystemProperties.Ordered(properties))
        {
            var entry = new JsonObject
            {
                ["service"] = property.Service,
                ["name"] = property.Name,
                ["state"] = property.State.ToString(),
            };
            if (property.Source is { } source)
            {
                entry["source"] = source;
            }

            if (property.Detail is { } detail)
            {
                entry["detail"] = detail;
            }

            array.Add(entry);
        }

        return array.ToJsonString();
    }

    /// <summary>A version's system properties, read back; a state the row does not name is refused rather than guessed.</summary>
    private static List<SystemProperty> ParseSystemProperties(string json, string scope, string version)
    {
        try
        {
            var properties = new List<SystemProperty>();
            foreach (var entry in (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? []).OfType<JsonObject>())
            {
                var service = entry["service"]?.GetValue<string>();
                var name = entry["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(name)
                    || SystemProperties.ParseState(entry["state"]?.GetValue<string>()) is not { } state)
                {
                    throw new DeliveryException(
                        $"Version {version} of the cache of partition '{scope}' lists a system property without a service, a name or a known state ({entry.ToJsonString()}).");
                }

                properties.Add(new SystemProperty(service, name, state, entry["source"]?.GetValue<string>(), entry["detail"]?.GetValue<string>()));
            }

            return properties;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeliveryException($"Version {version} of the cache of partition '{scope}' has system properties that are not valid JSON ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// A version's types as its row lists them. The hash, the change and the version a type dates from are absent from a
    /// version written before types were hashed; one that is there and says something the store never writes (a hash that
    /// is not 64 lower-case hex digits, a change it does not name) is refused rather than taken on trust.
    /// </summary>
    private static List<CacheVersionType> ParseTypes(string json, string scope, string version)
    {
        try
        {
            return (JsonNode.Parse(json) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(type =>
                {
                    var name = type["name"]?.GetValue<string>() ?? throw new DeliveryException($"Version {version} of the cache of partition '{scope}' lists a type without a name.");
                    var hash = type["hash"]?.GetValue<string>();
                    if (hash is not null && !IsHash(hash))
                    {
                        throw new DeliveryException($"Version {version} of the cache of partition '{scope}' records a hash for {name} that is not a content hash ('{Clip(hash, 80)}').");
                    }

                    var changeText = type["change"]?.GetValue<string>();
                    var change = CacheTypeChanges.Parse(changeText);
                    if (changeText is not null && change is null)
                    {
                        throw new DeliveryException(
                            $"Version {version} of the cache of partition '{scope}' records {name} as '{Clip(changeText, 80)}', which is not a change (added, changed or unchanged).");
                    }

                    return new CacheVersionType(
                        name,
                        type["entityType"]?.GetValue<string>() ?? string.Empty,
                        type["items"]?.GetValue<long>() ?? 0,
                        type["key"]?.GetValue<string>(),
                        hash,
                        change,
                        type["since"]?.GetValue<string>());
                })
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeliveryException($"Version {version} of the cache of partition '{scope}' has a type list that is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static bool IsHash(string text)
        => text.Length == Hashing.ContentHash.HexLength && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static ReferenceFields Fields(string json, string scope, string version, StringPool pool)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A record of version {version} of the cache of partition '{scope}' holds values that are not valid JSON ({ex.Message}).", ex);
        }

        return ReferenceFields.Of(
            (node ?? []).Where(field => field.Value is not null).Select(field => KeyValuePair.Create(field.Key, ReferenceValue.From(field.Value!, pool))),
            pool);
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>An instant the catalog stores without its kind, read back as the UTC instant it was written as.</summary>
    private static DateTime Utc(DateTime stored) => DateTime.SpecifyKind(stored, DateTimeKind.Utc);

    /// <summary>Drops the named versions of a partition from memory, once their records are pruned.</summary>
    private void Forget(string scope, IReadOnlyCollection<string> versions)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null;)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Scope, scope, StringComparison.Ordinal) && versions.Contains(node.Value.Version, StringComparer.Ordinal))
                {
                    _recent.Remove(node);
                }

                node = next;
            }
        }
    }

    private ReferenceSnapshot? Recent(string scope, string version)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Scope, scope, StringComparison.Ordinal) && string.Equals(node.Value.Version, version, StringComparison.Ordinal))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return node.Value.Snapshot;
                }
            }
        }

        return null;
    }

    private void Remember(string scope, string version, ReferenceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_recent.Any(e => string.Equals(e.Scope, scope, StringComparison.Ordinal) && string.Equals(e.Version, version, StringComparison.Ordinal)))
            {
                return;
            }

            _recent.AddFirst((scope, version, snapshot));
            while (_recent.Count > RetainedVersions)
            {
                _recent.RemoveLast();
            }
        }
    }
}
