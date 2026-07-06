using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A lineage object as it appears in lists: the canonical identity and metadata, without the heavy module
/// body (<c>Definition</c>). Keyed by <see cref="Key"/>, the global identity that joins the same physical object
/// across every repo.</summary>
public sealed record ObjectDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A single lineage object with its full module body (<c>Definition</c>) and generating script
/// (<c>Script</c>) for the detail view; the definition is null for plain tables, an unconnected sync, or an
/// encrypted module, and the script is null when no tier saw the object created.</summary>
public sealed record ObjectDetailDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind,
    string? Definition, string? Script, string? ScriptTier, DateTime? ScriptUpdatedUtc,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

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

/// <summary>One attributed lineage fact: a flow (or a module body) relating to an object.</summary>
public sealed record EdgeDto(
    long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule,
    string Relation, string ObjectKey, string ObjectName, string Tier);

/// <summary>Everything known about one object in a single payload: its identity and metadata, its columns, its
/// generating script and module body, and the lineage edges that reference it. This is the "ask about this
/// object" aggregate a model uses to reason about, or author SQL against, the object. Each list is bounded for
/// a stable payload.</summary>
public sealed record ObjectDossierDto(
    ObjectDetailDto Object,
    IReadOnlyList<ObjectColumnDto> Columns,
    IReadOnlyList<EdgeDto> Edges);

/// <summary>One flow-level dependency in a repo's execution plan: <c>ToFlow</c> waits for <c>FromFlow</c>.</summary>
public sealed record FlowDependencyDto(
    long Id, Guid RepoId, string FromFlow, string ToFlow, Guid FromPipelineId, Guid ToPipelineId, string ViaObjects);

/// <summary>One execution wave: the active pipelines that share a wave have no dependency between them and run
/// together; a later wave runs only after every earlier wave finishes. Wave -1 means lineage has not been computed
/// for those pipelines yet.</summary>
public sealed record WaveDto(int Wave, IReadOnlyList<WavePipelineDto> Pipelines);

/// <summary>One pipeline within a wave: its stable id and name.</summary>
public sealed record WavePipelineDto(Guid Id, string Name, string Kind);

/// <summary>One (server, database, schema) grouping in the catalog with how many objects it holds: the schema
/// hierarchy a caller browses to answer "what schemas exist" and "how big is each" before drilling into
/// objects. A null database/schema is an object whose identity was only partially resolved (an offline sync).</summary>
public sealed record SchemaDto(string ServerRef, string? Database, string? Schema, int ObjectCount);

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
        lineage.MapGet("/objects", ListObjectsAsync).WithName("ListLineageObjects");
        lineage.MapGet("/objects/detail", GetObjectAsync).WithName("GetLineageObject");
        lineage.MapGet("/objects/columns", GetObjectColumnsAsync).WithName("GetLineageObjectColumns");
        lineage.MapGet("/objects/dossier", GetObjectDossierAsync).WithName("GetLineageObjectDossier");
        lineage.MapGet("/script", GetNodeScriptAsync).WithName("GetLineageNodeScript");

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

        var ordered = query.OrderBy(o => o.Name).ThenBy(o => o.Key);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(o => new ObjectDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.FirstSeenUtc, o.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ObjectDetailDto>, ProblemHttpResult>> GetObjectAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Definition, o.Script, o.ScriptTier,
                o.ScriptUpdatedUtc, o.FirstSeenUtc, o.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("object", key) : TypedResults.Ok(dto);
    }

    /// <summary>
    /// The unified script accessor: <c>GET /api/v1/lineage/script?key=&lt;node&gt;</c> returns the code behind any
    /// node in the graph, resolving the identifier across both sides of the bipartite graph. A catalog object
    /// (table/view/procedure/function/trigger) is matched by its node <c>Key</c> and returns its module body
    /// (<c>Definition</c>) when it has one, else its generated script (a table's CREATE TABLE); a pipeline is
    /// matched by its flow name, or by its stable id when the key is a GUID, and returns its authored YAML. So a
    /// single call serves every node kind the lineage graph draws.
    /// </summary>
    private static async Task<Results<Ok<NodeScriptDto>, ProblemHttpResult>> GetNodeScriptAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (an object node key or a pipeline name/id).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // A database object: the module body is the authoritative source for a view/procedure/function/trigger;
        // a table has no module body, so its generated CREATE TABLE script is returned instead.
        var obj = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new { o.Key, o.Kind, o.Name, o.Definition, o.Script, o.ScriptTier })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (obj is not null)
        {
            var script = obj.Definition ?? obj.Script;
            var source = obj.Definition is not null ? "Module" : obj.ScriptTier;
            return TypedResults.Ok(new NodeScriptDto(obj.Key, obj.Kind, "sql", script, source, obj.Name));
        }

        // A pipeline (flow) node: matched by its name, or by its stable id when the caller passed a GUID.
        var isId = Guid.TryParse(key, out var pipelineId);
        var pipe = await db.Pipelines.AsNoTracking()
            .Where(p => p.Name == key || (isId && p.Id == pipelineId))
            .Select(p => new { p.Name, p.Kind, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipe is not null)
        {
            return TypedResults.Ok(new NodeScriptDto(pipe.Name, "pipeline", "yaml", pipe.Yaml, "Authored", pipe.Name));
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

    /// <summary>The maximum rows each list of the dossier returns: an object's columns and its edges are
    /// bounded by the object, but a hot object can be referenced by many flows across many repos, so each
    /// collection is capped for a stable, single-response payload (the paged endpoints serve the full sets).</summary>
    private const int MaxDossierRows = 500;

    private static async Task<Results<Ok<ObjectDossierDto>, ProblemHttpResult>> GetObjectDossierAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var detail = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Definition, o.Script, o.ScriptTier,
                o.ScriptUpdatedUtc, o.FirstSeenUtc, o.LastSeenUtc))
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
        // what writes it, so the model sees the flows on both sides.
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key)
            .OrderBy(e => e.Relation).ThenBy(e => e.Flow).ThenBy(e => e.Id)
            .Take(MaxDossierRows)
            .Select(e => new EdgeDto(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName, e.Tier))
            .ToListAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(new ObjectDossierDto(detail, columns, edges));
    }

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
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(e => new EdgeDto(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName, e.Tier))
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
        Guid repoId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        // A dense estate can have many flow-to-flow dependencies, so this list is paged; ordered by the flow pair,
        // then row id as a stable secondary key so a page boundary is deterministic.
        var ordered = db.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .OrderBy(d => d.FromFlow).ThenBy(d => d.ToFlow).ThenBy(d => d.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var deps = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(d => new FlowDependencyDto(
                d.Id, d.RepoId, d.FromFlow, d.ToFlow, d.FromPipelineId, d.ToPipelineId, d.ViaObjects))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<FlowDependencyDto>(deps, p, size, total));
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
