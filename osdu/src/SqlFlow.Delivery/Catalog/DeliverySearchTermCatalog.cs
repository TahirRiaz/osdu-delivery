using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.SearchTerms;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The search terms of a repository (osdu/docs/search-terms.md), extracted from the mappings its active delivery flows pin:
/// for every column those mappings read, every route by which it reaches the records they render
/// (<see cref="SearchTermCompiler"/>), with the flows that read it. The repository sync writes them after the mappings,
/// the cache declarations and the interfaces it reads them from, in the same transaction; the control plane writes them
/// again when it starts, so a module upgraded since the last sync describes its terms as this version does. What a person
/// made of a term is never touched here: it is kept apart, by the term's identity.
/// </summary>
public static class DeliverySearchTermCatalog
{
    /// <summary>
    /// Writes the search terms of <paramref name="repoId"/> into <paramref name="context"/> from the rows the context holds:
    /// the repository's active interfaces, the valid mappings they pin, and the cached types the cache flows declare. A term
    /// no mapping gives any longer is removed; the counts say what changed. Nothing is read from the repository's files.
    /// </summary>
    public static async Task<(int Added, int Updated, int Unchanged, int Removed)> ReconcileAsync(
        OsduDbContext context, Guid repoId, DeliveryDocumentLoader documents, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(warnings);

        var interfaces = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId && i.Active)
            .Select(i => new { i.FlowName, i.MappingReference })
            .ToListAsync(ct).ConfigureAwait(false);
        var flowsByMapping = interfaces
            .GroupBy(i => i.MappingReference, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.FlowName).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var pinned = flowsByMapping.Keys.ToList();
        var rows = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.Status == "valid" && pinned.Contains(m.Reference))
            .Select(m => new { m.Reference, m.Yaml, m.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var mappings = new List<MappingDefinition>(rows.Count);
        foreach (var row in rows.OrderBy(r => r.Reference, StringComparer.Ordinal))
        {
            try
            {
                mappings.Add(documents.ParseMapping(row.Yaml, row.RelativePath));
            }
            catch (FlowValidationException ex)
            {
                // A mapping row is valid as the sync found it; one that no longer parses (a module upgraded since) gives no terms.
                warnings.Add($"{row.RelativePath}: its search terms are left out, since the mapping no longer reads: {ex.Message}");
            }
        }

        var cacheTypes = await CacheTypesAsync(context, repoId, warnings, ct).ConfigureAwait(false);
        var compiled = SearchTermCompiler.Compile(mappings, name => cacheTypes.GetValueOrDefault(name));

        var existing = await context.DeliverySearchTerms
            .Where(t => t.RepoId == repoId)
            .AsTracking()
            .ToDictionaryAsync(t => t.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0, removed = 0;
        foreach (var term in compiled)
        {
            var key = term.Key;
            if (Unfit(key) is { } why)
            {
                warnings.Add($"The search term {key.Text} is left out: {why}.");
                continue;
            }

            var id = FlowIdentity.FromName($"delivery-search-term/{repoId:N}/{key.Text}");
            seen.Add(id);
            var flows = term.Routes
                .SelectMany(r => r.Mappings)
                .SelectMany(m => flowsByMapping.GetValueOrDefault(m) ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var routesJson = SearchRoute.ToJson(term.Routes);
            var flowsJson = JsonSerializer.Serialize(flows);
            if (!existing.TryGetValue(id, out var row))
            {
                context.DeliverySearchTerms.Add(new DeliverySearchTerm
                {
                    Id = id,
                    RepoId = repoId,
                    TermId = key.Id,
                    TermKey = key.Text,
                    System = key.System,
                    EntityType = key.EntityType,
                    Dataset = key.Dataset,
                    Column = key.Column,
                    RoutesJson = routesJson,
                    FlowsJson = flowsJson,
                    FirstSeenUtc = nowUtc,
                    LastSeenUtc = nowUtc,
                });
                added++;
                continue;
            }

            row.LastSeenUtc = nowUtc;
            if (row.RoutesJson == routesJson && row.FlowsJson == flowsJson && row.Column == key.Column)
            {
                unchanged++;
                continue;
            }

            row.RoutesJson = routesJson;
            row.FlowsJson = flowsJson;
            row.Column = key.Column;
            updated++;
        }

        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                context.DeliverySearchTerms.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return (added, updated, unchanged, removed);
    }

    /// <summary>
    /// The cached types the mappings' lookups read, by name, as the cache flows declare them: the repository's own first,
    /// then any other's, each named by the first flow that declares it. A declaration whose fields do not read is passed
    /// over with a warning.
    /// </summary>
    private static async Task<Dictionary<string, SearchCacheType>> CacheTypesAsync(
        OsduDbContext context, Guid repoId, ICollection<string> warnings, CancellationToken ct)
    {
        var declarations = await context.DeliveryCacheDefinitions.AsNoTracking()
            .Select(c => new { c.RepoId, c.FlowName, c.Name, c.Origin, c.Kind, c.FieldsJson, c.Scope })
            .ToListAsync(ct).ConfigureAwait(false);
        var types = new Dictionary<string, SearchCacheType>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in declarations
            .OrderBy(c => c.RepoId == repoId ? 0 : 1)
            .ThenBy(c => c.FlowName, StringComparer.Ordinal)
            .ThenBy(c => c.Scope, StringComparer.Ordinal))
        {
            if (types.ContainsKey(declaration.Name))
            {
                continue;
            }

            try
            {
                var fields = OsduCacheStore.ParseFields(declaration.FieldsJson, declaration.FlowName, declaration.Name);
                types[declaration.Name] = new SearchCacheType(declaration.Name, declaration.Origin, declaration.Kind, fields);
            }
            catch (DeliveryException ex)
            {
                warnings.Add($"The cached type {declaration.Name} of {declaration.FlowName} is not read for search terms: {ex.Message}");
            }
        }

        return types;
    }

    /// <summary>Why a term's key does not fit the table's columns, or null when it does.</summary>
    private static string? Unfit(SearchTermKey key)
    {
        if (key.Text.Length > DeliveryModel.SearchTermKeyLength)
        {
            return $"its key is {key.Text.Length} characters, and at most {DeliveryModel.SearchTermKeyLength} are kept";
        }

        if (key.System.Length > DeliverySearchTerm.MaxSystemLength)
        {
            return $"its source system is longer than {DeliverySearchTerm.MaxSystemLength} characters";
        }

        if (key.EntityType.Length > DeliverySearchTerm.MaxEntityTypeLength)
        {
            return $"its entity type is longer than {DeliverySearchTerm.MaxEntityTypeLength} characters";
        }

        if (key.Dataset is { Length: > DeliverySearchTerm.MaxDatasetLength })
        {
            return $"its dataset is longer than {DeliverySearchTerm.MaxDatasetLength} characters";
        }

        return key.Column.Length > DeliverySearchTerm.MaxColumnLength ? $"its column is longer than {DeliverySearchTerm.MaxColumnLength} characters" : null;
    }
}
