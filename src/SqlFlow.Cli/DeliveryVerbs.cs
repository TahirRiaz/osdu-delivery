using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Cli;

/// <summary>
/// The delivery kind's own verbs: <c>check</c> (everything checkable offline for one flow: the mapping against its
/// pinned template, the reference snapshot, and the drop's manifest when the drop is present), <c>snapshot</c> (capture
/// or list the reference snapshots of the cache a flow renders against), and <c>template</c> (capture, import, list,
/// show and delete the templates in the catalog, docs/delivery/mapping-templates.md).
/// </summary>
internal static class DeliveryVerbs
{
    private const string TemplateUsage =
        "Usage: sqlflow template (capture --kind <kind> [--release <tag>] | import <schema.json> --kind <kind> [--release <tag>] | import --from-dir <dir> --kind <kind> | list | show --kind <kind> [--version <version>] | delete --kind <kind> --version <version>) [--db <conn-ref>] [--json]";

    public static async Task<int> CheckAsync(IServiceProvider provider, string flowPath, string[] args, bool json, CancellationToken ct)
    {
        var engine = provider.GetRequiredService<EngineContext>();
        var values = RunParameters.ParseValues(Program.GetOptions(args, "--set"));
        var dropOverride = Program.GetOption(args, "--drop");
        using var runtime = await FlowRuntime.CreateAsync(engine, flowPath, values, dropOverride, ct).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["flow"] = runtime.Flow.Name,
            ["flowId"] = runtime.Flow.Id.ToString("D"),
            ["mapping"] = runtime.Mapping.Mapping.Reference,
            ["template"] = new JsonObject { ["kind"] = runtime.Mapping.Mapping.Template.Kind, ["version"] = runtime.Mapping.Mapping.Template.Version },
            ["renderContext"] = JsonNode.Parse(runtime.Mapping.Context.Canonical()),
            ["drop"] = runtime.DropLocation,
            ["mappings"] = runtime.Layout.MappingsDirectory,
            ["snapshots"] = runtime.Layout.SnapshotsRoot,
        };

        // The drop is checked when it is there (or was named explicitly); a flow whose drop has not landed yet still
        // validates its documents, so the check is usable before the first submission.
        var dropChecked = false;
        var warnings = new List<string>();
        if (dropOverride is not null || Directory.Exists(runtime.DropLocation) || runtime.DropLocation.Contains("://", StringComparison.Ordinal))
        {
            try
            {
                var drop = await engine.Drops.OpenAsync(runtime.DropLocation, runtime.Flow.Source.Manifest, ct).ConfigureAwait(false);
                var issues = Preflight.Check(runtime.Mapping.Mapping, runtime.Mapping.Schema, runtime.Mapping.References, runtime.Mapping.Context, drop.Manifest.DeclaredColumns());
                Preflight.ThrowIfFailed(issues, runtime.Flow.SourcePath ?? runtime.Flow.Name);
                warnings.AddRange(issues.Select(i => i.Message));
                result["manifest"] = new JsonObject { ["submissionId"] = drop.Manifest.SubmissionId.ToString("D"), ["recordCount"] = drop.Manifest.RecordCount };
                result["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray());
                dropChecked = true;
            }
            catch (FlowValidationException) when (dropOverride is null)
            {
                // No drop present at the declared location: the documents were validated without it.
            }
        }

        if (json)
        {
            Console.WriteLine(CanonicalJson.Pretty(result));
            return 0;
        }

        Console.WriteLine($"OK  {runtime.Flow.Name} ({runtime.Flow.Id:D})");
        Console.WriteLine($"    mapping     {runtime.Mapping.Mapping.Reference}");
        Console.WriteLine($"    template    {runtime.Mapping.Mapping.Template} (saved {runtime.Mapping.Schema.CapturedUtc:u})");
        Console.WriteLine($"    references  {runtime.Mapping.References.Version} ({runtime.Mapping.References.Types.Count} type(s))");
        Console.WriteLine($"    context     {runtime.Mapping.Context.Hash()[..16]}");
        Console.WriteLine($"    mappings    {runtime.Layout.MappingsDirectory}");
        Console.WriteLine($"    snapshots   {runtime.Layout.SnapshotsRoot}");
        Console.WriteLine(dropChecked
            ? $"    drop        {runtime.DropLocation} (manifest and source bindings checked)"
            : $"    drop        {runtime.DropLocation} (not present; documents validated without it)");
        foreach (var warning in warnings)
        {
            Console.WriteLine($"WARN  {warning}");
        }

        return 0;
    }

    public static async Task<int> SnapshotAsync(IServiceProvider provider, string flowPath, string[] positional, string[] args, CancellationToken ct)
    {
        var engine = provider.GetRequiredService<EngineContext>();
        var flow = engine.Documents.LoadFlow(flowPath);
        var layout = DeliveryLayout.Resolve(flow);
        var (mappings, store) = FlowRuntime.RenderInputs(engine, flow);
        var builder = new SnapshotBuilder(store, engine.Time, engine.Loggers.CreateLogger<SnapshotBuilder>());
        var verb = positional.Length > 2 ? positional[2].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "references":
            {
                var makeCurrent = !args.Contains("--no-current");
                ReferenceSnapshot snapshot;
                if (Program.GetOption(args, "--from-dir") is { } directory)
                {
                    snapshot = await builder.ReferencesFromDirectoryAsync(directory, makeCurrent, ct).ConfigureAwait(false);
                }
                else
                {
                    var specPath = Program.GetOption(args, "--spec")
                        ?? throw new FlowValidationException("Usage: sqlflow snapshot <flow.yaml> references (--from-dir <dir> | --spec <spec.json> [--endpoint <url>]) [--no-current]");
                    var spec = ReferenceCaptureSpec.Parse(await File.ReadAllTextAsync(specPath, ct).ConfigureAwait(false), specPath);
                    using var osdu = await ConnectAsync(flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, flow.Reliability, args, engine, ct).ConfigureAwait(false);
                    snapshot = await builder.ReferencesFromOsduAsync(osdu, spec, makeCurrent, ct).ConfigureAwait(false);
                }

                Console.WriteLine($"references {snapshot.Version} ({snapshot.Types.Count} type(s), {snapshot.Types.Sum(t => t.Items.Count)} item(s)){(makeCurrent ? ", now current" : string.Empty)} -> {layout.SnapshotsRoot}");
                return 0;
            }

            case "list":
            {
                Console.WriteLine($"snapshot store {layout.SnapshotsRoot}");
                var current = await store.CurrentReferenceVersionAsync(ct).ConfigureAwait(false);
                var versions = await store.ListReferenceVersionsAsync(ct).ConfigureAwait(false);
                if (versions.Count == 0)
                {
                    Console.WriteLine("  no reference snapshots captured yet");
                }

                foreach (var version in versions)
                {
                    Console.WriteLine($"  references {version}{(version == current ? "  (current)" : string.Empty)}");
                }

                var mapping = mappings.Load(flow.Render.Mapping);
                if (engine.Templates is not { } templates)
                {
                    Console.WriteLine($"  template {mapping.Template}: not checked (templates live in the catalog; add --db)");
                }
                else
                {
                    var template = await templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false);
                    Console.WriteLine(template is null
                        ? $"  template {mapping.Template}: not saved (save it on the Templates page, or with 'sqlflow template import')"
                        : $"  template {mapping.Template} (saved {template.CapturedUtc:u})");
                }

                return 0;
            }

            default:
                throw new FlowValidationException("Usage: sqlflow snapshot <flow.yaml> (references | list) ...");
        }
    }

    public static async Task<int> TemplateAsync(IServiceProvider provider, string[] positional, string[] args, bool json, CancellationToken ct)
    {
        var engine = provider.GetRequiredService<EngineContext>();
        var verb = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        var store = engine.Templates
            ?? throw new FlowValidationException("Templates live in the catalog. Run 'sqlflow template' with --db <conn-ref>, or set the catalog variable.");
        var actor = "cli:" + Environment.UserName;

        switch (verb)
        {
            case "capture":
            {
                // The canonical OSDU schemas are the Open Group's data definitions, not whatever one platform happens to host.
                if (positional.Length > 2)
                {
                    throw new FlowValidationException(
                        "'sqlflow template capture' takes no flow: it saves the kind's schema from the OSDU data definitions. " + TemplateUsage);
                }

                var kind = Program.GetOption(args, "--kind") ?? throw new FlowValidationException(TemplateUsage);
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var file = await DataDefinitions(http, engine.Time).FetchAsync(Program.GetOption(args, "--release"), kind, ct).ConfigureAwait(false);
                return Report(await store.SaveAsync(file.Schema, file.Origin, actor, ct).ConfigureAwait(false), json);
            }

            case "import":
            {
                var kind = Program.GetOption(args, "--kind") ?? throw new FlowValidationException(TemplateUsage);
                if (Program.GetOption(args, "--from-dir") is { } directory)
                {
                    var schema = await TemplateSources.FromDirectoryAsync(directory, kind, engine.Time, ct).ConfigureAwait(false);
                    return Report(await store.SaveAsync(schema, $"data definitions under {Path.GetFullPath(directory)}", actor, ct).ConfigureAwait(false), json);
                }

                // A bundled file is saved as it is. A file as the data definitions publish it is bundled with the shared schemas
                // it refers to from --release (the newest when it is not given), exactly as the Templates page imports it.
                var path = positional.Length > 2 ? positional[2] : throw new FlowValidationException(TemplateUsage);
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var imported = await DataDefinitions(http, engine.Time)
                    .ImportAsync(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), kind, Program.GetOption(args, "--release"), path, ct)
                    .ConfigureAwait(false);
                return Report(await store.SaveAsync(imported.Schema, imported.Origin($"file {Path.GetFileName(path)}"), actor, ct).ConfigureAwait(false), json);
            }

            case "list":
            {
                var templates = await store.ListAsync(ct).ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(CanonicalJson.Pretty(new JsonArray(templates.Select(t => (JsonNode)Describe(t)).ToArray())));
                    return 0;
                }

                if (templates.Count == 0)
                {
                    Console.WriteLine("no templates saved yet");
                }

                foreach (var t in templates)
                {
                    Console.WriteLine($"{t.Kind}  {t.Version}  saved {t.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} by {t.CapturedBy}  ({t.Origin})");
                }

                return 0;
            }

            case "show":
            {
                var kind = Program.GetOption(args, "--kind") ?? throw new FlowValidationException(TemplateUsage);
                var version = Program.GetOption(args, "--version")
                    ?? (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Kind == kind)?.Version
                    ?? throw new FlowValidationException($"There is no saved template for '{kind}'.");
                var schema = await store.LoadAsync(new TemplateReference(kind, version), ct).ConfigureAwait(false)
                    ?? throw new FlowValidationException($"There is no template {new TemplateReference(kind, version)}.");
                var template = OsduTemplate.From(schema);
                if (json)
                {
                    Console.WriteLine(CanonicalJson.Pretty(new JsonObject
                    {
                        ["kind"] = template.Kind,
                        ["version"] = template.Version,
                        ["variables"] = new JsonArray(template.Variables.Select(v => (JsonNode)Describe(v)).ToArray()),
                    }));
                    return 0;
                }

                Console.WriteLine($"{template.Kind} version {template.Version}: {template.Variables.Count} variable(s)");
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

                    Console.WriteLine($"  {v.Path.Text,-60} {Shape(v),-22} {string.Join("; ", notes)}");
                }

                return 0;
            }

            case "delete":
            {
                var kind = Program.GetOption(args, "--kind") ?? throw new FlowValidationException(TemplateUsage);
                var version = Program.GetOption(args, "--version") ?? throw new FlowValidationException(TemplateUsage);
                var reference = new TemplateReference(kind, version);
                await store.DeleteAsync(reference, ct).ConfigureAwait(false);
                Console.WriteLine($"deleted template {reference}");
                return 0;
            }

            default:
                throw new FlowValidationException(TemplateUsage);
        }
    }

    /// <summary>The OSDU data definitions over <paramref name="http"/>, kept in the default local copy.</summary>
    private static OsduDataDefinitions DataDefinitions(HttpClient http, TimeProvider time)
        => new(
            () => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, OsduDataDefinitions.DefaultCacheDirectory,
            OsduDataDefinitions.DefaultFreshness, OsduDataDefinitions.DefaultDownloadTimeout, time);

    private static int Report(TemplateSaved saved, bool json)
    {
        if (json)
        {
            var node = Describe(saved.Template);
            node["outcome"] = saved.Outcome == TemplateSaveOutcome.Created ? "created" : "unchanged";
            Console.WriteLine(CanonicalJson.Pretty(node));
            return 0;
        }

        Console.WriteLine(saved.Outcome == TemplateSaveOutcome.Created
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

    /// <summary>
    /// An OSDU connection over a flow's endpoint (or an explicit <c>--endpoint</c>), auth and headers, their references
    /// resolved by <see cref="OsduConnection.CreateAsync"/> like every other capture's.
    /// </summary>
    private static Task<OsduConnection> ConnectAsync(
        string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers, FlowReliability reliability, string[] args, EngineContext engine, CancellationToken ct)
        => OsduConnection.CreateAsync(Program.GetOption(args, "--endpoint") ?? endpoint, auth, headers, reliability, engine.Secrets, ct: ct);
}
