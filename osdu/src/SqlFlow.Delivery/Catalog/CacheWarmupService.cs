using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// Whether a host reads the current version of every partition's cache before anyone asks for it, and how often it looks
/// for a newer one (section <c>Osdu:CacheWarmup</c>; on a node, <c>OSDU_CACHE_WARMUP</c> and <c>OSDU_CACHE_WARMUP_SECONDS</c>).
/// A render needs the whole version of the cache it reads in memory, and reading a large one (hundreds of thousands of
/// records) takes seconds, which without this the first render after a host starts, or after a refresh writes a version,
/// waits through.
/// </summary>
public sealed class CacheWarmupOptions
{
    /// <summary>The configuration section the control plane binds this from.</summary>
    public const string SectionName = "Osdu:CacheWarmup";

    /// <summary>The fewest seconds between two looks: each is a query, and a version is written far less often.</summary>
    public const int MinimumPollSeconds = 5;

    /// <summary>The most seconds between two looks: past an hour, a refresh's version waits too long to be read ahead.</summary>
    public const int MaximumPollSeconds = 3600;

    /// <summary>Reads each partition's current version at start and every version written after it. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between the looks at which version of each partition's cache is current. Default 60.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public void Validate()
    {
        if (PollSeconds is < MinimumPollSeconds or > MaximumPollSeconds)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{SectionName}:PollSeconds must be between {MinimumPollSeconds} and {MaximumPollSeconds}: each look at which cache version is current is a query, and a refresh writes a version far less often than that."));
        }
    }

    /// <summary>
    /// The options a node reads from its environment, as it reads every other setting: <c>OSDU_CACHE_WARMUP</c>
    /// (<c>true</c> or <c>false</c>) and <c>OSDU_CACHE_WARMUP_SECONDS</c>. A value the setting cannot hold stops the node
    /// with a message naming it, rather than being read as the default.
    /// </summary>
    /// <param name="variable">Reads one environment variable.</param>
    /// <exception cref="InvalidOperationException">A variable holds something the setting cannot be, or a value outside its range.</exception>
    public static CacheWarmupOptions FromEnvironment(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var options = new CacheWarmupOptions();
        if (Text(variable, "OSDU_CACHE_WARMUP") is { } enabled)
        {
            options.Enabled = bool.TryParse(enabled, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"OSDU_CACHE_WARMUP is '{enabled}'; it is true or false.");
        }

        if (Text(variable, "OSDU_CACHE_WARMUP_SECONDS") is { } seconds)
        {
            options.PollSeconds = int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"OSDU_CACHE_WARMUP_SECONDS is '{seconds}'; it is a whole number of seconds.");
        }

        options.Validate();
        return options;
    }

    /// <summary>One environment variable, trimmed, or null when it is absent or blank.</summary>
    private static string? Text(Func<string, string?> variable, string name)
        => variable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>What a look found of one partition's current cache version.</summary>
public enum CacheWarmState
{
    /// <summary>The version is in memory, and this is the first look to find it there: it was read now, or by a render before.</summary>
    Read,

    /// <summary>The version was in memory at an earlier look, and still is (it is read again when memory let it go).</summary>
    Held,

    /// <summary>The version cannot be read (its records no longer match the hash it was written with); it is not tried again.</summary>
    Refused,
}

/// <summary>One partition's current cache version as a look left it.</summary>
/// <param name="Scope">The partition whose cache it is.</param>
/// <param name="Version">The version current at the look.</param>
/// <param name="State">Whether it is in memory, and since when.</param>
/// <param name="Items">How many records the version holds.</param>
public sealed record CacheWarmed(string Scope, string Version, CacheWarmState State, long Items);

/// <summary>
/// Reads the current version of every partition's cache into memory before a render asks for it: at start, and at each look
/// (every <see cref="CacheWarmupOptions.PollSeconds"/>) that finds a newer version current, whichever process wrote it. A
/// render (a run's, a record's Render, a flow's Preview) then finds the version in memory instead of reading it, and one that
/// asks while it is read waits on the same read (<see cref="ICacheStore.LoadAsync"/>). It reads through the store renders read
/// through, so nothing is read twice and nothing is kept that a render would not keep.
/// </summary>
/// <remarks>
/// Hosted by the control plane, whose renders are a person's and, in a single-node estate, every run's, and by a node, which
/// runs runs. A host without the module's database has no cache, and this does nothing there. Each version read stays in
/// memory as long as the store keeps it: the store keeps the versions asked for last, so the current version of each
/// partition is kept as long as it is current, at the memory its records take. A version whose records no longer match its
/// hash is reported once and not read again; any other failure (the database not answering) is tried again at the next look.
/// </remarks>
public sealed partial class CacheWarmupService : BackgroundService
{
    private readonly IServiceProvider? _services;
    private readonly ICacheStore? _cache;
    private readonly CacheWarmupOptions _options;
    private readonly ILogger<CacheWarmupService> _logger;

    /// <summary>The version of each partition found in memory at an earlier look.</summary>
    private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);

    /// <summary>The version of each partition that could not be read, which is not tried again while it is current.</summary>
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);

    /// <summary>The service a host composes: the store is the module database's, when the host has one.</summary>
    public CacheWarmupService(IServiceProvider services, CacheWarmupOptions options, ILogger<CacheWarmupService> logger)
        : this(options, logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>The service over a store a test gives it.</summary>
    internal CacheWarmupService(ICacheStore cache, CacheWarmupOptions options, ILogger<CacheWarmupService> logger)
        : this(options, logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    private CacheWarmupService(CacheWarmupOptions options, ILogger<CacheWarmupService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolved here rather than composed in: a node started without the module's database has no cache, and must start.
        var cache = _cache ?? (_services is { } services ? services.GetService<DeliveryLedgerSource>()?.Cache(services) : null);
        if (cache is null)
        {
            LogNoCache();
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(cache, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this look; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogLookFailed(_options.PollSeconds, SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One look: the current version of every partition's cache, each read into memory unless it is there already or cannot
    /// be read. One look runs at a time. A failure to reach the database ends the look, and the next tries again.
    /// </summary>
    internal async Task<IReadOnlyList<CacheWarmed>> PassAsync(ICacheStore cache, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var current = await cache.CurrentVersionsAsync(ct).ConfigureAwait(false);
        var looked = new List<CacheWarmed>(current.Count);
        foreach (var version in current)
        {
            if (_refused.TryGetValue(version.Scope, out var refused) && string.Equals(refused, version.Version, StringComparison.Ordinal))
            {
                looked.Add(new CacheWarmed(version.Scope, version.Version, CacheWarmState.Refused, version.Items));
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            ReferenceSnapshot? snapshot;
            try
            {
                snapshot = await cache.LoadAsync(version.Scope, version.Version, ct).ConfigureAwait(false);
            }
            catch (CacheVersionPrunedException)
            {
                // A newer version became current and the retention pruned this one since the look began: the next look reads that one.
                continue;
            }
            catch (DeliveryException ex)
            {
                _refused[version.Scope] = version.Version;
                LogRefused(version.Version, version.Scope, SecretHygiene.RedactedMessage(ex));
                looked.Add(new CacheWarmed(version.Scope, version.Version, CacheWarmState.Refused, version.Items));
                continue;
            }

            if (snapshot is null)
            {
                // The version's row went between the two reads: only a version no longer current can, so the next look has the newer one.
                continue;
            }

            if (_held.TryGetValue(version.Scope, out var held) && string.Equals(held, version.Version, StringComparison.Ordinal))
            {
                looked.Add(new CacheWarmed(version.Scope, version.Version, CacheWarmState.Held, version.Items));
                continue;
            }

            _held[version.Scope] = version.Version;
            LogRead(version.Version, version.Scope, version.Items, Stopwatch.GetElapsedTime(started).TotalSeconds);
            looked.Add(new CacheWarmed(version.Scope, version.Version, CacheWarmState.Read, version.Items));
        }

        // A partition whose cache is gone is forgotten, so a cache written for it again is read again.
        var partitions = current.Select(v => v.Scope).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _held.Keys.Concat(_refused.Keys).Where(scope => !partitions.Contains(scope)).ToList())
        {
            _held.Remove(gone);
            _refused.Remove(gone);
        }

        return looked;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Version {Version} of the cache of partition '{Scope}' ({Items} records) is in memory, {Seconds:F1} s after it was asked for: a render against it reads none of its records.")]
    private partial void LogRead(string version, string scope, long items, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Version {Version} of the cache of partition '{Scope}' cannot be read ahead, and is not tried again while it is current: {Reason}")]
    private partial void LogRefused(string version, string scope, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking at which cache versions are current failed; looking again in {Seconds} s: {Reason}")]
    private partial void LogLookFailed(int seconds, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "This host has no module database, so it keeps no cache, and no cache version is read ahead.")]
    private partial void LogNoCache();
}
