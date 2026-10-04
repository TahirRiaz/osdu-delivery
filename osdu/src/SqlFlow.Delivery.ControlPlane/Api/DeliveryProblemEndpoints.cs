using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One problem keeping a flow's records blocked (docs/ledger.md, Problems): its id, the error its records share with every
/// part that names a record replaced, how many records it keeps blocked (held and failed), when they last changed, its
/// most recently changed record as an example, with that record's own error, and where it lies: <c>set</c> when its records
/// carry the same error (no value of their own, or the same values in each), <c>rows</c> when they name values of their
/// own rows. <c>Values</c> are the values the example names, which for a set error every record names.
/// </summary>
public sealed record DeliveryProblemDto(
    string Problem, string Pattern, long Records, long Held, long Failed, DateTime OldestUtc, DateTime NewestUtc, DeliveryRecordDto? Example,
    string Shape, IReadOnlyList<string> Values);

/// <summary>A record of a problem looked at closely, with the values its error names.</summary>
public sealed record DeliveryProblemSampleDto(DeliveryRecordDto Record, IReadOnlyList<string> Values);

/// <summary>
/// A flow's problems, the most records first: the ones listed, how many there are and how many records they keep blocked in
/// all, and how many blocked records are not sorted into a problem yet (blocked before the ledger kept problems).
/// </summary>
public sealed record DeliveryProblemListDto(IReadOnlyList<DeliveryProblemDto> Problems, long TotalProblems, long TotalRecords, long Unsorted);

/// <summary>An ingestion file some of a problem's records were left at, and how many; a null name for those naming none.</summary>
public sealed record DeliveryProblemFileDto(string? FileName, long Records);

/// <summary>
/// One problem with the files its records came from, the most records first, and samples spread evenly across its records
/// (its newest, its oldest and the ones between), which are what tell its shape: the problem's <c>Shape</c> here is the
/// samples' word, which knows more than the listing's two records.
/// </summary>
public sealed record DeliveryProblemDetailDto(DeliveryProblemDto Problem, IReadOnlyList<DeliveryProblemFileDto> Files, IReadOnlyList<DeliveryProblemSampleDto> Samples);

/// <summary>
/// A release of every record a problem keeps blocked. <c>Run</c> also queues a deliver run of the flow, read under the
/// parameter values the problem's example record was last planned with, which sends what the release queued and plans the
/// first records it asked to be planned again.
/// </summary>
public sealed record DeliveryProblemReleaseRequest(bool Run = false, string? Pool = null);

/// <summary>What a release of a problem's records did: how many it released, and the run it queued when asked to.</summary>
public sealed record DeliveryProblemReleaseResult(string Problem, int Released, Guid? RunId);

/// <summary>
/// The problems of a flow: what keeps its records blocked, grouped by the problem each record's error names, so an operator
/// facing a million blocked records reads a handful of problems, checks one record of a problem once its cause is fixed,
/// and releases the problem's records together. Every count is the ledger's, read through the problem index; a release
/// names every record it released under its activity, so each record's history shows it.
/// </summary>
public static class DeliveryProblemEndpoints
{
    /// <summary>The problems a listing names unless asked for fewer.</summary>
    private const int DefaultProblems = 100;

    /// <summary>The files a problem names.</summary>
    private const int MaxFiles = 50;

    /// <summary>The samples a problem names unless asked for another number.</summary>
    private const int DefaultSamples = 5;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/problems", ListProblemsAsync).WithName("ListDeliveryProblems");
        delivery.MapGet("/flows/{pipelineId:guid}/problems/{problem}", GetProblemAsync).WithName("GetDeliveryProblem");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/problems/{problem}/release", ReleaseProblemAsync).WithName("ReleaseDeliveryProblem");
    }

    /// <summary>The problems of one interface's ledger, the most records first, at most <paramref name="max"/>.</summary>
    private static async Task<Results<Ok<DeliveryProblemListDto>, ProblemHttpResult>> ListProblemsAsync(
        Guid pipelineId, int? max, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await DeliveryEndpoints.ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var listing = await ledger.ListProblemsAsync(flow.FlowId, Math.Clamp(max ?? DefaultProblems, 1, OsduLedger.MaxProblems), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryProblemListDto(listing.Problems.Select(ToDto).ToList(), listing.TotalProblems, listing.TotalRecords, listing.Unsorted));
    }

    /// <summary>One problem of one interface's ledger, with the files its records came from and samples spread across it.</summary>
    private static async Task<Results<Ok<DeliveryProblemDetailDto>, ProblemHttpResult>> GetProblemAsync(
        Guid pipelineId, string problem, int? samples, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        if (Parse(problem) is not { } hash)
        {
            return Invalid(problem);
        }

        var (flow, unresolved) = await DeliveryEndpoints.ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var group = await ledger.GetProblemAsync(flow.FlowId, hash, ct).ConfigureAwait(false);
        if (group is null)
        {
            return NoSuchProblem(problem, flow);
        }

        var files = await ledger.ListProblemFilesAsync(flow.FlowId, hash, MaxFiles, ct).ConfigureAwait(false);
        var sampled = await ledger.ListProblemSamplesAsync(flow.FlowId, hash, Math.Clamp(samples ?? DefaultSamples, 1, OsduLedger.MaxProblemSamples), ct).ConfigureAwait(false);
        var shape = sampled.Count == 0 ? group.Shape : ProblemSignature.ShapeOf(sampled.Select(s => s.LastError).Append(group.Example?.LastError));
        return TypedResults.Ok(new DeliveryProblemDetailDto(
            ToDto(group with { Shape = shape }),
            files.Select(f => new DeliveryProblemFileDto(f.FileName, f.Records)).ToList(),
            sampled.Select(s => new DeliveryProblemSampleDto(DeliveryEndpoints.ToDto(s), ProblemSignature.Values(s.LastError))).ToList()));
    }

    /// <summary>
    /// Releases every record the problem keeps blocked, as the caller, recorded on the audit trail with the problem and its
    /// pattern and with every record it released named under it. With <c>run</c>, a deliver run of the flow is queued too.
    /// </summary>
    private static async Task<Results<Ok<DeliveryProblemReleaseResult>, Accepted<DeliveryProblemReleaseResult>, ProblemHttpResult>> ReleaseProblemAsync(
        Guid pipelineId, string problem, DeliveryProblemReleaseRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Parse(problem) is not { } hash)
        {
            return Invalid(problem);
        }

        var (flow, unresolved) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        // The pattern the release records is the problem as the ledger names it now, which is what the operator was shown.
        var group = await ledger.GetProblemAsync(flow.FlowId, hash, ct).ConfigureAwait(false);
        if (group is null)
        {
            return NoSuchProblem(problem, flow);
        }

        using var runtime = FlowRuntime.ForTarget(await DeliveryEndpoints.ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseProblemAsync(hash, group.Pattern, ct).ConfigureAwait(false);
        var named = ProblemSignature.Format(hash);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryProblemReleaseResult(named, released, null));
        }

        // The run reads under the parameter values the example was last planned with, as a record's own run does, so a flow
        // whose scope is a parameter reads the records in the scope they were planned in.
        var last = group.Example?.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var runId = await DeliveryEndpoints.EnqueueRunAsync(db, dispatcher, flow, DeliveryEndpoints.DeliverRun(flow, last?.ParametersJson), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryProblemReleaseResult(named, released, runId));
    }

    /// <summary>A problem as a route names it: the sixteen characters the listing gives it.</summary>
    private static long? Parse(string? text)
        => ProblemSignature.TryParse(text?.Trim().ToLowerInvariant(), out var hash) ? hash : null;

    private static ProblemHttpResult Invalid(string? problem) => TypedResults.Problem(
        detail: $"'{problem}' is not a problem: a problem is the {ProblemSignature.TextLength} characters the flow's problems listing gives it.",
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid request");

    private static ProblemHttpResult NoSuchProblem(string problem, DeliveryEndpoints.FlowContext flow) => TypedResults.Problem(
        detail: $"No record of {flow.Flow.Label} is blocked by problem {problem}: its records were released, their source changed, or they were planned again since.",
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found");

    private static DeliveryProblemDto ToDto(ProblemGroup group) => new(
        ProblemSignature.Format(group.Problem), group.Pattern, group.Records, group.Held, group.Failed, group.OldestUtc, group.NewestUtc,
        group.Example is { } example ? DeliveryEndpoints.ToDto(example) : null,
        ShapeName(group.Shape), group.Values);

    /// <summary>A problem's shape as the API names it.</summary>
    private static string ShapeName(ProblemShape shape) => shape == ProblemShape.Rows ? "rows" : "set";
}
