using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane;

/// <summary>
/// The OSDU module as a control plane composes it: the delivery, retrieval and cache flow kinds with their executors and
/// compute operations, the ledger over the module's own database (schema <c>osdu</c>), the mapping and cache documents the
/// repository sync reconciles beside the flows, the OSDU data definitions the Templates page browses, the background work
/// that carries approved cache changes out, the delivery search category, and every delivery endpoint on the control
/// plane's own authenticated route groups.
/// </summary>
/// <remarks>
/// A host passes this to <c>ControlPlaneHost.RunAsync</c>; nothing else about the control plane changes. Every service
/// here is registered after SQLFlow's own, so the module extends the platform rather than replacing it.
/// </remarks>
public sealed class DeliveryControlPlaneModule : IControlPlaneModule
{
    /// <summary>The module's name: what the platform calls it in errors, and the name its database is registered under.</summary>
    public string Name => OsduSchema.Module;

    public void ConfigureServices(ControlPlaneModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var database = services.AddOptions<OsduDatabaseOptions>(OsduDatabaseOptions.SectionName);
        var rollout = services.AddOptions<CacheRolloutOptions>(CacheRolloutOptions.SectionName, options => options.Validate());
        var repository = services.AddOptions<SchemaRepositoryOptions>(SchemaRepositoryOptions.SectionName, options => options.Validate());

        // The module's database: the osdu schema in the catalog's own database unless the deployment gives it one of its
        // own. Bootstrap migrates it after the catalog, and readiness stays red until it verifies against this build.
        services.AddDatabase(OsduModuleDatabase.Create(
            OsduModuleDatabase.ResolveReference(database.Connection, Environment.GetEnvironmentVariable)));

        // The kinds, their executors and the compute operations a node runs for this control plane, plus the ledger,
        // the templates and the caches over the module database's context factory.
        services.Services.AddDeliveryKind();
        services.Services.AddDeliveryLedger();

        // One module context per request, opened from the module database's factory, so an endpoint reads the osdu
        // tables exactly as the ledger does and the catalog's own context stays SQLFlow's.
        services.Services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<OsduDbContext>>().CreateDbContext());

        // The delivery records category of the control plane's search: resolved per request, beside the built-in ones.
        services.Services.AddScoped<ISearchContributor, RecordSearchContributor>();

        // The OSDU data definitions (the Open Group's public repository of OSDU schemas) the Templates page browses and
        // saves templates from, kept as a local copy: each release downloaded once as one archive and read from disk
        // after that, the release list read again when it is older than RefreshMinutes or when someone syncs.
        services.Services.AddHttpClient(OsduDataDefinitions.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.Services.AddSingleton(provider => new OsduDataDefinitions(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(OsduDataDefinitions.HttpClientName),
            repository.ApiUrl,
            repository.WebUrl,
            string.IsNullOrWhiteSpace(repository.CacheDirectory) ? OsduDataDefinitions.DefaultCacheDirectory : repository.CacheDirectory,
            TimeSpan.FromMinutes(repository.RefreshMinutes),
            TimeSpan.FromMinutes(repository.DownloadTimeoutMinutes),
            provider.GetRequiredService<TimeProvider>()));

        if (repository.WarmOnStart)
        {
            services.AddHostedService<DataDefinitionsWarmupService>();
        }

        if (rollout.Enabled)
        {
            services.AddHostedService<CacheUpdateRolloutService>();
        }
    }

    public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Reading the ledger, the mappings, the caches and the templates is any signed-in user's; running something
        // (a redelivery, a removal, a data definitions sync) is an operate action; saving or deleting a
        // template changes what mappings can pin, so it is an author action, as SQLFlow's own proposals are.
        endpoints.Read
            .MapDeliveryReadEndpoints()
            .MapDeliveryTemplateReadEndpoints();
        endpoints.Operate
            .MapDeliveryWriteEndpoints()
            .MapDeliveryTemplateOperateEndpoints();
        endpoints.Author
            .MapDeliveryTemplateAuthorEndpoints();
    }
}
