using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The Workflow service calls every route that starts an OSDU workflow makes (openapi workflow v1;
/// osdu/specs/workflows/INTEGRATION.md section 2): a workflow looked up by name, a run triggered under a run id of its
/// own, and the run polled until it reaches a terminal status or its timeout passes. The manifest route and the workflow
/// route both go through it, so a status is read the same way whichever route started the run.
/// </summary>
public sealed class WorkflowClient
{
    public const string DefaultRunPath = "/api/workflow/v1/workflow/{workflow}/workflowRun";
    public const string DefaultStatusPath = "/api/workflow/v1/workflow/{workflow}/workflowRun/{runId}";
    public const string DefaultWorkflowPath = "/api/workflow/v1/workflow/{workflow}";
    public const string DefaultProbePath = "/api/workflow/v1/info";

    /// <summary>The status a run is taken to be in when the trigger's answer names none: the contract's first state.</summary>
    public const string Submitted = "SUBMITTED";

    public const string Failed = "FAILED";

    /// <summary>
    /// The terminal run statuses, compared upper case because the workflow service reports them in both cases: the run
    /// detail schema (openapi workflow v1, WorkflowRunResponse) is upper (SUBMITTED, INPROGRESS, PARTIAL_SUCCESS, SUCCESS,
    /// FAILED) while the run schema behind the listing (WorkflowRun) is lower (submitted, running, queued, finished,
    /// success, failed), and the DAGs' own status task writes the lower set (section 2.3). FINISHED belongs here: it is
    /// how the Airflow-backed service reports a run that reached its end.
    /// </summary>
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "SUCCESS", "PARTIAL_SUCCESS", "FINISHED", Failed };

    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal) { Submitted, "INPROGRESS", "IN_PROGRESS", "RUNNING", "QUEUED" };

    private readonly OsduHttpClient _client;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly string _runPath;
    private readonly string _statusPath;
    private readonly string _workflowPath;

    public WorkflowClient(OsduHttpClient client, ILogger logger, TimeProvider? time = null, string? runPath = null, string? statusPath = null, string? workflowPath = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _runPath = runPath ?? DefaultRunPath;
        _statusPath = statusPath ?? DefaultStatusPath;
        _workflowPath = workflowPath ?? DefaultWorkflowPath;
    }

    /// <summary>True for a status the run ends in.</summary>
    public static bool IsTerminal(string status) => Terminal.Contains(status);

    /// <summary>
    /// Triggers <paramref name="workflow"/> with <paramref name="executionContext"/> under <paramref name="runId"/>
    /// (openapi workflow v1, POST /v1/workflow/{workflow_name}/workflowRun, TriggerWorkflowRequest). The run id is chosen
    /// by the caller, so a request the service accepted before a retry sent it again answers 409 and is taken as the run
    /// already started rather than started twice (section 2.1, step 3). The caller records the run before it polls.
    /// </summary>
    public async Task<WorkflowRun> TriggerAsync(string workflow, string runId, JsonObject executionContext, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(executionContext);
        var body = new JsonObject { ["runId"] = runId, ["executionContext"] = executionContext };
        var url = _client.Url(_runPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = workflow });
        var started = _time.GetUtcNow().UtcDateTime;
        int answered;
        string? workflowId = null;
        var status = Submitted;
        try
        {
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 409 }, ct, idempotent: true).ConfigureAwait(false);
            answered = (int)result.Status;
            if (answered != 409 && result.Body.Length > 0)
            {
                var root = OsduHttpClient.ParseJson(result, url);
                workflowId = JsonPathReader.SelectValue(root, "workflowId");
                status = JsonPathReader.SelectValue(root, "status") ?? status;
                runId = JsonPathReader.SelectValue(root, "runId") ?? runId;
            }
        }
        finally
        {
            // The context is the caller's, and a caller that triggers again gives it to another request.
            body.Remove("executionContext");
        }

        return new WorkflowRun(workflow, runId, workflowId, status.ToUpperInvariant(), null, null, started) { TriggerStatus = answered };
    }

    /// <summary>
    /// Polls the run every <paramref name="interval"/> until it reaches a terminal status (openapi workflow v1, GET
    /// /v1/workflow/{workflow_name}/workflowRun/{runId}), or throws when <paramref name="timeout"/> passes first: a run
    /// can stay running in the Workflow service after Airflow finished it, so the route keeps a timeout of its own and a
    /// later try resumes the same run (section 2.3). A status outside both vocabularies is an error, never a guess.
    /// </summary>
    public async Task<WorkflowRun> PollAsync(string workflow, string runId, DateTime started, TimeSpan interval, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var url = _client.Url(_statusPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = workflow, ["runId"] = runId });
        var wait = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
        var limit = timeout < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : timeout;
        var deadline = _time.GetUtcNow() + limit;
        var polls = 0;
        while (true)
        {
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            var root = OsduHttpClient.ParseJson(result, url);
            polls++;
            var status = (JsonPathReader.SelectValue(root, "status") ?? throw new DeliveryException($"{url.AbsolutePath} did not report the run's status.")).ToUpperInvariant();
            if (Terminal.Contains(status))
            {
                _logger.LogInformation("Workflow run {RunId} of {Workflow} finished {Status} after {Polls} poll(s).", runId, workflow, status, polls);
                return new WorkflowRun(workflow, runId, JsonPathReader.SelectValue(root, "workflowId"), status, JsonPathReader.SelectValue(root, "startTimeStamp"), JsonPathReader.SelectValue(root, "endTimeStamp"), started);
            }

            if (!Pending.Contains(status))
            {
                throw new DeliveryException($"workflow run {runId} of {workflow} reported an unknown status '{status}'");
            }

            if (_time.GetUtcNow() + wait > deadline)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"workflow run {runId} of {workflow} is still {status} after {limit.TotalMinutes} minute(s); the next try resumes polling it"));
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the Workflow service knows <paramref name="workflow"/> in this partition (openapi workflow v1, GET
    /// /v1/workflow/{workflow_name}, which answers 200 with the workflow's metadata and lists 404). Workflow names are
    /// deployment configuration (section 1.4), so a route asks before it relies on one. Any other answer throws.
    /// </summary>
    public async Task<bool> ExistsAsync(string workflow, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        var url = _client.Url(_workflowPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = workflow });
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        return (int)result.Status != 404;
    }
}

/// <summary>One workflow run as the Workflow service reported it, and when this route triggered or resumed it.</summary>
public sealed record WorkflowRun(string Workflow, string RunId, string? WorkflowId, string Status, string? StartTimeStamp, string? EndTimeStamp, DateTime Started)
{
    /// <summary>The HTTP status the trigger was answered with; 0 for a run that was resumed or polled rather than triggered.</summary>
    public int TriggerStatus { get; init; }

    public bool HasFailed => Status == WorkflowClient.Failed;

    /// <summary>What the run is recorded by on a step: its id, status, and the service's own workflow id and times when it gave them.</summary>
    public Dictionary<string, string> Values()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = RunId, ["status"] = Status };
        if (WorkflowId is not null)
        {
            values["workflowId"] = WorkflowId;
        }

        if (StartTimeStamp is not null)
        {
            values["startTimeStamp"] = StartTimeStamp;
        }

        if (EndTimeStamp is not null)
        {
            values["endTimeStamp"] = EndTimeStamp;
        }

        return values;
    }
}
