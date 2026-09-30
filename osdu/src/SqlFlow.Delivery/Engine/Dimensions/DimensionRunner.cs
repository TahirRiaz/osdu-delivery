using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>One kind a dimension's pattern matched in the partition, with its records and the template it was read against.</summary>
public sealed record DimensionKind(string Kind, long Records, string? Template);

/// <summary>What a build changed of a dimension: its values that arrived, left or came back, and its keys that arrived, left, moved or came back.</summary>
public sealed record DimensionBuildChanges(
    long ValuesAdded, long ValuesRemoved, long ValuesRestored, long KeysAdded, long KeysRemoved, long KeysMoved, long KeysRestored)
{
    public static DimensionBuildChanges None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>The changes as the ledger counts them, where a value is a member and a key an original.</summary>
    public static DimensionBuildChanges Of(DimensionChangeCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new(
            counts.MembersAdded, counts.MembersRemoved, counts.MembersRestored, counts.OriginalsAdded, counts.OriginalsRemoved, counts.OriginalsMoved,
            counts.OriginalsRestored);
    }
}

/// <summary>
/// One dimension as a build left it: the values and keys it holds, the keys of no value and those no query can carry, the
/// keys a label was read for, what it changed, how many requests it made, and why it failed when it did.
/// </summary>
public sealed record DimensionBuildSummary(
    string Dimension,
    string Status,
    long? BuildId,
    long Values,
    long Keys,
    long LeftOut,
    long Unfilterable,
    long Labelled,
    DimensionBuildChanges Changes,
    int Requests,
    string? AggregateBy,
    string? Error,
    IReadOnlyList<string> Notes);

/// <summary>
/// The <c>result</c> of a build run of a dimension flow: every dimension the run selected, built, failed or skipped. The whole
/// of every build is in the ledger; the run's own result stays small enough for the run list to show it.
/// </summary>
public sealed record DimensionBuildOutcome(string Operation, string Flow, string Partition, int Built, int Failed, int Skipped, IReadOnlyList<DimensionBuildSummary> Dimensions)
{
    /// <summary>The most notes a dimension's summary carries in the run's result; every note is on its build in the ledger.</summary>
    public const int MaxNotes = 5;

    /// <summary>The keys the run's builds hold, which the run list shows beside the run as the work it did.</summary>
    public long RowsLoaded => Dimensions.Sum(d => d.Keys);

    /// <summary>What the run came to, as a run's error states it when a build failed it.</summary>
    public string Describe()
    {
        var failed = Dimensions.Where(d => d.Status == DimensionRunStatus.Failed).Select(d => $"{d.Dimension} ({d.Error})").Take(5).ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"dimension flow '{Flow}' in partition '{Partition}': {Built} dimension(s) built, {Failed} failed{(failed.Count > 0 ? ": " + string.Join("; ", failed) : string.Empty)}{(Skipped > 0 ? $", {Skipped} not built in this partition" : string.Empty)}.");
    }
}

/// <summary>A build run in which a dimension failed: it carries the outcome, so the run still reports every dimension.</summary>
public sealed class DimensionBuildsFailedException : DeliveryException
{
    public DimensionBuildsFailedException(DimensionBuildOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public DimensionBuildOutcome Outcome { get; }
}

/// <summary>What a plan found of one dimension: the field it would read, the kinds and records it would read them from, and what stops it.</summary>
public sealed record DimensionPlanSummary(
    string Dimension, string Kind, string? Query, string Path, string? AggregateBy, bool? Repeats, long? Records, IReadOnlyList<DimensionKind> Kinds,
    IReadOnlyList<string> Problems, string? Skipped);

/// <summary>The <c>result</c> of a plan run of a dimension flow: every dimension selected, checked and counted, nothing read or kept.</summary>
public sealed record DimensionPlanOutcome(string Operation, string Flow, string Partition, IReadOnlyList<DimensionPlanSummary> Dimensions)
{
    /// <summary>The dimensions the plan checked, which the run list shows beside the run.</summary>
    public long RowsLoaded => Dimensions.Count;
}

/// <summary>
/// Builds a dimension flow's dimensions in one partition (docs/dimension-plan.md): for each dimension, settles how the index
/// stores its field (from the saved template of every kind its pattern matches, or the indexer's mapping of the record's own
/// properties), reads every distinct value through the search's aggregation paged by value ranges, cleans the values into
/// members, gives each member its search filter and, where asked, its exact record count, and writes the build in one
/// transaction. Dimensions build as many at once as the flow's concurrency allows, and one that fails leaves the others
/// building. The plan settles the fields and counts the records, reading no value and keeping nothing.
/// </summary>
public sealed class DimensionRunner
{
    /// <summary>The most kinds one dimension's pattern is read over.</summary>
    private const int MaxKinds = 10_000;

    private static readonly JsonSerializerOptions StepJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly EngineContext _context;
    private readonly DimensionFlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ILogger _log;
    private readonly HttpMessageHandler? _transport;
    private readonly bool _allowLoopback;

    /// <param name="context">The run's engine context.</param>
    /// <param name="flow">The flow bound to the partition the run builds in (or a flow whose partition is its header's).</param>
    /// <param name="values">The flow's parameter values, resolved.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="transport">Replaces the built transport (null builds the configured one); it exists for tests.</param>
    /// <param name="allowLoopback">Lets the URL guard accept a loopback endpoint; it exists for tests.</param>
    public DimensionRunner(
        EngineContext context, DimensionFlowDefinition flow, IReadOnlyDictionary<string, string> values, ILogger log,
        HttpMessageHandler? transport = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(log);
        _context = context;
        _flow = flow;
        _values = values;
        _log = log;
        _transport = transport;
        _allowLoopback = allowLoopback || EngineContext.LoopbackAllowed;
    }

    private DateTime Now => _context.Time.GetUtcNow().UtcDateTime;

    /// <summary>Builds the dimensions the run selects (every one when it names none) and keeps each build.</summary>
    public async Task<DimensionBuildOutcome> BuildAsync(IReadOnlyCollection<string> names, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        await ledger.RegisterLedgerAsync(
            new LedgerEntry
            {
                FlowId = _flow.LedgerId,
                Partition = partition,
                Kind = LedgerKinds.Dimension,
                FlowName = _flow.Name,
                LedgerName = _flow.LedgerName,
            },
            ct).ConfigureAwait(false);

        var skipped = selected.Where(d => !d.BuildsIn(partition)).ToList();
        var building = selected.Where(d => d.BuildsIn(partition)).ToList();
        _log.LogInformation(
            "dimension flow '{Flow}' in partition '{Partition}': {Count} dimension(s) to build{Skipped}",
            _flow.Name, partition, building.Count,
            skipped.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {skipped.Count} not built in this partition") : string.Empty);

        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log);
        using var templates = new TemplateCache(_context.Templates);
        var summaries = new List<DimensionBuildSummary>(selected.Count);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            building,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Reliability.Concurrency), CancellationToken = ct },
            async (dimension, token) =>
            {
                var summary = await BuildOneAsync(ledger, search, templates, dimension, partition, runId, actor, token).ConfigureAwait(false);
                lock (gate)
                {
                    summaries.Add(summary);
                }
            }).ConfigureAwait(false);

        foreach (var dimension in skipped)
        {
            summaries.Add(new DimensionBuildSummary(
                dimension.Name, "skipped", null, 0, 0, 0, 0, 0, DimensionBuildChanges.None, 0, null,
                $"The dimension is built in {PartitionNames.Listed(dimension.Partitions)}, not in '{partition}'.", []));
        }

        var order = selected.Select((d, i) => (d.Name, i)).ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        var ordered = summaries.OrderBy(s => order[s.Dimension]).ToList();
        var outcome = new DimensionBuildOutcome(
            DeliveryOperations.Build, _flow.Name, partition,
            ordered.Count(s => s.Status == DimensionRunStatus.Completed), ordered.Count(s => s.Status == DimensionRunStatus.Failed), skipped.Count, ordered);
        return outcome.Failed > 0 ? throw new DimensionBuildsFailedException(outcome) : outcome;
    }

    /// <summary>Settles the field of every dimension the run selects and counts the records each would read, reading no value.</summary>
    public async Task<DimensionPlanOutcome> PlanAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var selected = _flow.Select(names);
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var search = new OsduSearch(client, _flow.Source.QueryPath, _flow.Source.SearchPath, _log);
        using var templates = new TemplateCache(_context.Templates);
        var plans = new List<DimensionPlanSummary>(selected.Count);
        foreach (var dimension in selected)
        {
            ct.ThrowIfCancellationRequested();
            var query = Query(dimension, partition);
            if (!dimension.BuildsIn(partition))
            {
                plans.Add(new DimensionPlanSummary(dimension.Name, dimension.Kind, query, dimension.Path, null, null, null, [], [],
                    $"built in {PartitionNames.Listed(dimension.Partitions)}, not in '{partition}'"));
                continue;
            }

            var problems = new List<string>();
            long? records = null;
            ResolvedField? resolved = null;
            try
            {
                records = await search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = query, ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
                resolved = await ResolveFieldAsync(search, templates, dimension, query, ct).ConfigureAwait(false);
                if (resolved.Field is null)
                {
                    problems.Add(resolved.Note ?? "The field could not be settled.");
                }
            }
            catch (Exception ex) when (Expected(ex, ct))
            {
                problems.Add(SecretHygiene.RedactedMessage(ex));
            }

            _log.LogInformation(
                "plan {Dimension} ({Kind} {Path}): {Records}{Field}{Problems}",
                dimension.Name, dimension.Kind, dimension.Path, records is { } n ? string.Create(CultureInfo.InvariantCulture, $"{n} record(s)") : "not counted",
                resolved?.Field is { } f ? $", read as {f.AggregateBy}" : string.Empty,
                problems.Count > 0 ? "; " + problems[0] : string.Empty);
            plans.Add(new DimensionPlanSummary(
                dimension.Name, dimension.Kind, query, dimension.Path, resolved?.Field?.AggregateBy, resolved?.Field is null ? null : resolved.Repeats, records,
                resolved?.Kinds ?? [], problems, null));
        }

        return new DimensionPlanOutcome(DeliveryOperations.Plan, _flow.Name, partition, plans);
    }

    private async Task<DimensionBuildSummary> BuildOneAsync(
        ILedger ledger, OsduSearch search, TemplateCache templates, DimensionSpec dimension, string partition, Guid runId, string actor, CancellationToken ct)
    {
        var query = Query(dimension, partition);
        var (_, run) = await ledger.StartDimensionRunAsync(
            new DimensionDeclaration
            {
                FlowId = _flow.LedgerId,
                FlowName = _flow.Name,
                Name = dimension.Name,
                Description = dimension.Description,
                Kind = dimension.Kind,
                Query = query,
                Path = dimension.Path,
                CleanJson = JsonSerializer.Serialize(dimension.Clean, StepJson),
                LabelJson = dimension.Label.Count == 0 ? null : JsonSerializer.Serialize(dimension.Label),
                DefinitionHash = dimension.DefinitionHash,
            },
            runId, actor, Now, ct).ConfigureAwait(false);
        var read = DimensionReadCounts.None;
        try
        {
            var resolved = await ResolveFieldAsync(search, templates, dimension, query, ct).ConfigureAwait(false);
            var templatesJson = resolved.Kinds.Count == 0 ? null : JsonSerializer.Serialize(resolved.Kinds, StepJson);
            if (resolved.Field is not { } field)
            {
                // No record of the kind: nothing to settle the field by, and nothing to read. The dimension holds no value.
                read = new DimensionReadCounts { Records = 0, Templates = templatesJson, Notes = [resolved.Note ?? "No record matched."] };
                var empty = await ledger.WriteDimensionAsync(Write(run, dimension, null, [], [], read), ct).ConfigureAwait(false);
                _log.LogWarning("dimension {Dimension}: {Note}", dimension.Name, resolved.Note);
                return Summary(dimension, empty, read, null);
            }

            _log.LogInformation("dimension {Dimension}: reading {Kind} {Path} as {Field}", dimension.Name, dimension.Kind, dimension.Path, field.AggregateBy);
            var values = await DistinctValues.ReadAsync(
                new SearchDistinctSource(search, dimension.Kind, query, field),
                new DistinctReadOptions(_flow.Source.AggregationSize, dimension.MaxValues, resolved.Repeats),
                _log, ct).ConfigureAwait(false);
            read = Counts(values, templatesJson, 0, [], KeyLabels.None);

            // A key naming a record is followed to it for its label, which is what the key's value is cleaned from.
            var labels = dimension.Label.Count == 0
                ? KeyLabels.None
                : await new DimensionLabeler(search, _log).LabelAsync(values.Values.Keys.ToList(), dimension.Label, ct).ConfigureAwait(false);
            read = Counts(values, templatesJson, 0, [], labels);

            var cleaner = Cleaner(dimension);
            var (originals, members, notes, countQueries) = await GroupAsync(search, dimension, query, field, resolved.Repeats, cleaner, values, labels, ct).ConfigureAwait(false);
            read = Counts(values, templatesJson, countQueries, notes, labels);
            var written = await ledger.WriteDimensionAsync(
                Write(run, dimension, new DimensionFieldState(Index(field.Index), field.NestedPath, field.AggregateBy, resolved.Repeats), originals, members, read),
                ct).ConfigureAwait(false);
            _log.LogInformation(
                "dimension {Dimension}: {Values} value(s) from {Keys} key(s), {LeftOut} of none; {Added} arrived, {Removed} left, {Moved} moved, {Restored} came back",
                dimension.Name, written.Members, written.Originals, written.LeftOut, written.Changes.OriginalsAdded, written.Changes.OriginalsRemoved,
                written.Changes.OriginalsMoved, written.Changes.OriginalsRestored);
            return Summary(dimension, written, read, field.AggregateBy);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseAsync(ledger, run.DimensionRunId, DimensionRunStatus.Cancelled, read, "the run was cancelled").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            var error = SecretHygiene.RedactedMessage(ex);
            _log.LogError(RunFailure.IsExpected(ex) ? null : ex, "dimension {Dimension} failed: {Error}", dimension.Name, error);
            await CloseAsync(ledger, run.DimensionRunId, DimensionRunStatus.Failed, read, error).ConfigureAwait(false);
            return new DimensionBuildSummary(dimension.Name, DimensionRunStatus.Failed, run.DimensionRunId, 0, 0, 0, 0, 0, DimensionBuildChanges.None, read.Aggregations + read.ScanPages,
                null, error, read.Notes.Take(DimensionBuildOutcome.MaxNotes).ToList());
        }
    }

    /// <summary>
    /// Cleans every original into its member, gives each member its filter and its count, and says what cleaning and counting
    /// had to say, a line each.
    /// </summary>
    private async Task<(List<DimensionOriginalWrite> Originals, List<DimensionMemberWrite> Members, List<string> Notes, int CountQueries)> GroupAsync(
        OsduSearch search, DimensionSpec dimension, string? query, OsduField field, bool repeats, DimensionCleaner cleaner, DistinctRead read, KeyLabels labels,
        CancellationToken ct)
    {
        var originals = new List<DimensionOriginalWrite>(read.Values.Count);
        var groups = new Dictionary<string, List<DimensionOriginalWrite>>(StringComparer.Ordinal);
        var notes = new List<string>(read.Notes);
        notes.AddRange(labels.Notes);
        var tooLong = 0;
        var leftOut = new Dictionary<string, int>(StringComparer.Ordinal);
        var unfilterable = new List<string>();
        foreach (var (original, count) in read.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (original.Length > DimensionSpec.MaxOriginalLength)
            {
                tooLong++;
                continue;
            }

            // The key's value is its label, cleaned, when it has one, and the key itself, cleaned, when it has none; the key
            // keeps its own filter, the search that finds exactly the records holding it.
            var labelled = labels.Labels.TryGetValue(original, out var found) ? found : null;
            var cleaned = cleaner.Clean(labelled?.Label ?? original);
            var filterable = DimensionFilters.Filterable(field, original);
            var filter = filterable ? DimensionFilters.Of(field, [original])[0] : null;
            if (cleaned.Outcome == CleanOutcome.Member)
            {
                var kept = new DimensionOriginalWrite(original, cleaned.Value, null, cleaned.Note, count, filterable, labelled?.Label, labelled?.From, filter);
                originals.Add(kept);
                (groups.TryGetValue(cleaned.Value!, out var group) ? group : groups[cleaned.Value!] = []).Add(kept);
                if (!filterable)
                {
                    unfilterable.Add(original);
                }
            }
            else
            {
                var reason = LeftOut(cleaned.Outcome);
                originals.Add(new DimensionOriginalWrite(original, null, reason, cleaned.Note, count, filterable, labelled?.Label, labelled?.From, filter));
                leftOut[reason] = leftOut.GetValueOrDefault(reason) + 1;
            }
        }

        if (tooLong > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tooLong} key(s) are longer than the {DimensionSpec.MaxOriginalLength} characters a dimension keeps, and are left out."));
        }

        foreach (var (reason, count) in leftOut.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{count} key(s) belong to no value: {Describe(reason)}."));
        }

        if (unfilterable.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{unfilterable.Count} key(s) cannot be carried in a search query, so no filter finds their records: {string.Join(", ", unfilterable.Take(5).Select(o => $"'{Shown(o)}'"))}{(unfilterable.Count > 5 ? ", ..." : string.Empty)}."));
        }

        // A member's count is exact where its originals' counts are counts of records that no two of them share: a field a
        // record holds once, or a list outside a nested array holding one original. Elsewhere it is their sum, unless the
        // dimension asks for exact counts, which one count of its filter gives where the filter is one query covering all.
        var members = new List<DimensionMemberWrite>(groups.Count);
        var toCount = new List<(int Index, string Filter)>();
        var splitFilters = 0;
        foreach (var (value, group) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var filterableOriginals = group.Where(o => o.Filterable).Select(o => o.Original).ToList();
            IReadOnlyList<string> filters = filterableOriginals.Count == 0 ? [] : DimensionFilters.Of(field, filterableOriginals);
            var exact = !repeats || (field.NestedPath is null && group.Count == 1);
            var unfilterableCount = group.Count - filterableOriginals.Count;
            members.Add(new DimensionMemberWrite(
                value, group.Sum(o => o.Count), exact, group.Count, unfilterableCount, filters.Count == 1 ? filters[0] : null, filters.Count));
            if (!exact && dimension.CountRecords)
            {
                if (filters.Count == 1 && unfilterableCount == 0)
                {
                    toCount.Add((members.Count - 1, filters[0]));
                }
                else
                {
                    splitFilters++;
                }
            }
        }

        if (splitFilters > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{splitFilters} value(s) hold keys no one query covers (more than {DimensionFilters.MaxOriginalsPerQuery}, or one no query can carry), so their records are counted as the sum of their keys'."));
        }

        var countQueries = 0;
        var failedCounts = 0;
        string? firstFailure = null;
        var gate = new Lock();
        await Parallel.ForEachAsync(
            toCount,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Reliability.Concurrency), CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    var records = await search.CountAsync(
                        new OsduSearchQuery { Kind = dimension.Kind, Query = DimensionFilters.Within(query, item.Filter), ReturnedFields = ["id"] }, token).ConfigureAwait(false);
                    lock (gate)
                    {
                        members[item.Index] = members[item.Index] with { Records = records, RecordsExact = true };
                        countQueries++;
                    }
                }
                catch (DeliveryException ex)
                {
                    lock (gate)
                    {
                        countQueries++;
                        failedCounts++;
                        firstFailure ??= SecretHygiene.RedactedMessage(ex);
                    }
                }
            }).ConfigureAwait(false);

        if (failedCounts > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"The records of {failedCounts} value(s) could not be counted, so they are the sum of their keys': {firstFailure}"));
        }

        return (originals, members, notes, countQueries);
    }

    /// <summary>
    /// How the index stores the dimension's field: for a property of the record itself, as the indexer maps it for every kind;
    /// for a property of data, as the saved template of every kind the pattern matches in the partition says, which must all
    /// say the same. A partition holding no record of the kind settles nothing, and says so.
    /// </summary>
    private async Task<ResolvedField> ResolveFieldAsync(OsduSearch search, TemplateCache templates, DimensionSpec dimension, string? query, CancellationToken ct)
    {
        if (!SearchFields.IsDataPath(dimension.Path))
        {
            var shape = SearchFields.RecordProperty(dimension.Path);
            return shape.Field is { } own
                ? new ResolvedField(own, shape.Repeats, [], null)
                : throw new DeliveryException($"Dimension {dimension.Name} reads {dimension.Path}, which {shape.Problem}");
        }

        var kinds = await DistinctValues.ReadAsync(
            new SearchDistinctSource(search, dimension.Kind, query, OsduField.Keyword("kind")),
            new DistinctReadOptions(_flow.Source.AggregationSize, MaxKinds, Repeats: false),
            _log, ct).ConfigureAwait(false);
        if (kinds.Values.Count == 0)
        {
            return new ResolvedField(null, false, [], $"No record of kind {dimension.Kind} matches the dimension's query in this partition, so it holds no value.");
        }

        var found = new List<DimensionKind>();
        var missing = new List<string>();
        var shapes = new List<(string Kind, IndexedShape Shape)>();
        foreach (var (kind, records) in kinds.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var (schema, version) = await templates.NewestAsync(kind, ct).ConfigureAwait(false);
            if (schema is null)
            {
                missing.Add(string.Create(CultureInfo.InvariantCulture, $"{kind} ({records} record(s))"));
                found.Add(new DimensionKind(kind, records, null));
                continue;
            }

            found.Add(new DimensionKind(kind, records, version));
            shapes.Add((kind, SearchFields.ClassifyValue(schema, dimension.Path)));
        }

        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"Dimension {dimension.Name} reads {dimension.Path} of {dimension.Kind}, and no template is saved for {string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? $" and {missing.Count - 10} more" : string.Empty)}, so how the index stores the field there is not known. Capture each on the Templates page, or with 'sqlflow template capture --kind <kind>'.");
        }

        var refused = shapes.Where(s => s.Shape.Field is null).ToList();
        if (refused.Count > 0)
        {
            throw new DeliveryException(
                $"Dimension {dimension.Name} cannot read {dimension.Path}: in {refused[0].Kind}, {refused[0].Shape.Problem}");
        }

        var distinct = shapes.GroupBy(s => s.Shape.Field!).ToList();
        if (distinct.Count > 1)
        {
            throw new DeliveryException(
                $"Dimension {dimension.Name} reads {dimension.Path}, which the kinds its pattern matches index differently: "
                + string.Join("; ", distinct.Select(g => $"{g.Key} in {string.Join(", ", g.Select(s => s.Kind).Take(5))}"))
                + ". One dimension reads one field; narrow the kind to the versions that agree.");
        }

        return new ResolvedField(distinct[0].Key, shapes.Any(s => s.Shape.Repeats), found, null);
    }

    /// <summary>The cleaner of a dimension, with every dictionary its map steps read loaded beside the flow's file.</summary>
    private DimensionCleaner Cleaner(DimensionSpec dimension)
    {
        if (dimension.Clean.Count == 0)
        {
            return DimensionCleaner.Identity;
        }

        return DimensionCleaner.Build(dimension.Clean, name =>
        {
            var folder = _flow.SourcePath is { } path
                ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
                : throw new DeliveryException(
                    $"Dimension {dimension.Name} maps through dictionary {name}, which is found beside the flow's file, and this flow was not loaded from a file.");
            try
            {
                return new DictionaryCatalog(_context.Documents).Load(name, folder, full => Path.GetRelativePath(folder, full).Replace('\\', '/')).Dictionary;
            }
            catch (FlowValidationException ex)
            {
                throw new DeliveryException($"Dimension {dimension.Name} could not read dictionary {name}: {ex.Message}", ex);
            }
        });
    }

    private DimensionWrite Write(
        DimensionRunState run, DimensionSpec dimension, DimensionFieldState? field, List<DimensionOriginalWrite> originals, List<DimensionMemberWrite> members,
        DimensionReadCounts read)
        => new()
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = run.DimensionId,
            FlowId = _flow.LedgerId,
            Field = field,
            Originals = originals,
            Members = members,
            Read = read,
            CompletedUtc = Now,
        };

    private static DimensionBuildSummary Summary(DimensionSpec dimension, DimensionRunState written, DimensionReadCounts read, string? aggregateBy)
        => new(dimension.Name, written.Status, written.DimensionRunId, written.Members, written.Originals, written.LeftOut, written.Unfilterable, read.Labelled,
            DimensionBuildChanges.Of(written.Changes), read.Aggregations + read.ScanPages + read.CountQueries + read.LabelQueries, aggregateBy, null,
            read.Notes.Take(DimensionBuildOutcome.MaxNotes).ToList());

    private static DimensionReadCounts Counts(DistinctRead read, string? templates, int countQueries, IReadOnlyList<string> notes, KeyLabels labels) => new()
    {
        Labelled = labels.Labelled,
        Unlabelled = labels.Unlabelled,
        LabelQueries = labels.Queries,
        Records = read.Records,
        WithValue = read.WithValue,
        Nulls = read.Nulls,
        TooLong = read.TooLong,
        Unreadable = read.Unreadable,
        Aggregations = read.Aggregations,
        Slices = read.Slices,
        Splits = read.Splits,
        ScannedSlices = read.ScannedSlices,
        ScanPages = read.ScanPages,
        ScannedUnits = read.ScannedUnits,
        CountQueries = countQueries,
        Templates = templates,
        Notes = notes.Count > 0 ? notes : read.Notes,
    };

    private async Task CloseAsync(ILedger ledger, long dimensionRunId, string status, DimensionReadCounts read, string error)
    {
        try
        {
            await ledger.CloseDimensionRunAsync(dimensionRunId, status, read, error, Now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException || ex.GetType().Name.Contains("DbUpdate", StringComparison.Ordinal))
        {
            // The build's own outcome is what the run reports; a build left running says so on its page.
            _log.LogError("Could not close dimension build {DimensionRunId} as {Status}: {Message}", dimensionRunId, status, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>The query a dimension reads with, its parameters and the partition substituted; null for every record of the kind.</summary>
    private string? Query(DimensionSpec dimension, string partition)
        => dimension.Query is { } query
            ? FlowParameters.Substitute(query, new Dictionary<string, string>(_values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition })
            : null;

    /// <summary>
    /// The partition the run builds in, by its data-partition-id: the one the flow is bound to, or for a flow whose partition is
    /// its header's, the header's resolved on this node.
    /// </summary>
    private async Task<string> PartitionAsync(CancellationToken ct)
    {
        if (_flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = _flow.Source.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Dimension flow '{_flow.Name}' is bound to no partition and names no '{CacheScope.PartitionHeader}' in its source.headers, so the partition it builds in is unknown.");
        }

        return CacheScope.Normalize(await _context.Secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{_flow.Name}: source.headers");
    }

    /// <summary>What a failure of one dimension is: anything but the run itself being cancelled, which stops every dimension.</summary>
    private static bool Expected(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private static string LeftOut(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.Empty => DimensionLeftOut.Empty,
        CleanOutcome.TooLong => DimensionLeftOut.TooLong,
        CleanOutcome.Dropped => DimensionLeftOut.Dropped,
        _ => DimensionLeftOut.Failed,
    };

    private static string Describe(string leftOut) => leftOut switch
    {
        DimensionLeftOut.Empty => "cleaning left nothing",
        DimensionLeftOut.TooLong => $"their value would be longer than the {DimensionSpec.MaxCleanLength} characters a value may be",
        DimensionLeftOut.Dropped => "a map step left them out",
        _ => "a clean step could not run on them",
    };

    /// <summary>
    /// The clean steps a build kept of its dimension (<see cref="DimensionState.CleanJson"/>), as it wrote them; none when the
    /// text is not the steps a build writes.
    /// </summary>
    public static IReadOnlyList<CleanStep> StepsOf(string? cleanJson)
    {
        if (string.IsNullOrWhiteSpace(cleanJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CleanStep>>(cleanJson, StepJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The label paths a build kept of its dimension (<see cref="DimensionState.LabelJson"/>), as it wrote them; none when the
    /// dimension reads no label or the text is not the list a build writes.
    /// </summary>
    public static IReadOnlyList<string> LabelOf(string? labelJson)
    {
        if (string.IsNullOrWhiteSpace(labelJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(labelJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The kinds a build read, as it kept them with its counts (<see cref="DimensionReadCounts.Templates"/>); none when there are none.</summary>
    public static IReadOnlyList<DimensionKind> KindsOf(string? templatesJson)
    {
        if (string.IsNullOrWhiteSpace(templatesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DimensionKind>>(templatesJson, StepJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>How the ledger names the way a field is stored.</summary>
    public static string Index(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Text => "text",
        OsduFieldIndex.Keyword => "keyword",
        OsduFieldIndex.Number => "number",
        OsduFieldIndex.Boolean => "boolean",
        _ => "date",
    };

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    private sealed record ResolvedField(OsduField? Field, bool Repeats, IReadOnlyList<DimensionKind> Kinds, string? Note);

    /// <summary>The newest saved template of each exact kind, read once per run whichever dimension asks.</summary>
    private sealed class TemplateCache(ITemplateStore? store) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, (SchemaSnapshot? Schema, string? Version)> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<TemplateInfo>? _saved;

        public void Dispose() => _gate.Dispose();

        /// <summary>The schema of the newest template saved for <paramref name="kind"/>, with its version; nulls when none is saved.</summary>
        public async Task<(SchemaSnapshot? Schema, string? Version)> NewestAsync(string kind, CancellationToken ct)
        {
            if (store is null)
            {
                throw new DeliveryException($"The templates of the kinds a dimension reads are kept in the module database, and this host has none: {DeliveryServices.NoLedgerMessage}");
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_loaded.TryGetValue(kind, out var known))
                {
                    return known;
                }

                _saved ??= await store.ListAsync(ct).ConfigureAwait(false);
                var newest = _saved.Where(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.CapturedUtc).FirstOrDefault();
                var schema = newest is null ? null : await store.LoadAsync(newest.Reference, ct).ConfigureAwait(false);
                var loaded = (schema, schema is null ? null : newest!.Version);
                _loaded[kind] = loaded;
                return loaded;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
