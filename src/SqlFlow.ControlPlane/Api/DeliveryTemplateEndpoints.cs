using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
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

/// <summary>A release of the OSDU data definitions: its tag, the commit it names, when that was committed, and its schema folder on the web.</summary>
public sealed record DeliveryOsduReleaseDto(string Name, string Commit, DateTimeOffset? PublishedUtc, Uri WebUrl, bool Local);

/// <summary>The releases of the OSDU data definitions, newest first, each marked when it is in the local copy, and when the list was read from the repository.</summary>
public sealed record DeliveryOsduReleasesDto(DateTimeOffset? SyncedUtc, IReadOnlyList<DeliveryOsduReleaseDto> Releases);

/// <summary>A sync: the release to have in the local copy afterwards, the newest when none is named.</summary>
public sealed record DeliveryOsduSyncRequest(string? Release);

/// <summary>What a sync did: the release list as read just now, and the releases it downloaded.</summary>
public sealed record DeliveryOsduSyncDto(DateTimeOffset SyncedUtc, IReadOnlyList<DeliveryOsduReleaseDto> Releases, IReadOnlyList<string> Downloaded);

/// <summary>A record schema a release publishes, with its file in the release and the file's page on the web.</summary>
public sealed record DeliveryOsduSchemaDto(string Kind, string EntityType, string Version, string? Status, string Path, Uri WebUrl);

/// <summary>Every record schema one release of the OSDU data definitions publishes.</summary>
public sealed record DeliveryOsduSchemaIndexDto(DeliveryOsduReleaseDto Release, IReadOnlyList<DeliveryOsduSchemaDto> Schemas);

/// <summary>
/// A kind's schema bundled from a release of the OSDU data definitions: <c>Version</c> is the template version it saves as,
/// <c>Origin</c> what the saved template records as where it came from. Nothing is saved.
/// </summary>
public sealed record DeliveryOsduSchemaFileDto(
    string Kind, string Version, DeliveryOsduReleaseDto Release, string Path, Uri WebUrl, string Origin, JsonObject Schema);

/// <summary>
/// One side of a comparison: a kind's version in a release, the status the release gives it, the template version it
/// saves as, and its schema file exactly as the release publishes it.
/// </summary>
public sealed record DeliveryOsduComparisonSideDto(
    string Kind, DeliveryOsduReleaseDto Release, string Path, Uri WebUrl, string? Status, string TemplateVersion, string FileText);

/// <summary>A field of a variable that differs between the versions, its value in each, and what the difference means for a mapping.</summary>
public sealed record DeliveryTemplateFieldChangeDto(string Field, string? Before, string? After, string Impact);

/// <summary>A variable that differs between the versions: <c>Added</c>, <c>Removed</c> or <c>Changed</c>, and <c>Breaking</c>, <c>Additive</c> or <c>Wording</c>.</summary>
public sealed record DeliveryTemplateVariableChangeDto(string Path, string Change, string Impact, string Role, IReadOnlyList<DeliveryTemplateFieldChangeDto> Fields);

/// <summary>
/// A shared schema file the two versions refer to whose published text differs: its name without the version, and its path,
/// link and text on each side (null where a version does not refer to it).
/// </summary>
public sealed record DeliveryOsduReferencedFileDto(string Name, string? FromPath, string? ToPath, Uri? FromWebUrl, Uri? ToWebUrl, string? FromText, string? ToText);

/// <summary>
/// Two versions of a kind from the OSDU data definitions compared: whether the published files are the same, or differ
/// only in their own version identifiers, whether they save as the same template, how many variable changes of each
/// impact there are, every variable that differs, and the shared schema files they refer to that differ, with how many
/// are the same.
/// </summary>
public sealed record DeliveryOsduComparisonDto(
    DeliveryOsduComparisonSideDto From, DeliveryOsduComparisonSideDto To, bool SameFile, bool OnlyIdentifiersDiffer, bool SameTemplate,
    int Breaking, int Additive, int Wording, int Unchanged, IReadOnlyList<DeliveryTemplateVariableChangeDto> Changes,
    int SameReferencedFiles, IReadOnlyList<DeliveryOsduReferencedFileDto> ReferencedFiles);

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
/// Templates and the mapping builder (docs/delivery/mapping-templates.md). Reading and laying out templates, browsing the
/// OSDU data definitions (the public repository of OSDU schemas, which needs no credential), drafting and checking a
/// mapping are reads; saving and deleting a template changes what mappings can pin, so it is an author action.
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
        delivery.MapGet("/templates/osdu/releases", ListOsduReleasesAsync).WithName("ListDeliveryOsduReleases");
        delivery.MapGet("/templates/osdu/schemas", ListOsduSchemasAsync).WithName("ListDeliveryOsduSchemas");
        delivery.MapGet("/templates/osdu/schema", GetOsduSchemaAsync).WithName("GetDeliveryOsduSchema");
        delivery.MapGet("/templates/osdu/compare", CompareOsduSchemasAsync).WithName("CompareDeliveryOsduSchemas");
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
        delivery.MapPost("/templates/osdu/sync", SyncOsduAsync).WithName("SyncDeliveryOsduDataDefinitions");
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

    private static async Task<Results<Ok<DeliveryOsduReleasesDto>, ProblemHttpResult>> ListOsduReleasesAsync(
        OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var releases = await definitions.ReleasesAsync(ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduReleasesDto(definitions.SyncedUtc, releases.Select(r => Release(definitions, r)).ToList()));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduSchemaIndexDto>, ProblemHttpResult>> ListOsduSchemasAsync(
        string? release, OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var index = await definitions.IndexAsync(release, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSchemaIndexDto(
                Release(definitions, index.Release),
                index.Schemas.Select(s => new DeliveryOsduSchemaDto(s.Kind, s.EntityType, s.Version, s.Status, s.Path, definitions.FileWebUrl(index.Release, s.Path))).ToList()));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduSchemaFileDto>, ProblemHttpResult>> GetOsduSchemaAsync(
        string? release, string? kind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Problem("Name the schema with kind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var file = await definitions.FetchAsync(release, kind, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSchemaFileDto(
                file.Schema.Kind, file.Schema.Version, Release(definitions, file.Release), file.Path, definitions.FileWebUrl(file.Release, file.Path), file.Origin, file.Schema.Root));
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
        catch (DeliveryException ex)
        {
            // The release's file is there but is not a record schema a template can be saved from.
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema");
        }
    }

    /// <summary>Reads the release list again from the repository and downloads the release (the newest when none is named) when it is not on disk.</summary>
    private static async Task<Results<Ok<DeliveryOsduSyncDto>, ProblemHttpResult>> SyncOsduAsync(
        DeliveryOsduSyncRequest? request, OsduDataDefinitions definitions, CancellationToken ct)
    {
        try
        {
            var sync = await definitions.SyncAsync(request?.Release, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryOsduSyncDto(sync.SyncedUtc, sync.Releases.Select(r => Release(definitions, r)).ToList(), sync.Downloaded));
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
    }

    private static async Task<Results<Ok<DeliveryOsduComparisonDto>, ProblemHttpResult>> CompareOsduSchemasAsync(
        string? fromRelease, string? fromKind, string? toRelease, string? toKind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromKind) || string.IsNullOrWhiteSpace(toKind)
            || fromKind.Trim().Split(':').Length != 4 || toKind.Trim().Split(':').Length != 4)
        {
            return Problem("A comparison names both versions, fromKind and toKind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest);
        }

        var from = fromKind.Trim();
        var to = toKind.Trim();
        if (!string.Equals(from[..from.LastIndexOf(':')], to[..to.LastIndexOf(':')], StringComparison.Ordinal))
        {
            return Problem($"A comparison is between versions of one kind; '{from}' and '{to}' are different kinds.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var before = new ComparedSide(
                await definitions.PublishedFileAsync(fromRelease, from, ct).ConfigureAwait(false),
                await definitions.FetchAsync(fromRelease, from, ct).ConfigureAwait(false));
            var after = new ComparedSide(
                await definitions.PublishedFileAsync(toRelease, to, ct).ConfigureAwait(false),
                await definitions.FetchAsync(toRelease, to, ct).ConfigureAwait(false));
            var comparison = TemplateComparer.Compare(OsduTemplate.From(before.Bundled.Schema), OsduTemplate.From(after.Bundled.Schema));

            // The shared schemas each version refers to, as published: a kind's own file can be the same in two releases
            // while a schema it refers to changed under the same version, and that is where its template's changes come from.
            var referenced = new List<DeliveryOsduReferencedFileDto>();
            var sameReferenced = 0;
            foreach (var pair in TemplateComparer.PairReferencedFiles(before.Bundled.Files, after.Bundled.Files))
            {
                var beforeText = pair.BeforePath is null ? null : await definitions.FileTextAsync(before.File.Release, pair.BeforePath, ct).ConfigureAwait(false);
                var afterText = pair.AfterPath is null ? null : await definitions.FileTextAsync(after.File.Release, pair.AfterPath, ct).ConfigureAwait(false);
                if (beforeText is not null && afterText is not null && TemplateComparer.SameContent(beforeText, afterText))
                {
                    sameReferenced++;
                    continue;
                }

                referenced.Add(new DeliveryOsduReferencedFileDto(
                    pair.Name,
                    pair.BeforePath,
                    pair.AfterPath,
                    pair.BeforePath is null ? null : definitions.FileWebUrl(before.File.Release, pair.BeforePath),
                    pair.AfterPath is null ? null : definitions.FileWebUrl(after.File.Release, pair.AfterPath),
                    beforeText,
                    afterText));
            }

            return TypedResults.Ok(new DeliveryOsduComparisonDto(
                Side(definitions, before),
                Side(definitions, after),
                TemplateComparer.SameContent(before.File.Text, after.File.Text),
                TemplateComparer.DifferOnlyInIdentifiers(before.File.Text, from, after.File.Text, to),
                string.Equals(before.Bundled.Schema.Version, after.Bundled.Schema.Version, StringComparison.Ordinal),
                comparison.Count(TemplateChangeImpact.Breaking),
                comparison.Count(TemplateChangeImpact.Additive),
                comparison.Count(TemplateChangeImpact.Wording),
                comparison.Unchanged,
                comparison.Changes.Select(c => new DeliveryTemplateVariableChangeDto(
                    c.Path, c.Change.ToString(), c.Impact.ToString(), c.Role.ToString(),
                    c.Fields.Select(f => new DeliveryTemplateFieldChangeDto(f.Field, f.Before, f.After, f.Impact.ToString())).ToList())).ToList(),
                sameReferenced,
                referenced));
        }
        catch (FlowValidationException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (DataDefinitionsException ex)
        {
            return Unavailable(ex);
        }
        catch (DeliveryException ex)
        {
            // A release's file is there but is not a record schema a template can be laid out from.
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema");
        }
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

    private static DeliveryOsduReleaseDto Release(OsduDataDefinitions definitions, DataDefinitionsRelease release)
        => new(release.Name, release.Commit, release.PublishedUtc, definitions.ReleaseWebUrl(release), definitions.IsLocal(release));

    private static DeliveryOsduComparisonSideDto Side(OsduDataDefinitions definitions, ComparedSide side)
        => new(
            side.Bundled.Schema.Kind,
            Release(definitions, side.File.Release),
            side.File.Path,
            definitions.FileWebUrl(side.File.Release, side.File.Path),
            side.File.Status,
            side.Bundled.Schema.Version,
            side.File.Text);

    /// <summary>A version as a comparison reads it: its file as published, and bundled as a template.</summary>
    private sealed record ComparedSide(DataDefinitionsPublishedFile File, DataDefinitionsSchemaFile Bundled);

    /// <summary>What the data definitions could not answer: 404 for what they do not hold, 502 when they could not be read.</summary>
    private static ProblemHttpResult Unavailable(DataDefinitionsException ex)
        => ex.NotFound
            ? Problem(ex.Message, StatusCodes.Status404NotFound, "Not found")
            : Problem(ex.Message, StatusCodes.Status502BadGateway, "OSDU data definitions unavailable");

    private static ProblemHttpResult Problem(string detail, int status, string title = "Bad request")
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
