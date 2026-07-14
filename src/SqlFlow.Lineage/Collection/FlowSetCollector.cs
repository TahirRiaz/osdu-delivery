using SqlFlow.Core;
using SqlFlow.Core.Files;
using SqlFlow.Core.Invoke;
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

        // File producers (invokes that land files) and consumers (file ingestions) are gathered across the whole
        // estate, then reconciled once every document is in hand: a per-document pass cannot see the flow on the
        // other side of the file.
        var producers = new List<FileProducer>();
        var consumers = new List<FileConsumer>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                Collect(result, _documents.LoadFile(file), relative, File.GetLastWriteTimeUtc(file), root, producers, consumers);
            }
            catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
            }
        }

        ReconcileFileLinks(result, producers, consumers, root);

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

    private static void Collect(
        CollectionResult result, FlowDocument document, string file, DateTime fileWriteUtc, string root,
        List<FileProducer> producers, List<FileConsumer> consumers)
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
                    Node = new LineageFlowNode { Name = name, Kind = "ing", File = file, Batch = flow.Batch, Lifecycle = flow.Lifecycle },
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
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "exp", File = file, Batch = flow.Batch, Lifecycle = flow.Lifecycle },
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
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "sp", File = file, Batch = flow.Batch, Lifecycle = flow.Lifecycle },
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
                    Node = new LineageFlowNode { Name = flow.SysAlias, Kind = "hc", File = file, Batch = flow.Batch, Mode = flow.Mode, Lifecycle = flow.Lifecycle },
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
                    Node = new LineageFlowNode { Name = flow.Name, Kind = "file", File = file, Batch = flow.Batch, Lifecycle = flow.Lifecycle },
                    TargetServerRef = target,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                // The file the flow reads is its Location, or the srcPath option when no Location is given (the
                // loader accepts either). Its normalized identity is the file node; recording the same source spec
                // as a consumer lets an invoke that lands into this folder link to THIS node.
                var readLocation = !string.IsNullOrWhiteSpace(flow.Source.Location)
                    ? flow.Source.Location!
                    : Option(flow.Source.Options, "srcPath");
                if (!string.IsNullOrWhiteSpace(readLocation))
                {
                    var fileNode = NormalizeFileIdentity(readLocation!, root);
                    result.Facts.Add(FileFact(flow.Name, LineageRelation.Reads, readLocation!, root));
                    consumers.Add(new FileConsumer(flow.Name, fileNode, new FileSelectionSpec
                    {
                        Type = flow.Source.Type,
                        Location = readLocation,
                        Glob = Option(flow.Source.Options, "srcFile"),
                        Mask = Option(flow.Source.Options, "srcPathMask"),
                    }));
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
            {
                // An invoke triggers external compute (an ADF pipeline or Automation runbook). It moves no catalog
                // data of its own, but when the author declares the file(s) that compute lands via 'output:', the
                // invoke becomes a file producer: reconciliation connects it to the file ingestion that reads them,
                // so the graph chains invoke -> file -> landing table -> view -> downstream.
                var definition = doc.Document.Definition;
                result.Flows.Add(new CollectedFlow
                {
                    Node = new LineageFlowNode { Name = definition.InvokeAlias, Kind = "inv", File = file, Batch = definition.Batch, Lifecycle = definition.Lifecycle },
                    TargetServerRef = ServerIdentity.FileSystem,
                    Schedule = document.Schedule,
                    FileWriteUtc = fileWriteUtc,
                });
                if (definition.Outputs.Count > 0)
                {
                    producers.Add(new FileProducer(definition.InvokeAlias, definition.Outputs));
                }

                break;
            }

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

    private static LineageFact FileFact(string flow, LineageRelation relation, string location, string root)
        => new()
        {
            Flow = flow,
            Relation = relation,
            ServerRef = ServerIdentity.FileSystem,
            Name = NormalizeFileIdentity(location, root),
            Tier = LineageTier.Declared,
            KindHint = LineageNodeKind.File,
        };

    /// <summary>File-endpoint identity: cloud URLs verbatim; local paths normalized against the estate root
    /// (relative when inside it), so './data/x.csv' and 'data/x.csv' are one node and the identity survives
    /// a checkout moving between machines.</summary>
    private static string NormalizeFileIdentity(string location, string root)
    {
        if (location.Contains("://", StringComparison.Ordinal))
        {
            return location;
        }

        var full = Path.GetFullPath(Path.IsPathRooted(location) ? location : Path.Combine(root, location));
        var relative = Path.GetRelativePath(root, full);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? full : relative).Replace('\\', '/');
    }

    /// <summary>Connects each file producer (an invoke that lands files) to the file consumers (file ingestions) it
    /// feeds, with engine-parity file selection: the invoke is attributed a Writes of the SAME file node the
    /// matched ingestion reads, so the merged graph runs invoke -> file -> landing table -> view -> downstream and
    /// the execution waves order the chain. A producer nothing consumes still records its own declared output node,
    /// so the invoke is not a dangling node and links automatically once a matching ingestion is added.</summary>
    private static void ReconcileFileLinks(
        CollectionResult result, List<FileProducer> producers, List<FileConsumer> consumers, string root)
    {
        foreach (var producer in producers)
        {
            // One producer can declare several drops (an SFTP download of many file sets); each binds on its own,
            // and the invoke writes each distinct file node at most once (dedup across drops and consumers).
            var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in producer.Outputs)
            {
                var spec = output.ToSelectionSpec();
                var matched = false;
                foreach (var consumer in consumers)
                {
                    if (!FileSelection.Feeds(spec, consumer.Spec))
                    {
                        continue;
                    }

                    matched = true;
                    if (linked.Add(consumer.Node))
                    {
                        result.Facts.Add(new LineageFact
                        {
                            Flow = producer.Flow,
                            Relation = LineageRelation.Writes,
                            ServerRef = ServerIdentity.FileSystem,
                            Name = consumer.Node,
                            Tier = LineageTier.Declared,
                            KindHint = LineageNodeKind.File,
                        });
                    }
                }

                // A drop nothing consumes still records its own declared node, so it is visible and links
                // automatically once a matching ingestion is added.
                if (!matched && linked.Add(NormalizeFileIdentity(output.Location, root)))
                {
                    result.Facts.Add(FileFact(producer.Flow, LineageRelation.Writes, output.Location, root));
                }
            }
        }
    }

    private static string? Option(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<(string Alias, string ConnectionRef)> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);

    /// <summary>An invoke that declares it lands one or more file drops, awaiting reconciliation against the file
    /// ingestions.</summary>
    private sealed record FileProducer(string Flow, IReadOnlyList<InvokeOutput> Outputs);

    /// <summary>A file ingestion's source: the file node it reads and the selection spec a producer is matched
    /// against.</summary>
    private sealed record FileConsumer(string Flow, string Node, FileSelectionSpec Spec);
}
