using Azure.Core;

namespace SqlFlow.Azure;

/// <summary>
/// Produces the Azure <see cref="TokenCredential"/> for the .NET SDK access paths (Key Vault and blob storage).
/// Every path reads the same <c>SQLFLOW_AZURE_AUTH</c> mode (via <c>AzureAuth</c>): one intent, reused everywhere,
/// the basis for "drop it into the cloud and it authenticates itself".
/// </summary>
public interface IAzureCredentialFactory
{
    TokenCredential Create();
}
