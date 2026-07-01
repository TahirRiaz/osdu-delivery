using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Issues HS256 bearer tokens signed with the configured key, valid against the same issuer/audience the host
/// validates. The foundation uses this for the guarded bootstrap endpoint; the Identity phase swaps the
/// implementation for an asymmetric key / external provider without changing the validation contract or callers.
/// </summary>
public sealed class TokenIssuer
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public TokenIssuer(IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Jwt;
        // Validated at startup to be present and >= 32 bytes; constructing the key here is safe.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey!));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Issues a token for the subject with the given scopes (space-delimited <c>scope</c> claim), valid
    /// for the configured access-token lifetime. <paramref name="nowUtc"/> is injectable for deterministic tests.</summary>
    public TokenResult Issue(string subject, IReadOnlyList<string> scopes, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(scopes);

        var expires = nowUtc.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (scopes.Count > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', scopes)));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expires,
            SigningCredentials = _credentials,
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResult(token, expires);
    }
}

/// <summary>An issued token and its expiry.</summary>
public sealed record TokenResult(string Token, DateTime ExpiresUtc);
