using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The bootstrap token endpoint: present the configured bootstrap secret, receive a short-lived bearer token.
/// It exists ONLY to bootstrap access (and tests) before the Identity phase wires real issuance; it is mapped
/// only when <see cref="JwtOptions.BootstrapSecret"/> is configured, and the secret is compared in constant time.
/// Scopes are clamped to the operations the bootstrap principal is allowed to request.
/// </summary>
public static class AuthEndpoints
{
    private static readonly string[] AllowedScopes = ["read", "operate"];

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/auth/token", IssueAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("IssueBootstrapToken");

        return group;
    }

    private static Results<Ok<TokenResponse>, ProblemHttpResult> IssueAsync(
        TokenRequest request, TokenIssuer issuer, IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext)
    {
        // The response carries a bearer token; never let an intermediary cache it.
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

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
        var scopes = requested.Where(s => AllowedScopes.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No valid scope requested",
                detail: $"Allowed scopes: {string.Join(", ", AllowedScopes)}.");
        }

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? "bootstrap" : request.Subject.Trim();
        var result = issuer.Issue(subject, scopes, clock.GetUtcNow().UtcDateTime);
        var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - clock.GetUtcNow().UtcDateTime).TotalSeconds);
        return TypedResults.Ok(new TokenResponse(result.Token, "Bearer", expiresIn));
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
