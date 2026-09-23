using SqlFlow.Core.Secrets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
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
/// anywhere in the tree), what every cache flow (<c>flowType: cache</c>) declares it caches, and the interfaces of every
/// delivery flow (<c>flowType: delivery</c>) become rows of the <c>osdu</c> schema, so the GUI lists what a flow renders
/// with, what each cache holds and which pipeline keeps a ledger, without opening the repository. Rows are keyed by repository and reference; a document that disappears from the tree loses its row. The
/// sync only reads the repository: the versions of a cache are written by the runs of its cache flow, never by the sync.
/// <para>
/// Where those rows are written depends on where the module database is. In the default estate it is the catalog's own
/// database, and the <c>osdu</c> context opens on the catalog context's connection and joins its transaction, so the
/// rows commit or roll back with the sync. Given a database of its own, which on Azure SQL means no cross-database
/// statement and possibly another server, the context opens on the module's own connection and commits its own
/// transaction. The reconciliation is the same either way, and is written to be repeatable from what the repository
/// holds, so rows that commit while the sync then fails are settled by the next sync rather than left wrong.
/// </para>
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
    private readonly ISecretResolver? _secrets;
    private readonly IDbContextFactory<OsduDbContext>? _module;

    /// <summary>
    /// The sync over <paramref name="documents"/>, writing the module's rows wherever <paramref name="module"/> opens
    /// them. A host that registered no module database of its own passes none, and the rows are then the catalog
    /// database's own, which is where the <c>osdu</c> schema sits unless a deployment gives the module a database.
    /// </summary>
    public DeliveryCatalogSync(
        DeliveryDocumentLoader documents,
        IDbContextFactory<OsduDbContext>? module = null,
        ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        _module = module;
        _secrets = secrets;
    }

    /// <summary>
    /// The partition <paramref name="cache"/> fills, resolved where this host can resolve it, and as the document writes
    /// it where it cannot. Resolution never fails a sync: a repository is described by what its documents say, and a value
    /// this host has no way to know is not a reason to refuse the document.
    /// </summary>
    private async Task<string> ResolvedScopeAsync(CacheDefinition cache, CancellationToken ct)
    {
        if (_secrets is null)
        {
            return cache.Scope;
        }

        try
        {
            return Snapshots.CacheScope.Normalize(
                await _secrets.ResolveAsync(cache.Scope, ct).ConfigureAwait(false),
                cache.SourcePath ?? cache.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return cache.Scope;
        }
    }

    public async Task<CatalogSyncExtensionResult> SyncAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(warnings);
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"The repository sync of repository {repoId:D} called the delivery extension outside its transaction, so the rows it writes beside the catalog's could not commit with them.");
        }

        await using var work = await OpenAsync(context, write: true, ct).ConfigureAwait(false);
        var result = await ReconcileAsync(work.Context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        await work.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Whether a mapping document changed since the last sync reconciled the mappings: a delivery flow's OSDU type and cache
    /// reads come from its mapping, so a changed, added or removed mapping changes the lineage while every flow document
    /// stays the same. Compared by path and content hash against the rows the last sync wrote, through the same discovery
    /// the reconciliation uses. A document that is a second declaration of a reference already on record (which the
    /// reconciliation leaves out) is not a change. Cache flows need no check here: they are flows, which the sync compares
    /// itself.
    /// </summary>
    public async Task<bool> LineageInputsChangedAsync(CatalogDbContext context, Guid repoId, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        await using var work = await OpenAsync(context, write: false, ct).ConfigureAwait(false);
        return await MappingsChangedAsync(work.Context, repoId, root, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The module context this sync works through, and the transaction it is responsible for. Where the module's rows
    /// are decides: rows reachable on the catalog's connection are written there and join the sync's transaction, and
    /// rows in a database of the module's own are written on that database's connection, under a transaction of this
    /// sync's own when it writes (<see cref="ModuleDatabase.IsReachableOn"/>).
    /// </summary>
    /// <param name="context">The catalog's context, as the repository sync hands it to the extension.</param>
    /// <param name="write">Whether the caller writes, which is what an isolated database needs its own transaction for.</param>
    /// <param name="ct">Cancels the open.</param>
    private async Task<ModuleWork> OpenAsync(CatalogDbContext context, bool write, CancellationToken ct)
    {
        var host = context.Database.GetDbConnection();
        if (_module is not null)
        {
            var isolated = await _module.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (!ModuleDatabase.IsReachableOn(host, isolated.Database.GetConnectionString()))
            {
                // Its own database, which on Azure SQL is also its own server: no statement here may reach across, so
                // the rows are written and committed on the module's connection. The reconciliation is repeatable from
                // what the repository holds, so a sync that fails after this commit is settled by the next one.
                try
                {
                    var own = write ? await isolated.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
                    return new ModuleWork(isolated, own);
                }
                catch
                {
                    await isolated.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            await isolated.DisposeAsync().ConfigureAwait(false);
        }

        var osdu = new OsduDbContext(OsduDbContext.SqlServerOptions(host));
        if (context.Database.CurrentTransaction is { } transaction)
        {
            await osdu.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct).ConfigureAwait(false);
        }

        return new ModuleWork(osdu, own: null);
    }

    /// <summary>
    /// The module context a sync reconciles through, with the transaction it owns: none when the rows ride the
    /// catalog's transaction, its own when they cannot. Disposing without committing rolls its own transaction back.
    /// </summary>
    private sealed class ModuleWork(OsduDbContext context, IDbContextTransaction? own) : IAsyncDisposable
    {
        public OsduDbContext Context { get; } = context;

        public Task CommitAsync(CancellationToken ct) => own is null ? Task.CompletedTask : own.CommitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }

            await Context.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the repository's mapping documents on disk differ from the mapping rows <paramref name="context"/> holds for
    /// it, as <see cref="LineageInputsChangedAsync"/> describes.
    /// </summary>
    public async Task<bool> MappingsChangedAsync(OsduDbContext context, Guid repoId, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var stored = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId)
            .Select(m => new { m.RelativePath, m.ContentHash, m.Reference })
            .ToListAsync(ct).ConfigureAwait(false);
        var hashByPath = stored.ToDictionary(m => m.RelativePath, m => m.ContentHash, StringComparer.Ordinal);
        var references = stored.Select(m => m.Reference).ToHashSet(StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What cannot be read now may have been read before: the lineage is recomputed, and the reconciliation reports the file.
                return true;
            }

            if (!LooksLikeMapping(yaml))
            {
                continue;
            }

            var relative = Relative(root, file);
            if (hashByPath.TryGetValue(relative, out var hash) && hash == ContentHash.Of(yaml))
            {
                matched.Add(relative);
                continue;
            }

            // A file the rows do not hold as it is: a change, unless it declares a reference another file already holds.
            string reference;
            try
            {
                reference = _documents.ParseMapping(yaml, relative).Reference;
            }
            catch (FlowValidationException)
            {
                return true;
            }

            if (!references.Contains(reference) || hashByPath.ContainsKey(relative))
            {
                return true;
            }
        }

        return matched.Count != hashByPath.Count;
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
        var interfaces = await SyncInterfacesAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        return mappings.Add(caches).Add(interfaces);
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
        // Each document's partition is resolved once, and everything below is keyed by what it resolved to: the scopes
        // read from other repositories, the conflict check, and the rows written. Resolving in one place is what keeps
        // those three agreeing with each other and with the capture and the render.
        var scopeOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cache, _) in parsed)
        {
            scopeOf[cache.Name] = await ResolvedScopeAsync(cache, ct).ConfigureAwait(false);
        }

        var scopes = scopeOf.Values.Distinct(StringComparer.Ordinal).ToList();
        var names = flows.Keys.ToList();
        var elsewhere = await context.DeliveryCacheDefinitions.AsNoTracking()
            .Where(c => c.RepoId != repoId && scopes.Contains(c.Scope) && !names.Contains(c.FlowName))
            .Select(c => new { c.Scope, c.FlowName, c.Name, c.EntityType, c.Kind, c.Query, c.FieldsJson, c.Endpoint, c.Origin })
            .ToListAsync(ct).ConfigureAwait(false);
        var declared = scopes.ToDictionary(
            scope => scope,
            scope => elsewhere
                .Where(c => c.Scope == scope)
                .Select(c => new Snapshots.CacheTypeDeclaration(
                    c.FlowName, c.Name, c.EntityType, c.Kind, c.Query ?? "*", OsduCacheStore.ParseFields(c.FieldsJson, c.FlowName, c.Name), Snapshots.CacheChangeMode.Auto,
                    Snapshots.CacheOrigins.Parse(c.Origin)))
                .ToList(),
            StringComparer.Ordinal);

        foreach (var (cache, relative) in parsed)
        {
            // A cache is keyed by the partition its flow reaches, so the declaration is recorded under the resolved
            // partition, which is what a capture and a render both look under. A control plane that cannot resolve the
            // reference records it as written: the declaration is then found by a node that resolves it the same way, and
            // one that does not fails naming the partition it could not find rather than writing under two keys.
            var scope = scopeOf[cache.Name];
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

                declared[scope].Add(new Snapshots.CacheTypeDeclaration(cache.Name, type.Name, type.EntityType, type.Kind, type.Query, type.Fields, type.OnChange, type.Origin));
                var id = FlowIdentity.FromName($"delivery-cache/{repoId:N}/{cache.Name}/{type.Name}");
                seen.Add(id);
                var declaration = Declaration(cache, type, relative);
                if (!existing.TryGetValue(id, out var row))
                {
                    row = new DeliveryCacheDefinition { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                    context.DeliveryCacheDefinitions.Add(row);
                    added++;
                }
                else if (declaration.Matches(row) && row.FlowName == cache.Name && row.Scope == scope)
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
                declaration.WriteTo(row);
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

        // A partition has one cache holding what every one of its cache flows captures, so the flows searching it should search
        // one platform. A table or a dictionary is not searched, and says nothing about the platform.
        foreach (var scope in scopes)
        {
            var endpoints = parsed
                .Where(p => scopeOf[p.Cache.Name] == scope && p.Cache.Types.Any(t => t.Origin == Snapshots.CacheOrigin.Osdu) && p.Cache.Source.Endpoint is not null)
                .Select(p => Clip(p.Cache.Source.Endpoint!, 1000))
                .Concat(elsewhere.Where(c => c.Scope == scope && c.Endpoint is not null).Select(c => c.Endpoint!))
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

    /// <summary>
    /// The interfaces of the repository's delivery flows (docs/interfaces-design.md section 4), each with the ledger identity
    /// it keeps and the kind its mapping fills, as the mapping rows this sync just wrote name it. A flow document that does
    /// not parse describes no interface; the platform's own sync reports it.
    /// </summary>
    private async Task<CatalogSyncExtensionResult> SyncInterfacesAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var sources = new List<RepositorySource>();
        var invalid = 0;
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

            if (!DeclaresFlowType(yaml, FlowDefinition.FlowTypeName))
            {
                continue;
            }

            var relative = Relative(root, file);
            try
            {
                sources.Add(new RepositorySource(relative, _documents.ParseSource(yaml, relative)));
            }
            catch (FlowValidationException)
            {
                invalid++;
            }
        }

        var kinds = await MappingKindsAsync(context, repoId, ct).ConfigureAwait(false);
        var counts = await DeliveryInterfaceCatalog.ReconcileAsync(context, repoId, sources, kinds, nowUtc, warnings, ct).ConfigureAwait(false);
        return new CatalogSyncExtensionResult(counts.Added, counts.Updated, counts.Unchanged, counts.Removed, invalid);
    }

    /// <summary>The OSDU kind of every valid mapping the repository's rows hold, by reference.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> MappingKindsAsync(OsduDbContext context, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.Status == "valid")
            .Select(m => new { m.Reference, m.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(m => m.Reference, m => m.Kind, StringComparer.Ordinal);
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

    private static string? ClipOrNull(string? text, int length) => text is null ? null : Clip(text, length);

    /// <summary>
    /// What the catalog records of one declared type, computed once so the check for an unchanged row and the write agree:
    /// the origin, and only the settings of that origin, so a type that changes origin leaves nothing of the old one behind.
    /// </summary>
    private static CacheDefinitionRow Declaration(CacheDefinition cache, Snapshots.ReferenceTypeSpec type, string relative) => new(
        Snapshots.CacheOrigins.Text(type.Origin),
        type.Origin == Snapshots.CacheOrigin.Osdu ? ClipOrNull(cache.Source.Endpoint, 1000) : null,
        type.Origin == Snapshots.CacheOrigin.Table ? ClipOrNull(cache.Source.Connection, 1000) : null,
        type.Origin == Snapshots.CacheOrigin.Table ? type.Table : null,
        type.IsLookup ? type.Key : null,
        type.Origin == Snapshots.CacheOrigin.Dictionary ? ClipOrNull(type.DictionaryPath, 1000) : null,
        relative,
        type.Name,
        type.EntityType,
        type.Origin == Snapshots.CacheOrigin.Osdu ? type.Kind : null,
        type.Origin == Snapshots.CacheOrigin.Osdu ? type.Query : null,
        JsonSerializer.Serialize(type.Fields.Select(f => new { f.Path, As = f.Name }).ToList(), SummaryJson),
        type.OnChange == Snapshots.CacheChangeMode.Auto ? "auto" : "approve");

    private sealed record CacheDefinitionRow(
        string Origin, string? Endpoint, string? Connection, string? SourceObject, string? KeyField, string? DictionaryPath, string RelativePath,
        string Name, string EntityType, string? Kind, string? Query, string FieldsJson, string OnChange)
    {
        public bool Matches(DeliveryCacheDefinition row)
            => row.Origin == Origin && row.Endpoint == Endpoint && row.Connection == Connection && row.SourceObject == SourceObject
               && row.KeyField == KeyField && row.DictionaryPath == DictionaryPath && row.RelativePath == RelativePath && row.Name == Name
               && row.EntityType == EntityType && row.Kind == Kind && row.Query == Query && row.FieldsJson == FieldsJson && row.OnChange == OnChange;

        public void WriteTo(DeliveryCacheDefinition row)
        {
            row.Origin = Origin;
            row.Endpoint = Endpoint;
            row.Connection = Connection;
            row.SourceObject = SourceObject;
            row.KeyField = KeyField;
            row.DictionaryPath = DictionaryPath;
            row.RelativePath = RelativePath;
            row.Name = Name;
            row.EntityType = EntityType;
            row.Kind = Kind;
            row.Query = Query;
            row.FieldsJson = FieldsJson;
            row.OnChange = OnChange;
        }
    }
}
