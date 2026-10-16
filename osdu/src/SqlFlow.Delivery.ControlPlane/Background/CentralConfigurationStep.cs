using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Engine;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// The module's step of the control plane's start-up pass (<c>osdu-configuration</c>): writes the central configuration the
/// control plane's configuration sets into the module's database, in one transaction: the partitions it names
/// (<c>Osdu:Partitions</c>) into the partition registry with their values, and the values set for no partition
/// (<c>Osdu:Properties</c>), which every run of every kind resolves with. It runs after the module database is verified
/// against this build, so the rows it writes are this build's. A partition it registers makes every repository due for a
/// sync, which describes the registry-driven flows in it, as registering one by hand does. Until it completes, and while
/// the registry holds no partition after it, the control plane is not ready (<see cref="PartitionReadiness"/>).
/// </summary>
/// <remarks>
/// Once written, every Key Vault secret the configuration names (a partition's client secret, a connection kept in the
/// vault) is read once with the control plane's own identity, and the step fails naming each one that cannot be read, so
/// a secret not yet put in the vault is reported where the deployment looks (the start-up pass), not by every run that
/// would need it. The pass retries the step, and it succeeds once the secrets are there.
/// </remarks>
public sealed partial class CentralConfigurationStep : IBootstrapStep
{
    private readonly IReadOnlyList<ConfiguredPartition> _partitions;
    private readonly IReadOnlyDictionary<string, string> _properties;
    private readonly PartitionReadiness _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<CentralConfigurationStep> _logger;

    /// <exception cref="InvalidOperationException">The configuration is not one the control plane may write; the message names the setting.</exception>
    public CentralConfigurationStep(
        IOptions<ConfiguredPartitionsOptions> options, PartitionReadiness readiness, TimeProvider clock, ILogger<CentralConfigurationStep> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _partitions = options.Value.Configured();
        _properties = options.Value.ConfiguredProperties();
        _readiness = readiness;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The step's name in the start-up pass.</summary>
    public string Name => "osdu-configuration";

    public async Task<string> ApplyAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var contexts = services.GetRequiredService<DeliveryLedgerSource>().Contexts(services);
        if (contexts is null)
        {
            if (_partitions.Count > 0 || _properties.Count > 0)
            {
                LogNoDatabase(_partitions.Count, _properties.Count);
            }

            _readiness.MarkWritten();
            return "no module database to write into";
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var applied = await ConfiguredPartitions.ApplyAsync(contexts, _partitions, _properties, now, ct).ConfigureAwait(false);
        if (applied.Registered.Count > 0)
        {
            await RepositoryResync.NowAsync(
                services.GetRequiredService<CatalogDbContext>(), services.GetRequiredService<RepoSyncSignal>(), now, ct).ConfigureAwait(false);
        }

        if (applied.Changed)
        {
            LogApplied(
                Names(applied.Registered), Names(applied.Adopted), Names(applied.Released), applied.DefaultNow ?? "unchanged",
                applied.PropertiesWritten, applied.PropertiesReleased);
        }

        _readiness.MarkWritten();
        if ((await services.GetRequiredService<DeliveryPartitionRegistry>().ReadAsync(ct).ConfigureAwait(false)).All.Count == 0)
        {
            LogNoPartition(PartitionReadiness.NoPartition);
        }

        var missing = await UnreadableVaultSecretsAsync(_partitions, _properties, services.GetRequiredService<ISecretResolver>(), ct).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The configuration names {missing.Count} Key Vault secret(s) the control plane cannot read; put each in the vault (or grant this control plane's identity Key Vault Secrets User on it): {string.Join("; ", missing)}");
        }

        return applied.Changed
            ? $"partitions registered {Names(applied.Registered)}, taken over {Names(applied.Adopted)}, released {Names(applied.Released)}; {applied.PropertiesWritten} value(s) set, {applied.PropertiesReleased} released"
            : $"{_partitions.Count} partition(s) and {_properties.Count} value(s) set for no partition, unchanged";
    }

    private static string Names(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    /// <summary>
    /// Reads each Key Vault secret <paramref name="partitions"/> and <paramref name="properties"/> name, once, and answers one
    /// line per secret that cannot be read: the reference, the settings that name it, and the reason (redacted). A secret's
    /// value is never kept or shown.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UnreadableVaultSecretsAsync(
        IReadOnlyList<ConfiguredPartition> partitions, IReadOnlyDictionary<string, string> properties, ISecretResolver secrets, CancellationToken ct)
    {
        var named = partitions
            .SelectMany(p => p.Properties.Select(v => (Setting: ConfiguredPartitions.KeyOf(p.Name, v.Key), v.Value)))
            .Concat(properties.Select(v => (Setting: ConfiguredPartitions.PropertyKey(v.Key), v.Value)))
            .Where(v => v.Value.TrimStart().StartsWith(VaultScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var missing = new List<string>();
        foreach (var reference in named.Select(v => v.Value.Trim()).Distinct(StringComparer.Ordinal))
        {
            try
            {
                await secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var settings = string.Join(", ", named.Where(v => v.Value.Trim() == reference).Select(v => v.Setting));
                missing.Add($"{reference} ({settings}): {SecretHygiene.RedactedMessage(ex)}");
            }
        }

        return missing;
    }

    private const string VaultScheme = "${keyvault:";

    [LoggerMessage(Level = LogLevel.Information, Message = "Configured partitions written: registered {Registered}; taken over {Adopted}; no longer configured {Released}; default {Default}; {Written} value(s) set, {ReleasedValues} no longer configured.")]
    private partial void LogApplied(string registered, string adopted, string released, string @default, int written, int releasedValues);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configuration names {Partitions} partition(s) and {Properties} value(s) set for no partition, and this control plane has no module database to write them into.")]
    private partial void LogNoDatabase(int partitions, int properties);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message} The control plane reports its start-up as not done (/health/startup) until one is registered.")]
    private partial void LogNoPartition(string message);
}
