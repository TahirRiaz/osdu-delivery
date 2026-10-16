namespace SqlFlow.Delivery.Snapshots;

/// <summary>What a platform service says about one of a partition's system properties.</summary>
public enum SystemPropertyState
{
    /// <summary>The service reports the property on for the partition.</summary>
    Enabled,

    /// <summary>The service reports the property off for the partition.</summary>
    Disabled,

    /// <summary>No service has said, so nothing may rely on it being on.</summary>
    Unknown,
}

/// <summary>
/// A setting of the partition itself, as a platform service reports it for the partition: how the platform indexes and
/// searches the partition's records, rather than any record of it. It is captured with every version of the partition's
/// cache and kept apart from the cached records: it is neither reference nor master data, has no record id, and no mapping
/// reads it with <c>cache.&lt;Type&gt;.&lt;field&gt;</c>. What the engine itself relies on is named in
/// <see cref="SystemProperties"/>.
/// </summary>
/// <param name="Service">The service that reports it: <c>indexer</c> or <c>search</c>.</param>
/// <param name="Name">The property as that service names it, such as <c>featureFlag.keywordLower.enabled</c>.</param>
/// <param name="State">Whether it is on, off, or unknown.</param>
/// <param name="Source">Where the service says it took the value from (the data partition, or its own configuration), when it says.</param>
/// <param name="Detail">Why the state is unknown, or the last time it could not be read, when either is so.</param>
public sealed record SystemProperty(string Service, string Name, SystemPropertyState State, string? Source, string? Detail)
{
    /// <summary>Whether the service reports it on.</summary>
    public bool IsEnabled => State == SystemPropertyState.Enabled;
}

/// <summary>
/// What one service said about the partition's system properties when a capture asked it: every property it reports for
/// the partition, or why it could not be asked. A service that answered is the whole truth about its properties; one that
/// did not changes nothing the cache already knows.
/// </summary>
/// <param name="Service">The service asked.</param>
/// <param name="Properties">Every property it reports for the partition, or null when it could not be asked.</param>
/// <param name="Unread">Why it could not be asked, when it could not.</param>
public sealed record SystemPropertyReading(string Service, IReadOnlyList<SystemProperty>? Properties, string? Unread)
{
    /// <summary>A service that answered with <paramref name="properties"/>.</summary>
    public static SystemPropertyReading Read(string service, IReadOnlyList<SystemProperty> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(properties);
        return new SystemPropertyReading(service, properties, null);
    }

    /// <summary>A service that could not be asked, and <paramref name="reason"/> why.</summary>
    public static SystemPropertyReading Failed(string service, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SystemPropertyReading(service, null, reason);
    }
}

/// <summary>
/// The system properties the engine reads, and how a capture's readings become what a version of the cache holds.
/// </summary>
public static class SystemProperties
{
    /// <summary>The indexer, which decides how a partition's records are indexed (openapi indexer v2, <c>GET /info</c>).</summary>
    public const string Indexer = "indexer";

    /// <summary>The search service, which decides how a query over them is answered (openapi search v2, <c>GET /info</c>).</summary>
    public const string Search = "search";

    /// <summary>
    /// Whether the indexer gives every text property a <c>keywordLower</c> sub-field beside its <c>keyword</c> one
    /// (<c>IndexerConfigurationProperties.KEYWORD_LOWER_FEATURE_NAME</c>): a lowercased copy of the whole value, which is
    /// what an exact match regardless of case asks.
    /// </summary>
    public const string KeywordLower = "featureFlag.keywordLower.enabled";

    /// <summary>
    /// The properties every captured version names, known or not, because the engine reads them. A service that has never
    /// been read leaves them unknown, which nothing relies on being on.
    /// </summary>
    public static IReadOnlyList<(string Service, string Name)> Required { get; } = [(Indexer, KeywordLower)];

    /// <summary>
    /// The system properties a version holds once <paramref name="readings"/> are merged onto <paramref name="current"/>:
    /// a service that answered replaces every property it reported before, a service that could not be asked keeps what
    /// the current version knows of it, and every required property is there, unknown when nothing has ever said. Without
    /// readings (an import from files, which asks no platform) the properties are the current ones, exactly.
    /// </summary>
    /// <remarks>
    /// Carrying a property across a failed read is deliberate: a platform's settings do not change because one request
    /// for them failed, and writing unknown would write a new version, render every record built from the cache again,
    /// and write another when the next read succeeds.
    /// </remarks>
    public static IReadOnlyList<SystemProperty> Merge(IReadOnlyList<SystemProperty> current, IReadOnlyList<SystemPropertyReading> readings)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0)
        {
            return Ordered(current);
        }

        var merged = current.ToDictionary(p => (p.Service, p.Name));
        foreach (var reading in readings)
        {
            if (reading.Properties is { } properties)
            {
                foreach (var stale in merged.Keys.Where(k => string.Equals(k.Service, reading.Service, StringComparison.Ordinal)).ToList())
                {
                    merged.Remove(stale);
                }

                foreach (var property in properties)
                {
                    merged[(property.Service, property.Name)] = property;
                }
            }
        }

        foreach (var (service, name) in Required)
        {
            if (merged.ContainsKey((service, name)))
            {
                continue;
            }

            var unread = readings.FirstOrDefault(r => string.Equals(r.Service, service, StringComparison.Ordinal));
            var detail = unread switch
            {
                { Unread: { } reason } => $"the {service} could not be asked: {reason}",
                { Properties: not null } => $"the {service} does not report it for the partition",
                _ => $"no capture has asked the {service} yet",
            };
            merged[(service, name)] = new SystemProperty(service, name, SystemPropertyState.Unknown, null, detail);
        }

        return Ordered(merged.Values);
    }

    /// <summary>
    /// The state <paramref name="text"/> names exactly as a version or a render context stores it, or null for anything
    /// else: a number, another case or a name the engine does not know is refused rather than guessed at.
    /// </summary>
    public static SystemPropertyState? ParseState(string? text)
        => text is not null && Enum.GetNames<SystemPropertyState>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<SystemPropertyState>(text)
            : null;

    /// <summary>Properties in the order a version stores and hashes them: by service, then by name.</summary>
    public static IReadOnlyList<SystemProperty> Ordered(IEnumerable<SystemProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties
            .OrderBy(p => p.Service, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What a render that searches is pinned to: every <see cref="Required"/> property in the state
    /// <paramref name="known"/> gives it, unknown where it gives none (a partition no capture has asked yet, or a host
    /// without the module's database). Only the state is kept, since a render reads nothing else of a property, and the
    /// words a service or a failed read explained it with change without the property changing.
    /// </summary>
    public static IReadOnlyList<SystemProperty> Pinned(IEnumerable<SystemProperty> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        var states = new Dictionary<(string Service, string Name), SystemPropertyState>();
        foreach (var property in known)
        {
            states.TryAdd((property.Service, property.Name), property.State);
        }

        return Ordered(Required.Select(r => new SystemProperty(r.Service, r.Name, states.GetValueOrDefault(r, SystemPropertyState.Unknown), null, null)));
    }

    /// <summary>
    /// Whether <paramref name="properties"/> say the partition's indexer keeps a lowercased keyword of text, so a lookup
    /// that finds nothing exactly may ask again regardless of case. False when the property is off, unknown or not named:
    /// only a property known to be on changes how a lookup is written.
    /// </summary>
    public static bool KeywordLowerOn(IEnumerable<SystemProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Any(p => p.IsEnabled
            && string.Equals(p.Service, Indexer, StringComparison.Ordinal)
            && string.Equals(p.Name, KeywordLower, StringComparison.Ordinal));
    }
}
