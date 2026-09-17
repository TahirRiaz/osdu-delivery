using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Planning;

/// <summary>One record's place in a plan.</summary>
public sealed record PlanEntry
{
    public DeliveryKey? Key { get; init; }

    public required string SourceKey { get; init; }

    /// <summary>The record's key tuple as the source read it, a JSON array of strings in key order.</summary>
    public string? SourceKeyJson { get; init; }

    public string? Label { get; init; }

    public string? TargetId { get; init; }

    public required PlannedAction Action { get; init; }

    public SkipTier SkipTier { get; init; }

    public required string Reason { get; init; }

    public RenderResult? Render { get; init; }

    /// <summary>The ingestion fingerprint of the rows the record was read from.</summary>
    public string? SourceFingerprint { get; init; }

    /// <summary>When the record row last changed in business terms, when the flow declares <c>source.lastModified</c>.</summary>
    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The file, row and update time the record row carries, which every attempt of it is traced by.</summary>
    public SourceOrigin Origin { get; init; }

    public string? PayloadHash { get; init; }

    /// <summary>The newest modified time among the payload's files, when the flow takes them as the payload watermark.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    /// <summary>Where the payload's files are listed from: the folder and the pattern, as one text.</summary>
    public string? PayloadLocation { get; init; }

    public int? ChunkCount { get; init; }

    public RecordState? Existing { get; init; }

    public bool DeliverMetadata { get; init; }

    public bool DeliverPayload { get; init; }

    public bool IsDelivery => DeliverMetadata || DeliverPayload;
}

/// <summary>What the planner established before reading a single record: the opened source, the checks, the tier-0 decision.</summary>
public sealed record PlanHeader
{
    public required FlowDefinition Flow { get; init; }

    /// <summary>The opened read: its window, the tables' columns, the key columns and what the selection could not find.</summary>
    public required SourceHeader Source { get; init; }

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

    /// <summary>The payload set the protocol streams, or null for record-only protocols and for routes that send parts.</summary>
    public string? PayloadName { get; init; }

    /// <summary>The payload sets a route that sends parts reads, in the order it sends them; null for every other route.</summary>
    public IReadOnlyList<PayloadPart>? Parts { get; init; }

    /// <summary>How many key slices this plan is cut into; 1 when it runs on one node.</summary>
    public int Slices { get; init; } = 1;
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

    /// <summary>Records skipped because the source carries an older version than the ledger already holds.</summary>
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
/// volumes the engine is built for, consume <see cref="Planner.EntriesAsync"/> instead and keep only the summary.</summary>
public sealed record DeliveryPlan
{
    public required PlanHeader Header { get; init; }

    public required IReadOnlyList<PlanEntry> Entries { get; init; }

    public required PlanSummary Summary { get; init; }

    public FlowDefinition Flow => Header.Flow;

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
/// Reads a flow's ingestion tables against the ledger and decides per record what would change (docs/stage4-design.md
/// section 2). Works without a ledger, in which case every record is a create. Records stream from the source in key
/// order through a bounded channel to a set of parallel renderers, so a plan of any size runs in bounded memory and
/// uses every core (design.md section 16.1).
/// </summary>
public sealed class Planner
{
    /// <summary>Records per render batch: one ledger lookup, one unit of parallelism.</summary>
    public const int RenderBatch = 200;

    private readonly IIngestionSource _source;
    private readonly IPayloadFiles _payloads;
    private readonly ILedger? _ledger;
    private readonly ILogger<Planner> _logger;

    public Planner(IIngestionSource source, IPayloadFiles payloads, ILedger? ledger, ILogger<Planner> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _payloads = payloads;
        _ledger = ledger;
        _logger = logger;
    }

    /// <summary>The opened source, for the callers that read its slices or reopen a submission's window.</summary>
    public IIngestionSource Source => _source;

    /// <summary>
    /// Opens the read, checks the flow and the mapping against the tables, and applies the tier-0 gate.
    /// </summary>
    /// <param name="flow">The flow being planned.</param>
    /// <param name="resolved">The pinned render inputs (mapping, template and cache version).</param>
    /// <param name="parameters">The resolved flow parameter values.</param>
    /// <param name="selection">Which records to read: an incremental window, everything, named keys, or a submission's.</param>
    /// <param name="gate">Apply the tier-0 whole-run gate; false plans the selection whatever the window holds.</param>
    /// <param name="stored">The window a submission already recorded, so every run of it reads the same rows.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<PlanHeader> OpenAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        SourceSelection selection,
        bool gate = true,
        SourceWindow? stored = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(selection);
        var where = KeyPaths.Where(flow);
        var source = await _source.OpenAsync(selection, stored, ct).ConfigureAwait(false);

        var issues = Preflight.Check(resolved.Mapping, resolved.Schema, resolved.References, resolved.Context, source.Columns);
        Preflight.ThrowIfFailed(issues, where);
        SourceBindings.Check(flow, resolved.Mapping, source, where);

        var gatedSets = _ledger is null ? [] : (await _ledger.GatedCacheSetsAsync(ct).ConfigureAwait(false)).ToHashSet();
        var header = new PlanHeader
        {
            Flow = flow,
            Source = source,
            Mapping = resolved,
            Parameters = parameters,
            Issues = issues,
            PayloadName = PayloadName(flow),
            Parts = PayloadParts.Of(flow),
            GatedCacheSets = gatedSets,
        };

        foreach (var missing in source.MissingKeys)
        {
            _logger.LogWarning("The record table {Object} holds no record with key {Key}; it cannot be planned.", flow.Source.Record.Object, missing);
        }

        foreach (var outside in source.OutOfScopeKeys)
        {
            _logger.LogWarning("Record {Key} is outside this run's scope ({Scope}); the run of its own scope plans it.", outside, ScopeKey(parameters));
        }

        // Tier 0: nothing changed in the window and no record is waiting to be planned again, so the whole run is
        // skipped without reading a row. A moved render context is handled by the cache rollout and an explicit
        // replan, not by re-rendering the scope on every run.
        if (gate && selection.CoversScope && flow.Change.UseSourceVersions && !source.HasChanges)
        {
            var waiting = _ledger is not null && (await _ledger.ListPlanRequestedAsync(flow.Id, null, 1, ct).ConfigureAwait(false)).Count > 0;
            if (!waiting)
            {
                _logger.LogInformation(
                    "Tier 0: no row of {Object} changed in the window for scope {Scope}, and no record waits to be planned again; skipping the whole run.",
                    flow.Source.Record.Object, ScopeKey(parameters));
                return header with
                {
                    SkippedWholeRun = true,
                    SkipReason = source.Window.LowerUtc is { } lower
                        ? $"no row changed between {Moment(lower)} and {Moment(source.Window.UpperUtc)}"
                        : $"no row changed up to {Moment(source.Window.UpperUtc)}",
                };
            }

            _logger.LogInformation("Tier 0: no row changed in the window, but records wait to be planned again; planning those.");
        }

        return header;
    }

    /// <summary>The key bounds that cut this read into <paramref name="slices"/> contiguous ranges.</summary>
    public Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(PlanHeader header, int slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        return _source.SliceBoundsAsync(header.Source, slices, ct);
    }

    /// <summary>
    /// Streams the plan entries of the opened read (or of one key range of it), rendering <paramref name="parallelism"/>
    /// batches at a time. Entries arrive in no particular order. The summary is updated as entries are produced.
    /// </summary>
    public IAsyncEnumerable<PlanEntry> EntriesAsync(
        PlanHeader header,
        KeyRange? range,
        int parallelism,
        PlanSummary summary,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        return EntriesOfAsync(header, _source.ReadAsync(header.Source, range, ct), parallelism, summary, ct);
    }

    /// <summary>
    /// Streams the plan entries of the given records, rendering <paramref name="parallelism"/> batches at a time: the one
    /// plan every source takes, the ingestion tables and a test's rows alike. Entries arrive in no particular order.
    /// </summary>
    public async IAsyncEnumerable<PlanEntry> EntriesOfAsync(
        PlanHeader header,
        IAsyncEnumerable<SourceRecord> records,
        int parallelism,
        PlanSummary summary,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(summary);
        if (header.SkippedWholeRun)
        {
            yield break;
        }

        var workers = Math.Clamp(parallelism, 1, 256);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var input = Channel.CreateBounded<List<SourceRecord>>(new BoundedChannelOptions(workers * 2) { SingleWriter = true, SingleReader = false });
        var output = Channel.CreateBounded<List<PlanEntry>>(new BoundedChannelOptions(workers * 2) { SingleWriter = false, SingleReader = true });

        var reader = Task.Run(async () =>
        {
            try
            {
                var batch = new List<SourceRecord>(RenderBatch);
                await foreach (var record in records.WithCancellation(token).ConfigureAwait(false))
                {
                    batch.Add(record);
                    if (batch.Count == RenderBatch)
                    {
                        await input.Writer.WriteAsync(batch, token).ConfigureAwait(false);
                        batch = new List<SourceRecord>(RenderBatch);
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

    /// <summary>The whole plan collected in memory: for the CLI's check, the tests and small scopes.</summary>
    public async Task<DeliveryPlan> PlanAsync(
        FlowDefinition flow,
        ResolvedMapping resolved,
        IReadOnlyDictionary<string, string> parameters,
        SourceSelection selection,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var header = await OpenAsync(flow, resolved, parameters, selection, gate: !force, stored: null, ct).ConfigureAwait(false);
        var summary = new PlanSummary();
        var entries = new List<PlanEntry>();
        await foreach (var entry in EntriesAsync(header, null, flow.Reliability.EffectiveRenderParallelism, summary, ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        return new DeliveryPlan { Header = header, Entries = entries, Summary = summary };
    }

    /// <summary>The tier-0 scope key: one flow instance per distinct parameter set.</summary>
    public static string ScopeKey(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return string.Join(";", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
    }

    /// <summary>
    /// The payload set the flow's protocol streams, or null when it streams none or sends its payload in parts
    /// (<see cref="PayloadParts.Of"/>).
    /// </summary>
    public static string? PayloadName(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!DeliveryProtocols.CarriesPayload(flow.Target.Protocol) || PayloadParts.Composed(flow.Target.Protocol))
        {
            return null;
        }

        return flow.Target.ProtocolOptions.Payload ?? (flow.Source.Payloads.Count == 1 ? flow.Source.Payloads.Keys.First() : null);
    }

    private async Task PlanBatchAsync(PlanHeader header, List<SourceRecord> batch, List<PlanEntry> entries, CancellationToken ct)
    {
        var flow = header.Flow;
        var resolved = header.Mapping;
        var payloadName = header.PayloadName;
        var payload = payloadName is null ? null : flow.Source.Payloads.GetValueOrDefault(payloadName);
        var parts = header.Parts;
        var roles = PayloadParts.Roles(flow);
        var renderer = resolved.Renderer;
        var context = resolved.Context.Canonical();
        var gatedSets = header.GatedCacheSets;
        var ordered = flow.Source.LastModified is not null;
        var fileWatermark = (payload is not null || parts is not null) && flow.Change.PayloadDetect == ChangeDetection.LastModified;
        var keyed = new List<(SourceRecord Record, DeliveryKey? Key, string SourceKey, string? Label)>(batch.Count);
        foreach (var record in batch)
        {
            var key = renderer.DeriveKey(record.Row, out var values);
            keyed.Add((record, key, SourceKey.Display(resolved.Mapping.Dataset.System, values), renderer.Label(record.Row)));
        }

        var existing = _ledger is null
            ? new Dictionary<DeliveryKey, RecordState>()
            : await _ledger.GetRecordsAsync(flow.Id, keyed.Where(k => k.Key is not null).Select(k => k.Key!.Value), ct).ConfigureAwait(false);

        foreach (var (record, key, sourceKey, label) in keyed)
        {
            ct.ThrowIfCancellationRequested();
            if (key is null)
            {
                entries.Add(new PlanEntry
                {
                    Key = null,
                    SourceKey = sourceKey,
                    SourceKeyJson = record.SourceKeyJson,
                    Label = label,
                    Origin = record.Origin,
                    Action = PlannedAction.Hold,
                    Reason = "dataset key incomplete: every key column must be non-empty",
                });
                continue;
            }

            var state = existing.GetValueOrDefault(key.Value);
            var (source, sourceProblem) = ReadSourceVersion(flow, record);
            var hasPayload = payload is not null || parts is not null;

            // What every entry says about the record and the version the source carries, whatever is decided about it.
            var basis = new PlanEntry
            {
                Key = key,
                SourceKey = sourceKey,
                SourceKeyJson = record.SourceKeyJson,
                Label = label,
                TargetId = state?.TargetId,
                Existing = state,
                Action = PlannedAction.Hold,
                Reason = string.Empty,
                Origin = record.Origin,
                SourceFingerprint = source.Fingerprint,
                SourceModifiedUtc = source.ModifiedUtc,
            };

            // A row the source read but cannot deliver as it stands (a child dataset over its ceiling).
            if (record.Hold is { } refusal)
            {
                entries.Add(basis with { Reason = refusal });
                continue;
            }

            // A soft-deleted record row is not delivered: what OSDU holds is removed deliberately, through a removal
            // that records itself, never as a side effect of a row disappearing from a table.
            if (record.DeletedUtc is { } deleted)
            {
                entries.Add(basis with
                {
                    Reason = $"the ingestion table marked the record row deleted at {Moment(deleted)}; a deleted row is never delivered. "
                        + "Remove the record from OSDU deliberately, or restore the row in the source.",
                });
                continue;
            }

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
            // moves past the version it was left at (design.md section 7.4).
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

            // An older version than the ledger holds, delivered or queued: a late row, or a row re-landed behind one
            // already sent. Sending it would take OSDU back in time, so it is skipped and the intake records the skip.
            if (source.ModifiedUtc is { } modified && ChangeDetector.NewestSourceModified(state) is { } newest && modified < newest)
            {
                var standing = state!.HasPendingWork && state.PendingSourceModifiedUtc == newest ? "queued" : "delivered";
                entries.Add(basis with
                {
                    Action = PlannedAction.Skip,
                    SkipTier = SkipTier.Stale,
                    Reason = $"the source carries the row as last modified {Moment(modified)}, older than the version last modified {Moment(newest)} already {standing}; OSDU keeps the newer version",
                });
                continue;
            }

            string? payloadHash = null;
            DateTime? payloadModified = null;
            PayloadLocation? payloadLocation = null;
            int? chunkCount = null;
            List<ResolvedPart>? resolvedParts = null;
            string? carriedComposite = null;
            if (parts is not null)
            {
                var resolution = await ResolvePartsAsync(flow, header.Parameters, record.Row, parts, fileWatermark, ct).ConfigureAwait(false);
                if (resolution.Refusal is { } problem)
                {
                    entries.Add(basis with { Reason = problem });
                    continue;
                }

                resolvedParts = resolution.Parts;
                payloadHash = CompositePayload.HashOf(resolution.Parts.Select(r => r.Part));
                payloadModified = resolution.Parts.Select(r => r.Modified).Where(m => m is not null).Max();
            }
            else if (payload is not null)
            {
                var resolution = PayloadLocations.Resolve(flow, header.Parameters, record.Row, payloadName!);
                if (resolution.Refusal is { } problem)
                {
                    entries.Add(basis with { Reason = problem });
                    continue;
                }

                payloadLocation = resolution.Location;
                if (fileWatermark)
                {
                    // The files are the payload's watermark: listing them is the only way to see a rewrite.
                    var files = PayloadFiles.Of(await _payloads.ListAsync(payloadLocation!.Value.Folder, payloadLocation.Value.Pattern, ct).ConfigureAwait(false));
                    if (files.Count == 0)
                    {
                        entries.Add(basis with { Reason = $"no payload files under {payloadLocation}; the payload's watermark is its files, so there is nothing to compare or send" });
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
                    entries.Add(basis with { Reason = $"payload '{payloadName}' takes its content hash from column '{payload.HashColumn}', which this row leaves empty" });
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
                    if (parts is not null)
                    {
                        carriedComposite = state.PendingPayloadLocation;
                    }
                    else
                    {
                        payloadLocation = state.PendingPayloadLocation is { } stored ? PayloadLocation.Parse(stored) : payloadLocation;
                    }

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

            string? payloadText = payloadLocation?.ToString();
            if (decision.DeliverPayload && parts is not null)
            {
                // Each part's files are counted the way a single payload's are; the parts are listed with the parts the
                // delivery sends whatever their hashes say, and that list is what the ledger keeps as the payload.
                string? problem = null;
                if (carriedComposite is not null)
                {
                    payloadText = carriedComposite;
                }
                else
                {
                    (chunkCount, problem) = await CountPartsAsync(flow, record.Row, resolvedParts!, ct).ConfigureAwait(false);
                    payloadText = problem is null
                        ? new CompositePayload(resolvedParts!.Select(r => r.Part).ToList(), PayloadParts.Forced(state?.PayloadHash, flow.Change, roles)).Encode()
                        : null;
                    problem ??= CompositePayload.TooLong(payloadText!);
                }

                if (problem is not null)
                {
                    entries.Add(basis with
                    {
                        TargetId = render.TargetId,
                        Reason = problem,
                        Render = render,
                        PayloadHash = payloadHash,
                        PayloadModifiedUtc = payloadModified,
                    });
                    continue;
                }
            }
            else if (decision.DeliverPayload)
            {
                // The record row can declare how many files the payload has, which spares a storage listing per
                // record here (the drain lists them when it streams them anyway); without it they are listed. A
                // payload carried over from the queue was prepared by an earlier read, so its count is listed too.
                chunkCount ??= carriedPayload ? null : DeclaredChunkCount(payload!, record.Row);
                if (chunkCount is null)
                {
                    var files = await _payloads.ListAsync(payloadLocation!.Value.Folder, payloadLocation.Value.Pattern, ct).ConfigureAwait(false);
                    chunkCount = files.Count;
                }

                if (chunkCount == 0)
                {
                    entries.Add(basis with
                    {
                        TargetId = render.TargetId,
                        Reason = $"payload hash present but no files under {payloadLocation}",
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
                PayloadLocation = decision.DeliverPayload ? payloadText : null,
                ChunkCount = decision.DeliverPayload ? chunkCount : null,
                DeliverMetadata = decision.DeliverMetadata,
                DeliverPayload = decision.DeliverPayload,
            });
        }
    }

    /// <summary>
    /// The version the record carries: the ingestion fingerprint the source computed, completed with the business
    /// version when the flow declares <c>source.lastModified</c>; or why that column cannot be read.
    /// </summary>
    private static (SourceVersion Version, string? Problem) ReadSourceVersion(FlowDefinition flow, SourceRecord record)
    {
        var fingerprint = record.Version.Fingerprint;
        if (flow.Source.LastModified is not { } column)
        {
            return (SourceVersion.Of(fingerprint), null);
        }

        return LastModifiedColumn.TryRead(record.Row, column, out var modified, out var problem)
            ? (new SourceVersion(fingerprint, modified), null)
            : (default, problem);
    }

    private static string Moment(DateTime utc)
        => Json.CanonicalJson.FormatDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    /// <summary>One part of a record's payload as the plan resolved it, with the modified time of its files when they were listed.</summary>
    private sealed record ResolvedPart(CompositePayloadPart Part, PayloadPart Declared, DateTime? Modified);

    /// <summary>
    /// Each part's location and content hash, from the record's row as a single payload's are: the location column under
    /// the part's root, and the hash column (or, when the flow takes the files' modified times, the files themselves). An
    /// optional part whose row names no folder, or whose folder holds no files, is kept as a part without files; any other
    /// part that cannot say where its files are, or what they hold, holds the record.
    /// </summary>
    private async Task<(List<ResolvedPart> Parts, string? Refusal)> ResolvePartsAsync(
        FlowDefinition flow, IReadOnlyDictionary<string, string> parameters, SourceRow row, IReadOnlyList<PayloadPart> parts, bool fileWatermark, CancellationToken ct)
    {
        var resolved = new List<ResolvedPart>(parts.Count);
        foreach (var part in parts)
        {
            var declared = flow.Source.Payloads[part.Payload];
            if (part.Optional && declared.LocationColumn is { } column && string.IsNullOrWhiteSpace(row.GetString(column)))
            {
                resolved.Add(new ResolvedPart(new CompositePayloadPart(part.Role, part.Payload, null, CompositePayload.NoFiles), part, null));
                continue;
            }

            var resolution = PayloadLocations.Resolve(flow, parameters, row, part.Payload);
            if (resolution.Refusal is { } refusal)
            {
                return (resolved, refusal);
            }

            var location = resolution.Location!.Value;
            string? hash;
            DateTime? modified = null;
            if (fileWatermark)
            {
                var files = PayloadFiles.Of(await _payloads.ListAsync(location.Folder, location.Pattern, ct).ConfigureAwait(false));
                if (files.Count == 0)
                {
                    if (part.Optional)
                    {
                        resolved.Add(new ResolvedPart(new CompositePayloadPart(part.Role, part.Payload, null, CompositePayload.NoFiles), part, null));
                        continue;
                    }

                    return (resolved, $"no payload files under {location} for payload '{part.Payload}'; the payload's watermark is its files, so there is nothing to compare or send");
                }

                modified = files.ModifiedUtc;
                hash = declared.HashColumn is { } hashColumn ? row.GetString(hashColumn) : files.Signature;
            }
            else
            {
                hash = row.GetString(declared.HashColumn!);
            }

            if (string.IsNullOrWhiteSpace(hash))
            {
                return (resolved, $"payload '{part.Payload}' takes its content hash from column '{declared.HashColumn}', which this row leaves empty");
            }

            resolved.Add(new ResolvedPart(new CompositePayloadPart(part.Role, part.Payload, location.ToString(), hash), part, modified));
        }

        return (resolved, null);
    }

    /// <summary>
    /// How many files the parts of a record hold together, each counted from its declared count column or listed, and
    /// why the record is held when a part with a folder holds no file.
    /// </summary>
    private async Task<(int? Count, string? Problem)> CountPartsAsync(FlowDefinition flow, SourceRow row, List<ResolvedPart> parts, CancellationToken ct)
    {
        var total = 0;
        foreach (var resolved in parts)
        {
            if (resolved.Part.Location is not { } text)
            {
                continue;
            }

            var count = DeclaredChunkCount(flow.Source.Payloads[resolved.Part.Payload], row);
            if (count is null)
            {
                var location = PayloadLocation.Parse(text);
                count = (await _payloads.ListAsync(location.Folder, location.Pattern, ct).ConfigureAwait(false)).Count;
            }

            if (count == 0)
            {
                return (null, $"payload '{resolved.Part.Payload}' has a content hash but no files under {text}");
            }

            total += count.Value;
        }

        return (total, null);
    }

    private static int? DeclaredChunkCount(FlowPayload payload, SourceRow row)
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
        var chunks = e.ChunkCount is { } c ? $", {c.ToString(CultureInfo.InvariantCulture)} file(s)" : string.Empty;
        var label = e.Label is null ? string.Empty : $" [{e.Label}]";
        return $"{action,-24} {e.SourceKey}{label}  {e.TargetId ?? "-"}  ({e.Reason}{chunks})";
    }
}
