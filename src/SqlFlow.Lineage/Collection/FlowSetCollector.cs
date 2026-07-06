using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;
using SqlFlow.Yaml;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Phase one, declared tier: scans a folder of flow documents and turns each kind's endpoints into lineage
/// facts: what the author intends, available with nothing but the files. Pre/post-process hooks (raw T-SQL
/// in the documents) are extracted through the same AST extractor, so a hook that writes a side table is
/// declared lineage too. A document that fails to parse becomes a warning and the scan continues: one broken
/// file must not blind the whole estate.
/// </summary>
public sealed class FlowSetCollector
{
    private readonly YamlDocumentLoader _documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader());

    public CollectionResult Collect(string flowDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowDirectory);
        var root = Path.GetFullPath(flowDirectory);
        if (!Directory.Exists(root))
        {
            throw new SqlFlowException($"Flow directory not found: '{root}'.");
        }

        var result = new CollectionResult();
        var files = Directory.EnumerateFiles(root, "*.flow.yaml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                Collect(result, _documents.LoadFile(file), relative, File.GetLastWriteTimeUtc(file), root);
            }
            catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
            }
        }

        var duplicates = result.Flows
            .GroupBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in duplicates)
        {
            result.Warnings.Add(
                $"flow name '{group.Key}' is declared by {group.Count()} documents ({string.Join(", ", group.Select(f => f.Node.File))}); " +
                "their facts merge under one flow, which is almost never intended.");
        }

        return result;
    }

    /// <summary>Registers a document's connections in the server inventory the derived tier connects to.</summary>
    private static void RegisterServers(CollectionResult result, IEnumerable<Core.Connections.DataSource> connections)
    {
        foreach (var connection in connections)
        {
            var identity = ServerIdentity.From(connection.ConnectionRef);
            if (!result.Servers.TryAdd(identity, (connection.ConnectionRef, connection.Kind))
                && result.Servers[identity].Kind != connection.Kind)
            {
                result.Warnings.Add(
                    $"server '{identity}' is declared with conflicting providers ({result.Servers[identity].Kind} vs {connection.Kind}); the first wins.");
            }
        }
    }

    private static void Collect(CollectionResult result, FlowDocument document, string file, DateTime fileWriteUtc, string root)
    {
        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var name = flow.SysAlias ?? flow.Target.Table.Name;
                RegisterServers(result, doc.Document.Connections);
                var refs = ConnectionRefs(doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)));
                var source = ServerIdentity.From(refs[flow.Source.Server]);
                var target = ServerIdentity.From(refs[flow.Target.Server]);

                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = name, Kind = "ing", File = file, Batch = flow.Batch },
                    SourceServerRef = source,
                    TargetServerRef = target,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                result.Facts.Add(ObjectFact(name, LineageRelation.Reads, source, flow.Source.Table, LineageNodeKind.Unknown));
                result.Facts.Add(ObjectFact(name, LineageRelation.Writes, target, flow.Target.Table, LineageNodeKind.Table));

                // The transformation view is a run output too (external-DB landings): the flow refreshes
                // [schema].[v<Table>] over its target, and the downstream chained flow reads THE VIEW. Declaring
                // it written here connects "landing flow -> view -> downstream flow" so waves order the chain.
                if (flow.Transform.GeneratesView)
                {
                    result.Facts.Add(ObjectFact(
                        name, LineageRelation.Writes, target,
                        flow.Target.Table with { Name = $"v_{flow.Target.Table.Name}" }, LineageNodeKind.View));
                }

                ExtractHook(result, name, target, flow.Process.PreProcessOnTarget, $"{file}: preProcess", flow.Target.Table.Database);
                ExtractHook(result, name, target, flow.Process.PostProcessOnTarget, $"{file}: postProcess", flow.Target.Table.Database);
                break;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                var refs = ConnectionRefs(doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)));
                var source = ServerIdentity.From(refs[flow.SrcServer]);

                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "exp", File = file, Batch = flow.Batch },
                    SourceServerRef = source,
                    TargetServerRef = source,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                result.Facts.Add(ObjectFact(flow.SysAlias, LineageRelation.Reads, source, flow.Source, LineageNodeKind.Unknown));
                if (!string.IsNullOrWhiteSpace(flow.TrgPath))
                {
                    result.Facts.Add(FileFact(flow.SysAlias, LineageRelation.Writes, flow.TrgPath, root));
                }

                break;
            }

            case StoredProcedureFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                var refs = ConnectionRefs(doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)));
                var server = ServerIdentity.From(refs[flow.Server]);

                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "sp", File = file, Batch = flow.Batch },
                    TargetServerRef = server,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });

                // The flow requires the procedure; the procedure's own reads/writes are its module's
                // lineage, expanded by the derived tier.
                result.Facts.Add(ObjectFact(flow.SysAlias, LineageRelation.Requires, server, flow.Procedure, LineageNodeKind.Procedure));
                break;
            }

            case HealthCheckFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                var refs = ConnectionRefs(doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)));
                var server = ServerIdentity.From(refs[flow.Server]);

                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "hc", File = file, Batch = flow.Batch },
                    TargetServerRef = server,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                result.Facts.Add(ObjectFact(flow.SysAlias, LineageRelation.Reads, server, flow.Target, LineageNodeKind.Unknown));
                break;
            }

            case FileFlowDocument doc:
            {
                var flow = doc.Flow;
                var target = ServerIdentity.From(flow.Target.Connection);
                result.Servers.TryAdd(target, (flow.Target.Connection, Core.Connections.DataSourceKind.MSSQL));

                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = flow.Name, Kind = "file", File = file, Batch = flow.Batch },
                    TargetServerRef = target,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                if (!string.IsNullOrWhiteSpace(flow.Source.Location))
                {
                    result.Facts.Add(FileFact(flow.Name, LineageRelation.Reads, flow.Source.Location, root));
                }

                result.Facts.Add(new LineageFact
                {
                    Flow = flow.Name,
                    Relation = LineageRelation.Writes,
                    ServerRef = target,
                    Schema = flow.Target.Schema,
                    Name = flow.Target.Table,
                    Tier = LineageTier.Declared,
                    KindHint = LineageNodeKind.Table,
                });

                // The pre-ingestion transform view is a run output too: the flow refreshes [schema].[v<Table>]
                // over its loaded table, and downstream chained flows read THE VIEW, not the table. Declaring the
                // view as written here is what connects "landing flow -> view -> downstream ingestion flow" in
                // the graph, so flow dependencies and execution waves order the chain correctly.
                if (flow.Inference.GeneratesView)
                {
                    result.Facts.Add(new LineageFact
                    {
                        Flow = flow.Name,
                        Relation = LineageRelation.Writes,
                        ServerRef = target,
                        Schema = flow.Target.Schema,
                        Name = $"v_{flow.Target.Table}",
                        Tier = LineageTier.Declared,
                        KindHint = LineageNodeKind.View,
                    });
                }

                break;
            }

            case InvokeFlowDocument doc:
                // An invoke triggers external compute; it moves no catalog data itself.
                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = doc.Document.Definition.InvokeAlias, Kind = "inv", File = file, Batch = doc.Document.Definition.Batch },
                    TargetServerRef = ServerIdentity.FileSystem,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                break;

            case SourceControlFlowDocument:
            case BatchFlowDocument:
                // Orchestration/utility documents: they move no catalog data, so they are not lineage nodes and
                // contribute no facts. A batch's ordering is computed FROM lineage, never part of it.
                break;

            default:
                result.Warnings.Add($"{file}: unhandled document kind '{document.GetType().Name}'.");
                break;
        }
    }

    /// <summary>A document hook is raw author T-SQL: the same operation-wise extractor derives what it
    /// touches, attributed to the flow as declared lineage through the shared fact mapping.</summary>
    private static void ExtractHook(
        CollectionResult result, string flow, string serverRef, string? sql, string label, string defaultDatabase)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        var deps = TSqlLineageExtractor.Extract(sql, label, defaultDatabase);
        result.Warnings.AddRange(deps.Warnings);
        result.Facts.AddRange(ScriptFactBuilder.Facts(
            deps, flow, viaModuleKey: null, serverRef, LineageTier.Declared, minimumParts: 1));
        result.ObjectArtifacts.AddRange(ScriptFactBuilder.ObjectArtifacts(deps, serverRef, LineageTier.Declared, minimumParts: 1));
    }

    private static LineageFact ObjectFact(
        string flow, LineageRelation relation, string serverRef, Core.Ingestion.RelationalObject table, LineageNodeKind kind)
        => new()
        {
            Flow = flow,
            Relation = relation,
            ServerRef = serverRef,
            Database = table.Database,
            Schema = table.Schema,
            Name = table.Name,
            Tier = LineageTier.Declared,
            KindHint = kind,
        };

    /// <summary>File-endpoint identity: cloud URLs verbatim; local paths normalized against the estate root
    /// (relative when inside it), so './data/x.csv' and 'data/x.csv' are one node and the identity survives
    /// a checkout moving between machines.</summary>
    private static LineageFact FileFact(string flow, LineageRelation relation, string location, string root)
    {
        string identity;
        if (location.Contains("://", StringComparison.Ordinal))
        {
            identity = location;
        }
        else
        {
            var full = Path.GetFullPath(Path.IsPathRooted(location) ? location : Path.Combine(root, location));
            var relative = Path.GetRelativePath(root, full);
            identity = (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
        }

        return new LineageFact
        {
            Flow = flow,
            Relation = relation,
            ServerRef = ServerIdentity.FileSystem,
            Name = identity,
            Tier = LineageTier.Declared,
            KindHint = LineageNodeKind.File,
        };
    }

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<(string Alias, string ConnectionRef)> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);
}
