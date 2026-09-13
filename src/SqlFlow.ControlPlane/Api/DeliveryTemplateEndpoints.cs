using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A saved template version, and how many synced mappings pin it.</summary>
public sealed record DeliveryTemplateDto(string Kind, string Version, DateTime CapturedUtc, string CapturedBy, string Origin, int PinnedBy);

/// <summary>One template variable as the Templates page and the mapping builder show it.</summary>
public sealed record DeliveryTemplateVariableDto(
    string Path, string Shape, string Type, string? ItemType, string? Format, bool Required, string Role, IReadOnlyList<string> Relationships,
    string? Pattern, string? UnitContext, string? Title, string? Description, string? KeyValueType, bool Nested, IReadOnlyList<string> CacheTypes);

/// <summary>
/// A template laid out variable by variable. <c>Saved</c> is null for a schema being looked at before it is saved;
/// <c>CacheTypes</c> on a variable names the repository's cached types it can be read from, when a repository was given.
/// </summary>
public sealed record DeliveryTemplateDetailDto(
    string Kind, string Version, string? Title, string? Description, DeliveryTemplateDto? Saved, IReadOnlyList<DeliveryTemplateVariableDto> Variables);

/// <summary>A search of OSDU's schemas, run on a node through the delivery flow's OSDU connection.</summary>
public sealed record DeliverySchemaSearchRequest(
    Guid PipelineId, string? Authority, string? Source, string? EntityType, string? Status, bool? LatestVersion, int? Limit, int? Offset);

/// <summary>Fetches one kind's schema from OSDU on a node, through the delivery flow's OSDU connection.</summary>
public sealed record DeliverySchemaFetchRequest(Guid PipelineId, string Kind);

/// <summary>A bundled schema to lay out as a template without saving it.</summary>
public sealed record DeliveryTemplatePreviewRequest(string Kind, JsonElement Schema, Guid? RepoId);

/// <summary>A bundled schema to save as a template version, and where it came from.</summary>
public sealed record DeliveryTemplateSaveRequest(string Kind, JsonElement Schema, string Origin);

/// <summary>What saving did: <c>created</c> for a new version, <c>unchanged</c> for one already saved.</summary>
public sealed record DeliveryTemplateSavedDto(DeliveryTemplateDto Template, string Outcome);

/// <summary>A delivery flow of a repository, as the builder and the Templates page offer it: its connection and what it renders with.</summary>
public sealed record DeliveryBuilderFlowDto(Guid PipelineId, string Name, string Mapping, IReadOnlyDictionary<string, string> Parameters, string Endpoint);

/// <summary>A cached type of a repository's cache.</summary>
public sealed record DeliveryCachedTypeDto(string Name, string EntityType, IReadOnlyList<string> Fields);

/// <summary>A repository as the mapping builder offers it: the source a proposal is opened against, its cache and its delivery flows.</summary>
public sealed record DeliveryBuilderRepoDto(
    Guid RepoId, string Name, Guid? SourceId, string? SourceBranch, string? CacheVersion, IReadOnlyList<DeliveryCachedTypeDto> CacheTypes,
    IReadOnlyList<DeliveryBuilderFlowDto> Flows);

/// <summary>Starts a mapping for a repository and a saved template version.</summary>
public sealed record DeliveryMappingDraftRequest(Guid RepoId, string Kind, string Version, string Name, string MappingVersion, string System);

/// <summary>A draft to write as YAML and check; <c>Parameters</c> are the values the check renders the fixtures and static ids with.</summary>
public sealed record DeliveryMappingComposeRequest(Guid? RepoId, MappingDraft Draft, IReadOnlyDictionary<string, string>? Parameters);

/// <summary>The draft as YAML, what the checks found, and whether the mapping loads and passes the preflight.</summary>
public sealed record DeliveryMappingComposeResult(string Yaml, IReadOnlyList<MappingDraftIssue> Issues, bool Valid);

/// <summary>A mapping document to open in the builder.</summary>
public sealed record DeliveryMappingParseRequest(string Yaml, string? Path);

public sealed record DeliveryMappingParseResult(MappingDraft? Draft, IReadOnlyList<MappingDraftIssue> Issues);

/// <summary>
/// Templates and the mapping builder (docs/delivery/mapping-templates.md). Reading and laying out templates, drafting and
/// checking a mapping are reads; browsing OSDU runs on a node under the delivery flow's credentials, like every other
/// target operation; saving and deleting a template changes what mappings can pin, so it is an author action.
/// </summary>
public static class DeliveryTemplateEndpoints
{
    /// <summary>Repositories the builder lists at once.</summary>
    private const int MaxBuilderRepos = 200;

    public static RouteGroupBuilder MapDeliveryTemplateReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapGet("/templates", ListTemplatesAsync).WithName("ListDeliveryTemplates");
        delivery.MapGet("/templates/detail", GetTemplateAsync).WithName("GetDeliveryTemplate");
        delivery.MapGet("/templates/schema", GetTemplateSchemaAsync).WithName("GetDeliveryTemplateSchema");
        delivery.MapPost("/templates/preview", PreviewTemplateAsync).WithName("PreviewDeliveryTemplate");
        delivery.MapGet("/mapping-builder/repos", ListBuilderReposAsync).WithName("ListDeliveryMappingBuilderRepos");
        delivery.MapPost("/mapping-builder/draft", DraftMappingAsync).WithName("DraftDeliveryMapping");
        delivery.MapPost("/mapping-builder/compose", ComposeMappingAsync).WithName("ComposeDeliveryMapping");
        delivery.MapPost("/mapping-builder/parse", ParseMappingAsync).WithName("ParseDeliveryMapping");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryTemplateOperateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapPost("/templates/search", SearchSchemasAsync).WithName("SearchDeliveryOsduSchemas");
        delivery.MapPost("/templates/fetch", FetchSchemaAsync).WithName("FetchDeliveryOsduSchema");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryTemplateAuthorEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapPost("/templates", SaveTemplateAsync).WithName("SaveDeliveryTemplate");
        delivery.MapDelete("/templates", DeleteTemplateAsync).WithName("DeleteDeliveryTemplate");
        return group;
    }

    private static async Task<Ok<IReadOnlyList<DeliveryTemplateDto>>> ListTemplatesAsync(ITemplateStore templates, CatalogDbContext db, CancellationToken ct)
    {
        var saved = await templates.ListAsync(ct).ConfigureAwait(false);
        var pins = await PinsAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryTemplateDto>>(saved.Select(t => ToDto(t, pins)).ToList());
    }

    private static async Task<Results<Ok<DeliveryTemplateDetailDto>, ProblemHttpResult>> GetTemplateAsync(
        string? kind, string? version, Guid? repoId, ITemplateStore templates, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(version))
        {
            return Problem("Name the template with kind and version.", StatusCodes.Status400BadRequest);
        }

        var reference = new TemplateReference(kind.Trim(), version.Trim());
        var schema = await templates.LoadAsync(reference, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return Problem($"There is no saved template {reference}.", StatusCodes.Status404NotFound, "Not found");
        }

        var info = (await templates.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Reference == reference);
        var pins = await PinsAsync(db, ct).ConfigureAwait(false);
        var cache = repoId is { } r ? await CatalogCacheReader.TypesAsync(db, r, ct).ConfigureAwait(false) : [];
        return TypedResults.Ok(Detail(OsduTemplate.From(schema), info is null ? null : ToDto(info, pins), cache));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetTemplateSchemaAsync(
        string? kind, string? version, ITemplateStore templates, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(version))
        {
            return Problem("Name the template with kind and version.", StatusCodes.Status400BadRequest);
        }

        var reference = new TemplateReference(kind.Trim(), version.Trim());
        var schema = await templates.LoadAsync(reference, ct).ConfigureAwait(false);
        return schema is null
            ? Problem($"There is no saved template {reference}.", StatusCodes.Status404NotFound, "Not found")
            : TypedResults.Text(Delivery.Json.CanonicalJson.Pretty(schema.Root), "application/json");
    }

    private static async Task<Results<Ok<DeliveryTemplateDetailDto>, ProblemHttpResult>> PreviewTemplateAsync(
        DeliveryTemplatePreviewRequest request, ITemplateStore templates, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind))
        {
            return Problem("A preview needs the kind and the bundled schema.", StatusCodes.Status400BadRequest);
        }

        SchemaSnapshot schema;
        try
        {
            schema = TemplateSources.FromBundledJson(request.Schema.GetRawText(), request.Kind.Trim(), clock.GetUtcNow(), "the schema");
        }
        catch (Exception ex) when (ex is DeliveryException or FlowValidationException)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Not a record schema");
        }

        var info = (await templates.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.Kind == schema.Kind && t.Version == schema.Version);
        var pins = await PinsAsync(db, ct).ConfigureAwait(false);
        var cache = request.RepoId is { } r ? await CatalogCacheReader.TypesAsync(db, r, ct).ConfigureAwait(false) : [];
        return TypedResults.Ok(Detail(OsduTemplate.From(schema), info is null ? null : ToDto(info, pins), cache));
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> SearchSchemasAsync(
        DeliverySchemaSearchRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null)
        {
            return Problem("A search needs the delivery flow whose OSDU connection to use.", StatusCodes.Status400BadRequest);
        }

        if (request.Limit is < 1 or > OsduSchemaQuery.MaxLimit || request.Offset is < 0)
        {
            return Problem($"A search returns between 1 and {OsduSchemaQuery.MaxLimit} schemas per page, from an offset of zero or more.", StatusCodes.Status400BadRequest);
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, request.PipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        Put(arguments, "authority", request.Authority);
        Put(arguments, "source", request.Source);
        Put(arguments, "entityType", request.EntityType);
        Put(arguments, "status", request.Status);
        arguments["latestVersion"] = request.LatestVersion == false ? "false" : "true";
        arguments["limit"] = (request.Limit ?? OsduSchemaQuery.MaxLimit).ToString(System.Globalization.CultureInfo.InvariantCulture);
        arguments["offset"] = (request.Offset ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await DeliveryEndpoints.EnqueueOperationAsync(db, dispatcher, flow, SearchSchemasOperation.OperationName, arguments, user, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> FetchSchemaAsync(
        DeliverySchemaFetchRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind) || request.Kind.Trim().Split(':').Length != 4)
        {
            return Problem("A fetch needs the kind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, request.PipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = request.Kind.Trim() };
        return await DeliveryEndpoints.EnqueueOperationAsync(db, dispatcher, flow, FetchSchemaOperation.OperationName, arguments, user, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<DeliveryTemplateSavedDto>, ProblemHttpResult>> SaveTemplateAsync(
        DeliveryTemplateSaveRequest request, ITemplateStore templates, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Origin))
        {
            return Problem("Saving a template needs the kind, the bundled schema, and where it came from.", StatusCodes.Status400BadRequest);
        }

        SchemaSnapshot schema;
        try
        {
            schema = TemplateSources.FromBundledJson(request.Schema.GetRawText(), request.Kind.Trim(), clock.GetUtcNow(), "the schema");
        }
        catch (Exception ex) when (ex is DeliveryException or FlowValidationException)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "Not a record schema");
        }

        var saved = await templates.SaveAsync(schema, request.Origin.Trim(), RequestActor.Label(user), ct).ConfigureAwait(false);
        var pins = await PinsAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryTemplateSavedDto(
            ToDto(saved.Template, pins), saved.Outcome == TemplateSaveOutcome.Created ? "created" : "unchanged"));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTemplateAsync(
        string? kind, string? version, ITemplateStore templates, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(version))
        {
            return Problem("Name the template with kind and version.", StatusCodes.Status400BadRequest);
        }

        var reference = new TemplateReference(kind.Trim(), version.Trim());
        if (await templates.LoadAsync(reference, ct).ConfigureAwait(false) is null)
        {
            return Problem($"There is no saved template {reference}.", StatusCodes.Status404NotFound, "Not found");
        }

        try
        {
            await templates.DeleteAsync(reference, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return Problem(ex.Message, StatusCodes.Status409Conflict, "Template in use");
        }

        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<DeliveryBuilderRepoDto>>> ListBuilderReposAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var repos = await db.Repos.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).Take(MaxBuilderRepos).ToListAsync(ct).ConfigureAwait(false);
        var sources = await db.RepoSources.AsNoTracking().Select(s => new { s.Id, s.Name, s.Branch }).ToListAsync(ct).ConfigureAwait(false);
        var sourceByRepo = new Dictionary<Guid, (Guid Id, string Branch)>();
        foreach (var source in sources)
        {
            // A tracked source's repository is identified by the source's name, exactly as the managed sync derives it.
            sourceByRepo.TryAdd(FlowIdentity.FromName(source.Name), (source.Id, source.Branch));
        }

        var ids = repos.Select(r => r.Id).ToList();
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => ids.Contains(p.RepoId) && p.Active && p.Kind == FlowDefinition.FlowTypeName)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.RepoId, p.Name, p.Yaml, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<DeliveryBuilderRepoDto>(repos.Count);
        foreach (var repo in repos)
        {
            var flows = new List<DeliveryBuilderFlowDto>();
            foreach (var pipeline in pipelines.Where(p => p.RepoId == repo.Id))
            {
                try
                {
                    var flow = documents.ParseFlow(pipeline.Yaml, pipeline.RelativePath);
                    flows.Add(new DeliveryBuilderFlowDto(pipeline.Id, pipeline.Name, flow.Render.Mapping, flow.Render.Parameters, flow.Target.Endpoint));
                }
                catch (FlowValidationException)
                {
                    // A flow whose catalog copy does not parse is reported on its own page; it offers no connection here.
                }
            }

            var cache = await CatalogCacheReader.TypesAsync(db, repo.Id, ct).ConfigureAwait(false);
            var resolved = await CacheVersions.ResolveAsync(db, repo.Id, version: null, ct).ConfigureAwait(false);
            var version = resolved.Count > 0 ? resolved[0].Version : null;
            var source = sourceByRepo.TryGetValue(repo.Id, out var s) ? s : ((Guid Id, string Branch)?)null;
            result.Add(new DeliveryBuilderRepoDto(
                repo.Id, repo.Name, source?.Id, source?.Branch, version,
                cache.Select(c => new DeliveryCachedTypeDto(c.Name, c.EntityType, c.Fields)).ToList(), flows));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryBuilderRepoDto>>(result);
    }

    private static async Task<Results<Ok<MappingDraft>, ProblemHttpResult>> DraftMappingAsync(
        DeliveryMappingDraftRequest request, ITemplateStore templates, CatalogDbContext db, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Version))
        {
            return Problem("A draft needs the saved template version it fills.", StatusCodes.Status400BadRequest);
        }

        var reference = new TemplateReference(request.Kind.Trim(), request.Version.Trim());
        var schema = await templates.LoadAsync(reference, ct).ConfigureAwait(false);
        if (schema is null)
        {
            return Problem($"There is no saved template {reference}.", StatusCodes.Status404NotFound, "Not found");
        }

        var cache = await CatalogCacheReader.TypesAsync(db, request.RepoId, ct).ConfigureAwait(false);
        return TypedResults.Ok(MappingBuilder.Draft(OsduTemplate.From(schema), cache, request.Name, request.MappingVersion, request.System));
    }

    private static async Task<Results<Ok<DeliveryMappingComposeResult>, ProblemHttpResult>> ComposeMappingAsync(
        DeliveryMappingComposeRequest request, ITemplateStore templates, CatalogCacheReader cacheReader, CatalogDbContext db,
        DeliveryDocumentLoader documents, CancellationToken ct)
    {
        if (request?.Draft is null)
        {
            return Problem("Compose needs the draft.", StatusCodes.Status400BadRequest);
        }

        var draft = request.Draft;
        var issues = MappingBuilder.Incomplete(draft).ToList();
        var yaml = MappingBuilder.ToYaml(draft);
        if (issues.Any(IsError))
        {
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        MappingDefinition mapping;
        try
        {
            mapping = documents.ParseMapping(yaml, $"mappings/{draft.Name}@{draft.Version}.yaml");
        }
        catch (FlowValidationException ex)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message));
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        var schema = await templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false);
        if (schema is null)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, $"The mapping pins template {mapping.Template}, which is not saved. Save it on the Templates page first."));
            return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, false));
        }

        var references = request.RepoId is { } repoId ? await cacheReader.CurrentAsync(db, repoId, ct).ConfigureAwait(false) : ReferenceSnapshot.Empty;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in mapping.Parameters)
        {
            if (declared.Default is not null)
            {
                parameters[name] = declared.Default;
            }
        }

        foreach (var (name, value) in request.Parameters ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[name] = value.Trim();
            }
        }

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            ReferenceSnapshotVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = parameters,
        };

        try
        {
            foreach (var issue in Preflight.Check(mapping, schema, references, context, dropColumns: null))
            {
                issues.Add(new MappingDraftIssue(
                    issue.Severity == IssueSeverity.Error ? MappingDraftIssue.ErrorSeverity : MappingDraftIssue.WarningSeverity, issue.Message));
            }
        }
        catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
        {
            issues.Add(new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message));
        }

        return TypedResults.Ok(new DeliveryMappingComposeResult(yaml, issues, !issues.Any(IsError)));
    }

    private static Ok<DeliveryMappingParseResult> ParseMappingAsync(DeliveryMappingParseRequest request, DeliveryDocumentLoader documents)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Yaml))
        {
            return TypedResults.Ok(new DeliveryMappingParseResult(null, [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, "There is no mapping document to open.")]));
        }

        try
        {
            var mapping = documents.ParseMapping(request.Yaml, string.IsNullOrWhiteSpace(request.Path) ? "mapping.yaml" : request.Path);
            return TypedResults.Ok(new DeliveryMappingParseResult(MappingBuilder.FromDefinition(mapping), []));
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.Ok(new DeliveryMappingParseResult(null, [new MappingDraftIssue(MappingDraftIssue.ErrorSeverity, ex.Message)]));
        }
    }

    private static bool IsError(MappingDraftIssue issue) => issue.Severity == MappingDraftIssue.ErrorSeverity;

    private static async Task<Dictionary<(string Kind, string Version), int>> PinsAsync(CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.DeliveryMappings.AsNoTracking()
            .Where(m => m.TemplateVersion != string.Empty)
            .GroupBy(m => new { m.Kind, m.TemplateVersion })
            .Select(g => new { g.Key.Kind, g.Key.TemplateVersion, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => (r.Kind, r.TemplateVersion), r => r.Count);
    }

    private static DeliveryTemplateDto ToDto(TemplateInfo info, Dictionary<(string Kind, string Version), int> pins)
        => new(info.Kind, info.Version, info.CapturedUtc, info.CapturedBy, info.Origin, pins.GetValueOrDefault((info.Kind, info.Version)));

    private static DeliveryTemplateDetailDto Detail(OsduTemplate template, DeliveryTemplateDto? saved, IReadOnlyList<CachedTypeInfo> cache)
        => new(
            template.Kind,
            template.Version,
            template.Title,
            template.Description,
            saved,
            template.Variables.Select(v => new DeliveryTemplateVariableDto(
                v.Path.Text, v.Shape.ToString(), v.Type, v.ItemType, v.Format, v.Required, v.Role.ToString(), v.Relationships, v.Pattern, v.UnitContext,
                v.Title, v.Description, v.KeyValueType, v.Nested, MappingBuilder.CacheTypesFor(v, cache).Select(c => c.Name).ToList())).ToList());

    private static void Put(Dictionary<string, string> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments[name] = value.Trim();
        }
    }

    private static ProblemHttpResult Problem(string detail, int status, string title = "Bad request")
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
