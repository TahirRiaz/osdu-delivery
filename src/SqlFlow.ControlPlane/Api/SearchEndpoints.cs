using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A catalog object that matched a name search: the identity a client needs to locate it.</summary>
public sealed record ObjectHitDto(
    string Key, string Name, string Kind, string ServerRef, string? Database, string? Schema);

/// <summary>A column that matched a name search, carried with its owning object for context: this answers
/// "every table/view with a column named X" across all repos.</summary>
public sealed record ColumnHitDto(
    string ObjectKey, string ObjectName, string ColumnName, string? DataType, bool Nullable);

/// <summary>An object whose code matched a definition search, with a short excerpt around the first occurrence so a
/// list can show why it matched. <see cref="Source"/> says which body carried the match: the live module body
/// (<c>Module</c>) or the generating DDL the engine emitted (<c>Script</c>).</summary>
public sealed record DefinitionHitDto(string Key, string Name, string Kind, string Snippet, string Source);

/// <summary>A file a run processed whose name or path matched, carried with its run so the hit deep-links to the
/// run that touched it: this answers "which runs processed sess.20180529.csv". <see cref="PipelineId"/> and
/// <see cref="RepoId"/> are the flow that ingested the file and its repo, so the hit can also jump to that flow's
/// node in the lineage graph.</summary>
public sealed record FileHitDto(
    string Name, string? Path, string RunId, string FlowName, string FlowKind,
    long Rows, int Columns, long SizeBytes, DateTime? RunUtc, string PipelineId, string? RepoId, string? RepoName);

/// <summary>A flow (YAML document) that matched by name, repo-relative path, or a term inside its body, with a short
/// excerpt when the match was in the YAML text. <see cref="MatchedIn"/> is <c>Name</c>, <c>Path</c>, or <c>Body</c>.</summary>
public sealed record FlowHitDto(
    string Id, string Name, string Kind, string? Batch, string RelativePath, string RepoId, string RepoName,
    string MatchedIn, string Snippet);

/// <summary>One category of a combined search: the full match count plus a small preview of the top hits, so the
/// unified view can show "Files (37)" with the first few and a jump to the dedicated tab for the rest.</summary>
public sealed record SearchCategoryDto<T>(long Total, IReadOnlyList<T> Items);

/// <summary>The combined result of a single global search across every catalog surface, each category counted in
/// full and previewed with its top hits.</summary>
public sealed record AllSearchDto(
    SearchCategoryDto<ObjectHitDto> Objects,
    SearchCategoryDto<ColumnHitDto> Columns,
    SearchCategoryDto<DefinitionHitDto> Definitions,
    SearchCategoryDto<FileHitDto> Files,
    SearchCategoryDto<FlowHitDto> Flows);

/// <summary>
/// The cross-repo global search over the shadow catalog: one term fanned out across every searchable surface -
/// objects by name, columns by name, code (module bodies and emitted DDL), processed files, and flow YAML. Each
/// dedicated endpoint pages one surface; the combined <c>/all</c> endpoint runs the same queries and returns a
/// previewed count per surface for a single unified view. Every query is read-only
/// (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>), projected to a DTO (the raw EF entities never
/// leave the host), and bounded by page size.
/// </summary>
public static class SearchEndpoints
{
    /// <summary>The longest excerpt a code/flow search returns per hit, in characters.</summary>
    private const int SnippetMaxLength = 200;

    /// <summary>How many hits each category previews in the combined <c>/all</c> view before "see all N" takes over.</summary>
    private const int PreviewSize = 5;

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var search = group.MapGroup("/search").WithTags("Search");
        search.MapGet("/all", SearchAllAsync).WithName("SearchAll");
        search.MapGet("/objects", SearchObjectsAsync).WithName("SearchObjects");
        search.MapGet("/columns", SearchColumnsAsync).WithName("SearchColumns");
        search.MapGet("/definitions", SearchDefinitionsAsync).WithName("SearchDefinitions");
        search.MapGet("/files", SearchFilesAsync).WithName("SearchFiles");
        search.MapGet("/flows", SearchFlowsAsync).WithName("SearchFlows");

        return group;
    }

    private static async Task<Results<Ok<PagedResult<ObjectHitDto>>, ProblemHttpResult>> SearchObjectsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = Term(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(ObjectsQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<PagedResult<ColumnHitDto>>, ProblemHttpResult>> SearchColumnsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = Term(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(ColumnsQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Scans object code - both the live module body (<c>Definition</c>) and the generating DDL the engine emitted
    /// (<c>Script</c>) - for a term and returns a bounded excerpt around the first match, tagged with which body
    /// matched. This is a LIKE/Contains scan today; a SQL Server FULLTEXT index over the definition column (already
    /// provisioned by a guarded migration when the feature is installed) is the scalable path for large estates.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DefinitionHitDto>>, ProblemHttpResult>> SearchDefinitionsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = Term(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = DefinitionRows(db, term);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // The substring-around-the-first-index excerpt does not translate to SQL, so it runs after the page
        // materializes; only the page's rows carry their (large) code columns back.
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => MapDefinition(r, term)).ToList();
        return TypedResults.Ok(new PagedResult<DefinitionHitDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<FileHitDto>>, ProblemHttpResult>> SearchFilesAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        var term = Term(name);
        if (term is null)
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        return TypedResults.Ok(await PageAsync(FilesQuery(db, term), p, size, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<PagedResult<FlowHitDto>>, ProblemHttpResult>> SearchFlowsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = Term(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = FlowRows(db, term);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // The match-location tag and the YAML excerpt are computed in memory, so only the page's rows carry their
        // (large) YAML column back.
        var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(r => MapFlow(r, term)).ToList();
        return TypedResults.Ok(new PagedResult<FlowHitDto>(items, p, size, total));
    }

    /// <summary>
    /// One global search across every surface at once: each category is counted in full and previewed with its top
    /// <see cref="PreviewSize"/> hits, so the unified view shows "Files (37)" with the first few and defers the rest
    /// to the dedicated tab. Reuses the exact same query builders the paged endpoints use, so a hit that shows here
    /// is the same hit the tab shows.
    /// </summary>
    private static async Task<Results<Ok<AllSearchDto>, ProblemHttpResult>> SearchAllAsync(
        CatalogDbContext db, string? q, CancellationToken ct)
    {
        var term = Term(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var objects = await PreviewAsync(ObjectsQuery(db, term), ct).ConfigureAwait(false);
        var columns = await PreviewAsync(ColumnsQuery(db, term), ct).ConfigureAwait(false);
        var files = await PreviewAsync(FilesQuery(db, term), ct).ConfigureAwait(false);

        var defQuery = DefinitionRows(db, term);
        var defTotal = await defQuery.LongCountAsync(ct).ConfigureAwait(false);
        var defItems = (await defQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapDefinition(r, term)).ToList();

        var flowQuery = FlowRows(db, term);
        var flowTotal = await flowQuery.LongCountAsync(ct).ConfigureAwait(false);
        var flowItems = (await flowQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapFlow(r, term)).ToList();

        return TypedResults.Ok(new AllSearchDto(
            objects,
            columns,
            new SearchCategoryDto<DefinitionHitDto>(defTotal, defItems),
            files,
            new SearchCategoryDto<FlowHitDto>(flowTotal, flowItems)));
    }

    // ---- Shared query builders (one definition per surface, used by both the paged and combined endpoints) --------

    // .Contains over a column maps to LIKE '%term%' and matches case-insensitively under SQL Server's default
    // collation.
    private static IQueryable<ObjectHitDto> ObjectsQuery(CatalogDbContext db, string term) =>
        db.Objects.AsNoTracking()
            .Where(o => o.Name.Contains(term))
            .OrderBy(o => o.Name).ThenBy(o => o.Key)
            .Select(o => new ObjectHitDto(o.Key, o.Name, o.Kind, o.ServerRef, o.Database, o.Schema));

    // Join each matching column to its owning object for the display name (a soft link on ObjectKey; no FK).
    private static IQueryable<ColumnHitDto> ColumnsQuery(CatalogDbContext db, string term) =>
        from c in db.ObjectColumns.AsNoTracking()
        where c.Name.Contains(term)
        join o in db.Objects.AsNoTracking() on c.ObjectKey equals o.Key
        orderby c.Name, o.Name, c.Ordinal
        select new ColumnHitDto(c.ObjectKey, o.Name, c.Name, c.DataType, c.Nullable);

    // Only objects that carry a module body or an emitted script can match; the LIKE runs server-side over both.
    private static IQueryable<DefinitionRow> DefinitionRows(CatalogDbContext db, string term) =>
        db.Objects.AsNoTracking()
            .Where(o => (o.Definition != null && o.Definition.Contains(term))
                || (o.Script != null && o.Script.Contains(term)))
            .OrderBy(o => o.Name).ThenBy(o => o.Key)
            .Select(o => new DefinitionRow(o.Key, o.Name, o.Kind, o.Definition, o.Script));

    // Each processed file joined to the run that touched it, newest run first within a file name.
    private static IQueryable<FileHitDto> FilesQuery(CatalogDbContext db, string term) =>
        from f in db.RunFiles.AsNoTracking()
        where f.Name.Contains(term) || (f.Path != null && f.Path.Contains(term))
        join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
        // Left join the run's repo so the hit can name where its flow lives (a run may carry no repo).
        join repo in db.Repos.AsNoTracking() on r.RepoId equals repo.Id into repoJoin
        from repo in repoJoin.DefaultIfEmpty()
        orderby f.Name, r.StartUtc descending
        // Guid.ToString() here is translated to SQL Server's CONVERT, which yields UPPERCASE; the rest of the API
        // emits lowercase GUIDs (System.Text.Json), and the GUI matches graph node ids by exact string. Lowercase
        // the ids the client keys on (pipeline, repo) so a file's lineage/pipeline jump resolves.
        // ToLower() runs entirely server-side (translated to SQL LOWER), so the current-culture concern CA1304/CA1311
        // raise does not apply, and the invariant overloads are not guaranteed to translate; suppress here.
#pragma warning disable CA1304, CA1311
        select new FileHitDto(
            f.Name, f.Path, f.RunId.ToString().ToLower(), r.FlowName, r.FlowKind,
            f.Rows, f.Columns, f.SizeBytes, r.StartUtc,
            r.PipelineId.ToString().ToLower(), r.RepoId.HasValue ? r.RepoId.Value.ToString().ToLower() : null,
            repo != null ? repo.Name : null);
#pragma warning restore CA1304, CA1311

    // A flow matches on its name, its repo-relative path, or a term anywhere in its YAML body.
    private static IQueryable<FlowRow> FlowRows(CatalogDbContext db, string term) =>
        from p in db.Pipelines.AsNoTracking()
        where p.Name.Contains(term) || p.RelativePath.Contains(term) || p.Yaml.Contains(term)
        join repo in db.Repos.AsNoTracking() on p.RepoId equals repo.Id
        orderby p.Name, p.Id
        select new FlowRow(p.Id, p.Name, p.Kind, p.Batch, p.RelativePath, p.RepoId, repo.Name, p.Yaml);

    private static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query, int page, int size, CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<T>(items, page, size, total);
    }

    private static async Task<SearchCategoryDto<T>> PreviewAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false);
        return new SearchCategoryDto<T>(total, items);
    }

    private static DefinitionHitDto MapDefinition(DefinitionRow r, string term)
    {
        // Prefer the live module body when it carries the match; otherwise the emitted DDL did.
        var inModule = r.Definition is not null
            && r.Definition.Contains(term, StringComparison.OrdinalIgnoreCase);
        var body = inModule ? r.Definition! : (r.Script ?? r.Definition ?? string.Empty);
        var source = inModule ? "Module" : "Script";
        return new DefinitionHitDto(r.Key, r.Name, r.Kind, BuildSnippet(body, term), source);
    }

    private static FlowHitDto MapFlow(FlowRow r, string term)
    {
        // Name/path matches are self-explanatory; only a body match needs an excerpt to show why it matched.
        if (r.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Name", r.RelativePath);
        }

        if (r.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Path", r.RelativePath);
        }

        return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
            r.RepoName, "Body", BuildSnippet(r.Yaml, term));
    }

    /// <summary>
    /// Builds a bounded excerpt of <paramref name="text"/> centered on the first occurrence of
    /// <paramref name="term"/>, capped at <see cref="SnippetMaxLength"/> characters with ellipses where the text was
    /// trimmed. Match is case-insensitive to mirror the server-side LIKE.
    /// </summary>
    private static string BuildSnippet(string text, string term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            // The body matched server-side but the term is not found here (for example a collation difference):
            // fall back to the leading slice rather than failing.
            index = 0;
        }

        // Center the window on the match: keep some context on each side, clamped to the string's bounds.
        var lead = Math.Max(0, (SnippetMaxLength - term.Length) / 2);
        var start = Math.Max(0, index - lead);
        var length = Math.Min(SnippetMaxLength, text.Length - start);

        var slice = text.Substring(start, length);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < text.Length ? "..." : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    /// <summary>The trimmed search term, or null when the input was missing or blank.</summary>
    private static string? Term(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static ProblemHttpResult BadRequest(string detail)
        => TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid search request");

    /// <summary>The columns a definition search needs from an object before the excerpt is built in memory.</summary>
    private sealed record DefinitionRow(string Key, string Name, string Kind, string? Definition, string? Script);

    /// <summary>The columns a flow search needs from a pipeline before the match location and excerpt are computed.</summary>
    private sealed record FlowRow(
        Guid Id, string Name, string Kind, string? Batch, string RelativePath, Guid RepoId, string RepoName, string Yaml);
}
