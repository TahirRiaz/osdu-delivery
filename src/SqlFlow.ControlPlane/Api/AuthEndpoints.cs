using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The sign-in surface. Three ways in, one token out:
/// <list type="bullet">
/// <item><c>POST /auth/login</c>: a regular SQLFlow user (username + password held in the catalog).</item>
/// <item><c>POST /auth/exchange</c>: Azure single sign-on. The SPA signs in against Microsoft Entra ID (MSAL,
/// auth code + PKCE) and exchanges the Entra ID token for a SQLFlow token; first sign-in provisions the user
/// just-in-time with the configured default role. Mapped only when Entra is enabled.</item>
/// <item><c>POST /auth/token</c>: the break-glass bootstrap secret, mapped only when one is configured.</item>
/// </list>
/// Every path issues the same HS256 SQLFlow token, so authorization downstream is identical regardless of how the
/// caller signed in. <c>GET /auth/providers</c> tells the login page which of these are available.
/// </summary>
public static class AuthEndpoints
{
    private static readonly string[] BootstrapAllowedScopes = ["read", "operate", "admin"];

    /// <summary>A hash verified for sign-in attempts against unknown/ineligible accounts, so the response time
    /// does not reveal whether a username exists. Computed once from ephemeral random material.</summary>
    private static readonly Lazy<string> DecoyHash = new(() =>
        new PasswordHasher<CatalogUser>().HashPassword(new CatalogUser(), Guid.NewGuid().ToString("N")));

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group, ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(options);

        group.MapGet("/auth/providers", GetProviders)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("GetAuthProviders");

        group.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("Login");

        if (options.AzureAd.Enabled)
        {
            group.MapPost("/auth/exchange", ExchangeAsync)
                .AllowAnonymous()
                .WithTags("Authentication")
                .WithName("ExchangeExternalToken");
        }

        if (!string.IsNullOrEmpty(options.Jwt.BootstrapSecret))
        {
            group.MapPost("/auth/token", IssueBootstrapAsync)
                .AllowAnonymous()
                .WithTags("Authentication")
                .WithName("IssueBootstrapToken");
        }

        return group;
    }

    private static Ok<AuthProvidersDto> GetProviders(IOptions<ControlPlaneOptions> options)
    {
        var value = options.Value;
        var entra = value.AzureAd.Enabled
            ? new EntraProviderDto(true, value.AzureAd.TenantId, value.AzureAd.ClientId, value.AzureAd.ResolveAuthority())
            : new EntraProviderDto(false, null, null, null);
        return TypedResults.Ok(new AuthProvidersDto(
            Local: true,
            Bootstrap: !string.IsNullOrEmpty(value.Jwt.BootstrapSecret),
            Entra: entra));
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, CatalogDbContext catalog, TokenIssuer issuer, IPasswordHasher<CatalogUser> hasher,
        LoginThrottle throttle, IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Username and password are required");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsLockedOut(request.Username, clientKey, nowUtc))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many failed sign-in attempts",
                detail: $"Sign-in for this account is paused for up to {LoginThrottle.LockoutSeconds / 60} minutes.");
        }

        var user = await UserStore.FindByUsernameAsync(catalog, request.Username, ct).ConfigureAwait(false);
        var eligible = user is { Active: true, Provider: UserProviders.Local, PasswordHash: not null };

        // Always verify exactly one hash, real or decoy, so "user exists" is not readable from the timing.
        var verified = hasher.VerifyHashedPassword(
            user ?? new CatalogUser(),
            eligible ? user!.PasswordHash! : DecoyHash.Value,
            request.Password);
        if (!eligible || verified == PasswordVerificationResult.Failed)
        {
            throttle.RecordFailure(request.Username, clientKey, nowUtc);
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password");
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The hasher's work factor increased since this hash was written; upgrade it now that the cleartext
            // is in hand. Best-effort: a failure leaves the old (still valid) hash in place.
            try
            {
                var upgraded = hasher.HashPassword(user!, request.Password);
                await UserStore.SetPasswordHashAsync(catalog, user!.Id, upgraded, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Password rehash for user {UserId} failed; the existing hash remains valid.", user!.Id);
            }
        }

        throttle.RecordSuccess(request.Username, clientKey);
        await UserStore.RecordLoginAsync(catalog, user!.Id, nowUtc, ct).ConfigureAwait(false);
        return await IssueSessionAsync(catalog, issuer, user, nowUtc, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> ExchangeAsync(
        ExchangeRequest request, CatalogDbContext catalog, TokenIssuer issuer, IExternalTokenValidator validator,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext, CancellationToken ct)
    {
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An identity token is required");
        }

        var profile = await validator.ValidateAsync(request.Token, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid identity token");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var (status, user) = await UserStore.EnsureExternalAsync(
            catalog, profile, options.Value.AzureAd.DefaultRole, nowUtc, ct).ConfigureAwait(false);
        return status switch
        {
            // The Entra token already proved who the caller is, so account state is safe to disclose.
            ExternalSignInStatus.Inactive => TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Account is deactivated",
                detail: "Your account exists but has been deactivated by an administrator."),
            ExternalSignInStatus.UsernameConflict => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Sign-in name already in use",
                detail: "A different account already uses this sign-in name; an administrator must resolve the conflict."),
            ExternalSignInStatus.DefaultRoleMissing => TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Identity is not provisioned",
                detail: "The default role for single sign-on users does not exist; bootstrap provisioning has not completed."),
            _ => await IssueSessionAsync(catalog, issuer, user!, nowUtc, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Loads the user's role grants and issues the session. Fails closed (403) when the role row is
    /// gone: a user whose role was deleted has no defined scopes and must not get a fallback grant.</summary>
    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> IssueSessionAsync(
        CatalogDbContext catalog, TokenIssuer issuer, CatalogUser user, DateTime nowUtc, CancellationToken ct)
    {
        var role = await UserStore.FindRoleAsync(catalog, user.Role, ct).ConfigureAwait(false);
        if (role is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Role is not provisioned",
                detail: $"The role '{user.Role}' assigned to this account does not exist; an administrator must reassign the account.");
        }

        var scopes = role.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = issuer.Issue(user.Username, scopes, nowUtc, user.Role, user.Id);
        var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - nowUtc).TotalSeconds);
        return TypedResults.Ok(new SessionResponse(result.Token, "Bearer", expiresIn, user.Username, user.Role, scopes));
    }

    private static Results<Ok<TokenResponse>, ProblemHttpResult> IssueBootstrapAsync(
        TokenRequest request, TokenIssuer issuer, IOptions<ControlPlaneOptions> options, TimeProvider clock,
        HttpContext httpContext)
    {
        NeverCache(httpContext);

        var configured = options.Value.Jwt.BootstrapSecret;
        if (string.IsNullOrEmpty(configured))
        {
            // The endpoint is not mapped when no bootstrap secret is set; this is defense in depth.
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (request is null || string.IsNullOrEmpty(request.Secret) || !FixedTimeEquals(request.Secret, configured))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid bootstrap secret");
        }

        var requested = request.Scopes is { Count: > 0 } ? request.Scopes : (IReadOnlyList<string>)["read"];
        var scopes = requested.Where(s => BootstrapAllowedScopes.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No valid scope requested",
                detail: $"Allowed scopes: {string.Join(", ", BootstrapAllowedScopes)}.");
        }

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? "bootstrap" : request.Subject.Trim();
        var result = issuer.Issue(subject, scopes, clock.GetUtcNow().UtcDateTime);
        var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - clock.GetUtcNow().UtcDateTime).TotalSeconds);
        return TypedResults.Ok(new TokenResponse(result.Token, "Bearer", expiresIn));
    }

    /// <summary>Sign-in responses carry bearer tokens; never let an intermediary cache them.</summary>
    private static void NeverCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
