using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Entra single sign-on turns on automatically once a tenant id and client id are configured: supplying the
/// credentials is the whole switch, with no separate flag to remember, and an explicit <c>Enabled</c> either
/// requires SSO (fail fast when a credential is missing) or forces it off as a kill switch. Local username/password
/// sign-in is unaffected either way; these cases pin the additive Microsoft option's gate.
/// </summary>
public sealed class AzureAdOptionsTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000001";
    private const string Client = "00000000-0000-0000-0000-000000000002";

    [Fact]
    public void CredentialsPresent_AutoEnables()
    {
        var options = new AzureAdOptions { TenantId = Tenant, ClientId = Client };
        Assert.True(options.HasCredentials);
        Assert.True(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void NoCredentials_StaysOff_AndDoesNotThrow()
    {
        var options = new AzureAdOptions();
        Assert.False(options.HasCredentials);
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Theory]
    [InlineData(Tenant, null)]
    [InlineData(null, Client)]
    [InlineData(Tenant, "   ")]
    public void OnlyOneCredential_StaysOff(string? tenantId, string? clientId)
    {
        var options = new AzureAdOptions { TenantId = tenantId, ClientId = clientId };
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void ExplicitFalse_ForcesOff_EvenWithCredentials()
    {
        var options = new AzureAdOptions { Enabled = false, TenantId = Tenant, ClientId = Client };
        Assert.True(options.HasCredentials);
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void ExplicitTrue_WithoutCredentials_IsAStartupError()
    {
        var options = new AzureAdOptions { Enabled = true };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TenantId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitTrue_WithCredentials_Enables()
    {
        var options = new AzureAdOptions { Enabled = true, TenantId = Tenant, ClientId = Client };
        Assert.True(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void Enabled_WithBlankDefaultRole_IsAStartupError()
    {
        var options = new AzureAdOptions { TenantId = Tenant, ClientId = Client, DefaultRole = "  " };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("DefaultRole", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAuthority_DefaultsToPublicCloudTenantAuthority()
    {
        var options = new AzureAdOptions { TenantId = Tenant, ClientId = Client };
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void ResolveAuthority_HonoursOverride_AndTrimsTrailingSlash()
    {
        var options = new AzureAdOptions
        {
            TenantId = Tenant,
            ClientId = Client,
            Authority = "https://login.microsoftonline.us/" + Tenant + "/v2.0/",
        };
        Assert.Equal($"https://login.microsoftonline.us/{Tenant}/v2.0", options.ResolveAuthority());
    }
}
