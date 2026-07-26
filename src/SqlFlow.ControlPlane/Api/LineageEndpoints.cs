using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Files;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A file-ingestion pipeline whose source spec matches a given file: the flow that would ingest it. The
/// match is by the source's file-name glob (as the engine applies it) and, when a full path is given, the source
/// location or path mask. <see cref="PathConfirmed"/> is true when the path (not just the name) was matched, so a
/// caller can rank a definitively-located match above a name-only one.</summary>
public sealed record FilePipelineMatchDto(
    Guid PipelineId, string PipelineName, Guid RepoId, string RepoName,
    string SourceType, string? SourceLocation, string Pattern, bool PathConfirmed);

/// <summary>A lineage object as it appears in lists: the canonical identity and metadata, without the heavy module
/// body (<c>Definition</c>). Keyed by <see cref="Key"/>, the global identity that joins the same physical object
/// across every repo. <see cref="Level"/> is the object's depth in the estate-wide data-movement graph (0 for a
/// source nothing produces); null when the object takes part in no data movement.</summary>
public sealed record ObjectDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind, int? Level,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A single lineage object with its full module body (<c>Definition</c>) and generating script
/// (<c>Script</c>) for the detail view; the definition is null for plain tables, an unconnected sync, or an
/// encrypted module, and the script is null when no tier saw the object created. <c>KeyColumns</c> is the
/// object's interpreted primary/business key (comma-joined, in key order) with <c>KeyOrigin</c> saying how it
/// was interpreted (Constraint / Declared / Merge); both null when nothing in the codebase names a key.</summary>
public sealed record ObjectDetailDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind, int? Level,
    string? Definition, string? Script, string? ScriptTier, DateTime? ScriptUpdatedUtc,
    string? KeyColumns, string? KeyOrigin,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>One interpreted data-model relationship as seen FROM a dossier's object: the other side's identity
/// (key plus proper-cased location), this object's join columns (<c>OwnColumns</c>) positionally paired with
/// the other side's (<c>OtherColumns</c>), and the interpretation strength: <c>Origin</c> is Constraint (an
/// explicit FOREIGN KEY clause in the codebase) or Join (inferred from the equality predicates the code joins
/// on), <c>Occurrences</c> counts the distinct scripts that exhibited it (the canonical join path scores
/// highest), and <c>Tier</c> is the provenance of the strongest observation.</summary>
public sealed record ObjectRelationshipDto(
    string? Name, string Origin, string Tier, int Occurrences,
    string OtherObjectKey, string? OtherDatabase, string? OtherSchema, string OtherName,
    string OwnColumns, string OtherColumns);

/// <summary>One column of a lineage object; <c>Tier</c> records whether it was read live (Derived) or parsed
/// from the CREATE the run executed (Observed/Declared).</summary>
public sealed record ObjectColumnDto(int Ordinal, string Name, string? DataType, bool Nullable, string Tier);

/// <summary>The script for any lineage node in one consistent shape, whatever its kind: a pipeline's authored
/// YAML, a view's or stored procedure's/function's module body, or a table's generated CREATE TABLE. This is
/// the single "show me the code behind this node" contract the graph uses, so clicking any node - flow or
/// object - resolves through one endpoint. <c>Language</c> is <c>yaml</c> for a pipeline and <c>sql</c> for a
/// database object; <c>Source</c> records where the script came from (Authored for a pipeline, Module for a
/// sys.sql_modules body, or the script tier - Derived/Observed/Declared - for a generated table script).</summary>
public sealed record NodeScriptDto(
    string Key, string Kind, string Language, string? Script, string? Source, string? Name);

/// <summary>One attributed lineage fact: a flow (or a module body) relating to an object. The object's
/// database, schema, and kind are joined from the global object registry (proper-cased, unlike the normalized
/// key) so a graph can label the object with where it lives and classify it (a DB-managed view has no writing
/// pipeline, so its kind cannot be inferred from the edges alone); null for a file or a partially-resolved
/// identity the registry does not know.</summary>
public sealed record EdgeDto(
    long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule,
    string Relation, string ObjectKey, string ObjectName, string? ObjectDatabase, string? ObjectSchema, string Tier,
    string? ObjectKind = null);

/// <summary>Everything known about one object in a single payload: its identity and metadata, its columns, its
/// generating script and module body, and the lineage edges that reference it. This is the "ask about this
/// object" aggregate a model uses to reason about, or author SQL against, the object. Each list is bounded for
/// a stable payload.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "'Object' is the serialized JSON property name the GUI reads; renaming it would break the API contract.")]
public sealed record ObjectDossierDto(
    ObjectDetailDto Object,
    IReadOnlyList<ObjectColumnDto> Columns,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<ObjectRelationshipDto> References,
    IReadOnlyList<ObjectRelationshipDto> ReferencedBy);

/// <summary>One repo whose lineage references an object: how many edges in that repo touch it, and whether any of
/// them writes/creates it (the repo where a flow populates it). The list is ranked so the writing repo comes
/// first, which is the repo the lineage graph opens on when a search result jumps to "how is this populated".</summary>
public sealed record ObjectRepoDto(Guid RepoId, string RepoName, int EdgeCount, bool Writes);

/// <summary>One flow-level dependency in a repo's execution plan: <c>ToFlow</c> waits for <c>FromFlow</c>.</summary>
public sealed record FlowDependencyDto(
    long Id, Guid RepoId, string FromFlow, string ToFlow, Guid FromPipelineId, Guid ToPipelineId, string ViaObjects);

/// <summary>One execution wave: the active pipelines that share a wave have no dependency between them and run
/// together; a later wave runs only after every earlier wave finishes. Wave -1 means lineage has not been computed
/// for those pipelines yet.</summary>
public sealed record WaveDto(int Wave, IReadOnlyList<WavePipelineDto> Pipelines);

/// <summary>One pipeline within a wave: its stable id and name.</summary>
public sealed record WavePipelineDto(Guid Id, string Name, string Kind);

/// <summary>One selectable project for the lineage graph's scope picker: a repo-root folder within a repo, the repo
/// it lives in (so the picker can show "repo / project" and disambiguate a folder name shared across repos), and
/// how many active flows it holds. The project is the seed the cross-repo lineage closure starts from.</summary>
public sealed record LineageProjectDto(Guid RepoId, string RepoName, string Project, int FlowCount);

/// <summary>One pipeline node in a project's cross-repo lineage closure. <c>IsSeed</c> marks a flow that belongs to
/// the selected project itself (its base objects seed the graph); a non-seed flow was reached downstream, possibly
/// in another repo, which is why <c>RepoId</c>/<c>RepoName</c> travel with every node. <c>Depth</c> is how many
/// downstream hops from the seed the flow sits at, so a client can tint or lay out by distance.</summary>
public sealed record ProjectGraphPipelineDto(
    Guid Id, string Name, string Kind, int Wave, Guid RepoId, string RepoName, string RelativePath,
    bool IsSeed, int Depth);

/// <summary>A project's lineage as one cross-repo subgraph: the pipeline nodes in the downstream closure, the edges
/// among them and the objects they move (reusing <see cref="EdgeDto"/>, whose <c>RepoId</c> records which repo
/// attributed each fact), the object keys at the depth-capped frontier that still have un-included consumers (so a
/// client can offer to expand them), and whether a node cap truncated the walk. The walk crosses repo boundaries
/// freely via the global object keys: an object's origin repo is irrelevant to how data flows through it.</summary>
public sealed record ProjectGraphDto(
    IReadOnlyList<ProjectGraphPipelineDto> Pipelines,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<string> Frontier,
    bool Truncated);

/// <summary>One (server, database, schema) grouping in the catalog with how many objects it holds: the schema
/// hierarchy a caller browses to answer "what schemas exist" and "how big is each" before drilling into
/// objects. A null database/schema is an object whose identity was only partially resolved (an offline sync).</summary>
public sealed record SchemaDto(string ServerRef, string? Database, string? Schema, int ObjectCount);

/// <summary>One (server, database, schema, kind) grouping in the catalog with how many objects it holds: the
/// per-kind breakdown of a schema (its Tables, Views, Procedures, ...) a caller uses to render kind-grouped
/// folders under each schema without listing the objects themselves. A null database/schema is an object whose
/// identity was only partially resolved (an offline sync).</summary>
public sealed record SchemaKindCountDto(
    string ServerRef, string? Database, string? Schema, string Kind, int ObjectCount);

/// <summary>One file endpoint decomposed to its canonical parent for the source tree: the origin system
/// (<c>OriginKind</c> = AzureStorage / Sftp / Local / Other, <c>Origin</c> = the storage account, SFTP
/// <c>host:port</c>, or filesystem), the container (an Azure container or UNC share; null otherwise), the
/// folder path (slash-joined; null at the root), and the leaf name. <c>Key</c> is the object key, so selecting
/// a leaf loads its dossier. This is the file twin of <see cref="SchemaKindCountDto"/>: a file groups under
/// its storage account exactly as a table groups under its database.</summary>
public sealed record FileNodeDto(
    string Key, string OriginKind, string Origin, string? Container, string? Path, string Name);

/// <summary>One object a flow lands data into (a written or created target), for the provenance view of a
/// file source: where the data that came through this file ends up.</summary>
public sealed record LandingObjectDto(string Key, string? Database, string? Schema, string Name, string Kind);

/// <summary>One pipeline that reads a file source, with where it lands the data: the flow identity plus the
/// distinct database objects it writes or creates. This is the "source, through which pipeline, to where"
/// answer the catalog gives for a file.</summary>
public sealed record FileConsumerDto(
    Guid PipelineId, string Flow, string Kind, Guid RepoId, IReadOnlyList<LandingObjectDto> Lands);

/// <summary>One pipeline that produces a file (writes or creates it): where the file itself comes from (a
/// copy, an acquisition, an export), so provenance can chain upstream past the file.</summary>
public sealed record FileProducerDto(Guid PipelineId, string Flow, string Kind, Guid RepoId);

/// <summary>A file source's provenance in one payload: the pipelines that PRODUCE the file (where it comes
/// from) and the pipelines that CONSUME it, each with the tables the data lands in (where it goes). Answers
/// "what are the pipelines for this source, and where does the data land" without walking the whole graph.</summary>
public sealed record FileFlowsDto(
    IReadOnlyList<FileProducerDto> Producers, IReadOnlyList<FileConsumerDto> Consumers);

/// <summary>
/// The read API over the shadow catalog's lineage graph: objects (with their columns and module bodies), the
/// attributed lineage edges, the per-repo flow dependencies, and the computed execution waves. Every query is
/// read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>), projected to DTOs (the raw EF
/// entities never leave the host), and the listing endpoints are bounded by page size. The repo-scoped endpoints
/// first verify the repo exists and return 404 when it does not, so an unknown repo is reported (rather than
/// silently returning an empty result a caller could not distinguish from a real repo with no lineage yet).
/// </summary>
public static class LineageEndpoints
{
    public static RouteGroupBuilder MapLineageEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var lineage = group.MapGroup("/lineage").WithTags("Lineage");
        lineage.MapGet("/schemas", ListSchemasAsync).WithName("ListLineageSchemas");
        lineage.MapGet("/schemas/kinds", ListSchemaKindsAsync).WithName("ListLineageSchemaKinds");
        lineage.MapGet("/file-tree", ListFileTreeAsync).WithName("ListLineageFileTree");
        lineage.MapGet("/file-flows", GetFileFlowsAsync).WithName("GetLineageFileFlows");
        lineage.MapGet("/objects", ListObjectsAsync).WithName("ListLineageObjects");
        lineage.MapGet("/objects/detail", GetObjectAsync).WithName("GetLineageObject");
        lineage.MapGet("/objects/columns", GetObjectColumnsAsync).WithName("GetLineageObjectColumns");
        lineage.MapGet("/objects/repos", ListObjectReposAsync).WithName("ListLineageObjectRepos");
        lineage.MapGet("/objects/dossier", GetObjectDossierAsync).WithName("GetLineageObjectDossier");
        lineage.MapGet("/file-pipelines", MatchFilePipelinesAsync).WithName("MatchFilePipelines");
        lineage.MapGet("/script", GetNodeScriptAsync).WithName("GetLineageNodeScript");
        lineage.MapGet("/projects", ListProjectsAsync).WithName("ListLineageProjects");
        lineage.MapGet("/project-graph", GetProjectGraphAsync).WithName("GetLineageProjectGraph");

        var repos = group.MapGroup("/repos").WithTags("Lineage");
        repos.MapGet("/{repoId:guid}/lineage/edges", ListEdgesAsync).WithName("ListLineageEdges");
        repos.MapGet("/{repoId:guid}/waves", GetWavesAsync).WithName("GetExecutionWaves");
        repos.MapGet("/{repoId:guid}/dependencies", GetDependenciesAsync).WithName("GetFlowDependencies");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<SchemaDto>>> ListSchemasAsync(
        CatalogDbContext db, string? serverRef, string? database, CancellationToken ct)
    {
        var query = db.Objects.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        // The whole schema hierarchy in one response (no paging): the distinct (server, database, schema)
        // groupings are bounded by the estate's real schema count, not the object count, so this cannot grow
        // without bound. Ordered so the hierarchy reads top-down and deterministically.
        var groups = await query
            .GroupBy(o => new { o.ServerRef, o.Database, o.Schema })
            .Select(g => new SchemaDto(g.Key.ServerRef, g.Key.Database, g.Key.Schema, g.Count()))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal)
            .ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SchemaDto>>(ordered);
    }

    private static async Task<Ok<IReadOnlyList<SchemaKindCountDto>>> ListSchemaKindsAsync(
        CatalogDbContext db, string? serverRef, string? database, string? schema, CancellationToken ct)
    {
        var query = db.Objects.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            query = query.Where(o => o.Schema == schema);
        }

        // The whole per-kind breakdown in one response (no paging): the distinct (server, database, schema, kind)
        // groupings are bounded by the estate's schema count times the handful of object kinds, not the object
        // count, so this cannot grow without bound. Ordered so the hierarchy reads top-down and deterministically.
        var groups = await query
            .GroupBy(o => new { o.ServerRef, o.Database, o.Schema, o.Kind })
            .Select(g => new SchemaKindCountDto(
                g.Key.ServerRef, g.Key.Database, g.Key.Schema, g.Key.Kind, g.Count()))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal)
            .ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SchemaKindCountDto>>(ordered);
    }

    /// <summary>
    /// Every file endpoint in the catalog, each decomposed to its canonical parent (storage account / SFTP
    /// server / UNC share / local filesystem, then container, folders, and leaf), so the GUI folds them into a
    /// source tree the twin of the database tree: origin > container > folder > file. Returned in one response
    /// (no paging): file nodes are the distinct file LOCATIONS flows declare (a folder a flow reads is one
    /// node, not one per physical blob), so the set is bounded by the estate's flow count, not its data volume.
    /// The origin is parsed with <see cref="FileOrigin"/>, which every flow type's file identity funnels
    /// through, so no flow kind is missed and a malformed identity still yields a shown node.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<FileNodeDto>>> ListFileTreeAsync(CatalogDbContext db, CancellationToken ct)
    {
        var files = await db.Objects.AsNoTracking()
            .Where(o => o.Kind == "File")
            .Select(o => new { o.Key, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Parse the Name (the clean canonical identity, e.g. az://account/container/path), not the Key: the
        // Key is the node identity 'file|||<name>' (server-reference-prefixed and case-folded), whose prefix
        // would defeat the scheme detection and lower-cased blob path would lose case. The Key still rides
        // along as the leaf's object id so selecting it opens the dossier.
        var nodes = files
            .Select(file =>
            {
                var origin = FileOrigin.Parse(file.Name);
                return new FileNodeDto(
                    file.Key, origin.Kind.ToString(), origin.Origin, origin.Container, origin.Path, origin.Name);
            })
            .OrderBy(n => n.Origin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Container, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<FileNodeDto>>(nodes);
    }

    /// <summary>
    /// A file source's provenance: the pipelines that produce it and the pipelines that consume it, each
    /// consumer with the database objects it lands the data in. Resolved from the flow-attributed lineage
    /// edges on this object key (across every repo, since a file identity is global): a Writes/Creates edge is
    /// a producer, a Reads/Requires edge a consumer, and a consumer's landing is that flow's own Writes/Creates
    /// edges onto non-file objects. Bounded: a file is touched by a handful of flows, each landing a handful of
    /// tables. Answers "what pipelines use this source and where does the data land" in one call.
    /// </summary>
    private static async Task<Ok<FileFlowsDto>> GetFileFlowsAsync(CatalogDbContext db, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Ok(new FileFlowsDto([], []));
        }

        // Every flow-attributed edge on the file, across repos: its relation tells producer from consumer.
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key && e.PipelineId != null)
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.Relation })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            return TypedResults.Ok(new FileFlowsDto([], []));
        }

        var producerIds = edges.Where(e => e.Relation is "Writes" or "Creates").Select(e => e.PipelineId).Distinct().ToList();
        var consumerIds = edges.Where(e => e.Relation is "Reads" or "Requires").Select(e => e.PipelineId).Distinct().ToList();
        var pipelineIds = producerIds.Concat(consumerIds).Distinct().ToList();

        // The involved flows' identities (name/kind/repo) in one lookup.
        var pipelines = (await db.Pipelines.AsNoTracking()
                .Where(p => pipelineIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Kind, p.RepoId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        // Each consumer's landing: the non-file objects it writes or creates, joined to the registry for names.
        var landings = consumerIds.Count == 0
            ? []
            : await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId != null && consumerIds.Contains(e.PipelineId!.Value)
                    && (e.Relation == "Writes" || e.Relation == "Creates"))
                .Join(db.Objects.AsNoTracking().Where(o => o.Kind != "File"), e => e.ObjectKey, o => o.Key,
                    (e, o) => new { PipelineId = e.PipelineId!.Value, o.Key, o.Database, o.Schema, o.Name, o.Kind })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

        var landsByPipeline = landings
            .GroupBy(l => l.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LandingObjectDto>)g
                    .Select(l => new LandingObjectDto(l.Key, l.Database, l.Schema, l.Name, l.Kind))
                    .OrderBy(l => l.Database, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var producers = producerIds
            .Where(pipelines.ContainsKey)
            .Select(id => new FileProducerDto(id, pipelines[id].Name, pipelines[id].Kind, pipelines[id].RepoId))
            .OrderBy(p => p.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var consumers = consumerIds
            .Where(pipelines.ContainsKey)
            .Select(id => new FileConsumerDto(
                id, pipelines[id].Name, pipelines[id].Kind, pipelines[id].RepoId,
                landsByPipeline.TryGetValue(id, out var lands) ? lands : []))
            .OrderBy(c => c.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new FileFlowsDto(producers, consumers));
    }

    private static async Task<Ok<PagedResult<ObjectDto>>> ListObjectsAsync(
        CatalogDbContext db, string? name, string? serverRef, string? database, string? schema, string? kind,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Objects.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(o => o.Name.Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            query = query.Where(o => o.Schema == schema);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(o => o.Kind == kind);
        }

        // Dependency order: by level (an object's depth in the data-movement graph, sources first), objects
        // outside the movement graph (null level) last, then by identity so a page boundary is deterministic.
        var ordered = query
            .OrderBy(o => o.Level == null)
            .ThenBy(o => o.Level)
            .ThenBy(o => o.Database).ThenBy(o => o.Schema).ThenBy(o => o.Name).ThenBy(o => o.Key);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(o => new ObjectDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.FirstSeenUtc, o.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ObjectDetailDto>, ProblemHttpResult>> GetObjectAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.Definition, o.Script,
                o.ScriptTier, o.ScriptUpdatedUtc, o.KeyColumns, o.KeyOrigin, o.FirstSeenUtc, o.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("object", key) : TypedResults.Ok(dto);
    }

    /// <summary>
    /// The unified script accessor: <c>GET /api/v1/lineage/script?key=&lt;node&gt;</c> returns the code behind any
    /// node in the graph, resolving the identifier across both sides of the bipartite graph. A catalog object
    /// (table/view/procedure/function/trigger) is matched by its node <c>Key</c> and returns its module body
    /// (<c>Definition</c>) when it has one, else its generated script (a table's CREATE TABLE); a pipeline is
    /// matched by its flow name, or by its stable id when the key is a GUID, and returns its authored YAML. So a
    /// single call serves every node kind the lineage graph draws. A pipeline key has one more facet:
    /// <c>view=object</c> returns the SQL of the database object the flow executes instead of the YAML (an sp
    /// flow's procedure, resolved through the flow's <c>Requires</c> lineage edge), so a client can offer both
    /// "the flow definition" and "the code it runs" for the same node.
    /// </summary>
    private static async Task<Results<Ok<NodeScriptDto>, ProblemHttpResult>> GetNodeScriptAsync(
        string key, string? view, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (an object node key or a pipeline name/id).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // A database object's script in the endpoint's shape: the module body is the authoritative source for a
        // view/procedure/function/trigger; a table has no module body, so its generated CREATE TABLE script is
        // returned instead. Shared by the direct object-key path and the pipeline view=object path.
        async Task<NodeScriptDto?> ObjectScriptAsync(string objectKey)
        {
            var obj = await db.Objects.AsNoTracking().Where(o => o.Key == objectKey)
                .Select(o => new { o.Key, o.Kind, o.Name, o.Definition, o.Script, o.ScriptTier })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return obj is null
                ? null
                : new NodeScriptDto(
                    obj.Key, obj.Kind, "sql", obj.Definition ?? obj.Script,
                    obj.Definition is not null ? "Module" : obj.ScriptTier, obj.Name);
        }

        var direct = await ObjectScriptAsync(key).ConfigureAwait(false);
        if (direct is not null)
        {
            return TypedResults.Ok(direct);
        }

        // A pipeline (flow) node: matched by its name, or by its stable id when the caller passed a GUID.
        var isId = Guid.TryParse(key, out var pipelineId);
        var pipe = await db.Pipelines.AsNoTracking()
            .Where(p => p.Name == key || (isId && p.Id == pipelineId))
            .Select(p => new { p.Id, p.Name, p.Kind, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipe is not null)
        {
            if (!string.Equals(view, "object", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.Ok(new NodeScriptDto(pipe.Name, "pipeline", "yaml", pipe.Yaml, "Authored", pipe.Name));
            }

            // view=object: the code the flow runs rather than its YAML. The flow's `Requires` edge names the
            // executed object (an sp flow declares exactly one, its procedure). When the object registry has no
            // row yet (its server was never connected-synced) the response still identifies the object, with a
            // null script, so the client can render its "no captured script" guidance instead of an error.
            var required = await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId == pipe.Id && e.Relation == "Requires")
                .OrderBy(e => e.Id)
                .Select(e => new { e.ObjectKey, e.ObjectName })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (required is null)
            {
                return NotFound("executed database object for flow", pipe.Name);
            }

            var executed = await ObjectScriptAsync(required.ObjectKey).ConfigureAwait(false)
                ?? new NodeScriptDto(required.ObjectKey, "Procedure", "sql", null, null, required.ObjectName);
            return TypedResults.Ok(executed);
        }

        return NotFound("script for node", key);
    }

    private static async Task<Results<Ok<PagedResult<ObjectColumnDto>>, ProblemHttpResult>> GetObjectColumnsAsync(
        string key, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var exists = await db.Objects.AsNoTracking().AnyAsync(o => o.Key == key, ct).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound("object", key);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        // A wide table's column list can be large, so it is paged; ordered by the captured ordinal, then name as a
        // stable secondary key so a page boundary is deterministic.
        var ordered = db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == key)
            .OrderBy(c => c.Ordinal).ThenBy(c => c.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var columns = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(c => new ObjectColumnDto(c.Ordinal, c.Name, c.DataType, c.Nullable, c.Tier))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectColumnDto>(columns, p, size, total));
    }

    /// <summary>
    /// The repos whose lineage references an object, ranked so the repo that populates it comes first. An object
    /// key is global (the same physical table appears in every repo that reads or writes it), but the lineage
    /// graph is drawn per repo, so a client jumping from a search hit to "how is this object populated" needs to
    /// know which repo's graph to open. Grouping the object's edges by repo answers that in one call: the repo
    /// with a <c>Writes</c>/<c>Creates</c> edge (a flow producing the object) is ranked first, then by how much of
    /// the object's lineage lives in the repo. An empty list means the object has no recorded lineage edges, so it
    /// appears in no graph.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<ObjectRepoDto>>, ProblemHttpResult>> ListObjectReposAsync(
        string? key, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (a lineage object key).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Count the object's edges per repo and flag whether any of them writes/creates it (the producing repo).
        var groups = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key)
            .GroupBy(e => e.RepoId)
            .Select(g => new
            {
                RepoId = g.Key,
                EdgeCount = g.Count(),
                Writes = g.Any(e => e.Relation == "Writes" || e.Relation == "Creates"),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<ObjectRepoDto>>(Array.Empty<ObjectRepoDto>());
        }

        // Resolve the repo names in one round-trip (the repo set is small and bounded by the estate's repos).
        var repoIds = groups.Select(g => g.RepoId).ToList();
        var names = await db.Repos.AsNoTracking()
            .Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        // Rank: the repo that populates the object first (the "how is it populated" view the graph opens on), then
        // by how much of its lineage lives there, then by name so the order is stable.
        var ranked = groups
            .Select(g => new ObjectRepoDto(
                g.RepoId,
                names.TryGetValue(g.RepoId, out var name) ? name : g.RepoId.ToString(),
                g.EdgeCount,
                g.Writes))
            .OrderByDescending(r => r.Writes)
            .ThenByDescending(r => r.EdgeCount)
            .ThenBy(r => r.RepoName, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<ObjectRepoDto>>(ranked);
    }

    /// <summary>
    /// Resolve which file-ingestion pipelines would ingest a given file, by matching it against the source spec
    /// each file flow declares (the spec already in the catalog, so no run is required). The match mirrors the
    /// engine's file selection: the source's file-name glob (<c>source.options.srcFile</c>, defaulting per source
    /// type) is applied to the file NAME with the same matcher the engine's cloud store uses
    /// (<c>FileSystemName.MatchesSimpleExpression</c>); when a
    /// full path is given it is further constrained by the path mask (<c>source.options.srcPathMask</c>, a regex
    /// over the path) or, absent a mask, by the source <c>location</c> the path must sit under. A bare file name
    /// (no path) matches on the glob alone and can return several flows, which the caller disambiguates. This is
    /// the definitional "which flow feeds this file" answer, complementing the historical run-to-file record.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<FilePipelineMatchDto>>, ProblemHttpResult>> MatchFilePipelinesAsync(
        string? file, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return TypedResults.Problem(
                detail: "A 'file' query parameter is required (a file name or full path).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Normalize to forward slashes; the file name is the last path segment, the rest is the (optional) path.
        var input = file.Trim().Replace('\\', '/');
        var slash = input.LastIndexOf('/');
        var fileName = slash >= 0 ? input[(slash + 1)..] : input;
        var hasPath = slash >= 0;
        if (fileName.Length == 0)
        {
            return TypedResults.Problem(
                detail: "The 'file' value has no file name to match.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Only file flows have a file source spec; scan the active ones (bounded by the estate's file flows).
        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == "file" && p.Active)
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.Id, p.Name, p.RepoId, RepoName = r.Name, p.DefinitionJson })
            .ToListAsync(ct).ConfigureAwait(false);

        var matches = new List<(FilePipelineMatchDto Dto, bool Confirmed)>();
        foreach (var flow in flows)
        {
            var source = ExtractFileSource(flow.DefinitionJson);
            if (source is null)
            {
                continue;
            }

            var glob = string.IsNullOrWhiteSpace(source.Glob) ? FileSelection.DefaultPattern(source.Type) : source.Glob!;
            // The engine applies the glob to the file name; reuse the one shared matcher (the exact BCL matcher its
            // cloud store uses) so the read API and the lineage collector select files identically.
            if (!FileSelection.NameMatchesGlob(glob, fileName))
            {
                continue;
            }

            // Confirm against the path when one was supplied: the path mask (a regex over the path) takes priority;
            // otherwise the path must sit under the source location (both normalized). A location carrying an
            // unresolved ${...} reference cannot be compared, so it does not constrain the match.
            bool? pathConfirmed = null;
            if (hasPath)
            {
                if (!string.IsNullOrWhiteSpace(source.Mask))
                {
                    pathConfirmed = FileSelection.SafeRegexMatch(source.Mask!, input);
                }
                else if (!string.IsNullOrWhiteSpace(source.Location) && !source.Location!.Contains("${", StringComparison.Ordinal))
                {
                    var loc = source.Location!.Replace('\\', '/').TrimEnd('/');
                    pathConfirmed = loc.Length > 0
                        && input.Contains(loc, StringComparison.OrdinalIgnoreCase);
                }
            }

            // A path constraint we could evaluate and that failed excludes the flow; a glob-only match (no path, or
            // no constraint to check) is still returned, flagged as unconfirmed so the caller can rank it lower.
            if (pathConfirmed == false)
            {
                continue;
            }

            matches.Add((
                new FilePipelineMatchDto(
                    flow.Id, flow.Name, flow.RepoId, flow.RepoName, source.Type, source.Location, glob,
                    pathConfirmed == true),
                pathConfirmed == true));
        }

        var ordered = matches
            .OrderByDescending(m => m.Confirmed)
            .ThenBy(m => m.Dto.RepoName, StringComparer.Ordinal)
            .ThenBy(m => m.Dto.PipelineName, StringComparer.Ordinal)
            .Select(m => m.Dto)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<FilePipelineMatchDto>>(ordered);
    }

    /// <summary>The parsed file source of a flow: what a file must match to be ingested by it.</summary>
    private sealed record FileSource(string Type, string? Location, string? Glob, string? Mask);

    /// <summary>Reads a file flow's source spec out of its stored <c>DefinitionJson</c> (the parsed flow), reaching
    /// <c>flow.source</c> and its options. Returns null when the document is not a file flow or carries no source.</summary>
    private static FileSource? ExtractFileSource(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            if (!doc.RootElement.TryGetProperty("flow", out var flow)
                || flow.ValueKind != JsonValueKind.Object
                || !flow.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = StringProp(source, "type") ?? "";
            var location = StringProp(source, "location");
            string? glob = null, mask = null, srcPath = null;
            if (source.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
            {
                glob = StringProp(options, "srcFile");
                mask = StringProp(options, "srcPathMask");
                srcPath = StringProp(options, "srcPath");
            }

            // The source root is either the endpoint location or the srcPath option (the loader accepts either).
            location ??= srcPath;
            // Nothing to match on (neither a type to default a pattern from, nor an explicit glob): not a usable
            // file source.
            if (type.Length == 0 && string.IsNullOrEmpty(glob))
            {
                return null;
            }

            return new FileSource(type, location, glob, mask);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The maximum rows each list of the dossier returns: an object's columns and its edges are
    /// bounded by the object, but a hot object can be referenced by many flows across many repos, so each
    /// collection is capped for a stable, single-response payload (the paged endpoints serve the full sets).</summary>
    private const int MaxDossierRows = 500;

    private static async Task<Results<Ok<ObjectDossierDto>, ProblemHttpResult>> GetObjectDossierAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var detail = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.Definition, o.Script,
                o.ScriptTier, o.ScriptUpdatedUtc, o.KeyColumns, o.KeyOrigin, o.FirstSeenUtc, o.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (detail is null)
        {
            return NotFound("object", key);
        }

        var columns = await db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == key)
            .OrderBy(c => c.Ordinal).ThenBy(c => c.Name)
            .Take(MaxDossierRows)
            .Select(c => new ObjectColumnDto(c.Ordinal, c.Name, c.DataType, c.Nullable, c.Tier))
            .ToListAsync(ct).ConfigureAwait(false);

        // Object-level edges reference the key across every repo (the object is global): both what reads it and
        // what writes it, so the model sees the flows on both sides. Every edge here points at THE dossier
        // object, so its database/schema come straight from the detail row (no join needed).
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key)
            .OrderBy(e => e.Relation).ThenBy(e => e.Flow).ThenBy(e => e.Id)
            .Take(MaxDossierRows)
            .Select(e => new EdgeDto(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName,
                detail.Database, detail.Schema, e.Tier, detail.Kind))
            .ToListAsync(ct).ConfigureAwait(false);

        // The interpreted data model, both directions. Rows are repo-scoped (each repo's code exhibits its own
        // observations), so the same relationship is deduplicated here by its identity, keeping the strongest
        // interpretation (constraint over join), the highest occurrence count, and the first constraint name.
        var rawRelationships = await db.ObjectRelationships.AsNoTracking()
            .Where(r => r.FromObjectKey == key || r.ToObjectKey == key)
            .OrderBy(r => r.Id)
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);

        var deduped = rawRelationships
            .GroupBy(r => (r.FromObjectKey, r.FromColumns, r.ToObjectKey, r.ToColumns, r.Origin), r => r)
            .Select(g => new RelationshipAggregate(
                g.Select(r => r.Name).FirstOrDefault(n => n is not null),
                g.Key.FromObjectKey, g.Key.FromColumns, g.Key.ToObjectKey, g.Key.ToColumns, g.Key.Origin,
                g.Select(r => r.Tier).OrderByDescending(TierRank).First(),
                g.Max(r => r.Occurrences)))
            .ToList();

        var otherKeys = deduped
            .Select(r => r.FromObjectKey == key ? r.ToObjectKey : r.FromObjectKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var locations = await LoadObjectLocationsAsync(db, otherKeys, ct).ConfigureAwait(false);

        ObjectRelationshipDto Project(RelationshipAggregate r, bool outgoing)
        {
            var otherKey = outgoing ? r.ToObjectKey : r.FromObjectKey;
            var found = locations.TryGetValue(otherKey, out var other);
            return new ObjectRelationshipDto(
                r.Name, r.Origin, r.Tier, r.Occurrences,
                otherKey, other.Database, other.Schema, found ? other.Name : otherKey,
                OwnColumns: outgoing ? r.FromColumns : r.ToColumns,
                OtherColumns: outgoing ? r.ToColumns : r.FromColumns);
        }

        var references = deduped.Where(r => r.FromObjectKey == key)
            .Select(r => Project(r, outgoing: true))
            .OrderByDescending(r => r.Occurrences).ThenBy(r => r.OtherName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var referencedBy = deduped.Where(r => r.ToObjectKey == key && r.FromObjectKey != key)
            .Select(r => Project(r, outgoing: false))
            .OrderByDescending(r => r.Occurrences).ThenBy(r => r.OtherName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new ObjectDossierDto(detail, columns, edges, references, referencedBy));
    }

    /// <summary>Ranks a relationship tier string for the dossier's cross-repo dedupe: Derived is the live
    /// database's view, Observed a run's, Declared the author's, mirroring <see cref="Core.Lineage.LineageTier"/>.</summary>
    private static int TierRank(string tier) => tier switch
    {
        "Derived" => 2,
        "Observed" => 1,
        _ => 0,
    };

    /// <summary>One deduplicated data-model relationship while the dossier folds the per-repo rows.</summary>
    private sealed record RelationshipAggregate(
        string? Name, string FromObjectKey, string FromColumns, string ToObjectKey, string ToColumns,
        string Origin, string Tier, int Occurrences);

    private static async Task<Results<Ok<PagedResult<EdgeDto>>, ProblemHttpResult>> ListEdgesAsync(
        Guid repoId, CatalogDbContext db, Guid? pipelineId, string? objectKey, string? relation, string? tier,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.LineageEdges.AsNoTracking().Where(e => e.RepoId == repoId);
        if (pipelineId is { } pid)
        {
            query = query.Where(e => e.PipelineId == pid);
        }

        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            query = query.Where(e => e.ObjectKey == objectKey);
        }

        if (!string.IsNullOrWhiteSpace(relation))
        {
            query = query.Where(e => e.Relation == relation);
        }

        if (!string.IsNullOrWhiteSpace(tier))
        {
            query = query.Where(e => e.Tier == tier);
        }

        var ordered = query.OrderBy(e => e.ObjectName).ThenBy(e => e.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        // Left-join the global object registry for each edge's database/schema (proper-cased, unlike the
        // normalized key), so graph clients can label objects with where they live; an edge whose object row
        // is missing (a race with identity healing) still returns, with nulls.
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .GroupJoin(db.Objects.AsNoTracking(), e => e.ObjectKey, o => o.Key, (e, objects) => new { e, objects })
            .SelectMany(x => x.objects.DefaultIfEmpty(), (x, o) => new EdgeDto(
                x.e.Id, x.e.RepoId, x.e.Flow, x.e.PipelineId, x.e.ViaModule, x.e.Relation, x.e.ObjectKey,
                x.e.ObjectName, o != null ? o.Database : null, o != null ? o.Schema : null, x.e.Tier,
                o != null ? o.Kind : null))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<EdgeDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<IReadOnlyList<WaveDto>>, ProblemHttpResult>> GetWavesAsync(
        Guid repoId, CatalogDbContext db, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        // The whole execution plan is returned in one response (no paging): both the wave count and the pipelines
        // within them are bounded by the repo's active pipeline count, which is the same set listed elsewhere, so
        // this cannot grow without bound for a given repo.
        var rows = await db.Pipelines.AsNoTracking()
            .Where(x => x.RepoId == repoId && x.Active)
            .OrderBy(x => x.Wave).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new { x.Wave, x.Id, x.Name, x.Kind })
            .ToListAsync(ct).ConfigureAwait(false);

        var waves = rows
            .GroupBy(x => x.Wave)
            .Select(g => new WaveDto(
                g.Key,
                g.Select(x => new WavePipelineDto(x.Id, x.Name, x.Kind)).ToList()))
            .ToList();
        return TypedResults.Ok<IReadOnlyList<WaveDto>>(waves);
    }

    private static async Task<Results<Ok<PagedResult<FlowDependencyDto>>, ProblemHttpResult>> GetDependenciesAsync(
        Guid repoId, CatalogDbContext db, Guid? pipelineId, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.FlowDependencies.AsNoTracking().Where(d => d.RepoId == repoId);
        // Narrowed to one flow's edges (both directions) when requested: a flow detail view needs "what this flow
        // waits for" and "what it unblocks" without paging through the whole repo's dependency list.
        if (pipelineId is { } pid)
        {
            query = query.Where(d => d.FromPipelineId == pid || d.ToPipelineId == pid);
        }

        // A dense estate can have many flow-to-flow dependencies, so this list is paged; ordered by the flow pair,
        // then row id as a stable secondary key so a page boundary is deterministic.
        var ordered = query.OrderBy(d => d.FromFlow).ThenBy(d => d.ToFlow).ThenBy(d => d.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var deps = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(d => new FlowDependencyDto(
                d.Id, d.RepoId, d.FromFlow, d.ToFlow, d.FromPipelineId, d.ToPipelineId, d.ViaObjects))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<FlowDependencyDto>(deps, p, size, total));
    }

    /// <summary>Every selectable project across all repos for the lineage graph's scope picker, each with its repo
    /// (name + id) and active-flow count, sorted by repo then project. The project is derived per flow from its
    /// repo-relative path (see <see cref="ProjectPath"/>) and reduced in memory: the distinct (repo, project) pairs
    /// are bounded by the estate's folder count, not its flow count, so the whole list is returned unpaged.</summary>
    private static async Task<Ok<IReadOnlyList<LineageProjectDto>>> ListProjectsAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.RepoId, RepoName = r.Name, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var projects = rows
            .GroupBy(x => new { x.RepoId, x.RepoName, Project = ProjectPath.Of(x.RelativePath) })
            .Select(g => new LineageProjectDto(g.Key.RepoId, g.Key.RepoName, g.Key.Project, g.Count()))
            .OrderBy(x => x.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<LineageProjectDto>>(projects);
    }

    // The closure's guard rails: how far downstream a walk runs by default and at most, and the most pipeline nodes
    // one response carries. The frontier lets a client push past these deliberately (by expanding a node) rather
    // than dumping the whole estate at once, which is the readability problem the project scope exists to solve.
    private const int DefaultGraphDepth = 3;
    private const int MaxGraphDepth = 12;
    private const int MaxGraphPipelines = 500;

    /// <summary>One lineage fact reduced to what the closure walk needs plus what it must echo back as an
    /// <see cref="EdgeDto"/>; the object's database/schema are attached afterward from the global registry.</summary>
    private sealed record EdgeRow(
        long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule, string Relation,
        string ObjectKey, string ObjectName, string Tier);

    /// <summary>
    /// A project's lineage as a single cross-repo subgraph. The seed is the selected project's active flows (the base
    /// objects they read and the tables they write); from there the walk follows data strictly downstream, crossing
    /// repo boundaries freely because objects are global and an object's origin repo does not gate how data flows
    /// through it. The walk is bounded two ways so a widely-consumed project cannot return the whole estate: a hop
    /// depth (<paramref name="depth"/>, clamped) and a node cap. Objects at the depth boundary that still have
    /// un-included consumers are returned as the <c>Frontier</c>, and <paramref name="expand"/> re-seeds the walk from
    /// specific nodes (an object key or a pipeline id) so a client can push past the boundary node by node.
    /// </summary>
    private static async Task<Results<Ok<ProjectGraphDto>, ProblemHttpResult>> GetProjectGraphAsync(
        CatalogDbContext db, Guid? repoId, string? project, int? depth, string[]? expand, CancellationToken ct)
    {
        // The seed is either a project (a repo-root folder) or, for a deep-link onto a specific node, one or more
        // expand tokens. A GUID token is a pipeline re-seeded at depth 0; any other token is an object key whose
        // producers (its immediate upstream) and consumers (downstream) both re-enter the walk, so a jumped-to object
        // shows where it comes from and where it goes without a separate whole-repo drawing path.
        var expandPipelineIds = new HashSet<Guid>();
        var expandObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in expand ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (Guid.TryParse(token, out var pid))
            {
                expandPipelineIds.Add(pid);
            }
            else
            {
                expandObjectKeys.Add(token);
            }
        }

        var hasProject = !string.IsNullOrWhiteSpace(project);
        var hasExpand = expandPipelineIds.Count > 0 || expandObjectKeys.Count > 0;

        if (repoId is { } checkRepo && !await RepoExistsAsync(db, checkRepo, ct).ConfigureAwait(false))
        {
            return NotFound("repo", checkRepo);
        }

        if (!hasProject && !hasExpand)
        {
            return TypedResults.Problem(
                detail: "Provide a 'project' (a repo-root folder) or an 'expand' node to seed the lineage graph.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "No seed");
        }

        if (hasProject && repoId is null)
        {
            return TypedResults.Problem(
                detail: "A 'repoId' is required with a 'project': a project is a folder within one repo.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing repoId");
        }

        var maxDepth = Math.Clamp(depth ?? DefaultGraphDepth, 1, MaxGraphDepth);

        // Seed pipelines: the selected project's active flows in its repo. The project is derived in memory (a path
        // split SQL should not carry), so only this repo's (id, path) pairs are pulled, not the whole table.
        var seedPipelineIds = new HashSet<Guid>();
        if (hasProject)
        {
            var repoPipelines = await db.Pipelines.AsNoTracking()
                .Where(p => p.RepoId == repoId!.Value && p.Active)
                .Select(p => new { p.Id, p.RelativePath })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in repoPipelines)
            {
                if (ProjectPath.Of(p.RelativePath) == project)
                {
                    seedPipelineIds.Add(p.Id);
                }
            }
        }

        if (seedPipelineIds.Count == 0 && expandPipelineIds.Count == 0 && expandObjectKeys.Count == 0)
        {
            return TypedResults.Ok(new ProjectGraphDto(
                Array.Empty<ProjectGraphPipelineDto>(), Array.Empty<EdgeDto>(), Array.Empty<string>(), false));
        }

        // The whole estate's edges, once, minimally projected. This is the universal graph the walk runs over; it is
        // bounded by the edge count (the same set the per-repo edges endpoint pages through, summed across repos) and
        // reduced to in-memory adjacency below so the traversal itself hits no database.
        var edgeRows = await db.LineageEdges.AsNoTracking()
            .Select(e => new EdgeRow(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName, e.Tier))
            .ToListAsync(ct).ConfigureAwait(false);

        var writesByPipeline = new Dictionary<Guid, List<string>>();
        var readsByPipeline = new Dictionary<Guid, List<string>>();
        var consumersByObject = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var producersByObject = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var e in edgeRows)
        {
            if (e.PipelineId is not { } pid)
            {
                continue;
            }

            if (e.Relation is "Writes" or "Creates")
            {
                (writesByPipeline.TryGetValue(pid, out var w) ? w : writesByPipeline[pid] = new List<string>())
                    .Add(e.ObjectKey);
                (producersByObject.TryGetValue(e.ObjectKey, out var pr)
                    ? pr : producersByObject[e.ObjectKey] = new List<Guid>()).Add(pid);
            }
            else if (e.Relation == "Reads")
            {
                // A read makes the flow a consumer of that object (the downstream link) and records the object as
                // one of the flow's source inputs (shown as a base node, but never walked further upstream).
                (readsByPipeline.TryGetValue(pid, out var r) ? r : readsByPipeline[pid] = new List<string>())
                    .Add(e.ObjectKey);
                (consumersByObject.TryGetValue(e.ObjectKey, out var c)
                    ? c : consumersByObject[e.ObjectKey] = new List<Guid>()).Add(pid);
            }
        }

        var pipelineDepth = new Dictionary<Guid, int>();
        var includedObjects = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Guid Pipeline, int Depth)>();
        var truncated = false;

        bool TryEnqueue(Guid pid, int d)
        {
            if (pipelineDepth.TryGetValue(pid, out var existing))
            {
                if (existing <= d)
                {
                    return true;
                }

                pipelineDepth[pid] = d;
                queue.Enqueue((pid, d));
                return true;
            }

            if (pipelineDepth.Count >= MaxGraphPipelines)
            {
                truncated = true;
                return false;
            }

            pipelineDepth[pid] = d;
            queue.Enqueue((pid, d));
            return true;
        }

        foreach (var pid in seedPipelineIds)
        {
            TryEnqueue(pid, 0);
        }

        foreach (var pid in expandPipelineIds)
        {
            TryEnqueue(pid, 0);
        }

        foreach (var key in expandObjectKeys)
        {
            includedObjects.Add(key);
            // Its producer(s) sit one hop upstream (seeded at depth 0 so their own sources come in too); its
            // consumer(s) one hop downstream. Seeding both makes a jumped-to object show its full local context and,
            // for a frontier expand, simply continues the walk past it (the producer is already included, a no-op).
            if (producersByObject.TryGetValue(key, out var producers))
            {
                foreach (var p in producers)
                {
                    TryEnqueue(p, 0);
                }
            }

            if (consumersByObject.TryGetValue(key, out var consumers))
            {
                foreach (var c in consumers)
                {
                    TryEnqueue(c, 1);
                }
            }
        }

        while (queue.Count > 0)
        {
            var (pid, d) = queue.Dequeue();
            if (pipelineDepth[pid] < d)
            {
                // A shorter path to this pipeline was found after it was queued; the shallower visit does the work.
                continue;
            }

            if (readsByPipeline.TryGetValue(pid, out var reads))
            {
                foreach (var key in reads)
                {
                    includedObjects.Add(key);
                }
            }

            if (!writesByPipeline.TryGetValue(pid, out var writes))
            {
                continue;
            }

            foreach (var key in writes)
            {
                includedObjects.Add(key);
                if (!consumersByObject.TryGetValue(key, out var consumers) || consumers.Count == 0)
                {
                    continue;
                }

                if (d + 1 > maxDepth)
                {
                    frontier.Add(key);
                    continue;
                }

                foreach (var consumer in consumers)
                {
                    if (!TryEnqueue(consumer, d + 1))
                    {
                        frontier.Add(key);
                    }
                }
            }
        }

        var includedPipes = new HashSet<Guid>(pipelineDepth.Keys);
        var ids = includedPipes.ToList();
        var pipeRows = await db.Pipelines.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.Id, p.Name, p.Kind, p.Wave, p.RepoId, RepoName = r.Name, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var pipelines = pipeRows
            .Select(p => new ProjectGraphPipelineDto(
                p.Id, p.Name, p.Kind, p.Wave, p.RepoId, p.RepoName, p.RelativePath,
                seedPipelineIds.Contains(p.Id), pipelineDepth[p.Id]))
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var locations = await LoadObjectLocationsAsync(db, includedObjects, ct).ConfigureAwait(false);
        var edges = edgeRows
            .Where(e => includedObjects.Contains(e.ObjectKey)
                && (e.PipelineId is null || includedPipes.Contains(e.PipelineId.Value)))
            .Select(e =>
            {
                locations.TryGetValue(e.ObjectKey, out var loc);
                return new EdgeDto(
                    e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName,
                    loc.Database, loc.Schema, e.Tier, loc.Kind);
            })
            .ToList();

        // Only report a frontier object that genuinely has a consumer we left out; an object can be marked while a
        // shorter path still pulled all its consumers in, and that is not something to offer expanding.
        var openFrontier = frontier
            .Where(key => consumersByObject.TryGetValue(key, out var consumers)
                && consumers.Any(c => !includedPipes.Contains(c)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new ProjectGraphDto(pipelines, edges, openFrontier, truncated));
    }

    /// <summary>The database/schema/name/kind for a set of object keys from the global registry, chunked so the
    /// IN-list never exceeds the provider's parameter limit. Keys the registry does not know (a race with identity
    /// healing) are simply absent, and the caller falls back to nulls, exactly as the edges endpoint does.</summary>
    private static async Task<Dictionary<string, (string? Database, string? Schema, string Name, string? Kind)>> LoadObjectLocationsAsync(
        CatalogDbContext db, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var result = new Dictionary<string, (string?, string?, string, string?)>(StringComparer.Ordinal);
        const int chunk = 1000;
        var all = keys.ToArray();
        for (var i = 0; i < all.Length; i += chunk)
        {
            var slice = all.Skip(i).Take(chunk).ToList();
            var rows = await db.Objects.AsNoTracking()
                .Where(o => slice.Contains(o.Key))
                .Select(o => new { o.Key, o.Database, o.Schema, o.Name, o.Kind })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.Key] = (row.Database, row.Schema, row.Name, row.Kind);
            }
        }

        return result;
    }

    private static Task<bool> RepoExistsAsync(CatalogDbContext db, Guid repoId, CancellationToken ct)
        => db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId, ct);

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(
            detail: $"No {resource} with key '{key}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
}
