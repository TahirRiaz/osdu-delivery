using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The workflow runs a unit triggers, as its artifacts (docs/atomic-delivery-plan.md, Artifacts): each named before it is
/// triggered, under the run id the route chose, so an undo of the unit waits for every run it triggered to end, a run whose
/// trigger answer was lost and a run a later try triggered beside it alike, before it takes back what a run reads or writes.
/// The manifest and workflow routes both take this one path.
/// </summary>
internal static class WorkflowRuns
{
    /// <summary>The slot of a run: one per run, so a later try's run never hides an earlier one.</summary>
    public static string Slot(string runId) => "run:" + runId;

    /// <summary>A run the route is about to trigger under <paramref name="runId"/>: what an undo asks the Workflow service about.</summary>
    public static TargetArtifact Artifact(string workflow, string runId)
        => new() { Slot = Slot(runId), Role = ArtifactRoles.Run, TargetId = runId, Locator = workflow, Status = ArtifactStatus.Pending };

    /// <summary>
    /// Asks the Workflow service about each run <paramref name="runs"/> names: why the undo waits, when a run is in a state not
    /// known as ended (still going, a retry Airflow scheduled) or its state could not be asked; else what each ended as, by
    /// artifact, for its answer.
    /// </summary>
    public static async Task<(string? Waiting, IReadOnlyDictionary<long, string> Ended)> SettleAsync(WorkflowClient workflows, IEnumerable<UndoItem> runs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(runs);
        var ended = new Dictionary<long, string>();
        foreach (var item in runs)
        {
            if (item.Artifact.TargetId is not { } runId || item.Artifact.Locator is not { } workflow)
            {
                ended[item.ArtifactId] = "the run's id or workflow was not recorded, so nothing can ask about it";
                continue;
            }

            var (waiting, status) = await StateAsync(workflows, workflow, runId, ct).ConfigureAwait(false);
            if (waiting is not null)
            {
                return (waiting, ended);
            }

            ended[item.ArtifactId] = status is null
                ? $"the Workflow service has no run {runId} of {workflow}: its trigger did not land"
                : $"workflow run {runId} of {workflow} ended {status}; Airflow keeps its log, and records it wrote that the delivery did not name are left for an inventory of their kind";
        }

        return (null, ended);
    }

    /// <summary>
    /// Why the undo waits for the run <paramref name="runId"/> of <paramref name="workflow"/>, or its status (null when the
    /// Workflow service does not know it) once it has ended.
    /// </summary>
    public static async Task<(string? Waiting, string? Status)> StateAsync(WorkflowClient workflows, string workflow, string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        string? status;
        try
        {
            status = await workflows.StatusAsync(workflow, runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
        {
            return ($"whether workflow run {runId} of {workflow} has ended could not be asked: {ArtifactUndo.Redact(ex)}", null);
        }

        if (status is not null && !WorkflowClient.IsTerminal(status))
        {
            return (WorkflowClient.IsPending(status)
                ? $"workflow run {runId} of {workflow} is still {status} and may still read what the delivery sent and write records, so nothing is taken back until it ends"
                : $"workflow run {runId} of {workflow} reports {status}, which is not a state the route knows as ended (a retry Airflow schedules is one), so nothing is taken back until it ends", status);
        }

        return (null, status);
    }
}
