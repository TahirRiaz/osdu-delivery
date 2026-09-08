using SqlFlow.Core;
using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// A loaded flow document of any kind. The envelope (schedule, execution mode, lifecycle) is captured once for
/// every kind, and the header members every catalog consumer needs (name, kind, batch, the source and target
/// the pipeline row shows) are declared here, so the estate scan, the catalog sync and the run write-back
/// project every document identically without knowing the kinds that exist.
/// </summary>
public abstract record FlowDocument
{
    /// <summary>The flow's declared schedule from its top-level <c>schedule:</c> block, or null when it declares
    /// none. Captured at the document envelope so every flow kind carries a schedule the same way; the control
    /// plane turns it into actual runs (the engine itself never schedules anything).</summary>
    public ScheduleSpec? Schedule { get; init; }

    /// <summary>The flow's declared execution mode from its top-level <c>mode:</c> key (auto | manual | disabled;
    /// absent is auto). A <c>manual</c> pipeline is excluded from every group expansion (a schedule's member set)
    /// and runs only when named directly, which IS the manual trigger; <c>disabled</c> retires it visibly.</summary>
    public ExecutionMode Mode { get; init; }

    /// <summary>The flow's declared lifecycle (<c>lifecycle:</c>, production unless the document says otherwise):
    /// only production pipelines generate notification events. Execution is unaffected either way.</summary>
    public FlowLifecycle Lifecycle { get; init; } = FlowLifecycle.Production;

    /// <summary>The flow's name: its pipeline identity and its run-history folder.</summary>
    public abstract string Name { get; }

    /// <summary>The document kind, the value of its <c>flowType</c> key.</summary>
    public abstract string Kind { get; }

    /// <summary>The flow's grouping label (<c>batch:</c>), or null when it declares none.</summary>
    public abstract string? Batch { get; }

    /// <summary>What the flow reads, as the pipeline row and the search index show it: a location or endpoint
    /// reference with no credential in it. Null when the kind has no source side.</summary>
    public virtual string? SourceReference => null;

    /// <summary>What the flow writes to, as the pipeline row shows it. Null when the kind has no target side.</summary>
    public virtual string? TargetReference => null;

    /// <summary>
    /// Every value in the document that is meant to be a credential reference (<c>${env:...}</c>,
    /// <c>${keyvault:...}</c>), keyed by what it configures, so the loader can warn when one is a literal secret
    /// instead. The value itself is never echoed by the warning.
    /// </summary>
    public virtual IEnumerable<KeyValuePair<string, string>> CredentialReferences => [];

    /// <summary>
    /// True when the document cannot execute from a bare single-file snapshot because it addresses sibling files
    /// in the repository tree through a relative local path (committed sample data, say). Such a document runs
    /// from a full git materialization; cloud URIs and absolute paths resolve identically under either root and
    /// stay snapshot-safe.
    /// </summary>
    public virtual bool RequiresRepoTree => false;
}

/// <summary>The document envelope the loader has already read when it hands the body to a kind's parser.</summary>
public sealed record FlowDocumentEnvelope(ScheduleSpec? Schedule, ExecutionMode Mode, FlowLifecycle Lifecycle);

/// <summary>
/// One document kind the loader can parse, keyed by its <c>flowType</c> value. Kinds are registered in the
/// host's composition root, so the YAML layer never depends on the engines that implement them.
/// </summary>
public interface IFlowDocumentKind
{
    /// <summary>The <c>flowType</c> value that selects this kind (compared case-insensitively).</summary>
    string FlowType { get; }

    /// <summary>One line naming what the kind does, for the loader's unknown-kind message and the CLI's usage text.</summary>
    string Description { get; }

    /// <summary>Parses the document body. <paramref name="source"/> labels errors (the file path, or "&lt;inline&gt;").</summary>
    FlowDocument Parse(string yaml, string source, FlowDocumentEnvelope envelope);
}
