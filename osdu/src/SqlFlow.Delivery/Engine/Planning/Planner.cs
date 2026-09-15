using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Planning;

/// <summary>One record's place in a plan.</summary>
public sealed record PlanEntry
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    public string? Label { get; init; }

    public string? TargetId { get; init; }

    public required PlannedAction Action { get; init; }

    public SkipTier SkipTier { get; init; }

    public required string Reason { get; init; }

    public RenderResult? Render { get; init; }

    public string? SourceFingerprint { get; init; }

    /// <summary>When the drop says the source row last changed, when the flow declares source.lastModified.</summary>
    public DateTime? SourceModifiedUtc { get; init; }

    public string? PayloadHash { get; init; }

    /// <summary>The newest modified time among the payload's chunk files, when the flow takes them as the payload watermark.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    public string? PayloadLocation { get; init; }

    public int? ChunkCount { get; init; }

    public RecordState? Existing { get; init; }

    public bool DeliverMetadata { get; init; }

    public bool DeliverPayload { get; init; }

    public bool IsDelivery => DeliverMetadata || DeliverPayload;
}

/// <summary>
/// One record a plan takes in: read from the header's drop, or read from the flow's replica, where it carries the submission
/// whose drop it last came from (<see cref="Origin"/>), since that drop is where its payload is.
/// </summary>
public sealed class PlanInput
{
    private PlanInput(SourceRecord record, Guid? origin, Guid? replicaKey)
    {
        Record = record;
        Origin = origin;
        ReplicaKey = replicaKey;
    }

    public SourceRecord Record { get; }

    /// <summary>The submission whose drop a replica record was read from; null for a record read from the header's drop.</summary>
    public Guid? Origin { get; }

    /// <summary>The delivery key the replica holds the record under; null for a record read from a drop.</summary>
    public Guid? ReplicaKey { get; }

    public static PlanInput FromDrop(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new PlanInput(record, null, null);
    }

    public static PlanInput FromReplica(SourceRecord record, Guid origin, Guid key)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (origin == Guid.Empty || key == Guid.Empty)
        {
            throw new ArgumentException("A replica record names its delivery key and the submission it was read from.", nameof(origin));
        }

        return new PlanInput(record, origin, key);
    }
}

/// <summary>What the planner established before reading a single record: the drop, the checks, the tier-0 decision.</summary>
public sealed record PlanHeader
{
    public required FlowDefinition Flow { get; init; }

    /// <summary>The drop the submission was read from: opened from storage, or rebuilt from the manifest the source store kept. Null for a replan, whose rows each name their own.</summary>
    public required Drop? Drop { get; init; }

    /// <summary>Resolves the drop a stored row was read from; null for a plan without the ledger, which reads only its drop.</summary>
    public SourceOrigins? Origins { get; init; }

    public required ResolvedMapping Mapping { get; init; }

    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    /// <summary>
    /// The cache sets an unapproved change is holding back, read once per plan. Membership is the whole gate, so a
    /// run that must skip millions of records reads a handful of ids rather than a column on every one of them.
    /// </summary>
    public IReadOnlySet<long> GatedCacheSets { get; init; } = new HashSet<long>();

    public bool SkippedWholeRun { get; init; }

    public string? SkipReason { get; init; }

    /// <summary>The payload set the protocol streams, or null for record-only protocols.</summary>
    public string? PayloadName { get; init; }

    public int Partitions => Drop?.Manifest.PartitionCount ?? 0;
}

/// <summary>Running totals of a plan, safe to add to from the parallel renderers.</summary>
public sealed class PlanSummary
{
    private long _records;
    private long _deliveries;
    private long _skips;
    private long _awaitingApproval;
    private long _stale;
    private long _holds;
    private long _blocked;
    private long _untracked;

    public long Records => Interlocked.Read(ref _records);

    public long Deliveries => Interlocked.Read(ref _deliveries);

    /// <summary>Records skipped because nothing about them changed.</summary>
    public long Skips => Interlocked.Read(ref _skips);

    /// <summary>
    /// Records a cache change waiting for approval holds back. They are not unchanged: the change is rendered and
    /// ready, and an approval (or a rejection) is what decides whether it is sent, so a run says how many wait
    /// rather than counting them with the records it had no reason to send.
    /// </summary>
    public long AwaitingApproval => Interlocked.Read(ref _awaitingApproval);

    /// <summary>Records skipped because the drop carries an older version than the ledger already holds.</summary>
    public long Stale => Interlocked.Read(ref _stale);

    public long Holds => Interlocked.Read(ref _holds);

    public long Blocked => Interlocked.Read(ref _blocked);

    /// <summary>Records without a derivable delivery key: they cannot be tracked and are not delivered.</summary>
    public long Untracked => Interlocked.Read(ref _untracked);

    public void Add(PlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Interlocked.Increment(ref _records);
        if (entry.Key is null)
        {
            Interlocked.Increment(ref _untracked);
            return;
        }

        switch (entry.Action)
        {
            case PlannedAction.Skip when entry.SkipTier == SkipTier.Stale:
                Interlocked.Increment(ref _stale);
                break;
            case PlannedAction.Skip when entry.SkipTier == SkipTier.Approval:
                Interlocked.Increment(ref _awaitingApproval);
                break;
            case PlannedAction.Skip:
                Interlocked.Increment(ref _skips);
                break;
            case PlannedAction.Hold:
                Interlocked.Increment(ref _holds);
                break;
            case PlannedAction.Blocked:
                Interlocked.Increment(ref _blocked);
                break;
            default:
                if (entry.IsDelivery)
                {
                    Interlocked.Increment(ref _deliveries);
                }

                break;
        }
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Records} record(s): {Deliveries} to deliver, {Skips} unchanged, {AwaitingApproval} awaiting approval, {Stale} stale, {Holds} held, {Blocked} blocked, {Untracked} untracked");
}

/// <summary>What a run would do (design.md section 11: plan changes nothing), with every entry collected. For the
/// drops the engine is built for, consume <see cref="Planner.EntriesAsync"/> instead and keep only the summary.</summary>
public sealed record DeliveryPlan
{
    public required PlanHeader Header { get; init; }

    public required IReadOnlyList<PlanEntry> Entries { get; init; }

    public required PlanSummary Summary { get; init; }

    public FlowDefinition Flow => Header.Flow;

    public Drop Drop => Header.Drop ?? throw new InvalidOperationException("A collected plan reads a drop; this header plans stored rows.");

    public ResolvedMapping Mapping => Header.Mapping;

    public IReadOnlyDictionary<string, string> Parameters => Header.Parameters;

    public IReadOnlyList<ValidationIssue> Issues => Header.Issues;

    public bool SkippedWholeRun => Header.SkippedWholeRun;

    public string? SkipReason => Header.SkipReason;

    public int Count(PlannedAction action) => Entries.Count(e => e.Action == action);

    public int Deliveries => Entries.Count(e => e.IsDelivery);

    public int Skips => Entries.Count(e => e.Action == PlannedAction.Skip && e.SkipTier != SkipTier.Approval);

    /// <summary>Records a cache change waiting for approval holds back, counted apart from the unchanged.</summary>
    public int AwaitingApproval => Entries.Count(e => e.Action == PlannedAction.Skip && e.SkipTier == SkipTier.Approval);

    public int Holds => Entries.Count(e => e.Action == PlannedAction.Hold);

    public int Blocked => Entries.Count(e => e.Action == PlannedAction.Blocked);
}

/// <summary>
/// Renders a drop against the ledger and decides per record what would change (design.md sections 6.6 and 11).
/// Works offline: the only inputs are the drop, the pinned snapshots and the ledger (which may be absent, in which
/// case every record is a create). Records stream from the drop reader in batches through a bounded channel to a
/// set of parallel renderers, so a plan of any size runs in bounded memory and uses every core (section 16.1).
/// </summary>
public sealed class Planner
{
    /// <summary>Records per render batch: one ledger lookup, one unit of parallelism.</summary>
    public const int RenderBatch = 200;

    private readonly IDropReader _drops;
    private readonly ILedger? _ledger;
    private readonly ILogger<Planner> _logger;

    public Planner(IDropReader drops, ILedger? ledger, ILogger<Planner> logger)
    {
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(logger);
        _drops = drops;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>Opens the drop, checks it against the flow and the mapping, and applies the tier-0 gate.</summary>
    /// <param name="flow">The flow whose drop is planned.</param>
    /// <param name="resolved">The pinned render inputs (mapping, template and cache version).</param>
    /// <param name="parameters">The resolved flow parameter values.</param>
    /// <param name="dropLocation">The drop root to read.</param>
    /// <param name="force">Skip the tier-0 whole-run gate: plan every record even when no source table advanced.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<PlanHeader> OpenAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        var drop = await _drops.OpenAsync(dropLocation, flow.Source.Manifest, ct).ConfigureAwait(false);
        return await OpenDropAsync(flow, resolved, parameters, drop, gate: !force, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a submission the source store holds, without reading its drop. A loaded drop is opened from the manifest its
    /// load kept, under the same checks and (when <paramref name="gate"/>) the same tier-0 gate as a drop opened from
    /// storage. A replan is opened from the flow alone: each of its rows names the submission it was read from, and is
    /// checked against that submission's manifest when the plan first meets it. A null submission opens the latest stored
    /// rows of the flow without registering anything, which is what a plan from the source store reads.
    /// </summary>
    public async Task<PlanHeader> OpenStoredAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        SubmissionState? submission,
        bool gate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        var ledger = _ledger ?? throw new DeliveryException("Planning from the source store needs the ledger, which lives in the catalog database.");
        if (submission is not null && submission.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {submission.SubmissionId:D} belongs to flow '{submission.FlowName}', not '{flow.Name}'.");
        }

        if (submission is null || submission.IsReplan)
        {
            if (submission is { IsLoaded: false })
            {
                throw new DeliveryException(
                    $"Replan {submission.SubmissionId:D} holds no complete set of stored rows: it stopped before they were copied, or the retention prune removed some of them. Replan the flow again.");
            }

            return new PlanHeader
            {
                Flow = flow,
                Drop = null,
                Origins = new SourceOrigins(ledger, flow, resolved, null),
                Mapping = resolved,
                Parameters = parameters,
                Issues = [],
                PayloadName = PayloadName(flow),
                GatedCacheSets = (await ledger.GatedCacheSetsAsync(ct).ConfigureAwait(false)).ToHashSet(),
            };
        }

        if (!submission.IsLoaded || submission.ManifestJson is not { } json)
        {
            throw new DeliveryException($"Submission {submission.SubmissionId:D} is not loaded in the source store; a run of it from its drop loads it.");
        }

        var manifest = DropManifest.Parse(json, $"submission {submission.SubmissionId:D}");
        return await OpenDropAsync(flow, resolved, parameters, new Drop(submission.DropLocation, manifest), gate, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The checks a stored row's origin must pass before the row is rendered under the flow's current mapping: the drop was
    /// prepared for this flow, the mapping's columns are declared in it, and the flow's bindings hold. The mapping the drop
    /// was prepared for is deliberately not compared, because rendering stored rows under the mapping the flow pins now is
    /// what a replan is for.
    /// </summary>
    internal static void CheckOrigin(FlowDefinition flow, ResolvedMapping resolved, Drop drop, Guid submissionId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(drop);
        var where = $"{flow.SourcePath ?? flow.Name} (the stored rows of submission {submissionId:D})";
        if (!drop.Manifest.Flow.Equals(flow.Name, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{where}: the drop was prepared for flow '{drop.Manifest.Flow}', not '{flow.Name}'.");
        }

        var issues = Preflight.Check(resolved.Mapping, resolved.Schema, resolved.References, resolved.Context, drop.Manifest.DeclaredColumns());
        Preflight.ThrowIfFailed(issues, where);
        CheckFlowBindings(flow, drop.Manifest, where);
    }

    private async Task<PlanHeader> OpenDropAsync(
        FlowDefinition flow, ResolvedMapping resolved, IReadOnlyDictionary<string, string> parameters, Drop drop, bool gate, CancellationToken ct)
    {
        var where = flow.SourcePath ?? flow.Name;
        var manifest = drop.Manifest;
        CheckManifestAgainstFlow(flow, manifest, parameters, where);

        var issues = Preflight.Check(resolved.Mapping, resolved.Schema, resolved.References, resolved.Context, manifest.DeclaredColumns());
        Preflight.ThrowIfFailed(issues, where);
        CheckFlowBindings(flow, manifest, where);

        var payloadName = PayloadName(flow);
        var scopeKey = ScopeKey(parameters);
        var gatedSets = _ledger is null ? [] : (await _ledger.GatedCacheSetsAsync(ct).ConfigureAwait(false)).ToHashSet();
        var header = new PlanHeader
        {
            Flow = flow,
            Drop = drop,
            Origins = _ledger is null ? null : new SourceOrigins(_ledger, flow, resolved, drop),
            Mapping = resolved,
            Parameters = parameters,
            Issues = issues,
            PayloadName = payloadName,
            GatedCacheSets = gatedSets,
        };

        if (gate && _ledger is not null && flow.Change.UseSourceVersions && manifest.SourceVersions.Count > 0)
        {
            var watermarks = await _ledger.GetWatermarksAsync(flow.Id, scopeKey, ct).ConfigureAwait(false);
            var known = watermarks.ToDictionary(w => w.Table, w => w.Version, StringComparer.Ordinal);
            var advanced = manifest.SourceVersions.Where(kv => !known.TryGetValue(kv.Key, out var v) || v < kv.Value).Select(kv => kv.Key).ToList();

            // The source is only one of the four inputs. A cache, mapping or schema version that moved changes what
            // every record renders to, so the scope is re-rendered even when no source table advanced; skipping on
            // the source alone is how an estate silently keeps serving values the cache no longer holds.
            var contextHash = resolved.Context.Hash();
            var contextMoved = watermarks.Count == 0 || watermarks.Any(w => !string.Equals(w.ContextHash, contextHash, StringComparison.Ordinal));
            if (advanced.Count == 0 && contextMoved)
            {
                _logger.LogInformation(
                    "Tier 0: no source table advanced for scope {Scope}, but the render context moved to {Context}; planning the scope.",
                    scopeKey, contextHash);
            }

            if (advanced.Count == 0 && !contextMoved)
            {
                _logger.LogInformation("Tier 0: no source table advanced since the last run for scope {Scope}; skipping the whole run.", scopeKey);
                return header with
                {
                    SkippedWholeRun = true,
                    SkipReason = $"none of {manifest.SourceVersions.Count} source table(s) advanced since the last run",
                };
            }
        }

        return header;
    }

    /// <summary>
    /// Streams the plan entries of the header's drop (or of the given root partitions), rendering <paramref name="parallelism"/>
    /// batches at a time. Entries arrive in no particular order. The summary is updated as entries are produced.
    /// </summary>
    public IAsyncEnumerable<PlanEntry> EntriesAsync(
        PlanHeader header,
        IReadOnlyList<int>? partitions,
        int parallelism,
        PlanSummary summary,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        return EntriesOfAsync(header, DropInputsAsync(header, partitions, ct), parallelism, summary, ct);
    }

    /// <summary>The records of the header's drop (or of the given root partitions), as plan inputs.</summary>
    public async IAsyncEnumerable<PlanInput> DropInputsAsync(PlanHeader header, IReadOnlyList<int>? partitions, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        var drop = header.Drop ?? throw new DeliveryException("This plan has no drop to read: it plans rows of the source store.");
        await foreach (var record in _drops.ReadRecordsAsync(drop, partitions, ct).ConfigureAwait(false))
        {
            yield return PlanInput.FromDrop(record);
        }
    }

    /// <summary>
    /// Streams the plan entries of the given inputs, rendering <paramref name="parallelism"/> batches at a time: the one plan
    /// every source takes, a drop read from storage and rows of the source store alike. Entries arrive in no particular
    /// order. The summary is updated as entries are produced.
    /// </summary>
    public async IAsyncEnumerable<PlanEntry> EntriesOfAsync(
        PlanHeader header,
        IAsyncEnumerable<PlanInput> inputs,
        int parallelism,
        PlanSummary summary,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(summary);
        if (header.SkippedWholeRun)
        {
            yield break;
        }

        var workers = Math.Clamp(parallelism, 1, 256);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var input = Channel.CreateBounded<List<PlanInput>>(new BoundedChannelOptions(workers * 2) { SingleWriter = true, SingleReader = false });
        var output = Channel.CreateBounded<List<PlanEntry>>(new BoundedChannelOptions(workers * 2) { SingleWriter = false, SingleReader = true });

        var reader = Task.Run(async () =>
        {
            try
            {
                var batch = new List<PlanInput>(RenderBatch);
                await foreach (var record in inputs.WithCancellation(token).ConfigureAwait(false))
                {
                    batch.Add(record);
                    if (batch.Count == RenderBatch)
                    {
                        await input.Writer.WriteAsync(batch, token).ConfigureAwait(false);
                        batch = new List<PlanInput>(RenderBatch);
                    }
                }

                if (batch.Count > 0)
                {
                    await input.Writer.WriteAsync(batch, token).ConfigureAwait(false);
                }

                input.Writer.Complete();
            }
            catch (Exception ex)
            {
                input.Writer.TryComplete(ex);
                stop.Cancel();
            }
        }, CancellationToken.None);

        var renderers = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in input.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var entries = new List<PlanEntry>(batch.Count);
                    await PlanBatchAsync(header, batch, entries, token).ConfigureAwait(false);
                    foreach (var entry in entries)
                    {
                        summary.Add(entry);
                    }

                    await output.Writer.WriteAsync(entries, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
                stop.Cancel();
                throw;
            }
        }, CancellationToken.None)).ToList();

        var completion = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(renderers).ConfigureAwait(false);
                output.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var entries in output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var entry in entries)
                {
                    yield return entry;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(reader, completion).ConfigureAwait(false);
        }

        await reader.ConfigureAwait(false);
    }

    /// <summary>The whole plan collected in memory: for the CLI's check, tests and small drops.</summary>
    public async Task<DeliveryPlan> PlanAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        string dropLocation,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var header = await OpenAsync(flow, resolved, parameters, dropLocation, force, ct).ConfigureAwait(false);
        var summary = new PlanSummary();
        var entries = new List<PlanEntry>();
        await foreach (var entry in EntriesAsync(header, null, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        var issues = header.Issues;
        var declared = header.Drop!.Manifest.RecordCount;
        if (!header.SkippedWholeRun && declared > 0 && declared != summary.Records)
        {
            issues = [.. issues, RecordCountIssue(flow, declared, summary.Records)];
        }

        return new DeliveryPlan { Header = header with { Issues = issues }, Entries = entries, Summary = summary };
    }

    public static ValidationIssue RecordCountIssue(FlowDefinition flow, long declared, long found)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return ValidationIssue.Warning($"{flow.SourcePath ?? flow.Name}: the manifest declares {declared} record(s) but the drop holds {found}.");
    }

    /// <summary>The tier-0 scope key: one flow instance per distinct parameter set.</summary>
    public static string ScopeKey(IReadOnlyDictionary<string, string> parameters)
        => string.Join(";", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    public static string? PayloadName(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
        {
            return null;
        }

        return flow.Target.ProtocolOptions.Payload ?? (flow.Source.Payloads.Count == 1 ? flow.Source.Payloads.Keys.First() : null);
    }

    /// <summary>The drop an input's record was read from: the header's, or the one its replica origin names.</summary>
    private static async ValueTask<Drop> DropOfAsync(PlanHeader header, PlanInput input, CancellationToken ct)
    {
        if (input.Origin is not { } origin)
        {
            return header.Drop ?? throw new DeliveryException("A record read from a drop reached a plan that has no drop.");
        }

        var origins = header.Origins ?? throw new DeliveryException("Records of the replica reached a plan opened without the ledger.");
        return await origins.DropAsync(origin, ct).ConfigureAwait(false);
    }

    private async Task PlanBatchAsync(PlanHeader header, List<PlanInput> batch, List<PlanEntry> entries, CancellationToken ct)
    {
        var flow = header.Flow;
        var resolved = header.Mapping;
        var payloadName = header.PayloadName;
        var renderer = resolved.Renderer;
        var context = resolved.Context.Canonical();
        var gatedSets = header.GatedCacheSets;
        var ordered = flow.Source.LastModified is not null;
        var keyed = new List<(SourceRecord Record, Drop Drop, DeliveryKey? Key, string SourceKey, string? Label)>(batch.Count);
        foreach (var input in batch)
        {
            var record = input.Record;
            var from = await DropOfAsync(header, input, ct).ConfigureAwait(false);
            var key = renderer.DeriveKey(record.Row, out var values);
            keyed.Add((record, from, key, SourceKey.Display(resolved.Mapping.Dataset.System, values), renderer.Label(record.Row)));
        }

        var existing = _ledger is null
            ? new Dictionary<DeliveryKey, RecordState>()
            : await _ledger.GetRecordsAsync(flow.Id, keyed.Where(k => k.Key is not null).Select(k => k.Key!.Value), ct).ConfigureAwait(false);

        foreach (var (record, drop, key, sourceKey, label) in keyed)
        {
            ct.ThrowIfCancellationRequested();
            if (key is null)
            {
                entries.Add(new PlanEntry { Key = null, SourceKey = sourceKey, Label = label, Action = PlannedAction.Hold, Reason = "dataset key incomplete: every key column must be non-empty" });
                continue;
            }

            // The payload is declared by the drop the record was read from: a replan's records can come from many drops.
            var payload = payloadName is null ? null : drop.Manifest.Payloads.GetValueOrDefault(payloadName);
            var fileWatermark = payload is not null && flow.Change.PayloadDetect == ChangeDetection.LastModified;
            var state = existing.GetValueOrDefault(key.Value);
            var (source, sourceProblem) = ReadSourceVersion(flow, record.Row);
            var hasPayload = payload is not null;

            // What every entry says about the record and the version the drop carries, whatever is decided about it.
            var basis = new PlanEntry
            {
                Key = key,
                SourceKey = sourceKey,
                Label = label,
                TargetId = state?.TargetId,
                Existing = state,
                Action = PlannedAction.Hold,
                Reason = string.Empty,
                SourceFingerprint = source.Fingerprint,
                SourceModifiedUtc = source.ModifiedUtc,
            };

            // A cache change is tagged against the values this record was built from and nobody has approved it:
            // OSDU keeps the document it has. The gate is a set membership test, not a column on the record, so
            // holding back millions of records costs one small query per run. It sits ahead of the change tiers on
            // purpose: the render context moved with the cache version, so tier 1 would otherwise re-render and
            // send exactly the update being held back.
            if (state?.CacheSetId is { } cacheSet && gatedSets.Contains(cacheSet))
            {
                entries.Add(basis with
                {
                    Action = PlannedAction.Skip,
                    SkipTier = SkipTier.Approval,
                    Reason = "a cache change is tagged against this record and is waiting for approval",
                });
                continue;
            }

            // A record held, failed or deleted earlier stays where it is until an operator releases it or the source
            // moves past the version it was left at (design.md section 7.4). Without a version column, only a
            // release can unblock it.
            if (state is { Blocked: true } && ChangeDetector.StaysBlocked(state, source, ordered))
            {
                entries.Add(basis with
                {
                    Action = PlannedAction.Blocked,
                    Reason = $"{state.Status.ToString().ToLowerInvariant()} since {state.UpdatedUtc:u}: {state.LastError ?? "no reason recorded"}; release the record or change the source to plan it again",
                });
                continue;
            }

            if (sourceProblem is not null)
            {
                entries.Add(basis with { Reason = sourceProblem });
                continue;
            }

            // An older version than the ledger holds, delivered or queued: a replayed or late drop. Sending it would
            // take OSDU back in time, so it is skipped, and the intake records the skip against the record.
            if (source.ModifiedUtc is { } modified && ChangeDetector.NewestSourceModified(state) is { } newest && modified < newest)
            {
                var standing = state!.HasPendingWork && state.PendingSourceModifiedUtc == newest ? "queued" : "delivered";
                entries.Add(basis with
                {
                    Action = PlannedAction.Skip,
                    SkipTier = SkipTier.Stale,
                    Reason = $"the drop carries the row as last modified {Moment(modified)}, older than the version last modified {Moment(newest)} already {standing}; OSDU keeps the newer version",
                });
                continue;
            }

            string? payloadHash = null;
            DateTime? payloadModified = null;
            string? payloadLocation = null;
            int? chunkCount = null;
            if (payload is not null)
            {
                // A payload that names a location column says where each record's files are, rather than implying it
                // from the drop's own folders: that is what lets a submission point at files already on the lake
                // (design.md section 3.4). The node opens the location with its own identity when it delivers.
                if (payload.LocationColumn is { } locationColumn)
                {
                    payloadLocation = record.Row.GetString(locationColumn);
                    if (string.IsNullOrWhiteSpace(payloadLocation))
                    {
                        entries.Add(basis with { Reason = $"payload '{payloadName}' takes its location from column '{locationColumn}', which this row leaves empty; there is nowhere to read its files from" });
                        continue;
                    }
                }

                if (fileWatermark)
                {
                    // The chunk files are the payload's watermark: listing them is the only way to see a rewrite.
                    payloadLocation ??= _drops.PayloadLocation(drop, payloadName!, key.Value.Value);
                    var files = PayloadFiles.Of(await _drops.ListPayloadChunksAsync(payloadLocation, ct).ConfigureAwait(false));
                    if (files.Count == 0)
                    {
                        entries.Add(basis with { Reason = $"no payload chunk files under {payloadLocation}; the payload's watermark is its files, so there is nothing to compare or send" });
                        continue;
                    }

                    chunkCount = files.Count;
                    payloadModified = files.ModifiedUtc;
                    // A declared content hash stays the final check; without one the files' names, sizes and times are the payload's identity.
                    payloadHash = payload.HashColumn is { } hashColumn ? record.Row.GetString(hashColumn) : files.Signature;
                }
                else
                {
                    payloadHash = record.Row.GetString(payload.HashColumn!);
                }

                if (string.IsNullOrWhiteSpace(payloadHash))
                {
                    entries.Add(basis with { Reason = $"payload '{payloadName}' declares hash column '{payload.HashColumn}' but the row carries no value; no payload was prepared" });
                    continue;
                }
            }

            if (ChangeDetector.CanSkipWithoutRender(ChangeDetector.Expected(state), source, payloadHash, context, flow.Change))
            {
                entries.Add(basis with
                {
                    Action = PlannedAction.Skip,
                    SkipTier = SkipTier.Fingerprint,
                    Reason = state!.HasPendingWork
                        ? "source version, payload hash and render context equal the version already queued"
                        : "source version, payload hash and render context unchanged",
                    PayloadHash = payloadHash,
                    PayloadModifiedUtc = payloadModified,
                });
                continue;
            }

            var render = renderer.Render(record);
            if (render.IsHeld)
            {
                entries.Add(basis with
                {
                    TargetId = render.TargetId,
                    Reason = string.Join("; ", render.Holds),
                    Render = render,
                    PayloadHash = payloadHash,
                    PayloadModifiedUtc = payloadModified,
                });
                continue;
            }

            var decision = ChangeDetector.DecideWithQueue(state, render.MetadataHash, payloadHash, hasPayload, flow.Change);
            var carriedPayload = false;
            if (decision.DeliverPayload && payloadModified is { } filesModified && ChangeDetector.NewestPayloadModified(state) is { } newestPayload && filesModified < newestPayload)
            {
                var why = $"the payload files are last modified {Moment(filesModified)}, older than the payload last modified {Moment(newestPayload)} already delivered or queued";
                if (state is { HasPendingWork: true, PendingPayload: true } && state.PendingPayloadModifiedUtc == newestPayload)
                {
                    // The newer payload is still queued: the new document replaces the queue, but that payload goes with it.
                    carriedPayload = true;
                    payloadHash = state.PendingPayloadHash;
                    payloadModified = state.PendingPayloadModifiedUtc;
                    payloadLocation = state.PendingPayloadLocation;
                    chunkCount = null;
                    decision = decision with { Reason = $"{decision.Reason}; {why}, so the newer payload already queued is kept" };
                }
                else if (decision.DeliverMetadata)
                {
                    decision = decision with
                    {
                        Action = decision.Action == PlannedAction.Create ? PlannedAction.Create : PlannedAction.UpdateMetadata,
                        DeliverPayload = false,
                        Reason = $"{decision.Reason}; {why}, so only the metadata is sent",
                    };
                }
                else
                {
                    decision = new ChangeDecision(PlannedAction.Skip, SkipTier.Stale, false, false, $"{why}; OSDU keeps the newer payload");
                }
            }

            if (decision.Action == PlannedAction.Skip)
            {
                entries.Add(basis with
                {
                    TargetId = render.TargetId,
                    Action = PlannedAction.Skip,
                    SkipTier = decision.SkipTier,
                    Reason = decision.Reason,
                    Render = render,
                    PayloadHash = payloadHash,
                    PayloadModifiedUtc = payloadModified,
                });
                continue;
            }

            if (decision.DeliverPayload)
            {
                payloadLocation ??= _drops.PayloadLocation(drop, payloadName!, key.Value.Value);
                // The manifest can declare the chunk count per record, which spares a storage listing per record
                // here (the drain lists the chunks when it streams them anyway); without it the chunks are listed. A
                // payload carried over from the queue was prepared by another drop, so its count is listed too.
                chunkCount ??= carriedPayload ? null : DeclaredChunkCount(payload!, record.Row);
                if (chunkCount is null)
                {
                    var chunks = await _drops.ListPayloadChunksAsync(payloadLocation, ct).ConfigureAwait(false);
                    chunkCount = chunks.Count;
                }

                if (chunkCount == 0)
                {
                    entries.Add(basis with
                    {
                        TargetId = render.TargetId,
                        Reason = $"payload hash present but no chunk files under {payloadLocation}",
                        Render = render,
                        PayloadHash = payloadHash,
                        PayloadModifiedUtc = payloadModified,
                    });
                    continue;
                }
            }

            entries.Add(basis with
            {
                TargetId = render.TargetId,
                Action = decision.Action,
                SkipTier = decision.SkipTier,
                Reason = decision.Reason,
                Render = render,
                PayloadHash = payloadHash,
                PayloadModifiedUtc = payloadModified,
                PayloadLocation = decision.DeliverPayload ? payloadLocation : null,
                ChunkCount = decision.DeliverPayload ? chunkCount : null,
                DeliverMetadata = decision.DeliverMetadata,
                DeliverPayload = decision.DeliverPayload,
            });
        }
    }

    /// <summary>The version the row carries: its last-modified moment, or its fingerprint, as the flow declares; or why it cannot be read.</summary>
    private static (SourceVersion Version, string? Problem) ReadSourceVersion(FlowDefinition flow, SourceRow row)
    {
        if (flow.Source.LastModified is { } column)
        {
            return LastModifiedColumn.TryRead(row, column, out var modified, out var problem)
                ? (SourceVersion.At(modified), null)
                : (default, problem);
        }

        return (SourceVersion.Of(flow.Source.Fingerprint is { } fingerprint ? row.GetString(fingerprint) : null), null);
    }

    private static string Moment(DateTime utc)
        => Json.CanonicalJson.FormatDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    private static int? DeclaredChunkCount(ManifestPayload payload, SourceRow row)
    {
        if (payload.ChunkCountColumn is not { } column)
        {
            return null;
        }

        return row.Get(column) switch
        {
            long l when l >= 0 => (int)Math.Min(l, int.MaxValue),
            // A count is a whole number: a fraction, NaN or Infinity declares no count rather than a truncated one.
            double d when d >= 0 && double.IsFinite(d) && Math.Floor(d) == d => (int)Math.Min(d, int.MaxValue),
            decimal m when m >= 0 && decimal.Truncate(m) == m => (int)Math.Min(m, int.MaxValue),
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 => (int)Math.Min(parsed, int.MaxValue),
            _ => null,
        };
    }

    private static void CheckManifestAgainstFlow(FlowDefinition flow, DropManifest manifest, IReadOnlyDictionary<string, string> parameters, string where)
    {
        if (!manifest.Flow.Equals(flow.Name, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{where}: the drop was prepared for flow '{manifest.Flow}', not '{flow.Name}'.");
        }

        if (!manifest.Mapping.Equals(flow.Render.Mapping, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{where}: the drop was prepared for mapping '{manifest.Mapping}' but the flow pins '{flow.Render.Mapping}'. Re-prepare the drop or promote the flow deliberately.");
        }

        foreach (var (name, value) in manifest.Parameters)
        {
            if (parameters.TryGetValue(name, out var supplied) && !supplied.Equals(value, StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{where}: parameter '{name}' is '{supplied}' for this run but the drop was prepared with '{value}'.");
            }
        }
    }

    private static void CheckFlowBindings(FlowDefinition flow, DropManifest manifest, string where)
    {
        var columns = manifest.DeclaredColumns();
        var root = columns[DropManifest.RootScope];
        if (flow.Source.Fingerprint is { } fp && !root.Contains(fp))
        {
            throw new FlowValidationException($"{where}: source.fingerprint names column '{fp}', which the drop's root scope does not declare.");
        }

        if (flow.Source.LastModified is { } lastModified)
        {
            var declared = manifest.Root.Columns.FirstOrDefault(c => c.Name.Equals(lastModified, StringComparison.OrdinalIgnoreCase))
                ?? throw new FlowValidationException($"{where}: source.lastModified names column '{lastModified}', which the drop's root scope does not declare.");
            if (!declared.Type.Equals("timestamp", StringComparison.OrdinalIgnoreCase) && !declared.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{where}: source.lastModified column '{lastModified}' is declared as {declared.Type}; it must be a timestamp, or a string holding RFC 3339 text.");
            }
        }

        var payloadName = PayloadName(flow);
        if (payloadName is not null)
        {
            if (!manifest.Payloads.TryGetValue(payloadName, out var payload))
            {
                throw new FlowValidationException($"{where}: the protocol streams payload '{payloadName}' but the manifest declares no such payload.");
            }

            if (payload.HashColumn is { } hashColumn)
            {
                if (!root.Contains(hashColumn))
                {
                    throw new FlowValidationException($"{where}: payload '{payloadName}' hashColumn '{hashColumn}' is not declared in the drop's root scope.");
                }
            }
            else if (flow.Change.PayloadDetect != ChangeDetection.LastModified)
            {
                throw new FlowValidationException(
                    $"{where}: payload '{payloadName}' declares no hashColumn. The flow decides payload changes by content hash, so the drop must carry one; "
                    + "set change.payloadDetect: lastModified to take the chunk files' modified times as the watermark instead.");
            }

            if (payload.ChunkCountColumn is { } chunkColumn && !root.Contains(chunkColumn))
            {
                throw new FlowValidationException($"{where}: payload '{payloadName}' chunkCountColumn '{chunkColumn}' is not declared in the drop's root scope.");
            }

            if (payload.LocationColumn is { } locationColumn && !root.Contains(locationColumn))
            {
                throw new FlowValidationException($"{where}: payload '{payloadName}' locationColumn '{locationColumn}' is not declared in the drop's root scope.");
            }
        }

        foreach (var (name, _) in flow.Source.Scopes)
        {
            if (!manifest.Scopes.ContainsKey(name))
            {
                throw new FlowValidationException($"{where}: source.scopes declares '{name}', which the manifest does not declare.");
            }
        }
    }
}

public static class PlanFormatting
{
    public static string Describe(PlanEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var action = e.Action switch
        {
            PlannedAction.Skip => e.SkipTier switch
            {
                SkipTier.Fingerprint => "skip (tier 1)",
                SkipTier.Approval => "skip (awaiting approval)",
                SkipTier.Stale => "skip (stale)",
                _ => "skip (tier 2)",
            },
            PlannedAction.Create => "create",
            PlannedAction.UpdateMetadata => "update metadata",
            PlannedAction.UpdatePayload => "update payload",
            PlannedAction.UpdateBoth => "update metadata+payload",
            PlannedAction.Hold => "hold",
            PlannedAction.Blocked => "blocked",
            _ => e.Action.ToString().ToLowerInvariant(),
        };
        var chunks = e.ChunkCount is { } c ? $", {c.ToString(CultureInfo.InvariantCulture)} chunk(s)" : string.Empty;
        var label = e.Label is null ? string.Empty : $" [{e.Label}]";
        return $"{action,-24} {e.SourceKey}{label}  {e.TargetId ?? "-"}  ({e.Reason}{chunks})";
    }
}
