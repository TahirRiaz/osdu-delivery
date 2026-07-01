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

/// <summary>An object whose module body matched a definition (code) search, with a short excerpt around the
/// first occurrence so a list can show why it matched.</summary>
public sealed record DefinitionHitDto(string Key, string Name, string Kind, string Snippet);

/// <summary>
/// The cross-repo data-dictionary / code-search API over the shadow catalog: find objects by name, find every
/// object carrying a column of a given name, and scan module bodies (procedures/views/functions/triggers) for a
/// term. Every query is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>), projected to a
/// DTO (the raw EF entities never leave the host), and bounded by page size.
/// </summary>
public static class SearchEndpoints
{
    /// <summary>The longest excerpt a definition search returns per hit, in characters.</summary>
    private const int SnippetMaxLength = 200;

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var search = group.MapGroup("/search").WithTags("Search");
        search.MapGet("/objects", SearchObjectsAsync).WithName("SearchObjects");
        search.MapGet("/columns", SearchColumnsAsync).WithName("SearchColumns");
        search.MapGet("/definitions", SearchDefinitionsAsync).WithName("SearchDefinitions");

        return group;
    }

    private static async Task<Results<Ok<PagedResult<ObjectHitDto>>, ProblemHttpResult>> SearchObjectsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var term = name.Trim();

        // .Contains over the column maps to a LIKE '%term%' and matches case-insensitively under SQL Server's
        // default collation.
        var query = db.Objects.AsNoTracking()
            .Where(o => o.Name.Contains(term))
            .OrderBy(o => o.Name).ThenBy(o => o.Key);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Skip((p - 1) * size).Take(size)
            .Select(o => new ObjectHitDto(o.Key, o.Name, o.Kind, o.ServerRef, o.Database, o.Schema))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectHitDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<ColumnHitDto>>, ProblemHttpResult>> SearchColumnsAsync(
        CatalogDbContext db, string? name, int? page, int? pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest("A non-empty 'name' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var term = name.Trim();

        // Join each matching column to its owning object for the display name (a soft link on ObjectKey; no FK).
        var query =
            from c in db.ObjectColumns.AsNoTracking()
            where c.Name.Contains(term)
            join o in db.Objects.AsNoTracking() on c.ObjectKey equals o.Key
            orderby c.Name, o.Name, c.Ordinal
            select new ColumnHitDto(c.ObjectKey, o.Name, c.Name, c.DataType, c.Nullable);

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ColumnHitDto>(items, p, size, total));
    }

    /// <summary>
    /// Scans object module bodies (procedure/view/function/trigger definitions) for a term and returns a bounded
    /// excerpt around the first match. This is a LIKE/Contains scan today; a SQL Server FULLTEXT index over the
    /// definition column (already provisioned by a guarded migration when the feature is installed, keyed off the
    /// object's full-text surrogate) is the scalable path for large estates.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DefinitionHitDto>>, ProblemHttpResult>> SearchDefinitionsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var term = q.Trim();

        // Only objects that actually carry a module body can match; the LIKE runs server-side.
        var query = db.Objects.AsNoTracking()
            .Where(o => o.Definition != null && o.Definition.Contains(term))
            .OrderBy(o => o.Name).ThenBy(o => o.Key);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Fetch the matching objects (with their definitions), then build each excerpt in memory: the
        // substring-around-the-first-index logic does not translate to SQL, so it runs after materialization.
        var rows = await query
            .Skip((p - 1) * size).Take(size)
            .Select(o => new { o.Key, o.Name, o.Kind, o.Definition })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows
            .Select(r => new DefinitionHitDto(r.Key, r.Name, r.Kind, BuildSnippet(r.Definition, term)))
            .ToList();
        return TypedResults.Ok(new PagedResult<DefinitionHitDto>(items, p, size, total));
    }

    /// <summary>
    /// Builds a bounded excerpt of <paramref name="definition"/> centered on the first occurrence of
    /// <paramref name="term"/>, capped at <see cref="SnippetMaxLength"/> characters with ellipses where the text was
    /// trimmed. Match is case-insensitive to mirror the server-side LIKE. The definition is non-null here because
    /// only rows with a non-null, matching body reach this point.
    /// </summary>
    private static string BuildSnippet(string? definition, string term)
    {
        if (string.IsNullOrEmpty(definition))
        {
            return string.Empty;
        }

        var index = definition.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            // The body matched server-side but the term is not found here (for example a collation difference):
            // fall back to the leading slice rather than failing.
            index = 0;
        }

        // Center the window on the match: keep some context on each side, clamped to the string's bounds.
        var lead = Math.Max(0, (SnippetMaxLength - term.Length) / 2);
        var start = Math.Max(0, index - lead);
        var length = Math.Min(SnippetMaxLength, definition.Length - start);

        var slice = definition.Substring(start, length);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < definition.Length ? "..." : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    private static ProblemHttpResult BadRequest(string detail)
        => TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid search request");
}
