using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One flow that matched a search: where it lives, which of its fields matched, and an excerpt.</summary>
public sealed record FlowHitDto(
    string Id, string Name, string Kind, string? Batch, string RelativePath, string RepoId, string RepoName,
    string MatchedIn, string Snippet);

public sealed record SearchCategoryDto<T>(long Total, IReadOnlyList<T> Items);

/// <summary>The combined search: the parsed tokens (a multi-word term matches word by word, every word required)
/// and each category counted in full with a preview of its top hits.</summary>
public sealed record AllSearchDto(
    string Query,
    IReadOnlyList<string> Tokens,
    SearchCategoryDto<FlowHitDto> Flows);

internal sealed record SearchQuery(string Phrase, IReadOnlyList<string> Tokens)
{
    internal const int MaxTokens = 6;

    private const int MinTokenLength = 2;

    internal static SearchQuery? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var phrase = raw.Trim();
        var tokens = phrase
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= MinTokenLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTokens)
            .ToList();

        return tokens.Count == 0 ? null : new SearchQuery(phrase, tokens);
    }

    internal int TokenHits(string? text)
        => string.IsNullOrEmpty(text)
            ? 0
            : Tokens.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    internal (int Index, int Length) FirstMatch(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (-1, 0);
        }

        var phraseAt = text.IndexOf(Phrase, StringComparison.OrdinalIgnoreCase);
        if (phraseAt >= 0)
        {
            return (phraseAt, Phrase.Length);
        }

        var best = (Index: -1, Length: 0);
        foreach (var token in Tokens)
        {
            var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best.Index < 0 || at < best.Index))
            {
                best = (at, token.Length);
            }
        }

        return best;
    }
}

/// <summary>
/// Cross-repo search over the catalog: flow documents by name, path, or body text. One global search fans across
/// every category with a preview of top hits; each category also has its own paged endpoint.
/// </summary>
public static class SearchEndpoints
{
    private const int SnippetMaxLength = 200;

    private const int PreviewSize = 5;

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var search = group.MapGroup("/search").WithTags("Search");
        search.MapGet("/all", SearchAllAsync).WithName("SearchAll");
        search.MapGet("/flows", SearchFlowsAsync).WithName("SearchFlows");
        return group;
    }

    private static async Task<Results<Ok<PagedResult<FlowHitDto>>, ProblemHttpResult>> SearchFlowsAsync(
        CatalogDbContext db, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
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

    private static async Task<Results<Ok<AllSearchDto>, ProblemHttpResult>> SearchAllAsync(
        CatalogDbContext db, string? q, CancellationToken ct)
    {
        var term = SearchQuery.Parse(q);
        if (term is null)
        {
            return BadRequest("A non-empty 'q' query parameter is required.");
        }

        var flowQuery = FlowRows(db, term);
        var flowTotal = await flowQuery.LongCountAsync(ct).ConfigureAwait(false);
        var flowItems = (await flowQuery.Take(PreviewSize).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => MapFlow(r, term)).ToList();

        return TypedResults.Ok(new AllSearchDto(
            term.Phrase,
            term.Tokens,
            new SearchCategoryDto<FlowHitDto>(flowTotal, flowItems)));
    }

    // A flow matches on its name, its repo-relative path, or a term anywhere in its YAML body.
    internal static IQueryable<FlowRow> FlowRows(CatalogDbContext db, SearchQuery term)
    {
        var rows = from p in db.Pipelines.AsNoTracking()
                   join repo in db.Repos.AsNoTracking() on p.RepoId equals repo.Id
                   select new { Pipeline = p, Repo = repo };

        foreach (var token in term.Tokens)
        {
            var t = token;
            rows = rows.Where(x => x.Pipeline.Name.Contains(t)
                || x.Pipeline.RelativePath.Contains(t)
                || x.Pipeline.Yaml.Contains(t));
        }

        var phrase = term.Phrase;
        return rows
            .OrderByDescending(x => x.Pipeline.Name.Contains(phrase))
            .ThenBy(x => x.Pipeline.Name).ThenBy(x => x.Pipeline.Id)
            .Select(x => new FlowRow(
                x.Pipeline.Id, x.Pipeline.Name, x.Pipeline.Kind, x.Pipeline.Batch, x.Pipeline.RelativePath,
                x.Pipeline.RepoId, x.Repo.Name, x.Pipeline.Yaml));
    }

    private static FlowHitDto MapFlow(FlowRow r, SearchQuery term)
    {
        // Name/path matches are self-explanatory; only a body match needs an excerpt to show why it matched. The
        // field carrying the most tokens wins, so a flow named for the source and mentioning the term once in its
        // YAML still reads as a body hit.
        var nameHits = term.TokenHits(r.Name);
        var pathHits = term.TokenHits(r.RelativePath);
        var yamlHits = term.TokenHits(r.Yaml);

        if (nameHits >= pathHits && nameHits >= yamlHits && nameHits > 0)
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Name", r.RelativePath);
        }

        if (pathHits >= yamlHits && pathHits > 0)
        {
            return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
                r.RepoName, "Path", r.RelativePath);
        }

        return new FlowHitDto(r.Id.ToString(), r.Name, r.Kind, r.Batch, r.RelativePath, r.RepoId.ToString(),
            r.RepoName, "Body", BuildSnippet(r.Yaml, term));
    }

    /// <summary>
    /// Builds a bounded excerpt of <paramref name="text"/> centered on the verbatim phrase, or failing that on the
    /// earliest matching token, capped at <see cref="SnippetMaxLength"/> characters with ellipses where the text was
    /// trimmed.
    /// </summary>
    private static string BuildSnippet(string text, SearchQuery term)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var (index, matchLength) = term.FirstMatch(text);
        if (index < 0)
        {
            // The row matched on a sibling field (or through a collation difference): show the leading slice rather
            // than failing.
            (index, matchLength) = (0, 0);
        }

        // Center the window on the match: keep some context on each side, clamped to the string's bounds.
        var lead = Math.Max(0, (SnippetMaxLength - matchLength) / 2);
        var start = Math.Max(0, index - lead);
        var length = Math.Min(SnippetMaxLength, text.Length - start);

        var slice = text.Substring(start, length);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < text.Length ? "..." : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    private static ProblemHttpResult BadRequest(string detail)
        => TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid search request");

    /// <summary>The columns a flow search needs from a pipeline before the match location and excerpt are computed.</summary>
    internal sealed record FlowRow(
        Guid Id, string Name, string Kind, string? Batch, string RelativePath, Guid RepoId, string RepoName, string Yaml);
}
