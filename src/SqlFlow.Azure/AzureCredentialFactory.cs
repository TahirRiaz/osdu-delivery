using Azure.Core;
using Azure.Identity;
using SqlFlow.Core.Connections;

namespace SqlFlow.Azure;

/// <summary>
/// Builds the Azure credential from the ambient context. The auth mode is chosen by the
/// <c>SQLFLOW_AZURE_AUTH</c> environment variable (parsed once in <see cref="AzureAuth"/>, shared with the
/// cloud-storage credential provider); the default (<see cref="DefaultAzureCredential"/>) automatically chains
/// Managed Identity → Service Principal (env) → Azure CLI, so the same binary works in a cloud VM/container
/// (managed identity), in CI (service principal), and on a developer box (az login) with no configuration - and
/// no stored secrets.
/// </summary>
public sealed class AzureCredentialFactory : IAzureCredentialFactory
{
    public TokenCredential Create()
        => AzureAuth.Mode() switch
        {
            CloudAuthMode.ServicePrincipal => CreateServicePrincipal(),
            CloudAuthMode.ManagedIdentity => CreateManagedIdentity(),
            CloudAuthMode.AzureCli => new AzureCliCredential(),
            // The default chain excludes managed identity off-cloud and enables the developer credentials, so it
            // works from a dev PC (no stall on the IMDS probe) and in Azure (managed identity first) alike.
            _ => new DefaultAzureCredential(AzureEnvironment.DefaultCredentialOptions()),
        };

    private static TokenCredential CreateManagedIdentity()
    {
        // A non-empty AZURE_CLIENT_ID selects a specific user-assigned identity; otherwise the system-assigned
        // identity is used. (The old string ctor is obsolete in current Azure.Identity.)
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var id = string.IsNullOrWhiteSpace(clientId)
            ? ManagedIdentityId.SystemAssigned
            : ManagedIdentityId.FromUserAssignedClientId(clientId);
        return new ManagedIdentityCredential(id);
    }

    private static TokenCredential CreateServicePrincipal()
    {
        var tenantId = Required("AZURE_TENANT_ID");
        var clientId = Required("AZURE_CLIENT_ID");
        var clientSecret = Required("AZURE_CLIENT_SECRET");
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    private static string Required(string variable)
        => Environment.GetEnvironmentVariable(variable)
           ?? throw new InvalidOperationException($"Service-principal auth requires environment variable '{variable}'.");
}
