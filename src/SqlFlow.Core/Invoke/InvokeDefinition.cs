namespace SqlFlow.Core.Invoke;

/// <summary>
/// An flw.Invoke flow (legacy flw.Invoke): a DAG node that triggers one named external resource, addressed by
/// its unique <see cref="InvokeAlias"/>. It declares <em>what</em> to run (a pipeline or runbook name) and the
/// parameters to pass, never executable code: the legacy code-carrying columns (Code / InvokeFile / InvokePath /
/// Arguments) were removed so the control database cannot stage a script payload. It carries no secret either:
/// the legacy <c>trgServicePrincipalAlias</c> / <c>srcServicePrincipalAlias</c> (which joined to the plaintext
/// flw.SysServicePrincipal) collapse to secretless <see cref="TargetServicePrincipalReference"/> /
/// <see cref="SourceServicePrincipalReference"/> alias references, in keeping with the V3 secretless control
/// plane. Immutable.
/// </summary>
public sealed record InvokeDefinition
{
    /// <summary>The legacy numeric identity (flw.Invoke.FlowID).</summary>
    public int FlowId { get; init; }

    public string? Batch { get; init; }                          // Batch

    public string? SysAlias { get; init; }                       // SysAlias

    /// <summary>The unique alias of this invoke flow (legacy InvokeAlias, NOT NULL + UNIQUE). Pre/PostInvokeAlias
    /// on other flows reference this value.</summary>
    public required string InvokeAlias { get; init; }

    public InvokeType InvokeType { get; init; } = InvokeType.AzureAutomation;   // InvokeType (default aut)

    public string? PipelineName { get; init; }                   // PipelineName (ADF)

    public string? RunbookName { get; init; }                    // RunbookName (Automation)

    /// <summary>Pipeline / runbook parameters as a JSON object (legacy ParameterJSON).</summary>
    public string? ParameterJson { get; init; }

    public bool OnErrorResume { get; init; } = true;             // OnErrorResume (default true)

    /// <summary>Legacy DeactivateFromBatch. NOT consumed in V3 (carried only for control-DB / legacy import
    /// fidelity): batch membership is controlled by the batch document's <c>members.inactive</c> globs, not a
    /// per-flow flag. Not settable from YAML.</summary>
    public bool DeactivateFromBatch { get; init; }               // DeactivateFromBatch (default false)

    /// <summary>A secretless reference (alias) to the target service principal (legacy trgServicePrincipalAlias).
    /// The credential it names lives in the secretless registry, never inline.</summary>
    public string? TargetServicePrincipalReference { get; init; }

    /// <summary>Legacy srcServicePrincipalAlias. NOT consumed in V3 (an invoke has no source side; carried only
    /// for import fidelity). Not settable from YAML.</summary>
    public string? SourceServicePrincipalReference { get; init; }

    /// <summary>Legacy ToObjectMK lineage hint. NOT consumed in V3 (lineage is derived from the AST, not stored
    /// MK hints; carried only for import fidelity). Not settable from YAML.</summary>
    public int? ToObjectMK { get; init; }                        // ToObjectMK (lineage)

    public string? CreatedBy { get; init; }                      // CreatedBy

    public DateTime? CreatedDate { get; init; }                  // CreatedDate

    /// <summary>The legacy short code for this invoke type, used as the SysLog FlowType (legacy logs
    /// FlowType = InvokeType).</summary>
    public string FlowType => InvokeTypeCodes.ToCode(InvokeType);
}
