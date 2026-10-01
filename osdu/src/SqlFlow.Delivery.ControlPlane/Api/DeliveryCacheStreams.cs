using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A run of a flow as a stream shows it: when, how it ended, and who asked for it.</summary>
public sealed record DeliveryStreamRunDto(Guid RunId, string Status, DateTime? StartedUtc, DateTime? EndedUtc, string? RequestedBy, string? TriggerSource, string? Error);

/// <summary>
/// One flow of a stream: a flow that loads what the cache flow captures, or the cache flow itself. <c>Depth</c> is how far
/// upstream of the cache flow it runs (0 for the cache flow); <c>Inputs</c> are the objects it reads that no flow of the
/// estate writes, which is where the stream starts (a file folder, a table loaded outside SQLFlow, an OSDU kind).
/// </summary>
public sealed record DeliveryStreamStageDto(
    string Flow, Guid? PipelineId, string? Kind, string? Project, int Depth, DeliveryStreamRunDto? LastRun,
    IReadOnlyList<DeliveryCacheScheduleDto> Schedules, IReadOnlyList<DeliveryStreamInputDto> Inputs);

/// <summary>Where a stream starts: an object a stage reads that no flow of the estate writes.</summary>
/// <param name="Kind">file, table, osdu or dictionary.</param>
/// <param name="Name">The folder, the table's three-part name, the OSDU kind, or the dictionary's file.</param>
public sealed record DeliveryStreamInputDto(string Kind, string Name);

/// <summary>A delivery flow that reads a cached type, with the mappings its interfaces render with.</summary>
public sealed record DeliveryStreamReaderDto(string Flow, Guid? PipelineId, string? Project, IReadOnlyList<string> Mappings);

/// <summary>
/// One cached type of a partition as a stream: every flow on the way from where its content starts to the cache flow that
/// captures it, the delivery flows that read it, how many entries the current version holds against the version before
/// it, and its state: <c>fresh</c>, <c>failed</c> (the last refresh of its cache flow in the partition, or the last run of
/// a flow that loads it, failed), or <c>missing</c> (declared, but not in the current version), with why.
/// </summary>
public sealed record DeliveryCacheStreamDto(
    string Type, string EntityType, string Origin, IReadOnlyList<string> DeclaredBy, IReadOnlyList<string> Projects,
    IReadOnlyList<DeliveryStreamStageDto> Stages, IReadOnlyList<DeliveryStreamInputDto> Origins, IReadOnlyList<DeliveryStreamReaderDto> Readers,
    long? Items, long? PreviousItems, string State, string? StateReason);

/// <summary>
/// The last refresh of one cache flow in the partition, read from its runs: <c>written</c> (a version was written),
/// <c>unchanged</c> (the capture found nothing to change, which still checks the cache), or <c>failed</c>, with the
/// version the partition's cache held after it and the error of a failure.
/// </summary>
public sealed record DeliveryCacheCheckDto(
    string Flow, Guid? PipelineId, Guid RunId, DateTime? EndedUtc, string Outcome, string? Version, string? Error, string? RequestedBy, string? TriggerSource);

/// <summary>
/// Something about the partition's cache that needs reading, in one sentence: a version that removed or shrank types, a
/// refresh or a load that failed, a type a delivery flow reads that no cache flow captures, changes that wait for a
/// decision, or a cache with no version. <c>Severity</c> is error, warning or info; <c>Type</c> and <c>Flow</c> name what
/// it is about, and <c>Action</c> the cache page tab or the step that settles it: <c>history</c>, <c>deliveries</c>,
/// <c>streams</c> or <c>refresh</c>.
/// </summary>
public sealed record DeliveryCacheNoticeDto(string Kind, string Severity, string Message, string? Type, string? Flow, string? Action);

/// <summary>A type the previous version held that the current one does not, and how many entries it held.</summary>
public sealed record DeliveryCacheLeftTypeDto(string Type, string EntityType, long Items, string Version);

/// <summary>
/// A partition's cache as its streams: each type with where it comes from and who reads it, each cache flow's last
/// refresh there, when the cache was last checked (the last refresh that succeeded, whether or not it wrote a version),
/// the types the current version no longer holds, and the notices.
/// </summary>
public sealed record DeliveryCacheStreamsDto(
    string Partition, string? CurrentVersion, string? PreviousVersion, DateTime? LastCheckedUtc,
    IReadOnlyList<DeliveryCacheStreamDto> Types, IReadOnlyList<DeliveryCacheCheckDto> Checks, IReadOnlyList<DeliveryCacheLeftTypeDto> Left,
    IReadOnlyList<DeliveryCacheNoticeDto> Notices);

/// <summary>
/// Where each cached type of a partition comes from and who reads it (docs/partitions-design.md, the cache page). Nothing is
/// tracked for it: SQLFlow's lineage holds every hop (a file a pre flow lands, the table an ingestion flow loads, the table
/// a cache flow captures into the partition's cache, the cache type a delivery flow's mapping reads), and each flow's runs
/// hold its last run and a cache flow's refreshes. A stream is the upstream walk from the node a cache flow writes, a
/// reader a read of that node.
/// </summary>
public static class DeliveryCacheStreams
{
    /// <summary>How far upstream of a cache flow a stream is followed, in flows.</summary>
    public const int MaxDepth = 8;

    /// <summary>How many of a cache flow's latest runs are read to find its last refresh of one partition.</summary>
    public const int RunsRead = 25;

    /// <summary>A type holding fewer entries than this share of what the version before held is said to have shrunk.</summary>
    public const double ShrinkShare = 0.5;

    /// <summary>A type is said to have shrunk only when the version before held at least this many entries.</summary>
    public const long ShrinkFloor = 10;

    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the stream listing under the module's group.</summary>
    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/cache/streams", GetAsync).WithName("GetDeliveryCacheStreams");
    }

    private static async Task<Results<Ok<DeliveryCacheStreamsDto>, ProblemHttpResult>> GetAsync(
        [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return TypedResults.Problem(
                detail: "Name the partition whose cache streams to describe with ?partition=.", statusCode: StatusCodes.Status400BadRequest, title: "Partition required");
        }

        return TypedResults.Ok(await DescribeAsync(db, osdu, partition.Trim(), ct).ConfigureAwait(false));
    }

    /// <summary>The streams of <paramref name="partition"/>'s cache; empty, with a notice, for a partition no cache flow fills.</summary>
    public static async Task<DeliveryCacheStreamsDto> DescribeAsync(CatalogDbContext db, OsduDbContext osdu, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);

        var definitions = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .Where(d => d.Scope == partition)
            .OrderBy(d => d.Name).ThenBy(d => d.FlowName)
            .ToListAsync(ct).ConfigureAwait(false);
        var versions = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == partition && v.Current)
            .ToListAsync(ct).ConfigureAwait(false);
        var current = versions.Count == 0 ? null : OsduCacheStore.Info(versions[0]);
        var previous = current?.PreviousVersion is { } previousVersion
            ? await osdu.DeliveryCacheVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Scope == partition && v.Version == previousVersion, ct).ConfigureAwait(false) is { } row
                ? OsduCacheStore.Info(row)
                : null
            : null;
        var pending = await osdu.DeliveryUpdateTags.AsNoTracking().LongCountAsync(t => t.Scope == partition && t.Status == "pending", ct).ConfigureAwait(false);

        var repoIds = definitions.Select(d => d.RepoId).Distinct().ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => repoIds.Contains(p.RepoId))
            .Select(p => new PipelineRow(p.Id, p.RepoId, p.Name, p.Kind, p.RelativePath))
            .ToListAsync(ct).ConfigureAwait(false);
        var edges = (await db.LineageEdges.AsNoTracking()
                .Where(e => repoIds.Contains(e.RepoId) && e.Flow != null)
                .Select(e => new EdgeRow(e.RepoId, e.Flow!, e.Relation, e.ObjectKey))
                .ToListAsync(ct).ConfigureAwait(false))
            .Distinct()
            .ToList();
        var graph = new Graph(edges);
        var interfaces = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => repoIds.Contains(i.RepoId) && i.Active && (i.Partition == partition || i.Partition == ""))
            .Select(i => new { i.RepoId, i.FlowName, i.MappingReference })
            .ToListAsync(ct).ConfigureAwait(false);

        // Every flow the streams name, to read its last run and schedules once.
        var types = new List<(DeliveryCacheDefinition First, List<DeliveryCacheDefinition> Declared, List<Stage> Stages, List<string> Nodes)>();
        foreach (var declared in definitions.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var list = declared.ToList();
            var nodes = list.SelectMany(d => graph.CacheNodes(d.RepoId, d.FlowName, d.Name, d.DeclaresPartitions ? partition : null)).Distinct(StringComparer.Ordinal).ToList();
            var stages = new List<Stage>();
            foreach (var definition in list)
            {
                stages.AddRange(graph.Upstream(definition));
            }

            types.Add((list[0], list, stages.DistinctBy(s => (s.RepoId, s.Flow)).ToList(), nodes));
        }

        var named = types.SelectMany(t => t.Stages.Select(s => (s.RepoId, s.Flow)))
            .Concat(types.SelectMany(t => graph.Readers(t.Nodes, partition)))
            .Distinct()
            .ToList();
        var pipelineOf = named
            .Select(n => pipelines.FirstOrDefault(p => p.RepoId == n.RepoId && string.Equals(p.Name, n.Item2, StringComparison.OrdinalIgnoreCase)))
            .OfType<PipelineRow>()
            .DistinctBy(p => p.Id)
            .ToDictionary(p => (p.RepoId, p.Name.ToLowerInvariant()));
        var runs = await LastRunsAsync(db, pipelineOf.Values.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
        var schedules = await SchedulesAsync(db, pipelineOf.Values.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
        PipelineRow? Pipeline(Guid repoId, string flow) => pipelineOf.GetValueOrDefault((repoId, flow.ToLowerInvariant()));

        // Each cache flow's last refresh of this partition.
        var checks = new List<DeliveryCacheCheckDto>();
        foreach (var flow in definitions.Select(d => (d.RepoId, d.FlowName)).Distinct())
        {
            if (Pipeline(flow.RepoId, flow.FlowName) is { } pipeline
                && await LastCheckAsync(db, pipeline, partition, definitions.Where(d => d.FlowName == flow.FlowName).Any(d => d.DeclaresPartitions), ct).ConfigureAwait(false) is { } check)
            {
                checks.Add(check);
            }
        }

        var failedChecks = checks.Where(c => c.Outcome == "failed").ToDictionary(c => c.Flow, StringComparer.OrdinalIgnoreCase);
        var streams = new List<DeliveryCacheStreamDto>();
        foreach (var (first, declared, stages, nodes) in types)
        {
            var stageDtos = stages
                .OrderByDescending(s => s.Depth).ThenBy(s => s.Flow, StringComparer.Ordinal)
                .Select(s =>
                {
                    var pipeline = Pipeline(s.RepoId, s.Flow);
                    return new DeliveryStreamStageDto(
                        s.Flow, pipeline?.Id, pipeline?.Kind, pipeline is null ? null : ProjectOf(pipeline.RelativePath), s.Depth,
                        pipeline is null ? null : runs.GetValueOrDefault(pipeline.Id),
                        pipeline is null ? [] : schedules.GetValueOrDefault(pipeline.Id) ?? [],
                        s.Inputs);
                })
                .ToList();
            var readers = graph.Readers(nodes, partition)
                .Select(r =>
                {
                    var pipeline = Pipeline(r.RepoId, r.Flow);
                    return new DeliveryStreamReaderDto(
                        r.Flow, pipeline?.Id, pipeline is null ? null : ProjectOf(pipeline.RelativePath),
                        interfaces.Where(i => i.RepoId == r.RepoId && string.Equals(i.FlowName, r.Flow, StringComparison.OrdinalIgnoreCase))
                            .Select(i => i.MappingReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
                })
                .OrderBy(r => r.Flow, StringComparer.Ordinal)
                .ToList();
            var held = current?.Types.FirstOrDefault(t => t.Name.Equals(first.Name, StringComparison.OrdinalIgnoreCase));
            var before = previous?.Types.FirstOrDefault(t => t.Name.Equals(first.Name, StringComparison.OrdinalIgnoreCase));
            var origins = stageDtos.SelectMany(s => s.Inputs).Distinct().ToList();
            if (first.Origin == CacheOrigins.DictionaryText && first.DictionaryPath is { } dictionary)
            {
                origins.Add(new DeliveryStreamInputDto("dictionary", dictionary));
            }

            var (state, reason) = StateOf(first.Name, declared, stageDtos, held is not null, failedChecks);
            streams.Add(new DeliveryCacheStreamDto(
                first.Name, first.EntityType, first.Origin,
                declared.Select(d => d.FlowName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                declared.Select(d => ProjectOf(d.RelativePath)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                stageDtos, origins.Distinct().ToList(), readers, held?.Items, before?.Items, state, reason));
        }

        var left = previous is null || current is null
            ? []
            : previous.Types
                .Where(t => !current.Types.Any(c => c.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(t => new DeliveryCacheLeftTypeDto(t.Name, t.EntityType, t.Items, previous.Version))
                .OrderBy(t => t.Type, StringComparer.Ordinal)
                .ToList();
        var lastChecked = checks.Where(c => c.Outcome != "failed").Max(c => c.EndedUtc);
        var notices = Notices(partition, current, previous, streams, left, checks, graph, types.Select(t => t.Nodes).SelectMany(n => n).ToList(), definitions, pending);
        return new DeliveryCacheStreamsDto(partition, current?.Version, current?.PreviousVersion, lastChecked, streams, checks, left, notices);
    }

    /// <summary>
    /// A type's state: failed when the last refresh of a cache flow declaring it failed in the partition, or the last run of a
    /// flow that loads it failed (it then holds what was loaded before); missing when the current version does not hold it;
    /// fresh otherwise.
    /// </summary>
    private static (string State, string? Reason) StateOf(
        string type, IReadOnlyList<DeliveryCacheDefinition> declared, IReadOnlyList<DeliveryStreamStageDto> stages, bool held,
        IReadOnlyDictionary<string, DeliveryCacheCheckDto> failedChecks)
    {
        if (declared.Select(d => d.FlowName).FirstOrDefault(failedChecks.ContainsKey) is { } flow)
        {
            return ("failed", $"The last refresh of {flow} in this partition failed: {failedChecks[flow].Error ?? "no error recorded"}");
        }

        if (stages.FirstOrDefault(s => s.Depth > 0 && s.LastRun is { Status: RunStatuses.Failed }) is { } stage)
        {
            return ("failed", $"The last run of {stage.Flow}, which loads what {type} is captured from, failed; the cache holds what was loaded before.");
        }

        return held ? ("fresh", null) : ("missing", $"{type} is declared, and the current version does not hold it: refresh its cache flow to capture it.");
    }

    private static List<DeliveryCacheNoticeDto> Notices(
        string partition, CacheVersionInfo? current, CacheVersionInfo? previous, IReadOnlyList<DeliveryCacheStreamDto> streams,
        IReadOnlyList<DeliveryCacheLeftTypeDto> left, IReadOnlyList<DeliveryCacheCheckDto> checks, Graph graph, IReadOnlyList<string> nodes,
        IReadOnlyList<DeliveryCacheDefinition> definitions, long pending)
    {
        var notices = new List<DeliveryCacheNoticeDto>();
        if (definitions.Count == 0)
        {
            notices.Add(new DeliveryCacheNoticeDto("none", "info", $"No synced cache flow fills the cache of {partition}.", null, null, null));
            return notices;
        }

        if (current is null)
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "no-version", "warning", $"The cache of {partition} holds no version yet, so no delivery that reads it can render: refresh its cache flows.", null, null, "refresh"));
        }

        foreach (var check in checks.Where(c => c.Outcome == "failed"))
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "failed", "error", $"The last refresh of {check.Flow} in {partition} failed: {check.Error ?? "no error recorded"}", null, check.Flow, "refresh"));
        }

        foreach (var stream in streams.Where(s => s.State == "failed" && s.Stages.Any(st => st.Depth > 0 && st.LastRun is { Status: RunStatuses.Failed })))
        {
            notices.Add(new DeliveryCacheNoticeDto("failed", "warning", stream.StateReason!, stream.Type, null, "streams"));
        }

        if (left.Count > 0 && current is not null)
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "removed", "warning",
                string.Create(CultureInfo.InvariantCulture,
                    $"Version {current.Version} no longer holds {left.Count} type(s) version {previous!.Version} held ({string.Join(", ", left.Select(l => l.Type))}), {left.Sum(l => l.Items):N0} entries; a delivery that reads one of them holds its records."),
                null, null, "history"));
        }

        foreach (var stream in streams.Where(s => s.Items is { } now && s.PreviousItems is { } before && before >= ShrinkFloor && now < before * ShrinkShare))
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "shrunk", "warning",
                string.Create(CultureInfo.InvariantCulture, $"{stream.Type} holds {stream.Items:N0} entries, down from {stream.PreviousItems:N0} in the version before."),
                stream.Type, null, "history"));
        }

        // A type a delivery flow reads in this partition that no cache flow of it declares: its records hold at render.
        var declared = definitions.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, reader) in graph.UnfilledReads(nodes, partition).Where(r => !declared.Contains(r.Type)))
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "unfilled", "error", $"{reader} reads the cache type {type}, which no cache flow of {partition} captures; its records hold at render.", type, reader, null));
        }

        if (pending > 0)
        {
            notices.Add(new DeliveryCacheNoticeDto(
                "pending", "info",
                string.Create(CultureInfo.InvariantCulture, $"{pending:N0} cache change(s) wait for a decision before the records built from them are delivered again."),
                null, null, "deliveries"));
        }

        return notices;
    }

    /// <summary>The project a pipeline belongs to: the top folder of its file in the repository; null for a file at the root.</summary>
    private static string? ProjectOf(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? path[..slash] : null;
    }

    /// <summary>The latest run of each pipeline.</summary>
    private static async Task<Dictionary<Guid, DeliveryStreamRunDto>> LastRunsAsync(CatalogDbContext db, IReadOnlyList<Guid> pipelineIds, CancellationToken ct)
    {
        var runs = new Dictionary<Guid, DeliveryStreamRunDto>();
        foreach (var id in pipelineIds)
        {
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.PipelineId == id)
                .OrderByDescending(r => r.EnqueuedUtc ?? r.StartUtc ?? r.WrittenUtc)
                .Select(r => new DeliveryStreamRunDto(r.RunId, r.Status, r.StartUtc, r.EndUtc, r.RequestedBy, r.TriggerSource, r.Error))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is not null)
            {
                runs[id] = run with
                {
                    StartedUtc = Utc(run.StartedUtc),
                    EndedUtc = Utc(run.EndedUtc),
                    Error = run.Error is { Length: > 500 } error ? error[..500] + "..." : run.Error,
                };
            }
        }

        return runs;
    }

    /// <summary>The schedules each pipeline is a member of.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<DeliveryCacheScheduleDto>>> SchedulesAsync(CatalogDbContext db, IReadOnlyList<Guid> pipelineIds, CancellationToken ct)
    {
        var memberships = await db.ScheduleMembers.AsNoTracking()
            .Where(m => pipelineIds.Contains(m.PipelineId))
            .Select(m => new { m.PipelineId, m.ScheduleId })
            .ToListAsync(ct).ConfigureAwait(false);
        var scheduleIds = memberships.Select(m => m.ScheduleId).Distinct().ToList();
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => scheduleIds.Contains(s.Id))
            .Select(s => new DeliveryCacheScheduleDto(s.Id, s.Name, s.Cron, s.IntervalSeconds, s.Parents.Any()))
            .ToListAsync(ct).ConfigureAwait(false);
        return memberships
            .GroupBy(m => m.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DeliveryCacheScheduleDto>)g.Join(schedules, m => m.ScheduleId, s => s.Id, (_, s) => s).OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The last refresh of <paramref name="pipeline"/>, a cache flow, that reached <paramref name="partition"/>: read from the
    /// results its runs recorded, newest first. A refresh of one partition names it (<c>scope</c>), one of several lists each
    /// (<c>partitions</c>); a failed run whose result names no partition is this partition's when the flow fills this one
    /// alone or the run's values named it.
    /// </summary>
    private static async Task<DeliveryCacheCheckDto?> LastCheckAsync(CatalogDbContext db, PipelineRow pipeline, string partition, bool namesPartitions, CancellationToken ct)
    {
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipeline.Id
                && (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed)
                && (r.Operation == null || r.Operation == DeliveryOperations.Refresh))
            .OrderByDescending(r => r.EndUtc ?? r.WrittenUtc)
            .Take(RunsRead)
            .Select(r => new { r.RunId, r.Status, r.EndUtc, r.WrittenUtc, r.ValuesJson, r.ResultJson, r.Error, r.RequestedBy, r.TriggerSource })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var run in runs)
        {
            var ended = Utc(run.EndUtc) ?? Utc(run.WrittenUtc);
            DeliveryCacheCheckDto Check(string outcome, string? version, string? error)
                => new(pipeline.Name, pipeline.Id, run.RunId, ended, outcome, version, error, run.RequestedBy, run.TriggerSource);

            var result = Parse(run.ResultJson);
            if (result is { } root && Text(root, "scope") is { } scope)
            {
                if (scope.Equals(partition, StringComparison.Ordinal))
                {
                    return Check(root.TryGetProperty("written", out var written) && written.ValueKind == JsonValueKind.True ? "written" : "unchanged", Text(root, "version"), null);
                }

                continue;
            }

            if (result is { } several && several.TryGetProperty("partitions", out var partitions) && partitions.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in partitions.EnumerateArray())
                {
                    if (!string.Equals(Text(entry, "partition"), partition, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Text(entry, "error") is { } error)
                    {
                        return Check("failed", null, error);
                    }

                    var outcome = entry.TryGetProperty("outcome", out var inner) ? inner : default;
                    return Check(
                        inner.ValueKind == JsonValueKind.Object && inner.TryGetProperty("written", out var written) && written.ValueKind == JsonValueKind.True ? "written" : "unchanged",
                        inner.ValueKind == JsonValueKind.Object ? Text(outcome, "version") : null,
                        null);
                }

                continue;
            }

            if (run.Status == RunStatuses.Failed && (!namesPartitions || RunValue(run.ValuesJson, "partition") == partition))
            {
                return Check("failed", null, run.Error is { Length: > 500 } error ? error[..500] + "..." : run.Error);
            }
        }

        return null;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            // A result that does not parse says nothing about a partition; the run is passed over.
            return null;
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? RunValue(string? valuesJson, string name)
        => Parse(valuesJson) is { } values ? Text(values, name) : null;

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed record PipelineRow(Guid Id, Guid RepoId, string Name, string Kind, string RelativePath);

    private sealed record EdgeRow(Guid RepoId, string Flow, string Relation, string ObjectKey);

    /// <summary>A flow upstream of a cache flow, how far upstream it runs, and what it reads that no flow writes.</summary>
    private sealed record Stage(Guid RepoId, string Flow, int Depth, IReadOnlyList<DeliveryStreamInputDto> Inputs);

    /// <summary>The lineage edges of the repositories whose cache flows fill the partition, read as a graph.</summary>
    private sealed class Graph(IReadOnlyList<EdgeRow> edges)
    {
        private const string CacheNodePrefix = "dataset:osdu-cache|";

        private const string DatasetNodePrefix = "dataset:";

        private const string TypeNodePrefix = "dataset:osdu-type";

        private readonly ILookup<string, EdgeRow> _writers = edges.Where(e => e.Relation == "Writes").ToLookup(e => e.ObjectKey, StringComparer.Ordinal);
        private readonly ILookup<string, EdgeRow> _readers = edges.Where(e => e.Relation == "Reads").ToLookup(e => e.ObjectKey, StringComparer.Ordinal);
        private readonly ILookup<(Guid, string), EdgeRow> _byFlow = edges.ToLookup(e => (e.RepoId, e.Flow.ToLowerInvariant()));

        /// <summary>
        /// The cache type nodes a cache flow writes for a type: in <paramref name="partition"/> for a flow that works in
        /// partitions (lineage keys a cache type by the partition as the flow writes it, and a flow that serves every
        /// registered partition under <see cref="PartitionNames.Every"/>, which stands for each of them), and whatever its
        /// header names for one that does not.
        /// </summary>
        public IEnumerable<string> CacheNodes(Guid repoId, string flow, string type, string? partition)
            => _byFlow[(repoId, flow.ToLowerInvariant())]
                .Where(e => e.Relation == "Writes" && e.ObjectKey.StartsWith(CacheNodePrefix, StringComparison.Ordinal))
                .Where(e => Segments(e.ObjectKey) is [_, var named, _, var name]
                    && name.Equals(type, StringComparison.OrdinalIgnoreCase)
                    && (partition is null || InPartition(named, partition)))
                .Select(e => e.ObjectKey);

        /// <summary>Whether a cache node keyed by <paramref name="named"/> is in <paramref name="partition"/>: named for it, or for every partition.</summary>
        private static bool InPartition(string named, string partition)
            => named.Equals(partition, StringComparison.OrdinalIgnoreCase) || named == PartitionNames.Every;

        /// <summary>
        /// The flows that read any of <paramref name="nodes"/> in <paramref name="partition"/>: a read of the same type named
        /// for the partition, for every partition (a registry-driven delivery flow), or as the node itself is keyed (a
        /// partition a header names).
        /// </summary>
        public IEnumerable<(Guid RepoId, string Flow)> Readers(IEnumerable<string> nodes, string partition)
            => nodes.SelectMany(node => Aliases(node, partition)).Distinct(StringComparer.Ordinal)
                .SelectMany(key => _readers[key]).Select(e => (e.RepoId, e.Flow)).Distinct();

        /// <summary>The keys a cache type node answers to in <paramref name="partition"/>: itself, and its type named for the partition and for every partition.</summary>
        private static IEnumerable<string> Aliases(string node, string partition)
        {
            yield return node;
            if (Segments(node) is [var system, _, var name, var type])
            {
                yield return string.Join('|', system, partition.ToLowerInvariant(), name, type);
                yield return string.Join('|', system, PartitionNames.Every, name, type);
            }
        }

        /// <summary>
        /// Reads of cache types in the same partitions as <paramref name="nodes"/> (or named <paramref name="partition"/>,
        /// or every partition) whose type no node of them writes: a type a delivery flow reads that is not in the list at
        /// all. A read and a write of the same type meet across partition spellings, since a registry-driven flow's node
        /// stands for every partition.
        /// </summary>
        public IEnumerable<(string Type, string Reader)> UnfilledReads(IReadOnlyList<string> nodes, string partition)
        {
            var spelled = nodes.Select(n => Segments(n)[1]).Append(partition.ToLowerInvariant()).Append(PartitionNames.Every)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var writtenTypes = nodes.Select(n => Segments(n)[3]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _readers
                .Where(g => g.Key.StartsWith(CacheNodePrefix, StringComparison.Ordinal)
                    && Segments(g.Key) is [_, var named, _, var type] && spelled.Contains(named) && !writtenTypes.Contains(type))
                .SelectMany(g => g.Select(e => (Segments(g.Key)[3], e.Flow)))
                .Distinct()
                .OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Flow, StringComparer.Ordinal);
        }

        /// <summary>
        /// The flows a cache type's content passes through before its cache flow captures it, nearest first, each with what it
        /// reads that no flow writes: the table a table type is read from and every flow upstream of it, the OSDU kind an
        /// OSDU type is searched as (and a flow of the estate that delivers that kind), and for a dictionary, the cache flow
        /// alone. The cache flow itself is depth 0.
        /// </summary>
        public IEnumerable<Stage> Upstream(DeliveryCacheDefinition definition)
        {
            var reads = _byFlow[(definition.RepoId, definition.FlowName.ToLowerInvariant())].Where(e => e.Relation == "Reads").ToList();
            var start = definition.Origin switch
            {
                CacheOrigins.TableText when definition.SourceObject is { } table => reads.Where(e => IsTable(e.ObjectKey, table)).Select(e => e.ObjectKey).ToList(),
                CacheOrigins.OsduText when definition.Kind is { } kind => reads.Where(e => IsKind(e.ObjectKey, kind)).Select(e => e.ObjectKey).ToList(),
                _ => [],
            };

            yield return new Stage(definition.RepoId, definition.FlowName, 0, start.Where(key => !_writers[key].Any()).Select(Input).ToList());
            var visited = new HashSet<(Guid, string)> { (definition.RepoId, definition.FlowName.ToLowerInvariant()) };
            var frontier = start;
            for (var depth = 1; depth <= MaxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var writer in frontier.SelectMany(key => _writers[key]).Where(w => w.RepoId == definition.RepoId).Select(w => w.Flow).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!visited.Add((definition.RepoId, writer.ToLowerInvariant())))
                    {
                        continue;
                    }

                    var inputs = _byFlow[(definition.RepoId, writer.ToLowerInvariant())].Where(e => e.Relation == "Reads").Select(e => e.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
                    yield return new Stage(definition.RepoId, writer, depth, inputs.Where(key => !_writers[key].Any() && IsOrigin(key)).Select(Input).ToList());
                    next.AddRange(inputs.Where(key => _writers[key].Any()));
                }

                frontier = next.Distinct(StringComparer.Ordinal).ToList();
            }
        }

        private static string[] Segments(string key) => key.Split('|');

        /// <summary>Whether a lineage key names the three-part table <paramref name="table"/>, whatever server it is on.</summary>
        private static bool IsTable(string key, string table)
        {
            var parts = table.Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.');
            var segments = Segments(key);
            return parts.Length == 3 && segments.Length == 4
                && segments[1].Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                && segments[2].Equals(parts[1], StringComparison.OrdinalIgnoreCase)
                && segments[3].Equals(parts[2], StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a lineage key is the OSDU type node of <paramref name="kind"/>, in whichever partition it is keyed by.</summary>
        private static bool IsKind(string key, string kind)
            => key.StartsWith(TypeNodePrefix, StringComparison.Ordinal) && Segments(key) is [_, _, _, var named] && named.Equals(kind, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a node no flow writes is somewhere content starts: a file folder, a table, or an OSDU kind. The other
        /// nodes of the module are not: the mapping a delivery flow renders with is how it reads, a cache type nothing
        /// fills is reported as unfilled, and neither holds data a stream could begin at.
        /// </summary>
        private static bool IsOrigin(string key)
            => !key.StartsWith(DatasetNodePrefix, StringComparison.Ordinal) || key.StartsWith(TypeNodePrefix, StringComparison.Ordinal);

        /// <summary>An object no flow writes, as a stream's start: a file folder, an OSDU kind, or a table.</summary>
        private static DeliveryStreamInputDto Input(string key)
        {
            var segments = Segments(key);
            if (segments.Length == 4 && segments[0] == "file")
            {
                return new DeliveryStreamInputDto("file", segments[3]);
            }

            if (segments.Length == 4 && segments[0].StartsWith("dataset:osdu-type", StringComparison.Ordinal))
            {
                return new DeliveryStreamInputDto("osdu", segments[3]);
            }

            return segments.Length == 4
                ? new DeliveryStreamInputDto("table", $"{segments[1]}.{segments[2]}.{segments[3]}")
                : new DeliveryStreamInputDto("table", key);
        }
    }
}
