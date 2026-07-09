namespace SqlFlow.Core.Runs;

/// <summary>
/// When a definition-attached check executes: <see cref="Auto"/> runs it as part of every automatic execution
/// (a scheduled fire, a batch/node group member, an ingestion run's assertion step), <see cref="Manual"/>
/// reserves it for an explicit on-demand trigger (the GUI's run button, the API's single-flow trigger, a direct
/// CLI run). Applies to a health-check flow as a whole and to each ingestion assertion individually.
/// </summary>
public enum ExecutionMode
{
    /// <summary>Executes automatically (the default and the pre-existing behavior).</summary>
    Auto = 0,

    /// <summary>Executes only when explicitly triggered on demand.</summary>
    Manual = 1,
}
