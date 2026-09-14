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
/// The delivery kind's own verbs: <c>check</c> (everything checkable for one flow: the mapping against its pinned template,
/// the version of the partition cache it reads, and the drop's manifest when the drop is present), <c>cache</c> (list the
/// versions of a partition's cache, or import a cache flow's type files into it for offline work), and <c>template</c>
/// (capture, import, list, show and delete the templates in the catalog, docs/delivery/mapping-templates.md). A cache is
/// captured from OSDU by running a cache flow (<c>sqlflow run &lt;cache.yaml&gt;</c>), the same run the platform schedules.
/// </summary>
internal static class DeliveryVerbs
{
    private const string TemplateUsage =
        "Usage: sqlflow template (capture --kind <kind> [--release <tag>] | import <schema.json> --kind <kind> [--release <tag>] | import --from-dir <dir> --kind <kind> | list | show --kind <kind> [--version <version>] | delete --kind <kind> --version <version>) [--db <conn-ref>] [--json]";

    private const string CacheUsage =
        "Usage: sqlflow cache (list <partition | cache.yaml> | import <cache.yaml> --from-dir <dir>) [--db <conn-ref>] [--json]";

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
            ["cache"] = runtime.Mapping.Context.CacheScope is { } cacheScope
                ? new JsonObject
                {
                    ["partition"] = cacheScope,
                    ["version"] = runtime.Mapping.References.Version,
                    ["types"] = runtime.Mapping.References.Types.Count,
                }
                : null,
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
        Console.WriteLine(runtime.Mapping.Context.CacheScope is { } scope
            ? $"    cache       partition {scope} version {runtime.Mapping.References.Version} ({runtime.Mapping.References.Types.Count} type(s))"
            : "    cache       none (the mapping reads nothing from a cache)");
        Console.WriteLine($"    context     {runtime.Mapping.Context.Hash()[..16]}");
        Console.WriteLine($"    mappings    {runtime.Layout.MappingsDirectory}");
        Console.WriteLine(dropChecked
            ? $"    drop        {runtime.DropLocation} (manifest and source bindings checked)"
            : $"    drop        {runtime.DropLocation} (not present; documents validated without it)");
        foreach (var warning in warnings)
        {
            Console.WriteLine($"WARN  {warning}");
        }

        return 0;
    }

    public static async Task<int> CacheAsync(IServiceProvider provider, string[] positional, string[] args, bool json, CancellationToken ct)
    {
        var engine = provider.GetRequiredService<EngineContext>();
        var verb = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        var target = positional.Length > 2 ? positional[2] : throw new FlowValidationException(CacheUsage);
        var store = engine.Cache
            ?? throw new FlowValidationException("Caches live in the catalog. Run 'sqlflow cache' with --db <conn-ref>, or set the catalog variable.");

        switch (verb)
        {
            case "list":
            {
                var scope = File.Exists(target) ? engine.Documents.LoadCache(target).Scope : CacheScope.Normalize(target, "sqlflow cache list");
                var versions = await store.ListVersionsAsync(scope, ct).ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(CanonicalJson.Pretty(new JsonArray(versions.Select(v => (JsonNode)Describe(v)).ToArray())));
                    return 0;
                }

                if (versions.Count == 0)
                {
                    Console.WriteLine($"the cache of partition {scope} holds no version yet; run a cache flow of the partition with the refresh operation to capture one");
                }

                foreach (var v in versions)
                {
                    var run = v.RunId is { } runId ? $" in run {runId:D}" : string.Empty;
                    Console.WriteLine(
                        $"{v.Version}  {(v.Current ? "current" : "       ")}  {v.Items} record(s) in {v.Types.Count} type(s), written by cache flow {v.FlowName} at {v.CapturedUtc.ToString("u", CultureInfo.InvariantCulture)} for {v.CapturedBy}{run}");
                }

                return 0;
            }

            case "import":
            {
                var cache = engine.Documents.LoadCache(target);
                var directory = Path.GetFullPath(Program.GetOption(args, "--from-dir") ?? throw new FlowValidationException(CacheUsage));
                var builder = new SnapshotBuilder(store, cache.Scope, cache.Name, engine.Time, engine.Loggers.CreateLogger<SnapshotBuilder>());
                var write = await builder.ImportDirectoryAsync(
                    directory, cache.Types, new CacheCapture(null, "cli:" + Environment.UserName, $"files under {directory}"), ct).ConfigureAwait(false);
                var records = write.Snapshot.Types.Sum(t => t.Items.Count);
                if (json)
                {
                    Console.WriteLine(CanonicalJson.Pretty(new JsonObject
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

                Console.WriteLine(write.Written
                    ? $"cache of partition {cache.Scope}: version {write.Snapshot.Version} written from cache flow {cache.Name}, holding {write.Snapshot.Types.Count} type(s) and {records} record(s), now current"
                    : $"cache of partition {cache.Scope}: the files add nothing version {write.Snapshot.Version} does not already hold, so nothing was written");
                return 0;
            }

            default:
                throw new FlowValidationException(CacheUsage);
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
    };

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
}
