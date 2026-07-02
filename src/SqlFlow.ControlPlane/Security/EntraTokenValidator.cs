using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Validates an external identity token and extracts the profile JIT provisioning needs. Behind an interface so
/// the exchange endpoint is testable without a live identity provider.
/// </summary>
public interface IExternalTokenValidator
{
    /// <summary>The validated profile, or null when the token is invalid (expired, wrong audience/issuer, bad
    /// signature, or missing the identity claims). Never throws for an untrusted token; the caller answers 401.</summary>
    Task<ExternalUserProfile?> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>
/// Validates Microsoft Entra ID tokens for the token-exchange sign-in: the SPA signs in with MSAL and posts the
/// ID token here. Validation pins the tenant (issuer from the tenant authority's OIDC metadata), the app
/// registration (audience = client id), the signature (tenant JWKS, refreshed on key rollover), and the lifetime.
/// The profile is keyed on the immutable <c>oid</c> claim so a UPN rename never creates a duplicate user.
/// </summary>
public sealed class EntraTokenValidator : IExternalTokenValidator
{
    private readonly AzureAdOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ILogger<EntraTokenValidator> _logger;

    public EntraTokenValidator(IOptions<ControlPlaneOptions> options, ILogger<EntraTokenValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value.AzureAd;
        _logger = logger;
        var metadataAddress = $"{_options.ResolveAuthority()}/.well-known/openid-configuration";
        _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever());
    }

    public async Task<ExternalUserProfile?> ValidateAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await _metadata.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Metadata retrieval failing is an outage (network, authority misconfigured), not a bad token; it is
            // logged as such and the sign-in fails closed.
            _logger.LogError(ex, "Entra OIDC metadata could not be retrieved from the configured authority.");
            return null;
        }

        var result = await ValidateOnceAsync(token, metadata, ct).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rollover: the tenant published new signing keys since the metadata was cached. Refresh once and
            // re-validate; a still-unknown key is then a genuinely untrusted token.
            _metadata.RequestRefresh();
            try
            {
                metadata = await _metadata.GetConfigurationAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Entra OIDC metadata refresh after a signature key miss failed.");
                return null;
            }

            result = await ValidateOnceAsync(token, metadata, ct).ConfigureAwait(false);
            if (result is null)
            {
                return null;
            }
        }

        if (!result.IsValid)
        {
            _logger.LogWarning("Entra token rejected: {Reason}", result.Exception?.GetType().Name ?? "invalid");
            return null;
        }

        var claims = result.Claims;
        var objectId = StringClaim(claims, "oid");
        var username = StringClaim(claims, "preferred_username") ?? StringClaim(claims, "email") ?? StringClaim(claims, "upn");
        if (objectId is null || username is null)
        {
            _logger.LogWarning(
                "Entra token validated but is missing the oid or a username claim (preferred_username/email/upn); cannot provision a user from it.");
            return null;
        }

        return new ExternalUserProfile(
            objectId,
            username,
            StringClaim(claims, "name"),
            StringClaim(claims, "email") ?? (username.Contains('@', StringComparison.Ordinal) ? username : null));
    }

    private async Task<TokenValidationResult?> ValidateOnceAsync(
        string token, OpenIdConnectConfiguration metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // The tenant authority's metadata carries the tenant-specific issuer; pinning to it means a token
            // from any other tenant is rejected even though the signing keys are Microsoft-wide.
            ValidIssuer = metadata.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            return await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ValidateTokenAsync reports failures through the result; an exception here means the token is not
            // even parseable as a JWT.
            _logger.LogWarning("Entra token rejected: not a parseable JWT ({Reason}).", ex.GetType().Name);
            return null;
        }
    }

    private static string? StringClaim(IDictionary<string, object> claims, string name)
        => claims.TryGetValue(name, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
}
