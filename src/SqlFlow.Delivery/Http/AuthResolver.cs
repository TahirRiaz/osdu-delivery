// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/AuthResolver.cs. Changes: namespace, exception types, the flow's TargetAuth model in
// place of AcquireAuth, template rendering dropped (only ${secret} references are expanded), token caching added so
// one token serves a whole run and is refreshed before expiry.
using System.Text.Json;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Turns a <see cref="TargetAuth"/> plus its resolved secrets into the concrete <see cref="AppliedAuth"/> a
/// transport merges onto each request. Static schemes resolve their <c>${...}</c> secret references directly; the
/// OAuth2 client-credentials scheme performs the token request (optionally after OIDC discovery) and reads the
/// access token out of the response by path. Secret material never leaves this class except as the final header.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "The SemaphoreSlim wait handle is never accessed; the resolver lives as long as the HttpRuntime that owns it.")]
public sealed class AuthResolver
{
    private readonly ISecretResolver _secrets;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppliedAuth? _cached;
    private DateTimeOffset _cachedUntil;

    public AuthResolver(ISecretResolver secrets, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Resolves the auth, reusing a cached OAuth2 token until shortly before it expires.</summary>
    public async Task<AppliedAuth> ResolveAsync(TargetAuth auth, HttpExecutor authHttp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(authHttp);

        if (auth.Type != TargetAuthType.OAuth2ClientCredentials)
        {
            return await ResolveStaticAsync(auth, ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _time.GetUtcNow() < _cachedUntil)
            {
                return _cached;
            }

            var token = auth.Token ?? throw new DeliveryException("Auth type oauth2ClientCredentials requires a 'token' block with the token endpoint.");
            var (accessToken, lifetime) = await AcquireTokenAsync(auth, token, authHttp, ct).ConfigureAwait(false);
            _cached = AppliedAuth.Header("Authorization", token.ApplyPrefix + accessToken);
            // Refresh a minute early, and never trust a lifetime shorter than two minutes.
            var ttl = lifetime > TimeSpan.FromMinutes(2) ? lifetime - TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(1);
            _cachedUntil = _time.GetUtcNow() + ttl;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached token, for example after a 401.</summary>
    public void Invalidate()
    {
        _cached = null;
        _cachedUntil = DateTimeOffset.MinValue;
    }

    private async Task<AppliedAuth> ResolveStaticAsync(TargetAuth auth, CancellationToken ct)
    {
        switch (auth.Type)
        {
            case TargetAuthType.None:
                return AppliedAuth.None;

            case TargetAuthType.ApiKeyHeader:
                {
                    var header = Require(auth.HeaderName, "auth.headerName", "apiKeyHeader");
                    var value = await SecretAsync(auth.SecretRef, "auth.secretRef", "apiKeyHeader", ct).ConfigureAwait(false);
                    return AppliedAuth.Header(header, (auth.ValuePrefix ?? string.Empty) + value);
                }

            case TargetAuthType.Bearer:
                {
                    var token = await SecretAsync(auth.SecretRef, "auth.secretRef", "bearer", ct).ConfigureAwait(false);
                    return AppliedAuth.Header("Authorization", (auth.ValuePrefix ?? "Bearer ") + token);
                }

            case TargetAuthType.Basic:
                {
                    var user = await SecretAsync(auth.SecondarySecretRef, "auth.secondarySecretRef (username)", "basic", ct).ConfigureAwait(false);
                    var password = await SecretAsync(auth.SecretRef, "auth.secretRef (password)", "basic", ct).ConfigureAwait(false);
                    return AppliedAuth.Header("Authorization", AppliedAuth.BasicHeader(user, password));
                }

            default:
                throw new DeliveryException($"Unsupported auth type '{auth.Type}'.");
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> AcquireTokenAsync(TargetAuth auth, TargetTokenEndpoint token, HttpExecutor authHttp, CancellationToken ct)
    {
        var tokenUrl = await ResolveTokenUrlAsync(token, authHttp, ct).ConfigureAwait(false);

        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in token.Body)
        {
            body[key] = await _secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        body.TryAdd("grant_type", "client_credentials");
        if (!body.ContainsKey("client_id") && !string.IsNullOrWhiteSpace(auth.SecondarySecretRef))
        {
            body["client_id"] = await _secrets.ResolveAsync(auth.SecondarySecretRef!, ct).ConfigureAwait(false);
        }

        if (!body.ContainsKey("client_secret") && !string.IsNullOrWhiteSpace(auth.SecretRef))
        {
            body["client_secret"] = await _secrets.ResolveAsync(auth.SecretRef!, ct).ConfigureAwait(false);
        }

        string? basicHeader = null;
        if (token.BasicAuthClient)
        {
            var clientId = Take(body, "client_id") ?? throw new DeliveryException("basicAuthClient requires 'client_id' in the token body.");
            var clientSecret = Take(body, "client_secret") ?? throw new DeliveryException("basicAuthClient requires 'client_secret' in the token body.");
            basicHeader = AppliedAuth.BasicHeader(clientId, clientSecret);
        }

        var result = await authHttp.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            if (basicHeader is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", basicHeader);
            }

            request.Content = new FormUrlEncodedContent(body);
            return request;
        }, ct: ct, idempotent: true).ConfigureAwait(false);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(result.Body).RootElement;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"Token endpoint '{tokenUrl}' did not return JSON: {ex.Message}", ex);
        }

        var accessToken = JsonPathReader.SelectValue(root, token.TokenPath)
            ?? throw new DeliveryException($"Token endpoint '{tokenUrl}' response has no value at path '{token.TokenPath}'.");
        var lifetime = JsonPathReader.SelectValue(root, "expires_in") is { } expires && double.TryParse(expires, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(30);
        return (accessToken, lifetime);
    }

    private async Task<string> ResolveTokenUrlAsync(TargetTokenEndpoint token, HttpExecutor authHttp, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(token.DiscoveryUrl))
        {
            var discoveryUrl = await _secrets.ResolveAsync(token.DiscoveryUrl!, ct).ConfigureAwait(false);
            var result = await authHttp.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, discoveryUrl), ct: ct).ConfigureAwait(false);
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(result.Body).RootElement;
            }
            catch (JsonException ex)
            {
                throw new DeliveryException($"OIDC discovery document '{discoveryUrl}' is not valid JSON: {ex.Message}", ex);
            }

            return JsonPathReader.SelectValue(root, "token_endpoint")
                ?? throw new DeliveryException($"OIDC discovery document '{discoveryUrl}' has no 'token_endpoint'.");
        }

        var url = token.Url ?? throw new DeliveryException("The token block needs a 'url' (or a 'discoveryUrl' to resolve it).");
        return await _secrets.ResolveAsync(url, ct).ConfigureAwait(false);
    }

    private async Task<string> SecretAsync(string? reference, string field, string authType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new DeliveryException($"Auth type '{authType}' requires '{field}'.");
        }

        return await _secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
    }

    private static string Require(string? value, string field, string authType)
        => string.IsNullOrWhiteSpace(value) ? throw new DeliveryException($"Auth type '{authType}' requires '{field}'.") : value;

    private static string? Take(IDictionary<string, string> body, string key)
    {
        if (body.TryGetValue(key, out var value))
        {
            body.Remove(key);
            return value;
        }

        return null;
    }
}
