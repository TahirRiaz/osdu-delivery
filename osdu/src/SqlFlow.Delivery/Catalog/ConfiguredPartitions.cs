using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// One OSDU partition as the control plane's configuration names it (<c>Osdu:Partitions:&lt;name&gt;</c>): the
/// data-partition-id, what it is for, whether runs that name no partition run in it, and the values its runs resolve their
/// flows' <c>${env:NAME}</c> references with (the endpoint, the token URL and scope, the client id, a
/// <c>${keyvault:...}</c> reference to the client secret, the ACLs, the legal tag).
/// </summary>
/// <param name="Name">The data-partition-id, as runs name it.</param>
/// <param name="Description">What it is for; null for nothing said.</param>
/// <param name="IsDefault">True for the partition a run that names none runs in.</param>
/// <param name="Properties">The central configuration's values for runs bound to it, by the name a flow spells.</param>
public sealed record ConfiguredPartition(
    string Name, string? Description, bool IsDefault, IReadOnlyDictionary<string, string> Properties);

/// <summary>What one application of the configuration changed.</summary>
/// <param name="Registered">The partitions it registered, which were not registered before.</param>
/// <param name="Adopted">The partitions it took over or described again: registered before, now as the configuration says.</param>
/// <param name="Released">The partitions the configuration no longer names: still registered, no longer configured.</param>
/// <param name="DefaultNow">The partition it made the default, or null when the default stood.</param>
/// <param name="PropertiesWritten">The properties it set or took over.</param>
/// <param name="PropertiesReleased">The properties the configuration no longer sets: kept, no longer configured.</param>
public sealed record ConfiguredPartitionsApplied(
    IReadOnlyList<string> Registered,
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Released,
    string? DefaultNow,
    int PropertiesWritten,
    int PropertiesReleased)
{
    /// <summary>True when anything was changed.</summary>
    public bool Changed => Registered.Count > 0 || Adopted.Count > 0 || Released.Count > 0 || DefaultNow is not null
        || PropertiesWritten > 0 || PropertiesReleased > 0;
}

/// <summary>
/// The partitions the control plane's configuration names (osdu/docs/reference/concepts/partitions.md, Configured
/// partitions): checked when the control plane starts, and written into the partition registry and the central
/// configuration once the module's database is verified, so a deployment comes up with the partitions it delivers to and
/// the values its runs need, without anyone registering them.
/// </summary>
/// <remarks>
/// <para>
/// What the configuration sets is the configuration's: the registry and the central configuration mark the rows, and the
/// Partitions page, the API and <c>sqlflow partition</c> and <c>sqlflow config</c> refuse to change them
/// (<see cref="SetByConfigurationException"/>). Applying adds and updates; it removes nothing. A partition or a value the
/// configuration stops naming stays as it is, no longer marked, for an admin to keep or remove.
/// </para>
/// <para>
/// A value whose name says it is a secret (ending in SECRET, PASSWORD or TOKEN, or naming an API key) must be a
/// <c>${keyvault:vault/secret}</c> or <c>${env:NAME}</c> reference: the value is written into a catalog row and carried
/// in run payloads, where a literal secret is a defect. The node resolves the reference when a run needs it.
/// </para>
/// </remarks>
public static class ConfiguredPartitions
{
    /// <summary>The configuration section the partitions are named in, one child per data-partition-id.</summary>
    public const string SectionPath = "Osdu:Partitions";

    /// <summary>Who the registry and the central configuration record as having set what the configuration sets.</summary>
    public const string Actor = "configuration";

    /// <summary>The setting that names <paramref name="partition"/>.</summary>
    public static string KeyOf(string partition) => $"{SectionPath}:{partition}";

    /// <summary>The setting that gives <paramref name="partition"/>'s value of <paramref name="property"/>.</summary>
    public static string KeyOf(string partition, string property) => $"{SectionPath}:{partition}:Properties:{property}";

    /// <summary>The configuration section the values set for no partition are given in, one child per name.</summary>
    public const string PropertiesPath = "Osdu:Properties";

    /// <summary>The setting that gives the value set for no partition of <paramref name="property"/>.</summary>
    public static string PropertyKey(string property) => $"{PropertiesPath}:{property}";

    /// <summary>
    /// Whether <paramref name="name"/> says its value is a secret, which the configuration then takes only as a reference.
    /// </summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var upper = name.ToUpperInvariant();
        return upper.EndsWith("SECRET", StringComparison.Ordinal)
            || upper.EndsWith("PASSWORD", StringComparison.Ordinal)
            || upper.EndsWith("TOKEN", StringComparison.Ordinal)
            || upper.Contains("APIKEY", StringComparison.Ordinal)
            || upper.Contains("API_KEY", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="value"/> is one <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference and nothing else.</summary>
    public static bool IsReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        return (trimmed.StartsWith("${env:", StringComparison.Ordinal) || trimmed.StartsWith("${keyvault:", StringComparison.Ordinal))
            && trimmed.EndsWith('}')
            && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1
            && trimmed.Length > trimmed.IndexOf(':', StringComparison.Ordinal) + 2;
    }

    /// <summary>
    /// <paramref name="declared"/> checked and settled: names, descriptions and values trimmed, ordered by name. Every
    /// refusal names the setting at fault.
    /// </summary>
    /// <exception cref="InvalidOperationException">A partition or a value is not one the configuration may hold.</exception>
    public static IReadOnlyList<ConfiguredPartition> Check(IEnumerable<ConfiguredPartition> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var partitions = new List<ConfiguredPartition>();
        foreach (var partition in declared)
        {
            ArgumentNullException.ThrowIfNull(partition);
            string name;
            try
            {
                name = PartitionNames.Check(partition.Name, $"A partition named under {SectionPath}");
            }
            catch (FlowValidationException invalid)
            {
                throw new InvalidOperationException(invalid.Message, invalid);
            }

            if (partitions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } twice)
            {
                throw new InvalidOperationException(
                    $"{SectionPath} names partition '{name}' twice ('{twice.Name}' and '{name}'); a partition is one data-partition-id, whatever its case.");
            }

            var description = string.IsNullOrWhiteSpace(partition.Description) ? null : partition.Description.Trim();
            if (description is { Length: > DeliveryPartition.MaxDescriptionLength })
            {
                throw new InvalidOperationException(
                    $"{KeyOf(name)}:Description is {description.Length} characters; a partition's description is at most {DeliveryPartition.MaxDescriptionLength}.");
            }

            partitions.Add(partition with
            {
                Name = name,
                Description = description,
                Properties = Settle(partition.Properties, property => KeyOf(name, property), $"{KeyOf(name)}:Properties"),
            });
        }

        if (partitions.Count > DeliveryConfigNames.MaxPerRun)
        {
            throw new InvalidOperationException(
                $"{SectionPath} names {partitions.Count} partitions; a run carries the values of at most {DeliveryConfigNames.MaxPerRun}.");
        }

        var defaults = partitions.Where(p => p.IsDefault).Select(p => p.Name).ToList();
        if (defaults.Count > 1)
        {
            throw new InvalidOperationException(
                $"{SectionPath} makes {string.Join(" and ", defaults.Select(d => $"'{d}'"))} the default; one partition is the default, the one a run that names none runs in.");
        }

        if (defaults.Count == 0 && partitions.Count > 1)
        {
            throw new InvalidOperationException(
                $"{SectionPath} names {partitions.Count} partitions and none is the default; set {KeyOf("<name>")}:Default to true on the one a run that names no partition runs in.");
        }

        return [.. partitions.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The values set for no partition the configuration gives (<see cref="PropertiesPath"/>), checked and settled as a
    /// partition's are: every run of every kind resolves with them, a pre-ingestion or ingestion flow's connections as much
    /// as a delivery's platform. Every refusal names the setting at fault.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one the configuration may hold.</exception>
    public static IReadOnlyDictionary<string, string> CheckProperties(IReadOnlyDictionary<string, string>? properties)
        => Settle(properties, PropertyKey, PropertiesPath);

    private static IReadOnlyDictionary<string, string> Settle(
        IReadOnlyDictionary<string, string>? properties, Func<string, string> keyOf, string scope)
    {
        var settled = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (properties is null)
        {
            return settled;
        }

        foreach (var (name, value) in properties)
        {
            if (!DeliveryConfigNames.IsName(name))
            {
                throw new InvalidOperationException(
                    $"{keyOf(name)} does not name a configuration property: a property is named as a flow spells it in ${{env:NAME}}, a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
            }

            if (settled.Keys.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } twice)
            {
                throw new InvalidOperationException(
                    $"{scope} sets '{twice}' and '{name}', which the central configuration holds as one name.");
            }

            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new InvalidOperationException($"{keyOf(name)} is empty; leave the setting out instead.");
            }

            if (!DeliveryConfigNames.IsValue(trimmed))
            {
                throw new InvalidOperationException(
                    $"{keyOf(name)} is longer than {DeliveryConfigNames.MaxValueLength} characters ({trimmed.Length}) or holds a control character; a property holds an identifier, a URL, a connection string or a ${{env:...}} or ${{keyvault:...}} reference.");
            }

            if (IsSecretName(name) && !IsReference(trimmed))
            {
                // The value itself is never repeated: it is the secret.
                throw new InvalidOperationException(
                    $"{keyOf(name)} holds a value where a secret's reference belongs. Keep the secret in a key vault and set the reference: ${{keyvault:<vault>/<secret>}}, which the node resolves when a run needs it.");
            }

            if (SqlFlow.Core.Secrets.SuppliedReferences.EmbedsLiteralSecret(trimmed))
            {
                // Nor here: the connection string holds the secret.
                throw new InvalidOperationException(
                    $"{keyOf(name)} is a connection string with its password, secret or key written out. Keep the credential in a key vault and write it as a reference (Password=${{keyvault:<vault>/<secret>}}), or connect with an identity (Authentication=Active Directory Default).");
            }

            settled[name] = trimmed;
        }

        if (settled.Count > DeliveryConfigNames.MaxPerRun)
        {
            throw new InvalidOperationException(
                $"{scope} sets {settled.Count} values; a run carries at most {DeliveryConfigNames.MaxPerRun} in one scope.");
        }

        return settled;
    }

    /// <summary>
    /// Writes <paramref name="configured"/> into the partition registry and the central configuration in one transaction:
    /// registers each partition the registry does not hold, takes over and describes each it does, makes the configured
    /// default the default, sets each value for its partition at the control plane's scope, sets each of
    /// <paramref name="properties"/> (the values set for no partition) at the control plane's scope, and releases what is
    /// marked as configured and is configured no longer. Applying the same configuration again changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration is not one <see cref="Check"/> and <see cref="CheckProperties"/> accept.</exception>
    /// <exception cref="DeliveryException">The registry or the configuration could not be written; nothing was changed.</exception>
    public static async Task<ConfiguredPartitionsApplied> ApplyAsync(
        Func<OsduDbContext> contexts, IEnumerable<ConfiguredPartition> configured, IReadOnlyDictionary<string, string> properties,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(properties);
        var partitions = Check(configured);
        var based = CheckProperties(properties);
        await using var db = contexts();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var applied = await ApplyAsync(db, partitions, based, nowUtc, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return applied;
            }).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new DeliveryException(
                $"The configured partitions could not be written ({ex.GetBaseException().Message}); nothing was changed.", ex);
        }
    }

    /// <summary>
    /// <see cref="ApplyAsync(Func{OsduDbContext}, IEnumerable{ConfiguredPartition}, IReadOnlyDictionary{string, string}, DateTime, CancellationToken)"/>
    /// for a configuration that sets no value for no partition: the partitions alone, releasing any such value an earlier
    /// configuration set.
    /// </summary>
    public static Task<ConfiguredPartitionsApplied> ApplyAsync(
        Func<OsduDbContext> contexts, IEnumerable<ConfiguredPartition> configured, DateTime nowUtc, CancellationToken ct = default)
        => ApplyAsync(contexts, configured, System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty, nowUtc, ct);

    private static async Task<ConfiguredPartitionsApplied> ApplyAsync(
        OsduDbContext db, IReadOnlyList<ConfiguredPartition> partitions, IReadOnlyDictionary<string, string> based,
        DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.DeliveryPartitions.ToListAsync(ct).ConfigureAwait(false);
        var registered = new List<string>();
        var adopted = new List<string>();
        var released = new List<string>();

        // The registry's spelling of each configured partition: a partition registered before keeps the name its caches and
        // ledgers are kept under.
        var spelled = new Dictionary<string, DeliveryPartition>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in partitions)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.Name, partition.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new DeliveryPartition
                {
                    Name = partition.Name,
                    Description = partition.Description,
                    IsConfigured = true,
                    CreatedUtc = nowUtc,
                    CreatedBy = Actor,
                    UpdatedUtc = nowUtc,
                    UpdatedBy = Actor,
                };
                db.DeliveryPartitions.Add(row);
                rows.Add(row);
                registered.Add(row.Name);
            }
            else if (!row.IsConfigured || !string.Equals(row.Description, partition.Description, StringComparison.Ordinal))
            {
                row.IsConfigured = true;
                row.Description = partition.Description;
                row.UpdatedUtc = nowUtc;
                row.UpdatedBy = Actor;
                adopted.Add(row.Name);
            }

            spelled[partition.Name] = row;
        }

        foreach (var row in rows.Where(r => r.IsConfigured && !spelled.ContainsKey(r.Name)))
        {
            row.IsConfigured = false;
            row.UpdatedUtc = nowUtc;
            row.UpdatedBy = Actor;
            released.Add(row.Name);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The default: the one marked, or the only one named. The old default is cleared and saved first, because the
        // index that keeps one default checks each statement.
        string? defaultNow = null;
        if (partitions.Count > 0)
        {
            var wanted = spelled[(partitions.FirstOrDefault(p => p.IsDefault) ?? partitions[0]).Name];
            if (!wanted.IsDefault)
            {
                foreach (var old in rows.Where(r => r.IsDefault))
                {
                    old.IsDefault = false;
                    old.UpdatedUtc = nowUtc;
                    old.UpdatedBy = Actor;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                wanted.IsDefault = true;
                wanted.UpdatedUtc = nowUtc;
                wanted.UpdatedBy = Actor;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                defaultNow = wanted.Name;
            }
        }

        // The values, at the control plane's own scope for each partition. Names are matched as the column's collation and
        // its unique index match them, regardless of case, and take the configuration's spelling.
        var properties = await db.DeliveryConfigProperties
            .Where(c => c.RepoId == null && c.Partition != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var configuredKeys = new HashSet<(string Partition, string Name)>();
        var written = 0;
        foreach (var partition in partitions)
        {
            var scope = spelled[partition.Name].Name;
            foreach (var (name, value) in partition.Properties)
            {
                configuredKeys.Add((scope.ToUpperInvariant(), name.ToUpperInvariant()));
                var row = properties.FirstOrDefault(c =>
                    string.Equals(c.Partition, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    db.DeliveryConfigProperties.Add(new DeliveryConfigProperty
                    {
                        Id = Guid.NewGuid(),
                        RepoId = null,
                        Partition = scope,
                        Name = name,
                        Value = value,
                        IsConfigured = true,
                        UpdatedUtc = nowUtc,
                        UpdatedBy = Actor,
                    });
                    written++;
                }
                else if (!row.IsConfigured || !string.Equals(row.Value, value, StringComparison.Ordinal) || !string.Equals(row.Name, name, StringComparison.Ordinal))
                {
                    row.Name = name;
                    row.Value = value;
                    row.IsConfigured = true;
                    row.UpdatedUtc = nowUtc;
                    row.UpdatedBy = Actor;
                    written++;
                }
            }
        }

        var releasedProperties = 0;
        foreach (var row in properties.Where(c => c.IsConfigured && !configuredKeys.Contains((c.Partition!.ToUpperInvariant(), c.Name.ToUpperInvariant()))))
        {
            row.IsConfigured = false;
            row.UpdatedUtc = nowUtc;
            row.UpdatedBy = Actor;
            releasedProperties++;
        }

        // The values set for no partition, at the control plane's own scope, the same way.
        var baseRows = await db.DeliveryConfigProperties
            .Where(c => c.RepoId == null && c.Partition == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var (name, value) in based)
        {
            var row = baseRows.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                db.DeliveryConfigProperties.Add(new DeliveryConfigProperty
                {
                    Id = Guid.NewGuid(),
                    RepoId = null,
                    Partition = null,
                    Name = name,
                    Value = value,
                    IsConfigured = true,
                    UpdatedUtc = nowUtc,
                    UpdatedBy = Actor,
                });
                written++;
            }
            else if (!row.IsConfigured || !string.Equals(row.Value, value, StringComparison.Ordinal) || !string.Equals(row.Name, name, StringComparison.Ordinal))
            {
                row.Name = name;
                row.Value = value;
                row.IsConfigured = true;
                row.UpdatedUtc = nowUtc;
                row.UpdatedBy = Actor;
                written++;
            }
        }

        foreach (var row in baseRows.Where(c => c.IsConfigured && !based.Keys.Any(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))))
        {
            row.IsConfigured = false;
            row.UpdatedUtc = nowUtc;
            row.UpdatedBy = Actor;
            releasedProperties++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ConfiguredPartitionsApplied(registered, adopted, released, defaultNow, written, releasedProperties);
    }
}

/// <summary>
/// A change of something the control plane's configuration sets (a configured partition, the default it names, a value it
/// sets), refused because the next start would set it back. The message names the setting to change instead.
/// </summary>
public sealed class SetByConfigurationException : DeliveryException
{
    public SetByConfigurationException(string message, string setting)
        : base(message)
    {
        Setting = setting;
    }

    /// <summary>The configuration setting that holds what was to be changed.</summary>
    public string Setting { get; }
}
