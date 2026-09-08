using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The delivery kind's document families in the repository sync: every mapping document (<c>documentType:
/// mapping</c>, anywhere in the tree) and every snapshot the repository's <c>snapshots</c> directories hold
/// (schema snapshots by OSDU kind, reference snapshot versions and which one is current) become catalog rows, so
/// the GUI lists what a flow renders with without opening the repository. Rows are keyed by repository and
/// reference; a document that disappears from the tree loses its row.
/// </summary>
public sealed class DeliveryCatalogSync : ICatalogSyncExtension
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".sqlflow", "bin", "obj", "node_modules", "runs",
    };

    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);

    private readonly DeliveryDocumentLoader _documents;

    public DeliveryCatalogSync(DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
    }

    public async Task<CatalogSyncExtensionResult> SyncAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(warnings);

        var mappings = await SyncMappingsAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        var snapshots = await SyncSnapshotsAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        return mappings.Add(snapshots);
    }

    private async Task<CatalogSyncExtensionResult> SyncMappingsAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var existing = await context.DeliveryMappings.Where(m => m.RepoId == repoId).AsTracking().ToDictionaryAsync(m => m.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0, invalid = 0;

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                warnings.Add($"{Relative(root, file)}: could not be read ({ex.Message}).");
                continue;
            }

            if (!LooksLikeMapping(yaml))
            {
                continue;
            }

            var relative = Relative(root, file);
            MappingDefinition? mapping = null;
            string? message = null;
            try
            {
                mapping = _documents.ParseMapping(yaml, relative);
            }
            catch (FlowValidationException ex)
            {
                message = ex.Message;
            }

            var reference = mapping?.Reference ?? relative;
            var id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{reference}");
            if (!seen.Add(id))
            {
                warnings.Add($"{relative}: mapping '{reference}' is declared more than once in the repository; the first file wins.");
                continue;
            }

            var hash = ContentHash.Of(yaml);
            if (!existing.TryGetValue(id, out var row))
            {
                row = new DeliveryMapping { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                context.DeliveryMappings.Add(row);
                added++;
            }
            else if (row.ContentHash == hash && row.RelativePath == relative)
            {
                row.LastSeenUtc = nowUtc;
                unchanged++;
                if (mapping is null)
                {
                    invalid++;
                }

                continue;
            }
            else
            {
                updated++;
            }

            row.Reference = reference;
            row.Name = mapping?.Name ?? Path.GetFileNameWithoutExtension(file);
            row.Version = mapping?.Version ?? string.Empty;
            row.Kind = mapping?.Kind ?? string.Empty;
            row.RelativePath = relative;
            row.ContentHash = hash;
            row.Yaml = yaml;
            row.SummaryJson = mapping is null ? "{}" : JsonSerializer.Serialize(Summarize(mapping), SummaryJson);
            row.Status = mapping is null ? "invalid" : "valid";
            row.Message = message;
            row.LastSeenUtc = nowUtc;
            if (mapping is null)
            {
                invalid++;
                warnings.Add($"{relative}: {message}");
            }
        }

        var removed = 0;
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                context.DeliveryMappings.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CatalogSyncExtensionResult(added, updated, unchanged, removed, invalid);
    }

    private static async Task<CatalogSyncExtensionResult> SyncSnapshotsAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var existing = await context.DeliverySnapshots.Where(s => s.RepoId == repoId).AsTracking().ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0, invalid = 0;

        foreach (var store in EnumerateSnapshotStores(root))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var found in await ReadStoreAsync(root, store, warnings, ct).ConfigureAwait(false))
            {
                var id = FlowIdentity.FromName($"delivery-snapshot/{repoId:N}/{found.Kind}/{found.Name}");
                if (!seen.Add(id))
                {
                    warnings.Add($"{found.RelativePath}: {found.Kind} snapshot '{found.Name}' appears in more than one snapshot store; the first wins.");
                    continue;
                }

                if (found.Invalid)
                {
                    invalid++;
                }

                if (!existing.TryGetValue(id, out var row))
                {
                    row = new DeliverySnapshot { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                    context.DeliverySnapshots.Add(row);
                    added++;
                }
                else if (row.Version == found.Version && row.Current == found.Current && row.RelativePath == found.RelativePath && row.SummaryJson == found.SummaryJson)
                {
                    row.LastSeenUtc = nowUtc;
                    unchanged++;
                    continue;
                }
                else
                {
                    updated++;
                }

                row.Kind = found.Kind;
                row.Name = found.Name;
                row.Version = found.Version;
                row.CapturedUtc = found.CapturedUtc;
                row.Current = found.Current;
                row.RelativePath = found.RelativePath;
                row.SummaryJson = found.SummaryJson;
                row.LastSeenUtc = nowUtc;
            }
        }

        var removed = 0;
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                context.DeliverySnapshots.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CatalogSyncExtensionResult(added, updated, unchanged, removed, invalid);
    }

    private sealed record FoundSnapshot(string Kind, string Name, string Version, DateTime? CapturedUtc, bool Current, string RelativePath, string SummaryJson, bool Invalid);

    private static async Task<List<FoundSnapshot>> ReadStoreAsync(string root, string store, ICollection<string> warnings, CancellationToken ct)
    {
        var found = new List<FoundSnapshot>();

        var schemas = Path.Combine(store, "schemas");
        if (Directory.Exists(schemas))
        {
            foreach (var meta in Directory.EnumerateFiles(schemas, "*.meta.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Relative(root, meta);
                try
                {
                    var node = JsonNode.Parse(await File.ReadAllTextAsync(meta, ct).ConfigureAwait(false)) as JsonObject
                        ?? throw new DeliveryException("the meta file is not a JSON object");
                    var kind = node["kind"]?.GetValue<string>() ?? throw new DeliveryException("the meta file has no 'kind'");
                    var version = node["version"]?.GetValue<string>() ?? string.Empty;
                    var captured = ParseInstant(node["capturedUtc"]?.GetValue<string>());
                    var schemaFile = meta[..^".meta.json".Length] + ".json";
                    var summary = new JsonObject { ["kind"] = kind, ["version"] = version, ["capturedUtc"] = captured, ["file"] = Relative(root, schemaFile) };
                    if (File.Exists(schemaFile))
                    {
                        var schema = JsonNode.Parse(await File.ReadAllTextAsync(schemaFile, ct).ConfigureAwait(false)) as JsonObject;
                        var data = schema?["properties"]?["data"]?["properties"] as JsonObject;
                        summary["dataProperties"] = data?.Count ?? 0;
                        summary["required"] = new JsonArray((schema?["properties"]?["data"]?["required"] as JsonArray)?.Select(r => (JsonNode?)JsonValue.Create(r?.GetValue<string>())).ToArray() ?? []);
                    }
                    else
                    {
                        summary["missingSchemaFile"] = true;
                    }

                    found.Add(new FoundSnapshot("schema", kind, version, captured, false, relative, summary.ToJsonString(), !File.Exists(schemaFile)));
                }
                catch (Exception ex) when (ex is JsonException or DeliveryException or InvalidOperationException or FormatException)
                {
                    warnings.Add($"{relative}: schema snapshot is unreadable ({ex.Message}).");
                    found.Add(new FoundSnapshot("schema", Path.GetFileName(meta), string.Empty, null, false, relative, new JsonObject { ["error"] = ex.Message }.ToJsonString(), true));
                }
            }
        }

        var references = Path.Combine(store, "references");
        if (Directory.Exists(references))
        {
            var current = File.Exists(Path.Combine(references, "current"))
                ? (await File.ReadAllTextAsync(Path.Combine(references, "current"), ct).ConfigureAwait(false)).Trim()
                : null;
            foreach (var directory in Directory.EnumerateDirectories(references).OrderBy(d => d, StringComparer.Ordinal))
            {
                var manifest = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                var relative = Relative(root, manifest);
                var label = Path.GetFileName(directory);
                try
                {
                    var node = JsonNode.Parse(await File.ReadAllTextAsync(manifest, ct).ConfigureAwait(false)) as JsonObject
                        ?? throw new DeliveryException("the manifest is not a JSON object");
                    var version = node["version"]?.GetValue<string>() ?? label;
                    var captured = ParseInstant(node["capturedUtc"]?.GetValue<string>());
                    var types = new JsonArray();
                    var items = 0;
                    foreach (var name in (node["types"] as JsonArray)?.Select(t => t?.GetValue<string>()).Where(t => !string.IsNullOrWhiteSpace(t)) ?? [])
                    {
                        var typeFile = Path.Combine(directory, name + ".json");
                        var count = 0;
                        string? entityType = null;
                        if (File.Exists(typeFile))
                        {
                            var typeNode = JsonNode.Parse(await File.ReadAllTextAsync(typeFile, ct).ConfigureAwait(false)) as JsonObject;
                            count = (typeNode?["items"] as JsonArray)?.Count ?? 0;
                            entityType = typeNode?["entityType"]?.GetValue<string>();
                        }

                        items += count;
                        types.Add(new JsonObject { ["name"] = name, ["entityType"] = entityType, ["items"] = count });
                    }

                    var summary = new JsonObject
                    {
                        ["version"] = version,
                        ["capturedUtc"] = captured,
                        ["contentHash"] = node["contentHash"]?.GetValue<string>(),
                        ["types"] = types,
                        ["items"] = items,
                    };
                    found.Add(new FoundSnapshot("references", label, version, captured, string.Equals(current, label, StringComparison.Ordinal), relative, summary.ToJsonString(), false));
                }
                catch (Exception ex) when (ex is JsonException or DeliveryException or InvalidOperationException or FormatException)
                {
                    warnings.Add($"{relative}: reference snapshot is unreadable ({ex.Message}).");
                    found.Add(new FoundSnapshot("references", label, label, null, false, relative, new JsonObject { ["error"] = ex.Message }.ToJsonString(), true));
                }
            }
        }

        return found;
    }

    private static object Summarize(MappingDefinition mapping) => new
    {
        mapping.Name,
        mapping.Version,
        mapping.Kind,
        mapping.EntityType,
        mapping.Description,
        System = mapping.Source.System,
        Scopes = mapping.Source.Scopes,
        NaturalKey = mapping.Identity.NaturalKey,
        mapping.Identity.Label,
        Parameters = mapping.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        Properties = mapping.Properties.Count,
        Definitions = mapping.Definitions.Count,
        Fixtures = mapping.Fixtures.Count,
        LegalTags = mapping.Envelope.LegalTags,
        Countries = mapping.Envelope.OtherRelevantDataCountries,
        Owners = mapping.Envelope.Acl.Owners,
        Viewers = mapping.Envelope.Acl.Viewers,
    };

    /// <summary>A cheap textual pre-check, so only candidate documents are parsed: every mapping declares its type.</summary>
    private static bool LooksLikeMapping(string yaml)
        => yaml.Contains("documentType:", StringComparison.Ordinal) && yaml.Contains("mapping", StringComparison.Ordinal);

    private static IEnumerable<string> EnumerateYaml(string root)
        => Walk(root).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateSnapshotStores(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children.OrderBy(c => c, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (SkippedDirectories.Contains(name))
                {
                    continue;
                }

                if (string.Equals(name, DeliveryLayout.SnapshotsDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(e => e, StringComparer.Ordinal))
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static DateTime? ParseInstant(string? text)
        => text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
}
