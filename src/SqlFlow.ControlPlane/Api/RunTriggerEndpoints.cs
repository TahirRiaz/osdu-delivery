using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The body that triggers a run: references only (the repo, the flow name, an optional target pool, and an
/// optional commit SHA). A secret is never accepted here; the worker resolves every credential from the executing
/// host's own environment. <c>pool</c> routes the run to a node serving that pool (omit for any node);
/// <c>commitSha</c> pins the run to an exact git version the node materializes. Omitting it pins the run to the
/// repo's last synced commit (so any node can execute it, and the executed version always matches what the catalog
/// shows); only a repo with no resolvable synced commit runs unpinned from the node's local copy.</summary>
public sealed record RunTriggerRequest(Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null);

/// <summary>The accepted-run acknowledgement: the minted run id and its queued status. The run executes
/// asynchronously; poll <c>GET /api/v1/runs/{runId}</c> (the <c>Location</c> header) for the outcome.</summary>
public sealed record RunTriggerAccepted(Guid RunId, string Status);

/// <summary>
/// The run-trigger surface: <c>POST /api/v1/runs</c>. It validates the request (a non-blank flow name, and an
/// active pipeline in the catalog) and enqueues the run through the <see cref="IRunDispatcher"/>, returning
/// <c>202 Accepted</c> with the new run id and a <c>Location</c> pointing at the existing run-detail endpoint.
/// Triggering a run is privileged, so this is mapped under the "operate" scope (never the read group). The
/// contract is references-only: no secret is accepted in the request or echoed in the response.
/// </summary>
public static class RunTriggerEndpoints
{
    public static RouteGroupBuilder MapRunTriggerEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/runs", TriggerRunAsync)
            .WithTags("Runs")
            .WithName("TriggerRun")
            .RequireAuthorization("operate");

        group.MapPost("/runs/{runId:guid}/cancel", CancelRunAsync)
            .WithTags("Runs")
            .WithName("CancelRun")
            .RequireAuthorization("operate");

        return group;
    }

    private static async Task<Results<Accepted<RunTriggerAccepted>, ProblemHttpResult>> TriggerRunAsync(
        RunTriggerRequest request, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FlowName))
        {
            return TypedResults.Problem(
                detail: "A run trigger requires a non-blank flowName.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        // An explicit pin must at least look like a git object id; catching garbage here (the trust boundary)
        // beats queueing a run every node is guaranteed to fail materializing.
        if (request.CommitSha is not null && !IsPlausibleCommitSha(request.CommitSha.Trim()))
        {
            return TypedResults.Problem(
                detail: "commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        var flowName = request.FlowName.Trim();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, flowName);

        // The pipeline must exist AND be active in the catalog: an unknown or soft-deactivated flow is a 404, so a
        // run is never queued for something the estate no longer exposes. The kind is carried onto the queued run.
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.RepoId == request.RepoId)
            .Select(p => new { p.Active, p.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is not { Active: true })
        {
            return TypedResults.Problem(
                detail: $"No active pipeline '{flowName}' in repo '{request.RepoId}'.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found");
        }

        var runId = await dispatcher.EnqueueAsync(
            db, new RunEnqueueRequest(request.RepoId, flowName, pipeline.Kind, request.Pool, request.CommitSha), ct).ConfigureAwait(false);

        // 202 with the canonical run-detail location: GET /api/v1/runs/{runId} reflects the run from the moment it
        // is queued (status "queued"), through running, to its terminal state.
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new RunTriggerAccepted(runId, "queued"));
    }

    private static bool IsPlausibleCommitSha(string sha)
        => sha.Length is >= 4 and <= 64 && sha.All(char.IsAsciiHexDigit);

    private static async Task<Results<Ok<RunTriggerAccepted>, ProblemHttpResult>> CancelRunAsync(
        Guid runId, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await RunQueueStore.CancelAsync(db, runId, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return outcome switch
        {
            CancelOutcome.Cancelled => TypedResults.Ok(new RunTriggerAccepted(runId, "cancelled")),
            CancelOutcome.NotFound => TypedResults.Problem(
                detail: $"No run '{runId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found"),
            // A run already claimed for execution or finished cannot be cancelled through the queue.
            _ => TypedResults.Problem(
                detail: $"Run '{runId}' is no longer queued and cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
        };
    }
}
