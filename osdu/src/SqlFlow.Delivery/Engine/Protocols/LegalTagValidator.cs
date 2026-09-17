using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Asks the legal service which of a set of legal tags it would refuse, and why (openapi legal v1,
/// <c>POST /legaltags:validate</c>: <c>{"names": [...]}</c> answered by <c>{"invalidLegalTags": [{"name",
/// "reason"}]}</c>).
///
/// Storage refuses a record whose legal tag does not exist or has expired, and it refuses it on every record that
/// carries the tag, so a delivery that does not check first learns about one expired contract a few thousand
/// failed writes later. The legal service answers the question directly and says why (expired, not found, a country
/// the tag does not allow), which is a message an operator can act on without reading storage's error.
///
/// The request takes at most <see cref="MaxNamesPerRequest"/> names (RequestLegalTags, maxItems 25), so a longer set
/// is asked in batches. A tag's validity changes when a legal tag is edited or when the legal service's daily status
/// job expires it, not from one request to the next, so a verdict is kept for <see cref="CacheFor"/>: long enough
/// that every batch of a run does not ask again, short enough that a tag fixed during a run is noticed during it.
///
/// Two answers need care. The service answers <c>404 "LegalTag names were not found"</c> for a request whose names
/// it does not know at all, without saying which, so a 404 for several names is asked again name by name to
/// attribute it. And a 404 that is not the legal service speaking (no OSDU error body: a gateway, a DDMS facade
/// that does not route legal) is not a verdict on any tag, so it surfaces as a failure to reach the service rather
/// than as every tag being invalid.
/// </summary>
public sealed class LegalTagValidator
{
    public const string DefaultValidatePath = "/api/legal/v1/legaltags:validate";

    /// <summary>Names the legal service takes in one validate request (openapi legal v1, RequestLegalTags maxItems).</summary>
    public const int MaxNamesPerRequest = 25;

    /// <summary>How long a verdict on one tag is trusted before it is asked again.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly OsduHttpClient _client;
    private readonly string _validatePath;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Verdict> _verdicts = new(StringComparer.Ordinal);

    public LegalTagValidator(OsduHttpClient client, string? validatePath = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _validatePath = string.IsNullOrWhiteSpace(validatePath) ? DefaultValidatePath : validatePath;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Where a flow's target asks about legal tags, or null when it does not ask: the flow turned the check off, or its
    /// endpoint is a DDMS itself rather than the OSDU platform root (<paramref name="platformEndpoint"/> false: a ddms
    /// flow whose DDMS has no root, <see cref="DdmsRouting.PlatformEndpoint"/>) and it has not said where the legal
    /// service is. Under a platform endpoint the default resolves.
    /// </summary>
    public static string? PathFor(ProtocolOptions options, bool platformEndpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ValidateLegalTags)
        {
            return null;
        }

        if (options.LegalValidatePath is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        return platformEndpoint ? DefaultValidatePath : null;
    }

    /// <summary>
    /// The tags among <paramref name="names"/> the legal service would refuse, each with the reason it gives. Empty
    /// when every tag is valid. Blank and repeated names are ignored.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> InvalidAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var now = _time.GetUtcNow();
        var distinct = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = distinct.Where(n => !_verdicts.TryGetValue(n, out var verdict) || verdict.Until <= now).ToList();
        foreach (var batch in unknown.Chunk(MaxNamesPerRequest))
        {
            ct.ThrowIfCancellationRequested();
            var refused = await AskAsync(batch, ct).ConfigureAwait(false);
            var until = _time.GetUtcNow() + CacheFor;
            foreach (var name in batch)
            {
                _verdicts[name] = new Verdict(refused.TryGetValue(name, out var reason) ? reason : null, until);
            }
        }

        var invalid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in distinct)
        {
            if (_verdicts.TryGetValue(name, out var verdict) && verdict.Reason is { } reason)
            {
                invalid[name] = reason;
            }
        }

        return invalid;
    }

    /// <summary>One validate request: the names it refused, with reasons.</summary>
    private async Task<Dictionary<string, string>> AskAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var url = _client.Url(_validatePath);
        var body = new JsonObject
        {
            ["names"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };

        // A validation is a read: asking twice changes nothing.
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            if (!IsServiceError(result.Body))
            {
                throw new DeliveryException(
                    $"{url.AbsolutePath} answered 404 without an OSDU error body, so the legal service is not reachable at that path; the legal tags could not be checked.");
            }

            if (names.Count == 1)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal) { [names[0]] = "the legal service does not know this legal tag" };
            }

            // The service said some of these do not exist without saying which: ask it about each one.
            var attributed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                foreach (var (refused, reason) in await AskAsync([name], ct).ConfigureAwait(false))
                {
                    attributed[refused] = reason;
                }
            }

            return attributed;
        }

        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.Body.Length == 0)
        {
            return verdicts;
        }

        var root = OsduHttpClient.ParseJson(result, url);
        foreach (var entry in JsonPathReader.SelectElements(root, "invalidLegalTags[*]"))
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } tag)
            {
                continue;
            }

            verdicts[tag] = entry.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() is { Length: > 0 } text
                ? text
                : "the legal service reports this legal tag as invalid";
        }

        return verdicts;
    }

    /// <summary>True when the body is the legal service's own error (AppError), rather than a gateway's page.</summary>
    private static bool IsServiceError(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("code", out _) || document.RootElement.TryGetProperty("message", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct Verdict(string? Reason, DateTimeOffset Until);
}
