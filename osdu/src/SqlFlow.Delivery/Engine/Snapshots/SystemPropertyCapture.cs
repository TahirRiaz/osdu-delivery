using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Reads a partition's system properties from the platform, as every capture of its cache does: the feature flags the
/// indexer and the search service report for the partition through their info endpoints (openapi indexer v2 and search
/// v2, <c>GET /info</c>, <c>featureFlagStates</c>). The indexer's say how the partition's records are indexed, which is
/// how a lookup has to be written: whether text has a lowercased keyword sub-field, a custom analyser, a bag of words.
/// The search service's say how a query is answered.
/// </summary>
/// <remarks>
/// A system property is never a reason for a capture to fail. A service that cannot be asked, or answers with something
/// that is not its info, gives a reading that says why, the capture carries on, and what the cache knew of that
/// service's properties stays as it was (<see cref="SystemProperties.Merge"/>).
/// </remarks>
public static class SystemPropertyCapture
{
    /// <summary>The indexer's info (openapi indexer v2, server <c>/api/indexer/v2</c>).</summary>
    public const string IndexerInfoPath = "/api/indexer/v2/info";

    /// <summary>The search service's info (openapi search v2, server <c>/api/search/v2</c>).</summary>
    public const string SearchInfoPath = "/api/search/v2/info";

    /// <summary>How much of a failure a reading quotes.</summary>
    private const int ReasonLength = 500;

    /// <summary>What the indexer and the search service report for <paramref name="partition"/>, one reading each.</summary>
    public static async Task<IReadOnlyList<SystemPropertyReading>> ReadAsync(OsduConnection osdu, string partition, ILogger log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(log);
        return
        [
            await ReadAsync(osdu, SystemProperties.Indexer, IndexerInfoPath, partition, log, ct).ConfigureAwait(false),
            await ReadAsync(osdu, SystemProperties.Search, SearchInfoPath, partition, log, ct).ConfigureAwait(false),
        ];
    }

    private static async Task<SystemPropertyReading> ReadAsync(OsduConnection osdu, string service, string path, string partition, ILogger log, CancellationToken ct)
    {
        SystemPropertyReading reading;
        try
        {
            reading = Parse(service, path, partition, await osdu.GetJsonAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DeliveryException or JsonException)
        {
            reading = SystemPropertyReading.Failed(service, $"GET {path} failed: {Clip(SecretHygiene.RedactedMessage(ex.Message))}");
        }

        if (reading.Properties is { } properties)
        {
            foreach (var property in properties)
            {
                log.LogInformation(
                    "Partition {Partition}: the {Service} reports {Property} {State}{Source}.",
                    partition, service, property.Name, property.State.ToString().ToLowerInvariant(), property.Source is null ? string.Empty : $" (from {property.Source})");
            }
        }
        else
        {
            log.LogWarning(
                "Partition {Partition}: the {Service}'s system properties could not be read, so the cache keeps what it knew of them: {Reason}",
                partition, service, reading.Unread);
        }

        return reading;
    }

    /// <summary>
    /// The properties an info document reports for <paramref name="partition"/>: every feature flag state naming the
    /// partition, or naming none, the partition's own winning where a flag is reported both ways. A document without
    /// <c>featureFlagStates</c> is a service that does not publish them, which is a reading that could not be taken.
    /// </summary>
    internal static SystemPropertyReading Parse(string service, string path, string partition, JsonObject info)
    {
        if (info["featureFlagStates"] is not JsonArray states)
        {
            return SystemPropertyReading.Failed(
                service, $"{path} reports no featureFlagStates, so the {service} does not publish its settings (a deployment can turn that off)");
        }

        var found = new Dictionary<string, (SystemProperty Property, bool Own)>(StringComparer.Ordinal);
        foreach (var state in states.OfType<JsonObject>())
        {
            var name = Text(state["name"]);
            if (name is null)
            {
                continue;
            }

            var of = Text(state["partition"]);
            if (of is not null && !string.Equals(of, partition, StringComparison.Ordinal))
            {
                continue;
            }

            var property = state["enabled"] is JsonValue value && value.TryGetValue<bool>(out var enabled)
                ? new SystemProperty(service, name, enabled ? SystemPropertyState.Enabled : SystemPropertyState.Disabled, Text(state["source"]), null)
                : new SystemProperty(service, name, SystemPropertyState.Unknown, Text(state["source"]), $"the {service} reports it without saying whether it is on");
            var own = of is not null;
            if (!found.TryGetValue(name, out var earlier) || (own && !earlier.Own))
            {
                found[name] = (property, own);
            }
        }

        return SystemPropertyReading.Read(service, found.Values.Select(f => f.Property).ToList());
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string Clip(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= ReasonLength ? flat : flat[..ReasonLength] + "...";
    }
}
