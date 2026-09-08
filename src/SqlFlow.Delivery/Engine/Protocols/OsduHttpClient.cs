using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The thin HTTP surface both protocols share: endpoint joining, resolved headers, auth application and the
/// request factories the executor retries from. A payload request factory re-opens the chunk stream per attempt,
/// which is what lets streaming and retry coexist (design.md section 12.4).
/// </summary>
public sealed class OsduHttpClient
{
    private readonly HttpRuntime _http;
    private readonly TargetAuth _auth;
    private readonly string _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public OsduHttpClient(HttpRuntime http, string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _auth = auth;
        _headers = headers;
    }

    public string Endpoint => _endpoint;

    public Uri Url(string pathTemplate, string? id = null, string? sessionId = null)
    {
        var path = pathTemplate
            .Replace("{id}", id is null ? string.Empty : Uri.EscapeDataString(id), StringComparison.Ordinal)
            .Replace("{sessionId}", sessionId is null ? string.Empty : Uri.EscapeDataString(sessionId), StringComparison.Ordinal);
        return new Uri(_endpoint + "/" + path.TrimStart('/'));
    }

    public async Task<HttpFetchResult> SendJsonAsync(HttpMethod method, Uri url, JsonNode? body, IReadOnlySet<int>? allowStatuses, CancellationToken ct)
    {
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        var bytes = body is null ? null : CanonicalJson.ToBytes(body);
        try
        {
            return await _http.Data.SendAsync(() =>
            {
                var request = new HttpRequestMessage(method, url);
                Apply(request, auth);
                if (bytes is not null)
                {
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }

                return request;
            }, allowStatuses, ct).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (ex.StatusCode == 401)
        {
            _http.AuthResolver.Invalidate();
            throw;
        }
    }

    /// <summary>Streams an opaque payload; <paramref name="open"/> is invoked per attempt so a retry re-opens the blob.</summary>
    public async Task<HttpFetchResult> SendStreamAsync(HttpMethod method, Uri url, Func<Stream> open, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        var auth = await _http.AuthResolver.ResolveAsync(_auth, _http.Auth, ct).ConfigureAwait(false);
        return await _http.Data.SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            Apply(request, auth);
            var stream = open();
            request.Content = new StreamContent(stream, 1 << 16);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return request;
        }, null, ct).ConfigureAwait(false);
    }

    public static JsonElement ParseJson(HttpFetchResult result, Uri url)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            return JsonDocument.Parse(result.Body).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"{url} returned a body that is not JSON: {Preview(result)}", ex);
        }
    }

    private static string Preview(HttpFetchResult result)
    {
        var text = Encoding.UTF8.GetString(result.Body);
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    private void Apply(HttpRequestMessage request, AppliedAuth auth)
    {
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        auth.ApplyTo(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
