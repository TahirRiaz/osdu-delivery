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
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader());

    private readonly YamlScheduleLibraryLoader _scheduleLibraries = new();

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

        // Shared schedules: build the repo-wide library (dedicated schedules.yaml files plus named inline blocks),
        // then resolve every `schedule: <name>` reference to a concrete cadence. Done after the whole estate is
        // collected because a reference can point at a definition in any file.
        ResolveSchedules(result, root);

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

    /// <summary>Whether a file is a shared-schedule library: named <c>schedules.yaml</c> or ending in
    /// <c>.schedules.yaml</c>. These are not flow documents (the flow scan globs <c>*.flow.yaml</c>) and never
    /// become pipelines; they only publish named schedules for flows to reference.</summary>
    private static bool IsScheduleLibraryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("schedules.yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".schedules.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the repo's shared-schedule library and resolves each flow's <c>schedule: &lt;name&gt;</c> reference to
    /// the referenced cadence, in place on the collected flows. A named schedule may be defined in a dedicated
    /// <c>schedules.yaml</c> library file or as a <c>name:</c>d inline block on any flow; the two sources share one
    /// namespace, and the first definition of a name wins (a redefinition is warned). A reference to an unknown name
    /// leaves that flow unscheduled with a warning, never a broken schedule.
    /// </summary>
    private void ResolveSchedules(CollectionResult result, string root)
    {
        var library = new Dictionary<string, ScheduleSpec>(StringComparer.OrdinalIgnoreCase);

        void Register(string name, ScheduleSpec spec, string origin)
        {
            if (!library.TryAdd(name, spec))
            {
                result.Warnings.Add(
                    $"schedule name '{name}' is declared more than once ({origin} redefines an earlier definition); the first wins.");
            }
        }

        // 1) Dedicated library files.
        var libraryFiles = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(IsScheduleLibraryFile)
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in libraryFiles)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            var parsed = _scheduleLibraries.Parse(yaml, relative);
            result.Warnings.AddRange(parsed.Warnings);
            foreach (var named in parsed.Schedules)
            {
                Register(named.Name, named.Spec, relative);
            }
        }

        // 2) Named inline blocks a flow publishes for reuse (schedule: with a name: key).
        foreach (var flow in result.Flows)
        {
            if (flow.Schedule is { Ref: null, Name: { Length: > 0 } name } inline)
            {
                Register(name, inline with { Ref = null }, $"'{flow.Node.Name}' ({flow.Node.File})");
            }
        }

        // 3) Resolve references in place; strip the resolution metadata so a resolved schedule is a plain cadence.
        for (var i = 0; i < result.Flows.Count; i++)
        {
            var flow = result.Flows[i];
            if (flow.Schedule is not { Ref: { Length: > 0 } reference })
            {
                continue;
            }

            if (library.TryGetValue(reference, out var resolved))
            {
                result.Flows[i] = flow with { Schedule = resolved with { Name = null, Ref = null } };
            }
            else
            {
                result.Warnings.Add(
                    $"'{flow.Node.Name}' ({flow.Node.File}) references schedule '{reference}', which no schedules.yaml or " +
                    "named inline block defines; the flow is left unscheduled.");
                result.Flows[i] = flow with { Schedule = null };
            }
        }
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
        // The flow nodes come from the shared header projection, the single authority for what a document
        // declares (name, kind, batch, servers, mode, lifecycle, schedule), shared with the catalog's per-run
        // write-back so the two paths can never extract a document differently. An unknown document kind throws
        // there and is reported by the per-file catch in Collect(directory). The switch below contributes only what
        // lineage adds on top of the headers: the server inventory, the declared facts, and the file producers and
        // consumers reconciled after the whole estate is scanned.
        var headers = FlowDocumentHeaders.Project(document);
        foreach (var header in headers)
        {
            result.Flows.Add(new CollectedFlow
            {
                Node = new LineageFlowNode
                {
                    Name = header.Name,
                    Kind = header.Kind,
                    File = file,
                    Batch = header.Batch,
                    Mode = header.Mode,
                    Lifecycle = header.Lifecycle,
                },
                SourceServerRef = header.SourceServerRef,
                TargetServerRef = header.TargetServerRef,
                Schedule = header.Schedule,
                FileWriteUtc = fileWriteUtc,
            });
        }

        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var name = headers[0].Name;
                RegisterServers(result, doc.Document.Connections);
                var source = headers[0].SourceServerRef!;
                var target = headers[0].TargetServerRef;

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

                // The embedded healthCheck: block became its own flow node above (the projection's derived hc
                // sibling); it READS the load's target, which is exactly the dependency that orders it after the
                // load in waves and node runs.
                if (doc.Document.HealthCheck is { } check)
                {
                    result.Facts.Add(ObjectFact(check.SysAlias, LineageRelation.Reads, target, check.Target, LineageNodeKind.Table));
                }

                break;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                RegisterServers(result, doc.Document.Connections);
                result.Facts.Add(ObjectFact(headers[0].Name, LineageRelation.Reads, headers[0].TargetServerRef, flow.Source, LineageNodeKind.Unknown));
                if (!string.IsNullOrWhiteSpace(flow.TrgPath))
                {
                    result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, flow.TrgPath, root));
                }

                break;
            }

            case StoredProcedureFlowDocument doc:
            {
                RegisterServers(result, doc.Document.Connections);

                // The flow requires the procedure; the procedure's own reads/writes are its module's
                // lineage, expanded by the derived tier.
                result.Facts.Add(ObjectFact(
                    headers[0].Name, LineageRelation.Requires, headers[0].TargetServerRef, doc.Document.Flow.Procedure, LineageNodeKind.Procedure));
                break;
            }

            case HealthCheckFlowDocument doc:
            {
                RegisterServers(result, doc.Document.Connections);
                result.Facts.Add(ObjectFact(
                    headers[0].Name, LineageRelation.Reads, headers[0].TargetServerRef, doc.Document.Flow.Target, LineageNodeKind.Unknown));
                break;
            }

            case FileFlowDocument doc:
            {
                var flow = doc.Flow;
                var target = headers[0].TargetServerRef;
                result.Servers.TryAdd(target, (flow.Target.Connection, Core.Connections.DataSourceKind.MSSQL));

                // The file the flow reads is its Location, or the srcPath option when no Location is given (the
                // loader accepts either). Its normalized identity is the file node; recording the same source spec
                // as a consumer lets an invoke or acquisition that lands into this folder link to THIS node.
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
                if (definition.Outputs.Count > 0)
                {
                    producers.Add(new FileProducer(definition.InvokeAlias, definition.Outputs));
                }

                break;
            }

            case AcquireFlowDocument doc:
                // An acquisition fetches from a third party and lands raw files; its declared landing target chains
                // to the downstream file flow that reads that location.
                result.Facts.Add(FileFact(headers[0].Name, LineageRelation.Writes, doc.Flow.Landing.Target, root));
                break;

                // SourceControlFlowDocument/BatchFlowDocument project no headers and contribute no facts: a batch's
                // ordering is computed FROM lineage, never part of it.
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

    /// <summary>An invoke that declares it lands one or more file drops, awaiting reconciliation against the file
    /// ingestions.</summary>
    private sealed record FileProducer(string Flow, IReadOnlyList<InvokeOutput> Outputs);

    /// <summary>A file ingestion's source: the file node it reads and the selection spec a producer is matched
    /// against.</summary>
    private sealed record FileConsumer(string Flow, string Node, FileSelectionSpec Spec);
}
