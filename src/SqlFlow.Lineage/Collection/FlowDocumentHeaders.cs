using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// One runnable flow a document declares, as every catalog consumer must see it: the name, kind, batch, server
/// identities, execution mode, lifecycle, and schedule a pipeline row derives from the document itself. The first
/// header is always the document's primary flow and carries the document's <c>schedule:</c> block; a derived sibling
/// (an ingestion document's embedded <c>healthCheck:</c>) never carries it, following its own mode instead.
/// </summary>
public sealed record DocumentFlowHeader(
    string Name,
    string Kind,
    string? Batch,
    string? SourceServerRef,
    string TargetServerRef,
    ScheduleSpec? Schedule,
    ExecutionMode Mode = ExecutionMode.Auto,
    FlowLifecycle Lifecycle = FlowLifecycle.Production);

/// <summary>
/// THE single authority for "what flows does this document contain": the estate scan builds its lineage flow
/// nodes from this projection and the per-run catalog write-back builds its pipeline rows and schedule mirror
/// from it, so every element of a parsed document is extracted identically no matter which path triggered the
/// parse. Orchestration documents (scm/batch) declare no runnable flow and project to an empty list; a document
/// kind this projection does not know is a programming error and throws, so a future kind can never be silently
/// dropped by one consumer while the other handles it.
/// </summary>
public static class FlowDocumentHeaders
{
    public static IReadOnlyList<DocumentFlowHeader> Project(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var target = ServerIdentity.From(refs[flow.Target.Server]);
                var headers = new List<DocumentFlowHeader>
                {
                    new(flow.SysAlias ?? flow.Target.Table.Name, "ing", flow.Batch,
                        ServerIdentity.From(refs[flow.Source.Server]), target, document.Schedule,
                        Lifecycle: flow.Lifecycle),
                };

                // The embedded healthCheck: block is a full sibling pipeline sharing this file. The document's
                // schedule: block deliberately stays on the load; the check follows its own mode and the load's
                // lifecycle.
                if (doc.Document.HealthCheck is { } check)
                {
                    headers.Add(new DocumentFlowHeader(
                        check.SysAlias, "hc", check.Batch, null, target, Schedule: null, check.Mode, flow.Lifecycle));
                }

                return headers;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var server = ServerIdentity.From(refs[flow.SrcServer]);
                return [new DocumentFlowHeader(flow.SysAlias, "exp", flow.Batch, server, server, document.Schedule, Lifecycle: flow.Lifecycle)];
            }

            case StoredProcedureFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(flow.SysAlias, "sp", flow.Batch, null, ServerIdentity.From(refs[flow.Server]), document.Schedule, Lifecycle: flow.Lifecycle),
                ];
            }

            case HealthCheckFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(flow.SysAlias, "hc", flow.Batch, null, ServerIdentity.From(refs[flow.Server]), document.Schedule, flow.Mode, flow.Lifecycle),
                ];
            }

            case FileFlowDocument doc:
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "file", doc.Flow.Batch, null, ServerIdentity.From(doc.Flow.Target.Connection), document.Schedule, Lifecycle: doc.Flow.Lifecycle),
                ];

            case InvokeFlowDocument doc:
                // An invoke triggers external compute; it moves no catalog data itself.
                return
                [
                    new DocumentFlowHeader(doc.Document.Definition.InvokeAlias, "inv", doc.Document.Definition.Batch, null, ServerIdentity.FileSystem, document.Schedule, Lifecycle: doc.Document.Definition.Lifecycle),
                ];

            case AcquireFlowDocument doc:
                // An acquisition flow fetches from a third party and writes raw files to the lake; its landing
                // target is its declared output.
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "acq", doc.Flow.Batch, null, ServerIdentity.FileSystem, document.Schedule),
                ];

            case CopyFlowDocument doc:
                // A copy flow moves files between endpoints (both sides are file locations); it reads its source
                // and writes its target, so it is a filesystem flow.
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "cpy", doc.Flow.Batch, null, ServerIdentity.FileSystem, document.Schedule),
                ];

            case SourceControlFlowDocument:
            case BatchFlowDocument:
                // Orchestration/utility documents: they move no catalog data and never become pipeline rows.
                return [];

            default:
                throw new SqlFlowException(
                    $"unhandled flow document kind '{document.GetType().Name}'; a new document kind must be added to the shared header projection.");
        }
    }

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<Core.Connections.DataSource> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);
}
