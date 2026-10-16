using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using Xunit;

namespace SqlFlow.Delivery.ControlPlane.Tests;

/// <summary>
/// The module's start-up step reads every Key Vault secret the configuration names, once, so a secret not yet put in the
/// vault is reported by the start-up pass, naming the reference and the settings that name it, and never a value.
/// </summary>
public sealed class CentralConfigurationStepTests
{
    private static readonly ConfiguredPartition Dev = new("dev", null, true, new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OSDU_URL"] = "https://osdu-dev.example.com",
        ["OSDU_CLIENT_SECRET"] = "${keyvault:estate-vault/osdu-dev-client-secret}",
    });

    private static readonly ConfiguredPartition Test = new("test", null, false, new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OSDU_CLIENT_SECRET"] = "${keyvault:estate-vault/osdu-dev-client-secret}",
    });

    private static readonly Dictionary<string, string> Estate = new(StringComparer.Ordinal)
    {
        ["OSDU_DATA_DB"] = "${keyvault:estate-vault/osdu-data-db}",
        ["LANDING_URL"] = "abfss://landing@estate.dfs.core.windows.net/drops",
    };

    [Fact]
    public async Task EverySecretTheVaultHolds_LeavesNothingToReport()
    {
        var vault = new Vault("osdu-dev-client-secret", "osdu-data-db");

        var missing = await CentralConfigurationStep.UnreadableVaultSecretsAsync([Dev, Test], Estate, new SecretResolver([vault]), CancellationToken.None);

        Assert.Empty(missing);
        Assert.Equal(["estate-vault/osdu-data-db", "estate-vault/osdu-dev-client-secret"], vault.Read.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASecretNotInTheVault_IsReportedOnce_WithEverySettingThatNamesIt()
    {
        var vault = new Vault("osdu-data-db");

        var missing = await CentralConfigurationStep.UnreadableVaultSecretsAsync([Dev, Test], Estate, new SecretResolver([vault]), CancellationToken.None);

        var line = Assert.Single(missing);
        Assert.StartsWith(
            "${keyvault:estate-vault/osdu-dev-client-secret} (Osdu:Partitions:dev:Properties:OSDU_CLIENT_SECRET, Osdu:Partitions:test:Properties:OSDU_CLIENT_SECRET): ",
            line,
            StringComparison.Ordinal);
        Assert.Contains("was not found", line, StringComparison.Ordinal);
        Assert.DoesNotContain("the-secret-value", line, StringComparison.Ordinal);
    }

    /// <summary>A vault holding the named secrets; it records what was read and answers a value a test can look for.</summary>
    private sealed class Vault(params string[] held) : ISecretProvider
    {
        public List<string> Read { get; } = [];

        public string Scheme => "keyvault";

        public string Resolve(string locator)
        {
            Read.Add(locator);
            var name = locator[(locator.IndexOf('/', StringComparison.Ordinal) + 1)..];
            return held.Contains(name, StringComparer.Ordinal)
                ? "the-secret-value"
                : throw new SqlFlowException($"Key Vault secret '{name}' was not found in vault 'estate-vault'.");
        }
    }
}
