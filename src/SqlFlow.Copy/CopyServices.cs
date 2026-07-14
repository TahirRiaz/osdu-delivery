using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlFlow.Copy;

namespace SqlFlow.Copy;

/// <summary>
/// Registers the file-copy engine (flowType: cpy) into the SqlFlow engine container: the endpoints (local disk /
/// Azure Blob-ADLS / SFTP behind the <see cref="ICopyEndpoint"/> selector), the engine, and the flow runner. Assumes
/// the host has already registered <c>ISecretResolver</c> and <c>IAzureCredentialFactory</c> (the shared secret /
/// credential chain), exactly like the acquisition engine.
/// </summary>
public static class CopyServices
{
    public static IServiceCollection AddSqlFlowCopy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ICopyEndpoint, LocalCopyEndpoint>();
        services.AddSingleton<ICopyEndpoint, AzureBlobCopyEndpoint>();
        services.AddSingleton<ICopyEndpoint, SftpCopyEndpoint>();

        services.AddSingleton(sp => new CopyEngine(
            sp.GetServices<ICopyEndpoint>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CopyFlowRunner>();

        return services;
    }
}
