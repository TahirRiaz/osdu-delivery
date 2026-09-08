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
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Cli;

/// <summary>
/// The delivery kind's own verbs: <c>check</c> (everything checkable offline for one flow: the mapping against
/// the schema snapshot, the reference snapshot, and the drop's manifest when the drop is present) and
/// <c>snapshot</c> (capture or list the schema and reference snapshots a flow renders with, into the snapshot
/// store its repository layout locates). Both work without a catalog; a flow whose snapshots are captured here is
/// ready for <c>sqlflow run</c>.
/// </summary>
internal static class DeliveryVerbs
{
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
            ["kind"] = runtime.Mapping.Mapping.Kind,
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
        Console.WriteLine($"    mapping     {runtime.Mapping.Mapping.Reference} -> {runtime.Mapping.Mapping.Kind}");
        Console.WriteLine($"    schema      {runtime.Mapping.Schema.Version} (captured {runtime.Mapping.Schema.CapturedUtc:u})");
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
            case "schema":
            {
                var kind = Program.GetOption(args, "--kind")
                    ?? throw new FlowValidationException("Usage: sqlflow snapshot <flow.yaml> schema --kind <authority:source:entityType:version> [--from-dir <dir> | --endpoint <url>]");
                SchemaSnapshot snapshot;
                if (Program.GetOption(args, "--from-dir") is { } directory)
                {
                    snapshot = await builder.SchemaFromDirectoryAsync(directory, kind, ct).ConfigureAwait(false);
                }
                else
                {
                    using var osdu = Connect(flow, args, engine);
                    snapshot = await builder.SchemaFromOsduAsync(osdu, kind, ct).ConfigureAwait(false);
                }

                Console.WriteLine($"schema {snapshot.Kind} version {snapshot.Version} -> {layout.SnapshotsRoot}");
                return 0;
            }

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
                    using var osdu = Connect(flow, args, engine);
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
                var schema = await store.LoadSchemaAsync(mapping.Kind, ct).ConfigureAwait(false);
                Console.WriteLine(schema is null
                    ? $"  schema {mapping.Kind}: not captured (run 'sqlflow snapshot {Path.GetFileName(flowPath)} schema --kind {mapping.Kind}')"
                    : $"  schema {schema.Kind} version {schema.Version} (captured {schema.CapturedUtc:u})");

                return 0;
            }

            default:
                throw new FlowValidationException("Usage: sqlflow snapshot <flow.yaml> (schema | references | list) ...");
        }
    }

    /// <summary>The flow's target as a capture connection: its endpoint (or an explicit <c>--endpoint</c>), auth and headers.</summary>
    private static OsduConnection Connect(FlowDefinition flow, string[] args, EngineContext engine)
    {
        var endpoint = Program.GetOption(args, "--endpoint") ?? flow.Target.Endpoint;
        var headers = flow.Target.Headers.ToDictionary(kv => kv.Key, kv => engine.Secrets.Resolve(kv.Value), StringComparer.OrdinalIgnoreCase);
        return new OsduConnection(engine.Secrets.Resolve(endpoint), flow.Target.Auth, headers, flow.Reliability, engine.Secrets);
    }
}
