using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The delivery kind's document families in the repository sync: every mapping document (<c>documentType: mapping</c>,
/// anywhere in the tree) and what every cache flow (<c>flowType: cache</c>) declares it caches become rows of the
/// <c>osdu</c> schema, so the GUI lists what a flow renders with, and what each cache holds, without opening the
/// repository. Rows are keyed by repository and reference; a document that disappears from the tree loses its row. The
/// sync only reads the repository: the versions of a cache are written by the runs of its cache flow, never by the sync.
/// The rows commit with the sync: the <c>osdu</c> context opens on the catalog context's connection and joins its transaction.
/// </summary>
public sealed class DeliveryCatalogSync : ICatalogSyncExtension
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".sqlflow", "bin", "obj", "node_modules", "runs",
    };

    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);

    /// <summary>The widths of <c>osdu.Template</c>'s kind and version, which a mapping row's pin mirrors.</summary>
    private const int MaxTemplateKindLength = 200;

    private const int MaxTemplateVersionLength = 64;

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
        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                $"The repository sync of repository {repoId:D} called the delivery extension outside its transaction, so the mapping and cache rows could not commit with the sync.");

        await using var osdu = new OsduDbContext(OsduDbContext.SqlServerOptions(context.Database.GetDbConnection()));
        await osdu.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct).ConfigureAwait(false);
        return await ReconcileAsync(osdu, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reconciles the repository's mapping documents and cache declarations into <paramref name="context"/>: the one write the
    /// sync extension makes, on whatever connection and transaction the context was opened with.
    /// </summary>
    public async Task<CatalogSyncExtensionResult> ReconcileAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(warnings);

        var mappings = await SyncMappingsAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        var caches = await SyncCacheDefinitionsAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        return mappings.Add(caches);
    }

    private async Task<CatalogSyncExtensionResult> SyncMappingsAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
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

            // The template the document pins. A document that fails to load still names one, and still pins it: deleting
            // that template would leave the fix with nothing to render against.
            var pinned = mapping?.Template ?? NamedTemplate(yaml);
            var hash = ContentHash.Of(yaml);
            if (!existing.TryGetValue(id, out var row))
            {
                row = new DeliveryMapping { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                context.DeliveryMappings.Add(row);
                added++;
            }
            else if (row.ContentHash == hash && row.RelativePath == relative)
            {
                // The pin is derived again for an unchanged document too, so what counts as a pin follows this build
                // rather than the build that first synced the row.
                row.Kind = mapping?.Kind ?? pinned?.Kind ?? string.Empty;
                row.TemplateVersion = pinned?.Version ?? string.Empty;
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
            row.Kind = mapping?.Kind ?? pinned?.Kind ?? string.Empty;
            row.TemplateVersion = pinned?.Version ?? string.Empty;
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

    /// <summary>
    /// The types the repository's cache flows declare (<c>flowType: cache</c>): which records each flow captures into the cache
    /// of its partition, and which paths of them it keeps. A refresh reads every declaration of its partition, so it fetches
    /// every path the partition keeps for a type, and the GUI shows which files fill a partition's cache. A declaration that
    /// disagrees with another flow's declaration of the same type for the partition, another entity type or a name cached
    /// from another path, is left out with a warning, because merged, one name would hold two meanings.
    /// </summary>
    private async Task<CatalogSyncExtensionResult> SyncCacheDefinitionsAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var existing = await context.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).AsTracking().ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var flows = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<(CacheDefinition Cache, string Relative)>();
        int added = 0, updated = 0, unchanged = 0, invalid = 0;

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The mapping pass reports an unreadable file; one warning per file is enough.
                continue;
            }

            if (!DeclaresFlowType(yaml, CacheDefinition.FlowTypeName))
            {
                continue;
            }

            var relative = Relative(root, file);
            CacheDefinition cache;
            try
            {
                cache = _documents.ParseCache(yaml, relative);
            }
            catch (FlowValidationException ex)
            {
                invalid++;
                warnings.Add($"{relative}: {ex.Message}");
                continue;
            }

            if (!flows.TryAdd(cache.Name, relative))
            {
                warnings.Add($"{relative}: cache flow '{cache.Name}' is already declared by {flows[cache.Name]}; the first file wins.");
                continue;
            }

            parsed.Add((cache, relative));
        }

        // What the other repositories declare for the same partitions: this repository's declarations have to agree with them.
        var scopes = parsed.Select(p => p.Cache.Scope).Distinct(StringComparer.Ordinal).ToList();
        var names = flows.Keys.ToList();
        var elsewhere = await context.DeliveryCacheDefinitions.AsNoTracking()
            .Where(c => c.RepoId != repoId && scopes.Contains(c.Scope) && !names.Contains(c.FlowName))
            .Select(c => new { c.Scope, c.FlowName, c.Name, c.EntityType, c.Kind, c.Query, c.FieldsJson, c.Endpoint })
            .ToListAsync(ct).ConfigureAwait(false);
        var declared = scopes.ToDictionary(
            scope => scope,
            scope => elsewhere
                .Where(c => c.Scope == scope)
                .Select(c => new Snapshots.CacheTypeDeclaration(
                    c.FlowName, c.Name, c.EntityType, c.Kind, c.Query ?? "*", OsduCacheStore.ParseFields(c.FieldsJson, c.FlowName, c.Name), Snapshots.CacheChangeMode.Auto))
                .ToList(),
            StringComparer.Ordinal);

        foreach (var (cache, relative) in parsed)
        {
            var scope = cache.Scope;
            var endpoint = Clip(cache.Source.Endpoint, 1000);
            foreach (var type in cache.Types)
            {
                var problems = new Snapshots.CacheDeclaration(scope, declared[scope]).Conflicts(cache.Name, type);
                if (problems.Count > 0)
                {
                    invalid++;
                    warnings.Add(
                        $"{relative}: {type.Name} is left out of the cache of partition '{scope}', because {string.Join("; ", problems)}. Make the declarations agree, or give the type another name.");
                    continue;
                }

                declared[scope].Add(new Snapshots.CacheTypeDeclaration(cache.Name, type.Name, type.EntityType, type.Kind, type.Query, type.Fields, type.OnChange));
                var id = FlowIdentity.FromName($"delivery-cache/{repoId:N}/{cache.Name}/{type.Name}");
                seen.Add(id);
                var fields = JsonSerializer.Serialize(type.Fields.Select(f => new { f.Path, As = f.Name }).ToList(), SummaryJson);
                var onChange = type.OnChange == Snapshots.CacheChangeMode.Auto ? "auto" : "approve";
                if (!existing.TryGetValue(id, out var row))
                {
                    row = new DeliveryCacheDefinition { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                    context.DeliveryCacheDefinitions.Add(row);
                    added++;
                }
                else if (row.Kind == type.Kind && row.Query == type.Query && row.FieldsJson == fields && row.EntityType == type.EntityType
                         && row.RelativePath == relative && row.OnChange == onChange && row.FlowName == cache.Name && row.Scope == scope
                         && row.Endpoint == endpoint)
                {
                    row.LastSeenUtc = nowUtc;
                    unchanged++;
                    continue;
                }
                else
                {
                    updated++;
                }

                row.FlowName = cache.Name;
                row.Scope = scope;
                row.Endpoint = endpoint;
                row.RelativePath = relative;
                row.Name = type.Name;
                row.EntityType = type.EntityType;
                row.Kind = type.Kind;
                row.Query = type.Query;
                row.FieldsJson = fields;
                row.OnChange = onChange;
                row.LastSeenUtc = nowUtc;
            }
        }

        var removed = 0;
        var released = new List<(string Scope, string Flow, string Type)>();
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                released.Add((row.Scope, row.FlowName, row.Name));
                context.DeliveryCacheDefinitions.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        // A flow that no longer declares a type for a partition holds none of its records there: its membership goes, so the
        // next capture of the type by another flow lets go of what only this flow kept.
        foreach (var (scope, flow, type) in released)
        {
            if (await context.DeliveryCacheDefinitions.AnyAsync(c => c.Scope == scope && c.FlowName == flow && c.Name == type, ct).ConfigureAwait(false))
            {
                continue;
            }

            await context.DeliveryCacheMembers
                .Where(m => m.Scope == scope && m.FlowName == flow && m.TypeName == type)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // A partition has one cache holding what every one of its cache flows captures, so those flows should search one platform.
        foreach (var scope in scopes)
        {
            var endpoints = parsed.Where(p => p.Cache.Scope == scope).Select(p => Clip(p.Cache.Source.Endpoint, 1000))
                .Concat(elsewhere.Where(c => c.Scope == scope).Select(c => c.Endpoint))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (endpoints.Count > 1)
            {
                warnings.Add(
                    $"The cache flows of partition '{scope}' search different endpoints ({string.Join(", ", endpoints)}); the partition's one cache holds what all of them capture, so they should name the same OSDU platform.");
            }
        }

        // A cache flow is named globally, like every flow: the same flow declared by another repository captures into the same
        // partition's cache under the same name, which is never what either repository means.
        if (flows.Count > 0)
        {
            var shared = await context.DeliveryCacheDefinitions.AsNoTracking()
                .Where(c => c.RepoId != repoId && names.Contains(c.FlowName))
                .Select(c => c.FlowName)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var name in shared.Order(StringComparer.Ordinal))
            {
                warnings.Add($"{flows[name]}: cache flow '{name}' is also declared by another repository, and both would capture into the cache under the same name. Rename one of them.");
            }
        }

        return new CatalogSyncExtensionResult(added, updated, unchanged, removed, invalid);
    }

    private static object Summarize(MappingDefinition mapping) => new
    {
        mapping.Name,
        mapping.Version,
        mapping.Kind,
        TemplateVersion = mapping.Template.Version,
        mapping.EntityType,
        mapping.Description,
        System = mapping.Dataset.System,
        Key = mapping.Dataset.Key,
        mapping.Dataset.Label,
        ChildDatasets = mapping.ChildDatasets,
        Parameters = mapping.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        Entries = mapping.Entries.Count,
        Fixtures = mapping.Fixtures.Count,
        LegalTags = mapping.Envelope.LegalTags,
        Countries = mapping.Envelope.OtherRelevantDataCountries,
        Owners = mapping.Envelope.Owners,
        Viewers = mapping.Envelope.Viewers,
    };

    /// <summary>
    /// The template an invalid mapping document names. A kind or version longer than the catalog holds for a template
    /// can pin no saved template, so it counts as naming none rather than failing the row.
    /// </summary>
    private TemplateReference? NamedTemplate(string yaml)
        => _documents.TemplateNamedBy(yaml) is { } named && named.Kind.Length <= MaxTemplateKindLength && named.Version.Length <= MaxTemplateVersionLength
            ? named
            : null;

    /// <summary>A cheap textual pre-check, so only candidate documents are parsed: every mapping declares its type.</summary>
    private static bool LooksLikeMapping(string yaml)
        => yaml.Contains("documentType:", StringComparison.Ordinal) && yaml.Contains("mapping", StringComparison.Ordinal);

    /// <summary>A cheap textual pre-check: a top-level <c>flowType:</c> line whose value is <paramref name="flowType"/>, quoted or not.</summary>
    private static bool DeclaresFlowType(string yaml, string flowType)
    {
        foreach (var line in yaml.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("flowType:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["flowType:".Length..].Trim().Trim('"').Trim('\'');
            var comment = value.IndexOf('#');
            if (comment >= 0)
            {
                value = value[..comment].TrimEnd().Trim('"').Trim('\'');
            }

            return value.Equals(flowType, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateYaml(string root)
        => Walk(root).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));

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

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
