using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The OSDU module's own verbs: <c>check</c> (everything checkable for one flow: its documents, the mapping against its
/// pinned template, the version of the partition cache it reads, the roots its payload files may sit under, and, with
/// <c>--connect</c>, the ingestion tables themselves), <c>cache</c> (list the versions of a partition's cache, or import a
/// cache flow's type files into it for offline work), and <c>template</c> (capture, import, list, show and delete the
/// templates in the module's database, osdu/docs/mapping-templates.md). A cache is captured from OSDU by running a cache
/// flow (<c>sqlflow run &lt;cache.yaml&gt;</c>), the same run the platform schedules.
/// </summary>
internal static class DeliveryVerbs
{
    /// <summary>Child datasets a connected check reports the columns of before it summarises the rest.</summary>
    private const int MaxDatasetsShown = 20;

    public static async Task<int> CheckAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Arguments.Positional(1) is not { } flowPath)
        {
            return context.UsageError("name the flow document to check.");
        }

        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var values = RunParameters.ParseValues(context.Arguments.GetOptions("--set"));
        var connect = context.Arguments.HasFlag("--connect");
        var source = engine.Documents.LoadSource(flowPath);
        var named = context.Arguments.GetOption("--interface");
        var flows = named is null ? source.Interfaces : [source.Interface(named)];

        var checks = new List<FlowCheck>(flows.Count);
        foreach (var flow in flows)
        {
            checks.Add(await CheckFlowAsync(context, engine, flow, values, connect, ct).ConfigureAwait(false));
        }

        if (!source.DeclaresInterfaces)
        {
            // The single form reports as it always has: one flow, one object.
            var single = checks[0];
            if (context.Json)
            {
                context.Out.WriteLine(CanonicalJson.Pretty(single.Result));
                return 0;
            }

            single.Write(null);
            return 0;
        }

        // The order of the checked interfaces, from after: and the relationships their mappings fill, as a run orders them.
        Model.InterfaceOrderPlan order;
        try
        {
            order = Model.InterfaceOrder.Plan(
                checks.Select(c => c.Schema.Interface).ToList(), Model.InterfaceOrder.Declared(source), checks.Select(c => c.Schema).ToList());
        }
        catch (DeliveryException ex)
        {
            throw new FlowValidationException($"{source.SourcePath ?? flowPath}: {ex.Message}", ex);
        }
        if (context.Json)
        {
            foreach (var check in checks)
            {
                var name = check.Schema.Interface;
                check.Result["wave"] = order.WaveOf(name);
                check.Result["waitsFor"] = Dependencies(order.WaitsFor(name), d => d.DependsOn);
                check.Result["notWaitedFor"] = Dependencies(order.NotWaitedFor.Where(d => Same(d.Interface, name)).ToList(), d => d.DependsOn);
            }

            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = source.Name,
                ["order"] = order.Describe(),
                ["interfaces"] = new JsonArray(checks.Select(c => (JsonNode)c.Result).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{source.Name}: {checks.Count} of {source.Interfaces.Count} interface(s) checked, in the order they run: {order.Describe()}"));
        foreach (var check in checks)
        {
            check.Write(order);
        }

        return 0;
    }

    /// <summary>What one flow (one interface of a source) answers as JSON, how it writes itself as text, and what it refers to.</summary>
    private sealed record FlowCheck(JsonObject Result, Action<Model.InterfaceOrderPlan?> Write, Model.InterfaceSchema Schema);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static JsonArray Dependencies(IReadOnlyList<Model.InterfaceDependency> dependencies, Func<Model.InterfaceDependency, string> other)
        => new(dependencies
            .Select(d => (JsonNode)new JsonObject
            {
                ["interface"] = other(d),
                ["origin"] = d.Origin == Model.DependencyOrigin.After ? "after" : "schema",
                ["why"] = d.Why,
            })
            .ToArray());

    /// <summary>
    /// Everything checkable for one flow (one interface of a source): what the check answers as JSON, how it writes it as
    /// text once the order of the checked interfaces is known, and what its records refer to. The runtime is opened and
    /// disposed here, so a source's interfaces are checked one at a time.
    /// </summary>
    private static async Task<FlowCheck> CheckFlowAsync(
        CliVerbContext context, EngineContext engine, Model.FlowDefinition flow, IReadOnlyDictionary<string, string> values, bool connect, CancellationToken ct)
    {
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow, values, ct).ConfigureAwait(false);
        RouteChecks.Check(flow, runtime.Mapping.Mapping.Kind);
        var schema = await runtime.SchemaAsync(ct).ConfigureAwait(false);
        var roots = PayloadRoots.Of(flow, runtime.Parameters);

        var result = new JsonObject
        {
            ["flow"] = flow.Name,
            ["flowId"] = flow.Id.ToString("D"),
            ["mapping"] = runtime.Mapping.Mapping.Reference,
            ["template"] = new JsonObject { ["kind"] = runtime.Mapping.Mapping.Template.Kind, ["version"] = runtime.Mapping.Mapping.Template.Version },
            ["renderContext"] = JsonNode.Parse(runtime.Mapping.Context.Canonical()),
            ["source"] = new JsonObject
            {
                ["connection"] = flow.Source.Connection,
                ["record"] = flow.Source.Record.Object,
                ["key"] = new JsonArray(flow.Source.Record.Key.Select(k => (JsonNode)JsonValue.Create(k)).ToArray()),
                ["datasets"] = new JsonArray(flow.Source.Datasets
                    .OrderBy(d => d.Key, StringComparer.Ordinal)
                    .Select(d => (JsonNode)new JsonObject { ["name"] = d.Key, ["object"] = d.Value.Object })
                    .ToArray()),
                ["payloadRoots"] = new JsonArray(roots.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
            },
            ["mappings"] = runtime.Layout.MappingsDirectory,
            ["cache"] = runtime.Mapping.Context.CacheScope is { } cacheScope
                ? new JsonObject
                {
                    ["partition"] = cacheScope,
                    ["version"] = runtime.Mapping.References.Version,
                    ["types"] = runtime.Mapping.References.Types.Count,
                }
                : null,
        };

        if (flow.Interface is { } interfaceName)
        {
            result["interface"] = interfaceName;
            result["ledger"] = flow.LedgerName;
            result["route"] = new JsonObject
            {
                ["name"] = DeliveryProtocols.Name(flow.Target.Protocol),
                ["reason"] = flow.RouteReason,
            };
            result["after"] = new JsonArray(flow.After.Select(a => (JsonNode)JsonValue.Create(a)).ToArray());
        }

        // Where a ddms flow's records go is worked out from what the flow declares; a DDMS it names by registration is
        // read from the Register service when the flow runs, not by a check.
        var ddms = Protocols.DeliveryProtocols.ReachesDdms(flow.Target.Protocol) ? DdmsRouting.Of(flow).Explain(runtime.Mapping.Mapping.Kind) : null;
        if (ddms is not null)
        {
            result["ddms"] = ddms;
        }

        if (connect)
        {
            result["read"] = await ReadAsync(engine, runtime, ct).ConfigureAwait(false);
        }

        // What the text form needs is captured now: the runtime is gone by the time it writes.
        var reference = runtime.Mapping.Mapping.Reference;
        var template = runtime.Mapping.Mapping.Template.ToString();
        var captured = runtime.Mapping.Schema.CapturedUtc;
        var cacheLine = runtime.Mapping.Context.CacheScope is { } scope
            ? $"    cache       partition {scope} version {runtime.Mapping.References.Version} ({runtime.Mapping.References.Types.Count} type(s))"
            : "    cache       none (the mapping reads nothing from a cache)";
        var searchLine = runtime.Mapping.Mapping.Searches.Count == 0 ? null : SearchLine(runtime.Mapping.Context);
        var contextHash = runtime.Mapping.Context.Hash()[..16];
        var mappings = runtime.Layout.MappingsDirectory;

        void Write(Model.InterfaceOrderPlan? order)
        {
            context.Out.WriteLine($"OK  {flow.Label} ({flow.Id:D})");
            if (flow.Interface is { } name && order is not null)
            {
                context.Out.WriteLine($"    ledger      {flow.LedgerName}");
                context.Out.WriteLine($"    route       {DeliveryProtocols.Name(flow.Target.Protocol)}: {flow.RouteReason}");
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    wave        {order.WaveOf(name)}"));
                var waits = order.WaitsFor(name);
                if (waits.Count == 0)
                {
                    context.Out.WriteLine("    waits for   nothing");
                }

                foreach (var wait in waits)
                {
                    context.Out.WriteLine($"    waits for   {wait.DependsOn}: {wait.Why}");
                }

                foreach (var reference in order.NotWaitedFor.Where(d => Same(d.Interface, name)))
                {
                    context.Out.WriteLine($"    not waited  {reference.DependsOn}: {reference.Why}");
                }
            }

            if (ddms is not null)
            {
                context.Out.WriteLine($"    ddms        {ddms}");
            }

            context.Out.WriteLine($"    mapping     {reference}");
            context.Out.WriteLine($"    template    {template} (saved {captured:u})");
            context.Out.WriteLine(cacheLine);
            if (searchLine is not null)
            {
                context.Out.WriteLine(searchLine);
            }

            context.Out.WriteLine($"    context     {contextHash}");
            context.Out.WriteLine($"    mappings    {mappings}");
            context.Out.WriteLine(
                $"    source      {flow.Source.Connection} {flow.Source.Record.Object}"
                + (flow.Source.Datasets.Count == 0 ? string.Empty : $" (+{flow.Source.Datasets.Count} dataset(s))"));
            context.Out.WriteLine(roots.Count == 0
                ? "    payloads    none (the flow streams no payload files)"
                : $"    payloads    under {string.Join(", ", roots)}");
            if (result["read"] is JsonObject read)
            {
                WriteRead(context, read);
            }
            else
            {
                context.Out.WriteLine("    tables      not read (run with --connect to open the ingestion tables)");
            }
        }

        return new FlowCheck(result, Write, schema);
    }

    /// <summary>
    /// Opens the flow's ingestion tables on this machine, through the flow's own connection reference, and reports what a
    /// run would read now: the tables and their columns, the key columns with the SQL types the window and key parameters
    /// are typed by, the flow's system columns, the window the scope's watermark leaves, and how many records are in it.
    /// Nothing is planned, rendered or delivered.
    /// </summary>
    private static async Task<JsonObject> ReadAsync(EngineContext engine, FlowRuntime runtime, CancellationToken ct)
    {
        var flow = runtime.Flow;
        var scope = Planner.ScopeKey(runtime.Parameters);
        var watermark = engine.Ledger is { } ledger ? await ledger.GetWatermarkAsync(flow.Id, scope, ct).ConfigureAwait(false) : null;
        var selection = watermark is null
            ? SourceSelection.Full()
            : SourceSelection.Incremental(watermark.UpdatedThroughUtc.AddSeconds(-flow.Source.Incremental.OverlapSeconds));

        var source = engine.Sources.Open(flow, runtime.Parameters, engine.Loggers);
        var header = await source.OpenAsync(selection, null, ct).ConfigureAwait(false);
        var columns = new JsonObject();
        foreach (var (dataset, names) in header.Columns.OrderBy(c => c.Key, StringComparer.Ordinal).Take(MaxDatasetsShown + 1))
        {
            columns[dataset] = new JsonArray(names.Order(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
        }

        return new JsonObject
        {
            ["scope"] = scope,
            ["selection"] = selection.Describe(),
            ["watermark"] = watermark is null ? null : watermark.UpdatedThroughUtc.ToString("O", CultureInfo.InvariantCulture),
            ["window"] = new JsonObject
            {
                ["from"] = header.Window.LowerUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["to"] = header.Window.UpperUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            ["candidates"] = header.EstimatedCandidates,
            ["hasChanges"] = header.HasChanges,
            ["keyColumns"] = new JsonArray(header.KeyColumns
                .Select(k => (JsonNode)new JsonObject { ["name"] = k.Name, ["type"] = k.SqlType })
                .ToArray()),
            ["systemColumns"] = new JsonObject
            {
                ["updated"] = flow.Source.SystemColumns.Updated,
                ["fileName"] = flow.Source.SystemColumns.FileName,
                ["rowNumber"] = flow.Source.SystemColumns.RowNumber,
                ["deleted"] = flow.Source.SystemColumns.Deleted,
            },
            ["columns"] = columns,
        };
    }

    private static void WriteRead(CliVerbContext context, JsonObject read)
    {
        context.Out.WriteLine($"    scope       {read["scope"]?.GetValue<string>()} ({read["selection"]?.GetValue<string>()})");
        var from = read["window"]?["from"]?.GetValue<string>();
        var to = read["window"]?["to"]?.GetValue<string>();
        context.Out.WriteLine($"    window      {(from is null ? "everything" : "after " + from)} through {to}");
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    candidates  {read["candidates"]?.GetValue<long>() ?? 0} record(s) in the window; changes: {read["hasChanges"]?.GetValue<bool>() ?? false}"));
        if (read["keyColumns"] is JsonArray keys)
        {
            context.Out.WriteLine($"    key         {string.Join(", ", keys.Select(k => $"{k?["name"]?.GetValue<string>()} {k?["type"]?.GetValue<string>()}"))}");
        }

        if (read["columns"] is JsonObject columns)
        {
            foreach (var (dataset, names) in columns)
            {
                var count = names is JsonArray array ? array.Count : 0;
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    table       {dataset}: {count} column(s)"));
            }
        }
    }

    public static async Task<int> CacheAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } target)
        {
            return context.UsageError("name the partition or the cache document.");
        }

        var store = engine.Cache
            ?? throw new FlowValidationException("Caches live in the module's database. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.");

        switch (verb)
        {
            case "list":
            {
                // A partition is often named ${env:...}, by a cache flow or on the command line; the cache is keyed by
                // what that resolves to, as a capture and an import key it, so the partition is resolved first.
                var (declared, where) = File.Exists(target) ? (engine.Documents.LoadCache(target).Scope, target) : (target, "sqlflow cache list");
                var scope = CacheScope.Normalize(await engine.Secrets.ResolveAsync(CacheScope.Normalize(declared, where), ct).ConfigureAwait(false), where);
                var versions = await store.ListVersionsAsync(scope, ct).ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray(versions.Select(v => (JsonNode)Describe(v)).ToArray())));
                    return 0;
                }

                if (versions.Count == 0)
                {
                    context.Out.WriteLine($"the cache of partition {scope} holds no version yet; run a cache flow of the partition with the refresh operation to capture one");
                }

                foreach (var v in versions)
                {
                    var run = v.RunId is { } runId ? $" in run {runId:D}" : string.Empty;
                    context.Out.WriteLine(
                        $"{v.Version}  {(v.Current ? "current" : "       ")}  {v.Items} record(s) in {v.Types.Count} type(s), written by cache flow {v.FlowName} at {v.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} for {v.CapturedBy}{run}");
                }

                if (versions.FirstOrDefault(v => v.Current) is { } current)
                {
                    context.Out.WriteLine();
                    if (current.SystemProperties.Count == 0)
                    {
                        context.Out.WriteLine($"system properties: none read yet; refresh a cache flow of partition {scope} to read them");
                    }
                    else
                    {
                        context.Out.WriteLine($"system properties of partition {scope}, as version {current.Version} holds them (settings of the platform, not cached records):");
                        foreach (var property in current.SystemProperties)
                        {
                            var source = property.Source is null ? string.Empty : $"  from {property.Source}";
                            var detail = property.Detail is null ? string.Empty : $"  ({property.Detail})";
                            context.Out.WriteLine($"  {property.Service,-8}  {property.Name}  {property.State.ToString().ToLowerInvariant()}{source}{detail}");
                        }
                    }
                }

                return 0;
            }

            case "import":
            {
                var cache = engine.Documents.LoadCache(target);
                if (context.Arguments.GetOption("--from-dir") is not { } directory)
                {
                    return context.UsageError("name the directory the type files are in with --from-dir.");
                }

                var full = Path.GetFullPath(directory);
                // Keyed by the partition the document actually names, resolved, so an offline import lands in the same
                // cache a capture writes and a render reads.
                var importScope = CacheScope.Normalize(
                    await engine.Secrets.ResolveAsync(cache.Scope, ct).ConfigureAwait(false),
                    cache.SourcePath ?? cache.Name);
                var builder = new SnapshotBuilder(store, importScope, cache.Name, engine.Time, engine.Loggers.CreateLogger<SnapshotBuilder>());
                var write = await builder.ImportDirectoryAsync(
                    full, cache.Types, new CacheCapture(null, "cli:" + Environment.UserName, $"files under {full}"), ct).ConfigureAwait(false);
                var records = write.Snapshot.Types.Sum(t => t.Items.Count);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
                    {
                        ["partition"] = cache.Scope,
                        ["flow"] = cache.Name,
                        ["version"] = write.Snapshot.Version,
                        ["written"] = write.Written,
                        ["types"] = write.Snapshot.Types.Count,
                        ["records"] = records,
                    }));
                    return 0;
                }

                context.Out.WriteLine(write.Written
                    ? $"cache of partition {cache.Scope}: version {write.Snapshot.Version} written from cache flow {cache.Name}, holding {write.Snapshot.Types.Count} type(s) and {records} record(s), now current"
                    : $"cache of partition {cache.Scope}: the files add nothing version {write.Snapshot.Version} does not already hold, so nothing was written");
                return 0;
            }

            default:
                return context.UsageError($"'{verb}' is not a cache subcommand.");
        }
    }

    private static JsonObject Describe(CacheVersionInfo v) => new()
    {
        ["partition"] = v.Scope,
        ["flow"] = v.FlowName,
        ["version"] = v.Version,
        ["sequence"] = v.Sequence,
        ["current"] = v.Current,
        ["capturedUtc"] = v.CapturedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["capturedBy"] = v.CapturedBy,
        ["runId"] = v.RunId?.ToString("D"),
        ["origin"] = v.Origin,
        ["previousVersion"] = v.PreviousVersion,
        ["records"] = v.Items,
        ["types"] = new JsonArray(v.Types.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["entityType"] = t.EntityType, ["records"] = t.Items }).ToArray()),
        ["systemProperties"] = new JsonArray(v.SystemProperties.Select(p => (JsonNode)new JsonObject
        {
            ["service"] = p.Service,
            ["name"] = p.Name,
            ["state"] = p.State.ToString(),
            ["source"] = p.Source,
            ["detail"] = p.Detail,
        }).ToArray()),
    };

    public static async Task<int> TemplateAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        var store = engine.Templates
            ?? throw new FlowValidationException("Templates live in the module's database. Run 'sqlflow template' with --db <conn-ref>, or set the catalog variable.");
        var actor = "cli:" + Environment.UserName;

        switch (verb)
        {
            case "capture":
            {
                // The canonical OSDU schemas are the Open Group's data definitions, not whatever one platform happens to host.
                if (context.Arguments.Positional(2) is not null)
                {
                    return context.UsageError("'sqlflow template capture' takes no flow: it saves the kind's schema from the OSDU data definitions.");
                }

                if (context.Arguments.GetOption("--kind") is not { } kind)
                {
                    return context.UsageError("name the kind to capture with --kind.");
                }

                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var file = await DataDefinitions(http, engine.Time).FetchAsync(context.Arguments.GetOption("--release"), kind, ct).ConfigureAwait(false);

                // The bundled schema can be written beside the mapping that pins it, which is what a repository carries
                // so its templates can be imported again without reaching the data definitions (sqlflow template import).
                if (context.Arguments.GetOption("--out") is { } outPath)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    await File.WriteAllTextAsync(outPath, CanonicalJson.Pretty(file.Schema.Root), ct).ConfigureAwait(false);
                    context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote the bundled schema of {kind} to {Path.GetFullPath(outPath)}"));
                }

                return Report(context, await store.SaveAsync(file.Schema, file.Origin, actor, ct).ConfigureAwait(false));
            }

            case "import":
            {
                if (context.Arguments.GetOption("--kind") is not { } kind)
                {
                    return context.UsageError("name the kind the schema saves as with --kind.");
                }

                if (context.Arguments.GetOption("--from-dir") is { } directory)
                {
                    var schema = await TemplateSources.FromDirectoryAsync(directory, kind, engine.Time, ct).ConfigureAwait(false);
                    return Report(context, await store.SaveAsync(schema, $"data definitions under {Path.GetFullPath(directory)}", actor, ct).ConfigureAwait(false));
                }

                // A bundled file is saved as it is. A file as the data definitions publish it is bundled with the shared schemas
                // it refers to from --release (the newest when it is not given), exactly as the Templates page imports it.
                if (context.Arguments.Positional(2) is not { } path)
                {
                    return context.UsageError("name the schema file to import, or the data definitions directory with --from-dir.");
                }

                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var imported = await DataDefinitions(http, engine.Time)
                    .ImportAsync(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), kind, context.Arguments.GetOption("--release"), path, ct)
                    .ConfigureAwait(false);
                return Report(context, await store.SaveAsync(imported.Schema, imported.Origin($"file {Path.GetFileName(path)}"), actor, ct).ConfigureAwait(false));
            }

            case "list":
            {
                var templates = await store.ListAsync(ct).ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray(templates.Select(t => (JsonNode)Describe(t)).ToArray())));
                    return 0;
                }

                if (templates.Count == 0)
                {
                    context.Out.WriteLine("no templates saved yet");
                }

                foreach (var t in templates)
                {
                    context.Out.WriteLine($"{t.Kind}  {t.Version}  saved {t.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} by {t.CapturedBy}  ({t.Origin})");
                }

                return 0;
            }

            case "show":
            {
                if (context.Arguments.GetOption("--kind") is not { } kind)
                {
                    return context.UsageError("name the kind to show with --kind.");
                }

                var version = context.Arguments.GetOption("--version")
                    ?? (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Kind == kind)?.Version
                    ?? throw new FlowValidationException($"There is no saved template for '{kind}'.");
                var schema = await store.LoadAsync(new TemplateReference(kind, version), ct).ConfigureAwait(false)
                    ?? throw new FlowValidationException($"There is no template {new TemplateReference(kind, version)}.");
                var template = OsduTemplate.From(schema);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
                    {
                        ["kind"] = template.Kind,
                        ["version"] = template.Version,
                        ["variables"] = new JsonArray(template.Variables.Select(v => (JsonNode)Describe(v)).ToArray()),
                    }));
                    return 0;
                }

                context.Out.WriteLine($"{template.Kind} version {template.Version}: {template.Variables.Count} variable(s)");
                foreach (var v in template.Variables)
                {
                    var notes = new List<string>();
                    if (v.Required)
                    {
                        notes.Add("required");
                    }

                    if (v.Role != TemplateVariableRole.Mapping)
                    {
                        notes.Add(v.Role == TemplateVariableRole.Engine ? "written by OSDU Delivery" : "set by OSDU");
                    }

                    if (v.Relationships.Count > 0)
                    {
                        notes.Add("points to " + string.Join(", ", v.Relationships));
                    }

                    if (v.UnitContext is not null)
                    {
                        notes.Add("unit " + v.UnitContext);
                    }

                    context.Out.WriteLine($"  {v.Path.Text,-60} {Shape(v),-22} {string.Join("; ", notes)}");
                }

                return 0;
            }

            case "delete":
            {
                if (context.Arguments.GetOption("--kind") is not { } kind || context.Arguments.GetOption("--version") is not { } version)
                {
                    return context.UsageError("name the template to delete with --kind and --version.");
                }

                var reference = new TemplateReference(kind, version);
                await store.DeleteAsync(reference, ct).ConfigureAwait(false);
                context.Out.WriteLine($"deleted template {reference}");
                return 0;
            }

            default:
                return context.UsageError($"'{verb}' is not a template subcommand.");
        }
    }

    /// <summary>The OSDU data definitions over <paramref name="http"/>, kept in the default local copy.</summary>
    private static OsduDataDefinitions DataDefinitions(HttpClient http, TimeProvider time)
        => new(
            () => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, OsduDataDefinitions.DefaultCacheDirectory,
            OsduDataDefinitions.DefaultFreshness, OsduDataDefinitions.DefaultDownloadTimeout, time);

    private static int Report(CliVerbContext context, TemplateSaved saved)
    {
        if (context.Json)
        {
            var node = Describe(saved.Template);
            node["outcome"] = saved.Outcome == TemplateSaveOutcome.Created ? "created" : "unchanged";
            context.Out.WriteLine(CanonicalJson.Pretty(node));
            return 0;
        }

        context.Out.WriteLine(saved.Outcome == TemplateSaveOutcome.Created
            ? $"saved template {saved.Template.Reference}"
            : $"template {saved.Template.Reference} was already saved");
        return 0;
    }

    private static JsonObject Describe(TemplateInfo t) => new()
    {
        ["kind"] = t.Kind,
        ["version"] = t.Version,
        ["capturedUtc"] = t.CapturedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["capturedBy"] = t.CapturedBy,
        ["origin"] = t.Origin,
    };

    private static JsonObject Describe(TemplateVariable v) => new()
    {
        ["path"] = v.Path.Text,
        ["shape"] = v.Shape.ToString(),
        ["type"] = v.Type,
        ["itemType"] = v.ItemType,
        ["required"] = v.Required,
        ["role"] = v.Role.ToString(),
        ["relationships"] = new JsonArray(v.Relationships.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
        ["unitContext"] = v.UnitContext,
        ["description"] = v.Description,
    };

    private static string Shape(TemplateVariable v) => v.Shape switch
    {
        TemplateVariableShape.ValueList => $"list of {v.ItemType}",
        TemplateVariableShape.Group => "object",
        TemplateVariableShape.GroupList => "list of objects",
        TemplateVariableShape.Whole => $"whole {v.Type}",
        _ => v.Type,
    };

    /// <summary>How the flow's searches are written, and the system properties of the partition that decide it.</summary>
    private static string SearchLine(RenderContext context)
    {
        var rule = context.KeywordLower
            ? "exact, then regardless of case where one record answers"
            : "exact only";
        var properties = string.Join(", ", context.SystemProperties.Select(p => $"{p.Service} {p.Name} {p.State.ToString().ToLowerInvariant()}"));
        return $"    searches    {rule} ({properties})";
    }
}
