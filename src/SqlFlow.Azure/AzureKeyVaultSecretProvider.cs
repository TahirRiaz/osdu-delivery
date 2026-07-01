using System.Collections.Concurrent;
using Azure.Core;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Azure;

/// <summary>
/// Resolves <c>${keyvault:vault/secret}</c> references from Azure Key Vault using the ambient Azure credential.
/// No secret material is stored anywhere - the value is fetched at runtime under the caller's identity, which is
/// what makes this safe in a stateless cloud deployment. Reads route through one <see cref="AzureKeyVaultSecretVault"/>
/// per vault (the same client used for writes), so reading and writing share a single code path; the async
/// overload is genuinely asynchronous so a Key Vault round-trip never blocks a thread-pool thread.
/// </summary>
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, AzureKeyVaultSecretVault> _vaults = new(StringComparer.OrdinalIgnoreCase);

    public AzureKeyVaultSecretProvider(IAzureCredentialFactory credentialFactory)
    {
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _credential = credentialFactory.Create();
    }

    public string Scheme => "keyvault";

    public string Resolve(string locator)
    {
        var (vault, secretName) = ParseLocator(locator);
        var value = VaultFor(vault).GetSecretAsync(secretName).GetAwaiter().GetResult();
        return value ?? throw NotFound(vault, secretName);
    }

    public async Task<string> ResolveAsync(string locator, CancellationToken ct = default)
    {
        var (vault, secretName) = ParseLocator(locator);
        var value = await VaultFor(vault).GetSecretAsync(secretName, ct).ConfigureAwait(false);
        return value ?? throw NotFound(vault, secretName);
    }

    /// <summary>Splits a <c>vault/secret</c> locator. Throws <see cref="SqlFlowException"/> for a missing vault
    /// or secret part.</summary>
    public static (string Vault, string Secret) ParseLocator(string locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var separator = locator.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == locator.Length - 1)
        {
            throw new SqlFlowException($"Key Vault reference must be 'vault/secret', got '{locator}'.");
        }

        return (locator[..separator], locator[(separator + 1)..]);
    }

    private AzureKeyVaultSecretVault VaultFor(string vault)
        => _vaults.GetOrAdd(vault, v => new AzureKeyVaultSecretVault(_credential, AzureKeyVaultSecretVault.VaultUriFor(v)));

    private static SqlFlowException NotFound(string vault, string secretName)
        => new($"Key Vault secret '{secretName}' was not found in vault '{vault}'.");
}
