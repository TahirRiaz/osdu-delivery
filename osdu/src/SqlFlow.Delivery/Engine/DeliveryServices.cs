using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Listeners;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The delivery module's composition root. Every host (the control plane, a worker node, the CLI) calls
/// <see cref="AddDeliveryKind"/> after the platform's <c>AddSqlFlowEngine</c>, so the kinds, their executors and their
/// compute operations are wired the same way everywhere; a host that has the module's database connection also calls
/// <see cref="AddDeliveryLedger(IServiceCollection, Func{IServiceProvider, Func{OsduDbContext}?})"/>, which is what
/// turns plan-only into deliver and gives the ledger, the templates and the caches.
/// </summary>
public static class DeliveryServices
{
    /// <summary>What a host is told when an operation needs the module's database and it has none.</summary>
    public const string NoLedgerMessage =
        "This host has no osdu database connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB); the delivery ledger, templates and caches are unavailable. "
        + "Without them only validation and planning against the flow document are available, since a render reads its template, and any cache it reads, from that database.";

    public static IServiceCollection AddDeliveryKind(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Storage: the platform's file stores list and read forward; these writers and readers add the streamed
        // writes, the seekable reads and the range reads the engine needs.
        services.AddSingleton<IFileWriter, LocalFileWriter>();
        services.AddSingleton<IFileWriter, AzureBlobFileWriter>();
        services.AddSingleton<IFileReader, LocalFileReader>();
        services.AddSingleton<IFileReader, AzureBlobFileReader>();
        services.AddSingleton(sp => new FileStoreRegistry(
            sp.GetServices<IFileStore>(), sp.GetServices<IFileWriter>(), sp.GetServices<IFileReader>()));
        services.TryAddSingleton<IPayloadFiles>(sp => new StoragePayloadFiles(sp.GetRequiredService<FileStoreRegistry>()));

        // The ingestion tables a flow reads, opened on the node with the flow's own connection reference.
        services.TryAddSingleton<IIngestionSourceFactory>(sp => new SqlServerIngestionSourceFactory(
            sp.GetRequiredService<ISecretResolver>(), sp.GetRequiredService<ILoggerFactory>()));

        // Documents: the delivery loader behind the platform's envelope probe.
        services.AddSingleton<DeliveryDocumentLoader>();
        services.AddSingleton<IFlowDocumentKind, DeliveryFlowKind>();
        services.AddSingleton<IFlowDocumentKind, RetrievalFlowKind>();
        services.AddSingleton<IFlowDocumentKind, CacheFlowKind>();
        services.AddSingleton<ICatalogSyncExtension, DeliveryCatalogSync>();

        // Protocols and the completion callback. The logging listener is always on; hosts add their own (a live
        // feed, metrics, a webhook) by registering more IDeliveryListener instances.
        services.TryAddSingleton<IProtocolFactory>(sp => new DefaultProtocolFactory(sp.GetRequiredService<ISecretResolver>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IDeliveryListener, LoggingDeliveryListener>();

        services.AddSingleton(sp => new EngineContext(
            sp.GetRequiredService<DeliveryDocumentLoader>(),
            sp.GetRequiredService<IIngestionSourceFactory>(),
            sp.GetRequiredService<IPayloadFiles>(),
            sp.GetRequiredService<FileStoreRegistry>(),
            sp.GetRequiredService<ISecretResolver>(),
            sp.GetService<DeliveryLedgerSource>()?.Open(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IProtocolFactory>(),
            new CompositeDeliveryListener(sp.GetServices<IDeliveryListener>()),
            // The fan-out belongs to one run: the executor attaches the one the platform handed that run.
            FanOut: null,
            sp.GetService<DeliveryLedgerSource>()?.Templates(sp),
            sp.GetService<DeliveryLedgerSource>()?.Cache(sp)));

        // Execution: the run executors behind the platform's document executor, and the ad-hoc compute operations
        // a node runs for the control plane (target probe, record read-back, source row read-back and removal).
        services.AddSingleton<IFlowDocumentExecutor, DeliveryExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, RetrievalExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, CacheExecutor>();
        services.AddSingleton<IComputeOperation, ProbeTargetOperation>();
        services.AddSingleton<IComputeOperation, ReadRecordOperation>();
        services.AddSingleton<IComputeOperation, ReadSourceRowOperation>();
        services.AddSingleton<IComputeOperation, DeleteRecordOperation>();

        return services;
    }

    /// <summary>
    /// Wires the ledger, the templates and the caches over the module's own database (schema <c>osdu</c>).
    /// <paramref name="contexts"/> decides, once the host is built, whether that database is configured and, when it is,
    /// opens a fresh context per operation; the ledger disposes what it opens.
    /// </summary>
    public static IServiceCollection AddDeliveryLedger(this IServiceCollection services, Func<IServiceProvider, Func<OsduDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contexts);
        services.AddSingleton(new DeliveryLedgerSource(contexts));
        services.AddSingleton<ILedger>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Open(sp)
            ?? throw new DeliveryException(NoLedgerMessage));
        services.AddSingleton<ITemplateStore>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Templates(sp)
            ?? throw new DeliveryException(NoLedgerMessage));
        services.AddSingleton<ICacheStore>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Cache(sp)
            ?? throw new DeliveryException(NoLedgerMessage));
        return services;
    }

    /// <summary>
    /// Wires the ledger over the module database the host registered with the platform's module-database support, which
    /// migrates it, records its version and hands out contexts through an <see cref="IDbContextFactory{TContext}"/>. A
    /// host without that registration gets a module that plans but does not deliver.
    /// </summary>
    public static IServiceCollection AddDeliveryLedger(this IServiceCollection services)
        => services.AddDeliveryLedger(sp =>
        {
            var factory = sp.GetService<IDbContextFactory<OsduDbContext>>();
            return factory is null ? null : factory.CreateDbContext;
        });
}

/// <summary>The module database the host registered: resolved once, the first time the ledger is needed.</summary>
public sealed class DeliveryLedgerSource
{
    private readonly Func<IServiceProvider, Func<OsduDbContext>?> _contexts;
    private readonly Lock _gate = new();
    private bool _resolved;
    private Func<OsduDbContext>? _factory;
    private ILedger? _ledger;
    private ITemplateStore? _templates;
    private OsduCacheStore? _cache;

    public DeliveryLedgerSource(Func<IServiceProvider, Func<OsduDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>The ledger, or null when the host has no osdu database connection.</summary>
    public ILedger? Open(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _ledger;
    }

    /// <summary>The template store, or null when the host has no osdu database connection.</summary>
    public ITemplateStore? Templates(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _templates;
    }

    /// <summary>The cache store, or null when the host has no osdu database connection.</summary>
    public ICacheStore? Cache(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _cache;
    }

    /// <summary>The context factory, or null when the host has no osdu database connection.</summary>
    public Func<OsduDbContext>? Contexts(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _factory;
    }

    private void Resolve(IServiceProvider provider)
    {
        lock (_gate)
        {
            if (!_resolved)
            {
                _factory = _contexts(provider);
                _ledger = _factory is null ? null : new OsduLedger(_factory, provider.GetRequiredService<TimeProvider>());
                _templates = _factory is null ? null : new OsduTemplateStore(_factory, provider.GetRequiredService<TimeProvider>());
                _cache = _factory is null ? null : new OsduCacheStore(_factory);
                _resolved = true;
            }
        }
    }
}
