using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The run trigger: references only (repo + flow name), an optional target pool, an optional commit pin,
/// and the per-run parameters. A secret is never accepted here; the executing node resolves every credential from
/// its own environment.</summary>
public sealed record RunTriggerRequest(
    Guid RepoId, string FlowName, string? Pool = null, string? CommitSha = null, string? Scope = null,
    string? Operation = null, bool Force = false, IReadOnlyDictionary<string, string>? Values = null, string? Drop = null,
    Guid? SubmissionId = null, IReadOnlyList<Guid>? RecordKeys = null, string? PublishTo = null, string? Redeliver = null);

public sealed record RunTriggerAccepted(Guid RunId, string Status);

public sealed record RunGroupAccepted(Guid GroupId, int MemberCount, string Status);

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

        group.MapPost("/runs/groups/{groupId:guid}/cancel", CancelGroupAsync)
            .WithTags("Runs")
            .WithName("CancelRunGroup")
            .RequireAuthorization("operate");

        return group;
    }

    private static async Task<Results<Accepted<RunTriggerAccepted>, ProblemHttpResult>> TriggerRunAsync(
        RunTriggerRequest request, CatalogDbContext db, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                detail: "A run trigger requires a request body.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        if (RunScopeExpander.TryParseScope(request.Scope) is null)
        {
            return TypedResults.Problem(
                detail: "scope must be 'flow' (or omitted). To run a whole set of flows, fire its schedule "
                        + "(POST /schedules/{id}/run): membership is what a fire runs.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        if (request.CommitSha is not null && !IsPlausibleCommitSha(request.CommitSha.Trim()))
        {
            return TypedResults.Problem(
                detail: "commitSha must be a 4- to 64-character hexadecimal git object id (or omitted to pin to the last synced commit).",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        if (string.IsNullOrWhiteSpace(request.FlowName))
        {
            return TypedResults.Problem(
                detail: "A run trigger requires a non-blank flowName.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        // The parameters are validated at this trust boundary, so a run no engine path could honor is refused
        // before it is ever queued.
        var parameters = new RunParameters
        {
            Operation = string.IsNullOrWhiteSpace(request.Operation) ? RunParameters.DeliverOperation : request.Operation.Trim(),
            Force = request.Force,
            Values = request.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Drop = string.IsNullOrWhiteSpace(request.Drop) ? null : request.Drop.Trim(),
            SubmissionId = request.SubmissionId,
            RecordKeys = request.RecordKeys ?? [],
            PublishTo = string.IsNullOrWhiteSpace(request.PublishTo) ? null : request.PublishTo.Trim(),
            Redeliver = string.IsNullOrWhiteSpace(request.Redeliver) ? null : request.Redeliver.Trim().ToLowerInvariant(),
        };
        try
        {
            parameters.Validate();
        }
        catch (SqlFlowException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid run parameters");
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
            db, new RunEnqueueRequest(request.RepoId, flowName, pipeline.Kind, request.Pool, request.CommitSha, parameters, RequestedBy: RequestActor.Of(user)), ct).ConfigureAwait(false);

        // 202 with the canonical run-detail location: GET /api/v1/runs/{runId} reflects the run from the moment it
        // is queued (status "queued"), through running, to its terminal state.
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new RunTriggerAccepted(runId, "queued"));
    }

    private static bool IsPlausibleCommitSha(string sha)
        => sha.Length is >= 4 and <= 64 && sha.All(char.IsAsciiHexDigit);

    private static async Task<Results<Ok<RunGroupAccepted>, Accepted<RunGroupAccepted>, ProblemHttpResult>> CancelGroupAsync(
        Guid groupId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var result = await dispatcher.CancelGroupAsync(db, groupId, ct).ConfigureAwait(false);
        if (!result.Found)
        {
            return TypedResults.Problem(
                detail: $"No run group '{groupId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var affected = result.CancelledQueued + result.RequestedRunning;
        // A running member's cancel is asynchronous (its node aborts the in-flight work), so report "cancelling"
        // and 202 when any member is still running; otherwise every member was queued and is now cancelled outright.
        return result.RequestedRunning > 0
            ? TypedResults.Accepted(
                $"/api/v1/runs/groups/{groupId}", new RunGroupAccepted(groupId, affected, "cancelling"))
            : TypedResults.Ok(new RunGroupAccepted(groupId, affected, "cancelled"));
    }

    private static async Task<Results<Ok<RunTriggerAccepted>, Accepted<RunTriggerAccepted>, ProblemHttpResult>> CancelRunAsync(
        Guid runId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var outcome = await dispatcher.CancelAsync(db, runId, ct).ConfigureAwait(false);
        return outcome switch
        {
            // A still-queued run is cancelled synchronously (it never ran): 200 with the terminal status.
            CancelOutcome.Cancelled => TypedResults.Ok(new RunTriggerAccepted(runId, "cancelled")),
            // A running run's cancel is asynchronous: the owning node aborts the in-flight work and records the
            // run cancelled. 202 with a transitional status; poll GET /api/v1/runs/{runId} for the terminal outcome.
            CancelOutcome.CancelRequested => TypedResults.Accepted(
                $"/api/v1/runs/{runId}", new RunTriggerAccepted(runId, "cancelling")),
            CancelOutcome.NotFound => TypedResults.Problem(
                detail: $"No run '{runId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found"),
            // A run that has already finished has nothing to cancel.
            _ => TypedResults.Problem(
                detail: $"Run '{runId}' has already finished and cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
        };
    }
}
