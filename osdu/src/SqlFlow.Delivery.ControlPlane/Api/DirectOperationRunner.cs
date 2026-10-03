using System.Data.Common;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Runs an operation a person asks of a flow and waits on (<see cref="DirectOperations"/>) in this process, and answers with
/// what it found: a read of OSDU, a probe, a read of the ingestion tables, a preview. The operation is given exactly what a
/// node would be given (<see cref="DeliveryEndpoints.OperationPayloadAsync"/>: the flow file, the interface, the partition,
/// who asked) and the central configuration of the flow's partition as a queued task carries it
/// (<see cref="ConfiguredRunDispatcher.WithReferences"/>), so it reaches the same target with the same credentials. Nothing
/// is queued and nothing is polled; the request is the wait.
/// </summary>
/// <remarks>
/// A failure answers as a problem whose words say which side failed, every resolved secret redacted from them: the platform
/// or the database refusing or failing (502), not answering in time (504), or the request naming something the flow cannot
/// serve (422). A request the caller abandons stops the operation.
/// </remarks>
internal static partial class DirectOperationRunner
{
    /// <summary>The logger category every direct operation reports under.</summary>
    public const string LogCategory = "SqlFlow.Delivery.ControlPlane.DirectOperations";

    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(
        CatalogDbContext db, DeliveryConfigStore config, DirectOperations operations, DeliveryEndpoints.FlowContext flow, string operation,
        IReadOnlyDictionary<string, string> arguments, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await RunAsync(db, config, operations, flow, operation, arguments, RequestActor.Label(user), loggers, ct).ConfigureAwait(false);
    }

    /// <summary>The same, for a caller that is not a request (the scheduled probe): the actor is given rather than read from a principal.</summary>
    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(
        CatalogDbContext db, DeliveryConfigStore config, DirectOperations operations, DeliveryEndpoints.FlowContext flow, string operation,
        IReadOnlyDictionary<string, string> arguments, string actor, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(loggers);
        var (payload, problem) = await DeliveryEndpoints.OperationPayloadAsync(db, flow, operation, arguments, actor, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return problem!;
        }

        var log = loggers.CreateLogger(LogCategory);
        var configuration = await config.ConfigurationAsync(flow.Pipeline.RepoId, ct).ConfigureAwait(false);
        var configured = ConfiguredRunDispatcher.WithReferences(payload, configuration.For(flow.Flow.Partition));
        try
        {
            var answer = await operations.Get(operation).ExecuteAsync(configured, ct).ConfigureAwait(false);
            LogRan(log, operation, flow.Flow.Label, actor);
            return TypedResults.Text(answer, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            var said = DeliveryEndpoints.Redacted(ex);
            LogFailed(log, operation, flow.Flow.Label, actor, failure.Status, said);
            return TypedResults.Problem(detail: said, statusCode: failure.Status, title: failure.Title);
        }
    }

    /// <summary>
    /// What a failure of an operation says about where it failed, as the status and title it answers with; null for one this
    /// runner does not know, which the host answers as any unexpected failure.
    /// </summary>
    private static (int Status, string Title)? Classify(Exception ex) => ex switch
    {
        OsduStatusException => (StatusCodes.Status502BadGateway, "OSDU refused the request"),
        TimeoutException or TaskCanceledException => (StatusCodes.Status504GatewayTimeout, "No answer in time"),
        HttpRequestException => (StatusCodes.Status502BadGateway, "OSDU could not be reached"),
        DbException => (StatusCodes.Status502BadGateway, "The ingestion tables could not be read"),
        DeliveryException { InnerException: HttpRequestException or IOException } => (StatusCodes.Status502BadGateway, "OSDU could not be reached"),
        DeliveryException { InnerException: TaskCanceledException or TimeoutException } => (StatusCodes.Status504GatewayTimeout, "No answer in time"),
        SqlFlowException => (StatusCodes.Status422UnprocessableEntity, "The request could not be completed"),
        _ => null,
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Actor} ran {Operation} on {Flow} in process.")]
    private static partial void LogRan(ILogger logger, string operation, string flow, string actor);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Actor}'s {Operation} on {Flow} answered {Status}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string operation, string flow, string actor, int status, string reason);
}
