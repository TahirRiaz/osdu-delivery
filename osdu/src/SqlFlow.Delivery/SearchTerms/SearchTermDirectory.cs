using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>
/// A search term as people see it (osdu/docs/search-terms.md): the column it names, the name it is searched by, whether it
/// is left out, the routes it reaches the record by and the one it is searched through, and why it cannot be searched
/// where it cannot. A refinement whose term no mapping gives any longer is one with no routes (<see cref="Orphan"/>).
/// </summary>
public sealed record SearchTermView
{
    public required Guid Id { get; init; }

    /// <summary>The term's key as text (<see cref="SearchTermKey.Text"/>).</summary>
    public required string Key { get; init; }

    public required string System { get; init; }

    public required string EntityType { get; init; }

    public string? Dataset { get; init; }

    public required string Column { get; init; }

    /// <summary>The column as the source names it: <c>curves.curve_unit</c>.</summary>
    public required string ColumnLabel { get; init; }

    /// <summary>The name the term is searched by: the one a person gave it, else its column's.</summary>
    public required string Name { get; init; }

    public bool Renamed { get; init; }

    public bool Excluded { get; init; }

    public string? Note { get; init; }

    /// <summary>The route a person picked; null when the term is searched through the one the explorer prefers.</summary>
    public string? PickedRoute { get; init; }

    /// <summary>The route the term is searched through; null when none can be searched.</summary>
    public string? Route { get; init; }

    public IReadOnlyList<SearchRouteView> Routes { get; init; } = [];

    /// <summary>The active delivery flows whose mappings read the column.</summary>
    public IReadOnlyList<string> Flows { get; init; } = [];

    /// <summary>The mappings that read the column, each once.</summary>
    public IReadOnlyList<string> Mappings { get; init; } = [];

    /// <summary>Why the term cannot be searched; null when it can (and is not left out).</summary>
    public string? Problem { get; init; }

    /// <summary>
    /// Where the values to pick a value from are read: the property, and for a route through other records their kind, whose
    /// values are the source's own; null for a route whose written values are not the ones a person types.
    /// </summary>
    public SearchTermSuggest? Suggest { get; init; }

    /// <summary>True for a refinement whose term no mapping of an active flow gives any longer.</summary>
    public bool Orphan { get; init; }

    public string? UpdatedBy { get; init; }

    public DateTime? UpdatedUtc { get; init; }
}

/// <summary>Where the values held for a term are read, to pick one from: a property of a kind, as the platform indexes it.</summary>
public sealed record SearchTermSuggest(string? Kind, string Path, string Index, string? Nested);

/// <summary>A route of a term as people see it: how the column reaches the record, how that is indexed, and what it can be asked.</summary>
public sealed record SearchRouteView
{
    public required string Id { get; init; }

    public required string Target { get; init; }

    public required string Path { get; init; }

    /// <summary><c>copy</c>, <c>steps</c>, <c>lookup</c>, <c>search</c>, <c>key</c> or <c>expression</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>How the route reaches the record, in words.</summary>
    public required string How { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    public IReadOnlyList<string> Mappings { get; init; } = [];

    public string? Location { get; init; }

    public SearchRouteFind? Find { get; init; }

    public IReadOnlyList<string>? KeyColumns { get; init; }

    public string? When { get; init; }

    public string? Description { get; init; }

    /// <summary>How the platform indexes the property compared: text, keyword, number, boolean or date; null when it cannot be searched.</summary>
    public string? Index { get; init; }

    public string? Nested { get; init; }

    /// <summary>The conditions the route takes, the likeliest first, named as the explorer names them.</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];

    /// <summary>Why no condition can be asked through the route; null when one can.</summary>
    public string? Problem { get; init; }
}

/// <summary>What a person makes of a term: a name (null for the column's own), whether it is left out, the route (null for the preferred one), a note.</summary>
public sealed record SearchTermRefinementRequest(string? Name, bool Excluded, string? Route, string? Note);

/// <summary>
/// The search terms of the module (osdu/docs/search-terms.md): the terms the repository syncs extracted, merged across
/// repositories, with what people made of them, each route classified against the saved templates; the refinements people
/// make; and a condition on a term turned into the condition the explorer asks of the records. One directory serves one
/// request: it keeps the mappings and templates it read for the rest of it.
/// </summary>
public sealed class SearchTermDirectory
{
    private readonly OsduDbContext _db;
    private readonly ITemplateStore _templates;
    private readonly DeliveryDocumentLoader _documents;
    private readonly TimeProvider _time;
    private readonly Dictionary<(Guid Repo, string Reference), (MappingDefinition? Mapping, string? Problem)> _mappings = [];
    private readonly Dictionary<TemplateReference, SchemaSnapshot?> _schemas = [];
    private IReadOnlyList<TemplateInfo>? _saved;

    public SearchTermDirectory(OsduDbContext db, ITemplateStore templates, DeliveryDocumentLoader documents, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _templates = templates;
        _documents = documents;
        _time = time;
    }

    /// <summary>The entity types terms are extracted for, each with how many terms it has, by name.</summary>
    public async Task<IReadOnlyList<(string EntityType, int Terms)>> EntityTypesAsync(CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking()
            .Select(t => new { t.EntityType, t.TermId })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .GroupBy(r => r.EntityType, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The terms of <paramref name="entityType"/> (every entity type when null), by name; with <paramref name="orphans"/>, the
    /// refinements whose terms no mapping gives any longer too.
    /// </summary>
    public async Task<IReadOnlyList<SearchTermView>> ListAsync(string? entityType, bool orphans, CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking()
            .Where(t => entityType == null || t.EntityType == entityType)
            .ToListAsync(ct).ConfigureAwait(false);
        var refinements = await _db.DeliverySearchTermRefinements.AsNoTracking()
            .Where(r => entityType == null || r.EntityType == entityType)
            .ToDictionaryAsync(r => r.TermId, ct).ConfigureAwait(false);

        var views = new List<SearchTermView>();
        foreach (var term in rows.GroupBy(r => r.TermId))
        {
            views.Add(await ViewAsync(Merge(term.ToList()), refinements.GetValueOrDefault(term.Key), ct).ConfigureAwait(false));
        }

        if (orphans)
        {
            var held = rows.Select(r => r.TermId).ToHashSet();
            views.AddRange(refinements.Values.Where(r => !held.Contains(r.TermId)).Select(Orphaned));
        }

        return views
            .OrderBy(v => v.EntityType, StringComparer.Ordinal)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The term <paramref name="termId"/>, or its refinement while no mapping gives it; null for neither.</summary>
    public async Task<SearchTermView?> FindAsync(Guid termId, CancellationToken ct)
    {
        var rows = await _db.DeliverySearchTerms.AsNoTracking().Where(t => t.TermId == termId).ToListAsync(ct).ConfigureAwait(false);
        var refinement = await _db.DeliverySearchTermRefinements.AsNoTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return refinement is null ? null : Orphaned(refinement);
        }

        return await ViewAsync(Merge(rows), refinement, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps what <paramref name="actor"/> made of the term: its name (unique among its entity type's terms), whether it is
    /// left out, the route it is searched through (one of its routes that can be searched) and a note. A request that keeps
    /// nothing of the term's own removes its refinement.
    /// </summary>
    /// <exception cref="DeliveryException">The term does not exist, or the request is not one a term takes; the message says why.</exception>
    public async Task<SearchTermView> RefineAsync(Guid termId, SearchTermRefinementRequest request, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var current = await FindAsync(termId, ct).ConfigureAwait(false);
        if (current is null || current.Orphan)
        {
            throw new DeliveryException(current is null
                ? $"No search term {termId} is extracted from the mappings of an active delivery flow."
                : $"The search term {current.Key} is no longer extracted from any mapping, so only its refinement can be removed.");
        }

        var name = Clean(request.Name, DeliverySearchTermRefinement.MaxNameLength, "name", newlines: false);
        var note = Clean(request.Note, DeliverySearchTermRefinement.MaxNoteLength, "note", newlines: true);
        if (name is not null && string.Equals(name, current.ColumnLabel, StringComparison.Ordinal))
        {
            name = null;
        }

        if (name is not null)
        {
            var others = await ListAsync(current.EntityType, orphans: false, ct).ConfigureAwait(false);
            if (others.FirstOrDefault(o => o.Id != termId && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
            {
                throw new DeliveryException($"The name '{name}' is the search term {taken.ColumnLabel}'s already; two terms of {current.EntityType} cannot share one.");
            }
        }

        var route = string.IsNullOrWhiteSpace(request.Route) ? null : request.Route.Trim();
        if (route is not null)
        {
            var picked = current.Routes.FirstOrDefault(r => string.Equals(r.Id, route, StringComparison.Ordinal))
                ?? throw new DeliveryException($"'{route}' is not a route of the search term {current.ColumnLabel}: {string.Join(", ", current.Routes.Select(r => r.Id))}.");
            if (picked.Problem is { } problem)
            {
                throw new DeliveryException($"The search term {current.ColumnLabel} cannot be searched through {picked.Target}: {problem}.");
            }
        }

        var row = await _db.DeliverySearchTermRefinements.AsTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        if (name is null && !request.Excluded && route is null && note is null)
        {
            if (row is not null)
            {
                _db.DeliverySearchTermRefinements.Remove(row);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return (await FindAsync(termId, ct).ConfigureAwait(false))!;
        }

        if (row is null)
        {
            row = new DeliverySearchTermRefinement { TermId = termId };
            _db.DeliverySearchTermRefinements.Add(row);
        }

        row.TermKey = current.Key;
        row.EntityType = current.EntityType;
        row.Name = name;
        row.Excluded = request.Excluded;
        row.Route = route;
        row.Note = note;
        row.UpdatedBy = actor.Length <= 200 ? actor : actor[..200];
        row.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await FindAsync(termId, ct).ConfigureAwait(false))!;
    }

    /// <summary>Removes what people made of the term, so it is searched as its mappings give it; false when there was nothing to remove.</summary>
    public async Task<bool> ResetAsync(Guid termId, CancellationToken ct)
    {
        var removed = await _db.DeliverySearchTermRefinements.Where(r => r.TermId == termId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>
    /// The condition the explorer asks of the records for <paramref name="asked"/> on the term <paramref name="termId"/>, in
    /// <paramref name="partition"/>: on the property its route fills, the values put through its mapping, and for a route
    /// through other records, the way the node reads those.
    /// </summary>
    /// <exception cref="DeliveryException">The term does not exist, is left out, cannot be searched, or a value cannot be carried.</exception>
    public async Task<ExplorerFilter> ResolveAsync(Guid termId, SearchTermCondition asked, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var rows = await _db.DeliverySearchTerms.AsNoTracking().Where(t => t.TermId == termId).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new DeliveryException($"No search term {termId} is extracted from the mappings of an active delivery flow; sync the repository, or pick the property itself.");
        }

        var refinement = await _db.DeliverySearchTermRefinements.AsNoTracking().FirstOrDefaultAsync(r => r.TermId == termId, ct).ConfigureAwait(false);
        var term = Merge(rows);
        var view = await ViewAsync(term, refinement, ct).ConfigureAwait(false);
        if (view.Excluded)
        {
            throw new DeliveryException($"{view.Name} is left out of the search; include it again on the Search terms page.");
        }

        var chosenAt = view.Route is { } routeId ? term.Routes.ToList().FindIndex(r => r.Route.Id == routeId) : -1;
        if (chosenAt < 0)
        {
            throw new DeliveryException($"{view.Name} cannot be searched: {view.Problem}.");
        }

        var chosen = term.Routes[chosenAt];

        var (mapping, fields) = await ClassifyAsync(chosen.Route, chosen.Repo, ct).ConfigureAwait(false);
        var template = mapping is null ? null : await TemplateAsync(mapping.Template, ct).ConfigureAwait(false);
        if (mapping is null || template is null)
        {
            throw new DeliveryException($"{view.Name} cannot be searched: {fields.Problem}.");
        }

        SearchTermValues values;
        try
        {
            values = SearchTermValues.For(mapping, template, partition);
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"{view.Name} cannot be searched in partition {partition}: {ex.Message}", ex);
        }

        return SearchTermResolver.Resolve(view.Name, term.Key, chosen.Route, fields, asked, values);
    }

    /// <summary>A term's rows from every repository that gives it: its key, every route once (the latest sync's), and every flow.</summary>
    private static Term Merge(IReadOnlyList<DeliverySearchTerm> rows)
    {
        var first = rows[0];
        var key = new SearchTermKey(first.System, first.EntityType, first.Dataset, first.Column);
        var routes = new Dictionary<string, (SearchRoute Route, Guid Repo, DateTime Seen)>(StringComparer.Ordinal);
        var flows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var route in SearchRoute.FromJson(row.RoutesJson))
            {
                if (!routes.TryGetValue(route.Id, out var held) || held.Seen < row.LastSeenUtc)
                {
                    routes[route.Id] = (route, row.RepoId, row.LastSeenUtc);
                }
            }

            foreach (var flow in Flows(row.FlowsJson))
            {
                flows.Add(flow);
            }
        }

        return new Term(first.TermId, key, routes.Values.Select(r => (r.Route, r.Repo)).ToList(), flows.ToList());
    }

    private async Task<SearchTermView> ViewAsync(Term term, DeliverySearchTermRefinement? refinement, CancellationToken ct)
    {
        var routes = new List<(SearchRoute Route, SearchRouteFields Fields)>(term.Routes.Count);
        foreach (var (route, repo) in term.Routes)
        {
            var (_, fields) = await ClassifyAsync(route, repo, ct).ConfigureAwait(false);
            routes.Add((route, fields));
        }

        var usable = routes.Where(r => r.Fields.Target is not null).Select(r => r.Route).ToHashSet();
        var chosen = SearchTermResolver.Preferred(routes.Select(r => r.Route).ToList(), usable.Contains, refinement?.Route);
        var views = routes
            .Select(r => View(r.Route, r.Fields))
            .OrderBy(v => chosen is not null && v.Id == chosen.Id ? 0 : 1)
            .ThenBy(v => v.Problem is null ? 0 : 1)
            .ThenBy(v => v.Target, StringComparer.Ordinal)
            .ToList();
        var problem = chosen is null
            ? (routes.FirstOrDefault().Fields?.Problem is { } first ? $"none of its routes can be searched; {first}" : "no route reaches the record")
            : null;
        return new SearchTermView
        {
            Id = term.Id,
            Key = term.Key.Text,
            System = term.Key.System,
            EntityType = term.Key.EntityType,
            Dataset = term.Key.Dataset,
            Column = term.Key.Column,
            ColumnLabel = term.Key.ColumnLabel,
            Name = refinement?.Name ?? term.Key.ColumnLabel,
            Renamed = refinement?.Name is not null,
            Excluded = refinement?.Excluded ?? false,
            Note = refinement?.Note,
            PickedRoute = refinement?.Route,
            Route = chosen?.Id,
            Routes = views,
            Flows = term.Flows,
            Mappings = term.Routes.SelectMany(r => r.Route.Mappings).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Problem = problem,
            Suggest = chosen is null ? null : Suggest(chosen, routes.First(r => r.Route.Id == chosen.Id).Fields),
            UpdatedBy = refinement?.UpdatedBy,
            UpdatedUtc = refinement?.UpdatedUtc,
        };
    }

    /// <summary>A refinement whose term no mapping gives any longer: its name and note, and no route.</summary>
    private static SearchTermView Orphaned(DeliverySearchTermRefinement refinement)
    {
        var parts = refinement.TermKey.Split('/');
        var (system, dataset, column) = parts.Length == 4 ? (parts[0], parts[2].Length == 0 ? null : parts[2], parts[3]) : (string.Empty, null, refinement.TermKey);
        var label = dataset is null ? column : $"{dataset}.{column}";
        return new SearchTermView
        {
            Id = refinement.TermId,
            Key = refinement.TermKey,
            System = system,
            EntityType = refinement.EntityType,
            Dataset = dataset,
            Column = column,
            ColumnLabel = label,
            Name = refinement.Name ?? label,
            Renamed = refinement.Name is not null,
            Excluded = refinement.Excluded,
            Note = refinement.Note,
            PickedRoute = refinement.Route,
            Problem = "no mapping of an active delivery flow reads this column any longer",
            Orphan = true,
            UpdatedBy = refinement.UpdatedBy,
            UpdatedUtc = refinement.UpdatedUtc,
        };
    }

    /// <summary>
    /// Where the values to pick from are read for a term searched through <paramref name="route"/>: its property, for a route
    /// whose written values are the ones typed (a copy, steps that keep the text); the property its records are found by, for a
    /// lookup or a search; none for any other.
    /// </summary>
    private static SearchTermSuggest? Suggest(SearchRoute route, SearchRouteFields fields)
    {
        switch (route.Kind)
        {
            case SearchRouteKind.Copy:
            case SearchRouteKind.Steps when route.Keeps == SearchRouteKeeps.Text:
                return fields.Target is { } target ? new SearchTermSuggest(null, target.Path, IndexName(target.Index), target.NestedPath) : null;
            case SearchRouteKind.Lookup or SearchRouteKind.Search when fields.Match is { Count: > 0 } match:
                return new SearchTermSuggest(route.Find!.Kind, match[0].Path, IndexName(match[0].Index), match[0].NestedPath);
            default:
                return null;
        }
    }

    private static SearchRouteView View(SearchRoute route, SearchRouteFields fields) => new()
    {
        Id = route.Id,
        Target = route.Target,
        Path = route.Path,
        Kind = route.Kind.ToString().ToLowerInvariant(),
        How = SearchTermResolver.Spoken(route.Kind),
        Steps = route.Steps,
        Mappings = route.Mappings,
        Location = route.Location,
        Find = route.Find,
        KeyColumns = route.Key?.Columns,
        When = route.When,
        Description = route.Description,
        Index = fields.Target is { } target ? IndexName(target.Index) : null,
        Nested = fields.Target?.NestedPath,
        Conditions = SearchTermResolver.Conditions(route, fields).Select(ConditionName).ToList(),
        Problem = fields.Problem,
    };

    /// <summary>How the route compares, as the schemas say it is indexed, with the mapping it reads; or why it cannot be searched.</summary>
    private async Task<(MappingDefinition? Mapping, SearchRouteFields Fields)> ClassifyAsync(SearchRoute route, Guid repo, CancellationToken ct)
    {
        if (route.Problem is { } problem)
        {
            return (null, SearchRouteFields.Refused(problem));
        }

        var reference = route.Mappings[^1];
        var (mapping, mappingProblem) = await MappingAsync(repo, reference, ct).ConfigureAwait(false);
        if (mapping is null)
        {
            return (null, SearchRouteFields.Refused(mappingProblem!));
        }

        var template = await TemplateAsync(mapping.Template, ct).ConfigureAwait(false);
        if (template is null)
        {
            return (mapping, SearchRouteFields.Refused($"the template {mapping.Template.Kind} version {mapping.Template.Version} that {reference} pins is not saved; save it on the Templates page"));
        }

        SchemaSnapshot? found = null;
        if (route.Find is { } find)
        {
            if (route.Kind == SearchRouteKind.Search && mapping.Searches.TryGetValue(find.Name, out var search) && search.Schema is { } pinned)
            {
                found = await TemplateAsync(pinned, ct).ConfigureAwait(false);
            }
            else
            {
                _saved ??= await _templates.ListAsync(ct).ConfigureAwait(false);
                var matching = DimensionBlueprints.Matching(find.Kind, _saved);
                found = matching.Count == 0 ? null : await TemplateAsync(matching[0].Reference, ct).ConfigureAwait(false);
            }
        }

        return (mapping, SearchTermResolver.Classify(route, template, found));
    }

    private async Task<(MappingDefinition? Mapping, string? Problem)> MappingAsync(Guid repo, string reference, CancellationToken ct)
    {
        if (_mappings.TryGetValue((repo, reference), out var known))
        {
            return known;
        }

        var row = await _db.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repo && m.Reference == reference && m.Status == "valid")
            .Select(m => new { m.Yaml, m.RelativePath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        (MappingDefinition?, string?) found;
        if (row is null)
        {
            found = (null, $"its mapping {reference} is no longer in the catalog; sync the repository again");
        }
        else
        {
            try
            {
                found = (_documents.ParseMapping(row.Yaml, row.RelativePath), null);
            }
            catch (FlowValidationException ex)
            {
                found = (null, $"its mapping {reference} no longer reads: {ex.Message}");
            }
        }

        _mappings[(repo, reference)] = found;
        return found;
    }

    private async Task<SchemaSnapshot?> TemplateAsync(TemplateReference reference, CancellationToken ct)
    {
        if (!_schemas.TryGetValue(reference, out var schema))
        {
            schema = await _templates.LoadAsync(reference, ct).ConfigureAwait(false);
            _schemas[reference] = schema;
        }

        return schema;
    }

    private static IReadOnlyList<string> Flows(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Text a person gave for a term, trimmed and checked; null for none.</summary>
    private static string? Clean(string? text, int max, string what, bool newlines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > max)
        {
            throw new DeliveryException($"A term's {what} is at most {max} characters; this one is {trimmed.Length}.");
        }

        if (trimmed.Any(c => char.IsControl(c) && !(newlines && c is '\n' or '\r' or '\t')))
        {
            throw new DeliveryException($"A term's {what} holds a character that cannot be printed.");
        }

        return trimmed;
    }

    /// <summary>How the platform indexes a property, as the explorer names it.</summary>
    public static string IndexName(OsduFieldIndex index) => index.ToString().ToLowerInvariant();

    /// <summary>A condition as the explorer names it: <c>isNot</c>, <c>startsWith</c>.</summary>
    public static string ConditionName(ExplorerCondition condition) => JsonNamingPolicy.CamelCase.ConvertName(condition.ToString());

    /// <summary>A term as its rows give it: its identity and key, every route with the repository its mapping is read from, and the flows that read it.</summary>
    private sealed record Term(Guid Id, SearchTermKey Key, IReadOnlyList<(SearchRoute Route, Guid Repo)> Routes, IReadOnlyList<string> Flows);
}
