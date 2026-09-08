using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Azure;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Storage;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>
/// The single registration of the platform engine into a dependency-injection container: the file stores, the
/// secret chain (Azure credential factory, the env + Key Vault secret providers, the resolver), the YAML loaders
/// over the registered document kinds, the compute-task executor over the registered operations, and the shared
/// <see cref="DocumentExecutor"/>. Every host that runs flows (the CLI, the control plane, worker nodes) composes
/// the engine through this one extension, so there is exactly one engine wiring to maintain and a flow runs
/// identically wherever it is hosted. Document kinds and their executors are registered by the host next to this
/// call (<see cref="IFlowDocumentKind"/>, <see cref="IFlowDocumentExecutor"/>, <see cref="IComputeOperation"/>).
/// </summary>
public static class SqlFlowEngineServices
{
    /// <summary>
    /// Registers the platform engine. The caller still adds its own logging (and may override the
    /// <see cref="DocumentExecutor"/> registration afterwards to attach a host-specific warning sink: a later
    /// registration of the same service wins). The <see cref="DocumentExecutor"/> registered here has no warning
    /// sink, so engine-internal hygiene/history warnings are silent unless the host opts in.
    /// </summary>
    public static IServiceCollection AddSqlFlowEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IFileStore, LocalFileStore>();
        // Cloud object store: reads abfss/wasbs/https lake paths through the shared Azure credential (az login /
        // managed identity / service principal). Selected by CanHandle for Azure URIs; local paths stay on LocalFileStore.
        services.AddSingleton<IFileStore, AzureBlobFileStore>();
        services.AddSingleton<IFlowEventSink, ConsoleFlowEventSink>();

        // Secret resolution + Azure auth (Service Principal / Managed Identity / Azure CLI / Default).
        services.AddSingleton<IAzureCredentialFactory, AzureCredentialFactory>();
        services.AddSingleton<ISecretProvider, EnvSecretProvider>();
        services.AddSingleton<ISecretProvider, AzureKeyVaultSecretProvider>();
        services.AddSingleton<ISecretResolver, SecretResolver>();

        // The YAML loaders: the document loader dispatches on the kinds the host registered.
        services.AddSingleton<YamlScheduleLibraryLoader>();
        services.AddSingleton(sp => new YamlDocumentLoader(sp.GetServices<IFlowDocumentKind>()));

        // Ad-hoc compute: the executor behind queued compute tasks, dispatching on the operations the host registered.
        services.AddSingleton<ComputeTaskExecutor>();

        services.AddSingleton(sp => new DocumentExecutor(sp));

        return services;
    }
}
