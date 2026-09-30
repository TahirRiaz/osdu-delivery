using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>How the index stores a dimension's field, as its builds settled it.</summary>
public sealed record DeliveryDimensionFieldDto(string Index, string? NestedPath, string AggregateBy, bool Repeats);

/// <summary>What one build changed of a dimension.</summary>
public sealed record DeliveryDimensionChangesDto(
    long MembersAdded, long MembersRemoved, long MembersRestored, long OriginalsAdded, long OriginalsRemoved, long OriginalsMoved, long OriginalsRestored);

/// <summary>A kind a build read, with its records and the template it was read against.</summary>
public sealed record DeliveryDimensionKindDto(string Kind, long Records, string? Template);

/// <summary>
/// One build of a dimension: who ran it and what came of it, how it read (aggregations, ranges split, ranges scanned, count
/// queries), how complete the values are (records, records with a value, nulls, values too long for the index's exact field,
/// values not of the field's type), what it found, and what it changed.
/// </summary>
public sealed record DeliveryDimensionBuildDto(
    long DimensionRunId, int DimensionId, Guid? RunId, string Actor, string Status, DateTime StartedUtc, DateTime? CompletedUtc, string? Error,
    string DefinitionHash, string? Query, string? AggregateBy, long Members, long Originals, long LeftOut, long Unfilterable,
    long? Records, long? WithValue, long Nulls, long? TooLong, long Unreadable,
    int Aggregations, int Slices, int Splits, int ScannedSlices, int ScanPages, long ScannedUnits, int CountQueries,
    IReadOnlyList<DeliveryDimensionKindDto> Kinds, IReadOnlyList<string> Notes, DeliveryDimensionChangesDto Changes);

/// <summary>
/// One dimension of a dimension flow in a partition: what the flow declares of it (or, for one it no longer declares, what
/// its last build read with), how the index stores its field, what it holds now, the build that wrote that
/// (<c>Current</c>), and the newest build when that is another one (<c>Latest</c>: one that failed, was cancelled or is
/// running). <c>DimensionId</c> is null for a dimension no build has registered yet. <c>Changed</c> is true when the
/// declaration differs from the one the values it holds were built with.
/// </summary>
public sealed record DeliveryDimensionDto(
    int? DimensionId, string Name, string? Description, string Kind, string? Query, string? BuiltQuery, string Path, IReadOnlyList<string> Clean,
    bool CountRecords, long MaxValues, bool Declared, bool BuildsHere, bool Changed, DeliveryDimensionFieldDto? Field, long Members, long Originals,
    DateTime? LastBuiltUtc, DeliveryDimensionBuildDto? Current, DeliveryDimensionBuildDto? Latest);

/// <summary>
/// One dimension flow in the partition a board is read in: its dimensions, the partitions it builds in and whether the
/// board's is one, the parameters a run of it takes, and what keeps it from being shown (a document the catalog cannot parse,
/// a partition it does not build in).
/// </summary>
public sealed record DeliveryDimensionFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Description, string? Batch, string? Partition, bool BuildsPartition, IReadOnlyList<string> Partitions,
    Guid? LedgerId, IReadOnlyList<DeliveryParameterDto> Parameters, string? Problem, IReadOnlyList<DeliveryDimensionDto> Dimensions);

/// <summary>What a board adds up to, over the dimensions of the flows that build in its partition.</summary>
public sealed record DeliveryDimensionTotalsDto(
    int Flows, int Dimensions, int Built, int NotBuilt, int Failing, int Running, int Changed, long Members, long Originals);

/// <summary>The dimensions of every dimension flow, or of one, in a partition.</summary>
public sealed record DeliveryDimensionBoardDto(string? Partition, DeliveryDimensionTotalsDto Totals, IReadOnlyList<DeliveryDimensionFlowDto> Flows);

/// <summary>One dimension with the flow that declares it: the pipeline to build it with, when the catalog still holds the flow.</summary>
public sealed record DeliveryDimensionDetailDto(
    Guid? PipelineId, Guid? RepoId, string FlowName, Guid LedgerId, string? Partition, IReadOnlyList<DeliveryParameterDto> Parameters, DeliveryDimensionDto Dimension);

/// <summary>An original as a list of members shows it beside its member: its text and its count.</summary>
public sealed record DeliveryDimensionOriginalBriefDto(string Original, long Count);

/// <summary>
/// A member of a dimension: its clean value, the records holding any of its originals (exact, or the sum of its originals'
/// counts), its originals and those no query can carry, its search filter when one query holds it, when it arrived and when
/// a build stopped finding it, and the originals most records hold (<c>Top</c>).
/// </summary>
public sealed record DeliveryDimensionMemberDto(
    long MemberId, string Value, long Records, bool RecordsExact, int Originals, int Unfilterable, string? Filter, int FilterParts,
    long FirstSeenRunId, DateTime FirstSeenUtc, long? RemovedRunId, DateTime? RemovedUtc, IReadOnlyList<DeliveryDimensionOriginalBriefDto> Top);

/// <summary>A page of members, with the cursor of the next page; null when this is the last.</summary>
public sealed record DeliveryDimensionMemberPageDto(IReadOnlyList<DeliveryDimensionMemberDto> Items, string? Next);

/// <summary>An original of a dimension: its text exactly as the index holds it, its member or why it has none, and its count.</summary>
public sealed record DeliveryDimensionValueDto(
    long ValueId, string Original, long? MemberId, string? Member, string? LeftOut, string? Note, long Count, bool Filterable,
    long FirstSeenRunId, DateTime FirstSeenUtc, long MemberSinceRunId, long? RemovedRunId, DateTime? RemovedUtc);

/// <summary>A page of originals, with the cursor of the next page; null when this is the last.</summary>
public sealed record DeliveryDimensionValuePageDto(IReadOnlyList<DeliveryDimensionValueDto> Items, string? Next);

/// <summary>A change a build made to one original.</summary>
public sealed record DeliveryDimensionChangeDto(
    long ChangeId, long DimensionRunId, long ValueId, string Original, string Change, long? FromMemberId, string? FromValue, long? ToMemberId, string? ToValue,
    DateTime ChangedUtc);

/// <summary>A page of the change log, newest first, with the change the next page starts before; null when this is the last.</summary>
public sealed record DeliveryDimensionChangePageDto(IReadOnlyList<DeliveryDimensionChangeDto> Items, long? Next);

/// <summary>The members a filter is asked for by: by id, by clean value, or both.</summary>
public sealed record DeliveryDimensionFilterRequest(IReadOnlyList<long>? MemberIds, IReadOnlyList<string>? Values);

/// <summary>A member a filter covers.</summary>
public sealed record DeliveryDimensionFilterMemberDto(long MemberId, string Value, long Records, bool RecordsExact, int Originals);

/// <summary>
/// The search filter of a set of members: the kind to search, the filter queries alone and each joined with the dimension's
/// own query (the searches to send), the members it covers, and what it leaves out: originals no query can carry, members
/// no build finds any more, and names that are no member.
/// </summary>
public sealed record DeliveryDimensionFilterDto(
    string Kind, string? Query, string AggregateBy, IReadOnlyList<string> Filters, IReadOnlyList<string> Searches, IReadOnlyList<DeliveryDimensionFilterMemberDto> Members,
    int Originals, int Unfilterable, IReadOnlyList<string> UnfilterableNamed, IReadOnlyList<string> Removed, IReadOnlyList<string> Missing);

/// <summary>
/// A member with the whole of what the ledger holds of it: its originals, most records first; its filter, or why none can be
/// written; and the changes that brought originals to it or took them away, newest first.
/// </summary>
public sealed record DeliveryDimensionMemberDetailDto(
    DeliveryDimensionMemberDto Member, IReadOnlyList<DeliveryDimensionValueDto> Originals, bool MoreOriginals, DeliveryDimensionFilterDto? Filter,
    string? FilterProblem, IReadOnlyList<DeliveryDimensionChangeDto> History);

/// <summary>A build a platform run made, with the name of the dimension it built.</summary>
public sealed record DeliveryDimensionRunBuildDto(string Dimension, DeliveryDimensionBuildDto Build);

/// <summary>
/// The dimensions of dimension flows (docs/dimension-plan.md, Stage 5): boards across every dimension flow and for one, a
/// dimension with its declaration and builds, its members and originals a page at a time (searched, in value order or with
/// the most records first), a member with its originals, filter and history, the change log, the filter of any set of
/// members, an export of the whole, and the builds of a platform run. Every read answers from the ledger; nothing here talks
/// to OSDU, and building a dimension is a run like any other, queued through the platform's trigger.
/// </summary>
public static class DeliveryDimensionEndpoints
{
    /// <summary>The members or originals a page holds when the request names no limit.</summary>
    public const int DefaultPage = 100;

    /// <summary>The builds a dimension's history lists when the request names no limit.</summary>
    public const int DefaultBuilds = 30;

    /// <summary>The most dimension flows one board shows.</summary>
    private const int MaxFlows = 500;

    /// <summary>The longest search text: an original is at most this long.</summary>
    private const int MaxSearchLength = 1024;

    /// <summary>The originals a page of members shows beside each member.</summary>
    private const int TopOriginals = 5;

    /// <summary>The originals a member's page lists; a member with more pages through the originals list.</summary>
    private const int MemberOriginals = 500;

    /// <summary>The changes a member's page lists.</summary>
    private const int MemberHistory = 200;

    private static readonly string[] ChangeKinds =
        [DimensionChangeKinds.Added, DimensionChangeKinds.Removed, DimensionChangeKinds.Moved, DimensionChangeKinds.Restored];

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/dimensions", GetBoardAsync).WithName("GetDeliveryDimensionBoard");
        delivery.MapGet("/flows/{pipelineId:guid}/dimensions", GetFlowBoardAsync).WithName("GetDeliveryDimensionFlowBoard");
        delivery.MapGet("/dimensions/{dimensionId:int}", GetDimensionAsync).WithName("GetDeliveryDimension");
        delivery.MapGet("/dimensions/{dimensionId:int}/members", ListMembersAsync).WithName("ListDeliveryDimensionMembers");
        delivery.MapGet("/dimensions/{dimensionId:int}/members/{memberId:long}", GetMemberAsync).WithName("GetDeliveryDimensionMember");
        delivery.MapGet("/dimensions/{dimensionId:int}/values", ListValuesAsync).WithName("ListDeliveryDimensionValues");
        delivery.MapGet("/dimensions/{dimensionId:int}/builds", ListBuildsAsync).WithName("ListDeliveryDimensionBuilds");
        delivery.MapGet("/dimensions/{dimensionId:int}/changes", ListChangesAsync).WithName("ListDeliveryDimensionChanges");
        delivery.MapGet("/dimensions/{dimensionId:int}/export", ExportAsync).WithName("ExportDeliveryDimension");
        delivery.MapGet("/runs/{runId:guid}/dimension-builds", ListRunBuildsAsync).WithName("ListDeliveryRunDimensionBuilds");

        // Writing a filter reads the ledger and changes nothing; the members ride in the body, since a set of them can be long.
        delivery.MapPost("/dimensions/{dimensionId:int}/filter", FilterAsync).WithName("GetDeliveryDimensionFilter");
    }

    /// <summary>Every active dimension flow of the catalog, each in the partition the request reads.</summary>
    private static async Task<Ok<DeliveryDimensionBoardDto>> GetBoardAsync(
        string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry,
        CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        var named = WorkbenchPartition.Named(partition, request);
        var flows = await BoardAsync(pipelines, named, documents, ledger, registry, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionBoardDto(named, Totals(flows), flows));
    }

    /// <summary>One dimension flow's board.</summary>
    private static async Task<Results<Ok<DeliveryDimensionBoardDto>, ProblemHttpResult>> GetFlowBoardAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not a dimension flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a dimension flow.");
        }

        var named = WorkbenchPartition.Named(partition, request);
        var flows = await BoardAsync([pipeline], named, documents, ledger, registry, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionBoardDto(flows[0].Partition ?? named, Totals(flows), flows));
    }

    private static async Task<Results<Ok<DeliveryDimensionDetailDto>, ProblemHttpResult>> GetDimensionAsync(
        int dimensionId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var (pipeline, flow, spec) = await DeclarationAsync(db, documents, dimension, ct).ConfigureAwait(false);
        var builds = await BuildsAsync(ledger, [dimension], ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionDetailDto(
            pipeline?.Id, pipeline?.RepoId, dimension.FlowName, dimension.FlowId, dimension.Partition, flow is null ? [] : Parameters(flow),
            ToDto(spec, dimension.Partition, dimension, builds)));
    }

    private static async Task<Results<Ok<DeliveryDimensionMemberPageDto>, ProblemHttpResult>> ListMembersAsync(
        int dimensionId, string? search, string? order, bool? removed, string? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        DimensionMemberOrder sorted;
        switch (order?.Trim().ToLowerInvariant())
        {
            case null or "" or "value":
                sorted = DimensionMemberOrder.Value;
                break;
            case "records":
                sorted = DimensionMemberOrder.Records;
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "Unknown order", $"order '{order}' is not one of value, records.");
        }

        DimensionMemberCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            cursor = MemberCursorOf(after);
            if (cursor is null)
            {
                return BadCursor(after);
            }
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var take = PageSize(limit);
        var members = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(search, removed ?? false, cursor, take, sorted), ct).ConfigureAwait(false);
        var top = (await ledger.TopMemberOriginalsAsync(dimensionId, members.Select(m => m.MemberId).ToList(), TopOriginals, ct).ConfigureAwait(false))
            .GroupBy(v => v.MemberId ?? 0)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DeliveryDimensionOriginalBriefDto>)g
                .OrderByDescending(v => v.Count).ThenBy(v => v.ValueId)
                .Select(v => new DeliveryDimensionOriginalBriefDto(v.Original, v.Count)).ToList());
        var items = members.Select(m => ToDto(m, top.GetValueOrDefault(m.MemberId) ?? [])).ToList();
        return TypedResults.Ok(new DeliveryDimensionMemberPageDto(items, members.Count == take ? MemberCursor(members[^1]) : null));
    }

    private static async Task<Results<Ok<DeliveryDimensionMemberDetailDto>, ProblemHttpResult>> GetMemberAsync(
        int dimensionId, long memberId, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var named = await ledger.GetDimensionMembersAsync(dimensionId, [memberId], [], ct).ConfigureAwait(false);
        var member = named.Count > 0 ? named[0] : null;
        if (member is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Dimension {dimension.Name} has no member {memberId}.");
        }

        var originals = await ledger.ListDimensionValuesAsync(
            dimensionId, new DimensionValueQuery(null, memberId, false, false, null, MemberOriginals + 1, DimensionValueOrder.Count), ct).ConfigureAwait(false);
        var history = await ledger.ListDimensionChangesAsync(dimensionId, new DimensionChangeQuery(null, null, memberId, null, null, MemberHistory), ct).ConfigureAwait(false);
        DeliveryDimensionFilterDto? filter = null;
        string? filterProblem = null;
        if (member.RemovedRunId is not null)
        {
            filterProblem = "No build finds this member any more, so it holds no original a filter could find.";
        }
        else
        {
            try
            {
                filter = ToDto(await DimensionFilters.ForMembersAsync(ledger, dimension, [memberId], [], ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is DeliveryException or OsduQueryException)
            {
                filterProblem = ex.Message;
            }
        }

        var top = originals.Take(TopOriginals).Select(v => new DeliveryDimensionOriginalBriefDto(v.Original, v.Count)).ToList();
        return TypedResults.Ok(new DeliveryDimensionMemberDetailDto(
            ToDto(member, top), originals.Take(MemberOriginals).Select(ToDto).ToList(), originals.Count > MemberOriginals, filter, filterProblem,
            history.Select(ToDto).ToList()));
    }

    private static async Task<Results<Ok<DeliveryDimensionValuePageDto>, ProblemHttpResult>> ListValuesAsync(
        int dimensionId, string? search, long? member, bool? leftOut, bool? removed, string? order, string? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (SearchProblem(search) is { } badSearch)
        {
            return badSearch;
        }

        DimensionValueOrder sorted;
        switch (order?.Trim().ToLowerInvariant())
        {
            case null or "" or "arrival":
                sorted = DimensionValueOrder.Arrival;
                break;
            case "count":
                sorted = DimensionValueOrder.Count;
                break;
            default:
                return Problem(StatusCodes.Status400BadRequest, "Unknown order", $"order '{order}' is not one of arrival, count.");
        }

        if (member is not null && leftOut == true)
        {
            return Problem(StatusCodes.Status400BadRequest, "Conflicting filters", "An original under a member is not left out: ask for a member's originals or for those under none, not both.");
        }

        DimensionValueCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            cursor = ValueCursorOf(after);
            if (cursor is null)
            {
                return BadCursor(after);
            }
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var take = PageSize(limit);
        var values = await ledger.ListDimensionValuesAsync(
            dimensionId, new DimensionValueQuery(search, member, leftOut ?? false, removed ?? false, cursor, take, sorted), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionValuePageDto(values.Select(ToDto).ToList(), values.Count == take ? ValueCursor(values[^1]) : null));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryDimensionBuildDto>>, ProblemHttpResult>> ListBuildsAsync(
        int dimensionId, int? max, ILedger ledger, CancellationToken ct)
    {
        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var runs = await ledger.ListDimensionRunsAsync(dimensionId, Math.Clamp(max ?? DefaultBuilds, 1, 500), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryDimensionBuildDto>>(runs.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliveryDimensionChangePageDto>, ProblemHttpResult>> ListChangesAsync(
        int dimensionId, long? build, long? value, long? member, string? change, long? before, int? limit, ILedger ledger, CancellationToken ct)
    {
        var kind = string.IsNullOrWhiteSpace(change) ? null : change.Trim().ToLowerInvariant();
        if (kind is not null && !ChangeKinds.Contains(kind, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown change", $"change '{change}' is not one of {string.Join(", ", ChangeKinds)}.");
        }

        if (await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false) is null)
        {
            return NoDimension(dimensionId);
        }

        var take = PageSize(limit);
        var changes = await ledger.ListDimensionChangesAsync(dimensionId, new DimensionChangeQuery(build, value, member, kind, before, take), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryDimensionChangePageDto(changes.Select(ToDto).ToList(), changes.Count == take ? changes[^1].ChangeId : null));
    }

    private static async Task<Results<Ok<DeliveryDimensionFilterDto>, ProblemHttpResult>> FilterAsync(
        int dimensionId, DeliveryDimensionFilterRequest? body, ILedger ledger, CancellationToken ct)
    {
        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        var ids = body?.MemberIds ?? [];
        var values = (body?.Values ?? []).Where(v => v is not null).ToList();
        if (values.Any(v => v.Length > DimensionSpec.MaxCleanLength))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid member", $"A clean value is at most {DimensionSpec.MaxCleanLength} characters.");
        }

        try
        {
            return TypedResults.Ok(ToDto(await DimensionFilters.ForMembersAsync(ledger, dimension, ids, values, ct).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is DeliveryException or OsduQueryException)
        {
            return Problem(StatusCodes.Status400BadRequest, "No filter", ex.Message);
        }
    }

    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportAsync(
        int dimensionId, string? set, string? format, ILedger ledger, CancellationToken ct)
    {
        if (DimensionExport.SetOf(set) is not { } chosenSet)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown set", $"set '{set}' is not one of members, originals.");
        }

        if (DimensionExport.FormatOf(format) is not { } chosenFormat)
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown format", $"format '{format}' is not one of csv, jsonl.");
        }

        var dimension = await ledger.GetDimensionAsync(dimensionId, ct).ConfigureAwait(false);
        if (dimension is null)
        {
            return NoDimension(dimensionId);
        }

        // The export is written as it is read, a ledger page at a time, so a dimension of millions of originals is never held
        // whole; the request's own cancellation stops it when the caller goes away.
        return TypedResults.Stream(
            stream => DimensionExport.WriteAsync(ledger, dimension, chosenSet, chosenFormat, stream, ct),
            DimensionExport.MediaType(chosenFormat),
            DimensionExport.FileName(dimension, chosenSet, chosenFormat));
    }

    private static async Task<Ok<IReadOnlyList<DeliveryDimensionRunBuildDto>>> ListRunBuildsAsync(Guid runId, ILedger ledger, CancellationToken ct)
    {
        var runs = await ledger.DimensionRunsOfAsync(runId, ct).ConfigureAwait(false);
        var names = new Dictionary<int, string>();
        foreach (var id in runs.Select(r => r.DimensionId).Distinct())
        {
            names[id] = (await ledger.GetDimensionAsync(id, ct).ConfigureAwait(false))?.Name ?? id.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryDimensionRunBuildDto>>(runs.Select(r => new DeliveryDimensionRunBuildDto(names[r.DimensionId], ToDto(r))).ToList());
    }

    /// <summary>
    /// Each flow bound to the board's partition as a run binds it, with its dimensions: those it declares, then those the
    /// ledger keeps that it no longer declares, each with the build that wrote what it holds and its newest build.
    /// </summary>
    private static async Task<IReadOnlyList<DeliveryDimensionFlowDto>> BoardAsync(
        IReadOnlyList<CatalogPipeline> pipelines, string? partition, DeliveryDocumentLoader documents, ILedger ledger, IPartitionRegistry registry,
        CancellationToken ct)
    {
        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        var settled = new List<(CatalogPipeline Pipeline, DimensionFlowDefinition? Flow, DimensionFlowDefinition? Bound, string? Partition, string? Problem)>();
        foreach (var pipeline in pipelines)
        {
            DimensionFlowDefinition flow;
            try
            {
                flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException ex)
            {
                settled.Add((pipeline, null, null, partition, $"The catalog's copy of the flow does not parse: {ex.Message} Re-sync the repository."));
                continue;
            }

            var (bound, kept, why) = await SettleAsync(flow, partition, ledger, registered, ct).ConfigureAwait(false);
            settled.Add((pipeline, flow, bound, kept, why));
        }

        var held = new Dictionary<Guid, IReadOnlyList<DimensionState>>();
        foreach (var bound in settled.Select(s => s.Bound).OfType<DimensionFlowDefinition>())
        {
            if (!held.ContainsKey(bound.LedgerId))
            {
                held[bound.LedgerId] = await ledger.ListDimensionsAsync(null, bound.LedgerId, ct).ConfigureAwait(false);
            }
        }

        var builds = await BuildsAsync(ledger, held.Values.SelectMany(d => d).ToList(), ct).ConfigureAwait(false);
        var flows = new List<DeliveryDimensionFlowDto>(settled.Count);
        foreach (var (pipeline, flow, bound, kept, problem) in settled)
        {
            if (flow is null || bound is null)
            {
                flows.Add(new DeliveryDimensionFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, flow?.Description, flow?.Batch ?? pipeline.Batch, kept, false, flow?.Served(registered) ?? [], null,
                    flow is null ? [] : Parameters(flow), problem, []));
                continue;
            }

            flows.Add(new DeliveryDimensionFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, bound.Description, bound.Batch ?? pipeline.Batch, kept, true, bound.Served(registered), bound.LedgerId,
                Parameters(bound), null, Dimensions(bound, kept, held.GetValueOrDefault(bound.LedgerId) ?? [], builds)));
        }

        return flows;
    }

    /// <summary>
    /// A flow bound to the partition asked for, as a run binds it, with the partition its dimensions are kept in; for a flow
    /// whose partition is its header's, the flow as it is when that partition is the one its ledger is kept under (or it has
    /// not built yet, or none is asked for). Null with the reason otherwise.
    /// </summary>
    private static async Task<(DimensionFlowDefinition? Flow, string? Partition, string? Why)> SettleAsync(
        DimensionFlowDefinition flow, string? partition, ILedger ledger, RegisteredPartitions registered, CancellationToken ct)
    {
        if (!flow.Partitioned)
        {
            var kept = (await ledger.GetLedgerAsync(flow.LedgerId, ct).ConfigureAwait(false))?.Partition;
            return partition is null || kept is null || string.Equals(kept, partition, StringComparison.OrdinalIgnoreCase)
                ? (flow, kept ?? partition, null)
                : (null, kept, $"Dimension flow '{flow.Name}' builds in the partition its source.headers name, '{kept}', not '{partition}'.");
        }

        try
        {
            var bound = flow.ForRun(partition, registered);
            return (bound, bound.Partition, null);
        }
        catch (DeliveryException ex)
        {
            return (null, partition, ex.Message);
        }
    }

    /// <summary>
    /// The pipeline declaring <paramref name="dimension"/> (the active one first), the flow bound to the dimension's partition,
    /// and its declaration of the dimension; nulls for what the catalog no longer holds.
    /// </summary>
    private static async Task<(CatalogPipeline? Pipeline, DimensionFlowDefinition? Flow, DimensionSpec? Spec)> DeclarationAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, DimensionState dimension, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == DimensionFlowDefinition.FlowTypeName && p.Name == dimension.FlowName)
            .OrderByDescending(p => p.Active)
            .Take(20)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var pipeline in pipelines)
        {
            DimensionFlowDefinition flow;
            try
            {
                flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
                if (flow.Partitioned && dimension.Partition is { } partition)
                {
                    flow = flow.ForPartition(partition);
                }
            }
            catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
            {
                continue;
            }

            // The pipeline declares the dimension kept here only when its ledger is the one the dimension was built under.
            if ((flow.Partitioned && flow.Partition is null) || flow.LedgerId != dimension.FlowId)
            {
                continue;
            }

            return (pipeline, flow, flow.Dimension(dimension.Name));
        }

        return (pipelines.FirstOrDefault(p => p.Active), null, null);
    }

    private static List<DeliveryDimensionDto> Dimensions(DimensionFlowDefinition flow, string? partition, IReadOnlyList<DimensionState> held, BuildIndex builds)
    {
        var byName = held.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = flow.Dimensions.Select(spec => ToDto(spec, partition, byName.GetValueOrDefault(spec.Name), builds)).ToList();
        list.AddRange(held.Where(d => flow.Dimension(d.Name) is null).Select(d => ToDto(null, partition, d, builds)));
        return list;
    }

    /// <summary>The newest build of each dimension, and every build that wrote what a dimension holds, read together.</summary>
    private static async Task<BuildIndex> BuildsAsync(ILedger ledger, IReadOnlyList<DimensionState> dimensions, CancellationToken ct)
    {
        if (dimensions.Count == 0)
        {
            return new BuildIndex(new Dictionary<int, DimensionRunState>(), new Dictionary<long, DimensionRunState>());
        }

        var latest = (await ledger.LatestDimensionRunsAsync(dimensions.Select(d => d.DimensionId).ToList(), ct).ConfigureAwait(false))
            .ToDictionary(r => r.DimensionId);
        var byId = latest.Values.ToDictionary(r => r.DimensionRunId);
        var wanted = dimensions.Select(d => d.LastRunId).OfType<long>().Where(id => !byId.ContainsKey(id)).ToList();
        foreach (var run in await ledger.GetDimensionRunsAsync(wanted, ct).ConfigureAwait(false))
        {
            byId[run.DimensionRunId] = run;
        }

        return new BuildIndex(latest, byId);
    }

    private static DeliveryDimensionTotalsDto Totals(IReadOnlyList<DeliveryDimensionFlowDto> flows)
    {
        var building = flows.Where(f => f.BuildsPartition).ToList();
        var dimensions = building.SelectMany(f => f.Dimensions).Where(d => d.Declared && d.BuildsHere).ToList();
        return new DeliveryDimensionTotalsDto(
            building.Count,
            dimensions.Count,
            dimensions.Count(d => d.Current is not null),
            dimensions.Count(d => d.Current is null),
            dimensions.Count(d => d.Latest is { Status: DimensionRunStatus.Failed }),
            dimensions.Count(d => d.Latest is { Status: DimensionRunStatus.Running }),
            dimensions.Count(d => d.Changed),
            dimensions.Sum(d => d.Members),
            dimensions.Sum(d => d.Originals));
    }

    private static DeliveryDimensionDto ToDto(DimensionSpec? spec, string? partition, DimensionState? state, BuildIndex builds)
    {
        var current = state?.LastRunId is { } id && builds.ById.TryGetValue(id, out var wrote) ? wrote : null;
        var latest = state is not null && builds.Latest.TryGetValue(state.DimensionId, out var newest) && newest.DimensionRunId != current?.DimensionRunId ? newest : null;
        var steps = spec?.Clean ?? DimensionRunner.StepsOf(state?.CleanJson);
        return new DeliveryDimensionDto(
            state?.DimensionId,
            spec?.Name ?? state!.Name,
            spec?.Description ?? state?.Description,
            spec?.Kind ?? state!.Kind,
            spec?.Query ?? state?.Query,
            current?.Query ?? state?.Query,
            spec?.Path ?? state!.Path,
            steps.Select(s => s.Describe()).ToList(),
            spec?.CountRecords ?? false,
            spec?.MaxValues ?? DimensionSpec.DefaultMaxValues,
            spec is not null,
            spec?.BuildsIn(partition) ?? false,
            spec is not null && current is not null && !string.Equals(spec.DefinitionHash, current.DefinitionHash, StringComparison.Ordinal),
            state?.Field is { } field ? new DeliveryDimensionFieldDto(field.Index, field.NestedPath, field.AggregateBy, field.Repeats) : null,
            state?.Members ?? 0,
            state?.Originals ?? 0,
            state?.LastBuiltUtc,
            current is null ? null : ToDto(current),
            latest is null ? null : ToDto(latest));
    }

    private static DeliveryDimensionBuildDto ToDto(DimensionRunState r) => new(
        r.DimensionRunId, r.DimensionId, r.RunId, r.Actor, r.Status, r.StartedUtc, r.CompletedUtc, r.Error, r.DefinitionHash, r.Query, r.AggregateBy,
        r.Members, r.Originals, r.LeftOut, r.Unfilterable, r.Read.Records, r.Read.WithValue, r.Read.Nulls, r.Read.TooLong, r.Read.Unreadable,
        r.Read.Aggregations, r.Read.Slices, r.Read.Splits, r.Read.ScannedSlices, r.Read.ScanPages, r.Read.ScannedUnits, r.Read.CountQueries,
        DimensionRunner.KindsOf(r.Read.Templates).Select(k => new DeliveryDimensionKindDto(k.Kind, k.Records, k.Template)).ToList(),
        r.Read.Notes,
        new DeliveryDimensionChangesDto(
            r.Changes.MembersAdded, r.Changes.MembersRemoved, r.Changes.MembersRestored, r.Changes.OriginalsAdded, r.Changes.OriginalsRemoved,
            r.Changes.OriginalsMoved, r.Changes.OriginalsRestored));

    private static DeliveryDimensionMemberDto ToDto(DimensionMemberState m, IReadOnlyList<DeliveryDimensionOriginalBriefDto> top) => new(
        m.MemberId, m.Value, m.Records, m.RecordsExact, m.Originals, m.Unfilterable, m.Filter, m.FilterParts, m.FirstSeenRunId, m.FirstSeenUtc, m.RemovedRunId,
        m.RemovedUtc, top);

    private static DeliveryDimensionValueDto ToDto(DimensionValueState v) => new(
        v.ValueId, v.Original, v.MemberId, v.MemberValue, v.LeftOut, v.Note, v.Count, v.Filterable, v.FirstSeenRunId, v.FirstSeenUtc, v.MemberSinceRunId,
        v.RemovedRunId, v.RemovedUtc);

    private static DeliveryDimensionChangeDto ToDto(DimensionChangeState c) => new(
        c.ChangeId, c.DimensionRunId, c.ValueId, c.Original, c.Change, c.FromMemberId, c.FromValue, c.ToMemberId, c.ToValue, c.ChangedUtc);

    private static DeliveryDimensionFilterDto ToDto(DimensionFilterSet f) => new(
        f.Kind, f.Query, f.AggregateBy, f.Filters, f.Searches,
        f.Members.Select(m => new DeliveryDimensionFilterMemberDto(m.MemberId, m.Value, m.Records, m.RecordsExact, m.Originals)).ToList(),
        f.Originals, f.Unfilterable, f.UnfilterableNamed, f.Removed, f.Missing);

    /// <summary>The parameters a run of the flow takes, in the order the document declares them.</summary>
    private static List<DeliveryParameterDto> Parameters(DimensionFlowDefinition flow)
        => flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();

    private static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPage, 1, OsduLedger.MaxDimensionPage);

    private static ProblemHttpResult? SearchProblem(string? search)
        => search is { Length: > MaxSearchLength }
            ? Problem(StatusCodes.Status400BadRequest, "Search too long", $"A search is at most {MaxSearchLength} characters, the longest original a dimension keeps.")
            : null;

    /// <summary>
    /// The cursor a page of members ends at: the last member's records and clean value, carried opaquely so the next request
    /// hands it back as it was given.
    /// </summary>
    internal static string MemberCursor(DimensionMemberState last)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{last.Records}:{last.Value}")));

    /// <summary>The member cursor a request handed back, or null when it is not one this API gave.</summary>
    internal static DimensionMemberCursor? MemberCursorOf(string text)
    {
        if (Decoded(text) is not { } decoded)
        {
            return null;
        }

        // The records come first and hold no colon; the clean value is everything after the first one, colons included.
        var at = decoded.IndexOf(':', StringComparison.Ordinal);
        var value = at > 0 ? decoded[(at + 1)..] : string.Empty;
        return value.Length is > 0 and <= DimensionSpec.MaxCleanLength
            && long.TryParse(decoded.AsSpan(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out var records)
                ? new DimensionMemberCursor(value, records)
                : null;
    }

    /// <summary>The cursor a page of originals ends at: the last original's count and id.</summary>
    internal static string ValueCursor(DimensionValueState last)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{last.Count}:{last.ValueId}")));

    /// <summary>The original cursor a request handed back, or null when it is not one this API gave.</summary>
    internal static DimensionValueCursor? ValueCursorOf(string text)
    {
        if (Decoded(text) is not { } decoded || decoded.Split(':') is not [var count, var id])
        {
            return null;
        }

        return long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted)
            && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valueId)
                ? new DimensionValueCursor(valueId, counted)
                : null;
    }

    private static string? Decoded(string text)
    {
        if (text.Length > 2048)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(text.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ProblemHttpResult BadCursor(string after)
        => Problem(StatusCodes.Status400BadRequest, "Invalid cursor", $"'{(after.Length > 60 ? after[..60] + "..." : after)}' is not a cursor a page of this dimension gave; start from the first page.");

    private static ProblemHttpResult NoDimension(int dimensionId)
        => Problem(StatusCodes.Status404NotFound, "Not found", string.Create(CultureInfo.InvariantCulture, $"No dimension {dimensionId}."));

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);

    private sealed record BuildIndex(IReadOnlyDictionary<int, DimensionRunState> Latest, IReadOnlyDictionary<long, DimensionRunState> ById);
}
