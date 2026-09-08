using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One ad-hoc compute task as the queue holds it: what was asked, where it stands, and (once a node has
/// run it) its JSON result or its error.</summary>
public sealed record ComputeTaskDto(
    Guid TaskId, string Operation, string SourceRef, string Status, string? RequestedBy, DateTime EnqueuedUtc,
    DateTime? StartUtc, DateTime? EndUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc, string? Error, string? ResultJson);

/// <summary>
/// The read side of the ad-hoc compute queue: a client that enqueued an operation (a target probe, a record
/// read-back, a record removal) polls the task here until it is terminal and reads the result a node produced.
/// Cancelling a task goes through the dispatcher, exactly like a run.
/// </summary>
public static class ComputeTaskEndpoints
{
    public static RouteGroupBuilder MapComputeTaskEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var tasks = group.MapGroup("/compute/tasks").WithTags("Compute");
        tasks.MapGet("/{taskId:guid}", GetTaskAsync).WithName("GetComputeTask");
        return group;
    }

    public static RouteGroupBuilder MapComputeTaskControlEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/compute/tasks/{taskId:guid}/cancel", CancelTaskAsync)
            .WithTags("Compute")
            .WithName("CancelComputeTask")
            .RequireAuthorization("operate");
        return group;
    }

    private static async Task<Results<Ok<ComputeTaskDto>, ProblemHttpResult>> GetTaskAsync(Guid taskId, CatalogDbContext db, CancellationToken ct)
    {
        var task = await db.ComputeTasks.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new ComputeTaskDto(
                t.TaskId, t.Operation, t.SourceRef, t.Status, t.RequestedBy, t.EnqueuedUtc,
                t.StartUtc, t.EndUtc, t.ClaimedByNode, t.CancelRequestedUtc, t.Error, t.ResultJson))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return task is null
            ? TypedResults.Problem(detail: $"No compute task '{taskId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(task);
    }

    private static async Task<Results<Ok, Accepted, ProblemHttpResult>> CancelTaskAsync(
        Guid taskId, CatalogDbContext db, IRunDispatcher dispatcher, CancellationToken ct)
    {
        var outcome = await dispatcher.CancelComputeTaskAsync(db, taskId, ct).ConfigureAwait(false);
        return outcome switch
        {
            CancelOutcome.Cancelled => TypedResults.Ok(),
            CancelOutcome.CancelRequested => TypedResults.Accepted($"/api/v1/compute/tasks/{taskId}"),
            CancelOutcome.NotFound => TypedResults.Problem(detail: $"No compute task '{taskId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found"),
            _ => TypedResults.Problem(detail: "The task has already finished.", statusCode: StatusCodes.Status409Conflict, title: "Cannot cancel"),
        };
    }
}
