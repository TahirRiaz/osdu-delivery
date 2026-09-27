using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The central configuration a run is given, in the two layers a run resolves with: <see cref="Base"/>, the values set for
/// no partition, and <see cref="Partitions"/>, the values set for single partitions by partition name, which a run bound to
/// one of them reads first (docs/partitions-design.md section 5).
/// </summary>
public sealed record DeliveryConfiguration(
    IReadOnlyDictionary<string, string> Base,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Partitions)
{
    /// <summary>No configuration: every reference is left to the node.</summary>
    public static DeliveryConfiguration None { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>True when nothing is configured at any scope.</summary>
    public bool IsEmpty => Base.Count == 0 && Partitions.Count == 0;

    /// <summary>What a run bound to <paramref name="partition"/> resolves with: its values over the base; the base alone for none.</summary>
    public IReadOnlyDictionary<string, string> For(string? partition)
    {
        if (partition is null || !Partitions.TryGetValue(partition, out var own) || own.Count == 0)
        {
            return Base;
        }

        var merged = new Dictionary<string, string>(Base, StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            merged[name] = value;
        }

        return merged;
    }
}

/// <summary>
/// The central configuration: the values the control plane supplies to the runs it queues, so a flow naming
/// <c>${env:NAME}</c> resolves it from one place rather than from whatever the node that picks the run up happens to
/// hold.
/// </summary>
/// <remarks>
/// <para>
/// A property is set for the whole control plane, and may be set again for one repository, which is an estate. The
/// repository's value wins for the flows that repository holds. Either may be set for one OSDU partition as well, and a
/// run bound to that partition resolves with it first: the repository's value for the partition, the control plane's for
/// the partition, the repository's, the control plane's.
/// </para>
/// <para>
/// A value is a non-secret value or a <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, which travels
/// unresolved and is resolved on the node. A literal secret here would be a secret in a catalog row and in a run
/// payload, which is a defect.
/// </para>
/// </remarks>
public sealed class DeliveryConfigStore
{
    private readonly Func<OsduDbContext>? _contexts;

    /// <param name="contexts">
    /// The module database, or null when the host was started without one. Reading then answers that nothing is
    /// configured, which leaves every reference to the nodes; setting one says the host has no database to set it in.
    /// </param>
    public DeliveryConfigStore(Func<OsduDbContext>? contexts) => _contexts = contexts;

    /// <summary>Whether this control plane has a module database to hold a configuration at all.</summary>
    public bool Available => _contexts is not null;

    /// <summary>The module database, or a failure naming what the host was started without.</summary>
    private OsduDbContext Open()
        => (_contexts ?? throw new FlowValidationException(
            "The central configuration lives in the module's database, which this host was started without. Start it with the module's connection (Osdu:Database:Connection or SQLFLOW_OSDU_DB)."))();

    /// <summary>
    /// What a run of a flow of <paramref name="repoId"/> is given, in its two layers: the values set for no partition (the
    /// control plane's, with the repository's overriding them by name), and for each partition any value is set for, the
    /// values set for it (the control plane's, with the repository's overriding them). None when nothing is set.
    /// </summary>
    public async Task<DeliveryConfiguration> ConfigurationAsync(Guid? repoId, CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return DeliveryConfiguration.None;
        }

        await using var db = Open();
        var rows = await db.DeliveryConfigProperties
            .AsNoTracking()
            .Where(c => c.RepoId == null || c.RepoId == repoId)
            .Select(c => new { c.RepoId, c.Partition, c.Name, c.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var based = new Dictionary<string, string>(StringComparer.Ordinal);
        var partitions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // The control plane's rows first and the repository's second, so a repository's value replaces the control
        // plane's for the same name in the same layer.
        foreach (var row in rows.OrderBy(r => r.RepoId is not null))
        {
            if (row.Partition is null)
            {
                based[row.Name] = row.Value;
                continue;
            }

            if (!partitions.TryGetValue(row.Partition, out var own))
            {
                own = new Dictionary<string, string>(StringComparer.Ordinal);
                partitions[row.Partition] = own;
            }

            own[row.Name] = row.Value;
        }

        return new DeliveryConfiguration(
            based,
            partitions.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a run of a flow of <paramref name="repoId"/> bound to <paramref name="partition"/> resolves with (the values set
    /// for no partition when it is null): the view a person checks the configuration by, flattened.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EffectiveAsync(Guid? repoId, string? partition, CancellationToken ct = default)
        => (await ConfigurationAsync(repoId, ct).ConfigureAwait(false)).For(partition);

    /// <summary>
    /// The properties set at <paramref name="repoId"/> and <paramref name="partition"/> alone (null for the control plane's
    /// own, and for no partition), by name, as a listing shows them.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryConfigProperty>> ListAsync(Guid? repoId, string? partition, CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return [];
        }

        await using var db = Open();
        return await db.DeliveryConfigProperties
            .AsNoTracking()
            .Where(c => c.RepoId == repoId && c.Partition == partition)
            .OrderBy(c => c.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Every property of every scope, for a listing that shows what a whole control plane holds.</summary>
    public async Task<IReadOnlyList<DeliveryConfigProperty>> ListAllAsync(CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return [];
        }

        await using var db = Open();
        return await db.DeliveryConfigProperties
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.RepoId)
            .ThenBy(c => c.Partition)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own) for
    /// <paramref name="partition"/> (null for no particular partition), replacing what was there. Returns the row as it now
    /// stands.
    /// </summary>
    /// <exception cref="FlowValidationException">The name, the value or the partition is not one a property may hold.</exception>
    public async Task<DeliveryConfigProperty> SetAsync(
        Guid? repoId,
        string? partition,
        string name,
        string value,
        string? description,
        string actor,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var scope = CheckPartition(partition);
        if (!DeliveryConfigNames.IsName(name))
        {
            throw new FlowValidationException(
                $"'{name}' does not name a configuration property: a property is named as a flow spells it in ${{env:NAME}}, which is a letter or underscore followed by letters, digits and underscores, at most {DeliveryConfigNames.MaxNameLength} characters.");
        }

        if (!DeliveryConfigNames.IsValue(value))
        {
            throw new FlowValidationException(
                $"the value of '{name}' is empty, longer than {DeliveryConfigNames.MaxValueLength} characters, or holds a control character; a property holds an identifier, a URL or a ${{env:...}} or ${{keyvault:...}} reference.");
        }

        await using var db = Open();
        var row = await db.DeliveryConfigProperties
            .FirstOrDefaultAsync(c => c.RepoId == repoId && c.Partition == scope && c.Name == name, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new DeliveryConfigProperty { Id = Guid.NewGuid(), RepoId = repoId, Partition = scope, Name = name };
            db.DeliveryConfigProperties.Add(row);
        }

        row.Value = value;
        row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        row.UpdatedUtc = nowUtc;
        row.UpdatedBy = actor;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Removes <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own) for
    /// <paramref name="partition"/> (null for no particular partition). False when it was not set there, which is not an
    /// error: removing what is already absent leaves the same state.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid? repoId, string? partition, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var scope = CheckPartition(partition);
        await using var db = Open();
        var removed = await db.DeliveryConfigProperties
            .Where(c => c.RepoId == repoId && c.Partition == scope && c.Name == name)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }

    /// <summary>A partition a property is set for: null for none, or a data-partition-id written literally.</summary>
    /// <exception cref="FlowValidationException">The value is not a data-partition-id.</exception>
    private static string? CheckPartition(string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return null;
        }

        var name = partition.Trim();
        return CacheScope.IsPartitionId(name)
            ? name
            : throw new FlowValidationException(
                $"'{name}' is not a data-partition-id a property can be set for: letters, digits, underscore, hyphen and dot, at most {CacheScope.MaxLength} characters.");
    }
}
