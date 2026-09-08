using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Abstractions;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Listeners;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Storage;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The delivery kind's composition root. Every host (the control plane, a worker node, the CLI) calls
/// <see cref="AddDeliveryKind"/> after the platform's <c>AddSqlFlowEngine</c>, so the kind, its executor and its
/// compute operations are wired the same way everywhere; hosts that hold a catalog also call
/// <see cref="AddDeliveryLedger"/>, which is what turns plan-only into deliver and lets a run fan out.
/// </summary>
public static class DeliveryServices
{
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
        services.AddSingleton(sp => new FileStoreRegistry(sp.GetServices<IFileStore>(), sp.GetServices<IFileWriter>(), sp.GetServices<IFileReader>()));
        services.AddSingleton<IDropReader>(sp => new DropReader(sp.GetRequiredService<FileStoreRegistry>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<DropReader>()));

        // Documents: the delivery loader behind the platform's envelope probe.
        services.AddSingleton<DeliveryDocumentLoader>();
        services.AddSingleton<IFlowDocumentKind, DeliveryFlowKind>();
        services.AddSingleton<ICatalogSyncExtension, DeliveryCatalogSync>();

        // Protocols and the completion callback. The logging listener is always on; hosts add their own (a live
        // feed, metrics, a webhook) by registering more IDeliveryListener instances.
        services.TryAddSingleton<IProtocolFactory>(sp => new DefaultProtocolFactory(sp.GetRequiredService<ISecretResolver>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IDeliveryListener, LoggingDeliveryListener>();

        services.AddSingleton(sp => new EngineContext(
            sp.GetRequiredService<DeliveryDocumentLoader>(),
            sp.GetRequiredService<IDropReader>(),
            sp.GetRequiredService<FileStoreRegistry>(),
            sp.GetRequiredService<ISecretResolver>(),
            sp.GetService<DeliveryLedgerSource>()?.Open(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IProtocolFactory>(),
            new CompositeDeliveryListener(sp.GetServices<IDeliveryListener>()),
            sp.GetService<IFanOutDispatcher>() ?? NoFanOutDispatcher.Instance));

        // Execution: the run executor behind the platform's DocumentExecutor, and the ad-hoc compute operations
        // a node runs for the control plane (target probe, record read-back, record removal).
        services.AddSingleton<IFlowDocumentExecutor, DeliveryExecutor>();
        services.AddSingleton<IComputeOperation, ProbeTargetOperation>();
        services.AddSingleton<IComputeOperation, ReadRecordOperation>();
        services.AddSingleton<IComputeOperation, DeleteRecordOperation>();

        return services;
    }

    /// <summary>
    /// Wires the ledger over the catalog. <paramref name="contexts"/> decides, once the host is built, whether a
    /// catalog is available (the control plane and a worker always have one; a CLI run has one only with <c>--db</c>
    /// or the catalog variable) and, when it is, opens a fresh catalog context per ledger operation; the ledger
    /// disposes what it opens. Without a catalog the engine runs ledger-less: validate, plan and snapshot capture.
    /// The same catalog is what lets a run fan out across the fleet.
    /// </summary>
    public static IServiceCollection AddDeliveryLedger(this IServiceCollection services, Func<IServiceProvider, Func<CatalogDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contexts);
        services.AddSingleton(new DeliveryLedgerSource(contexts));
        services.AddSingleton<ILedger>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Open(sp)
            ?? throw new DeliveryException("This host has no catalog connection, so the delivery ledger is unavailable. Start it with the catalog connection (--db, or the catalog variable)."));
        services.AddSingleton<IFanOutDispatcher>(sp => new CatalogFanOutDispatcher(
            sp.GetRequiredService<DeliveryLedgerSource>().Contexts(sp), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}

/// <summary>The ledger factory a host registered: resolved once, the first time a ledger is needed.</summary>
public sealed class DeliveryLedgerSource
{
    private readonly Func<IServiceProvider, Func<CatalogDbContext>?> _contexts;
    private readonly Lock _gate = new();
    private bool _resolved;
    private Func<CatalogDbContext>? _factory;
    private ILedger? _ledger;

    public DeliveryLedgerSource(Func<IServiceProvider, Func<CatalogDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>The ledger, or null when the host resolved no catalog connection.</summary>
    public ILedger? Open(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _ledger;
    }

    /// <summary>The catalog context factory, or null when the host resolved no catalog connection.</summary>
    public Func<CatalogDbContext>? Contexts(IServiceProvider provider)
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
                _ledger = _factory is null ? null : new CatalogLedger(_factory, provider.GetRequiredService<TimeProvider>());
                _resolved = true;
            }
        }
    }
}
