using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The central configuration: the values the control plane supplies to the runs it queues, so a flow naming
/// <c>${env:NAME}</c> resolves it from one place rather than from whatever the node that picks the run up happens to
/// hold.
/// </summary>
/// <remarks>
/// <para>
/// A property is set for the whole control plane, and may be set again for one repository, which is an estate. The
/// repository's value wins for the flows that repository holds, so two estates in one catalog deliver to two partitions
/// without either naming its partition in a document.
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
    /// What a run of a flow of <paramref name="repoId"/> is given: the control plane's own properties, with the
    /// repository's own overriding them by name. Empty when nothing is set, which leaves every reference to the node.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EffectiveAsync(Guid? repoId, CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var db = Open();
        var rows = await db.DeliveryConfigProperties
            .AsNoTracking()
            .Where(c => c.RepoId == null || c.RepoId == repoId)
            .Select(c => new { c.RepoId, c.Name, c.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.RepoId is null))
        {
            effective[row.Name] = row.Value;
        }

        // A repository's own value is read second, so it replaces the control plane's for the same name.
        foreach (var row in rows.Where(r => r.RepoId is not null))
        {
            effective[row.Name] = row.Value;
        }

        return effective;
    }

    /// <summary>
    /// The properties set at <paramref name="repoId"/> alone (null for the control plane's own), newest first, as a
    /// listing shows them.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryConfigProperty>> ListAsync(Guid? repoId, CancellationToken ct = default)
    {
        if (_contexts is null)
        {
            return [];
        }

        await using var db = Open();
        return await db.DeliveryConfigProperties
            .AsNoTracking()
            .Where(c => c.RepoId == repoId)
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
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own), replacing what was
    /// there. Returns the row as it now stands.
    /// </summary>
    /// <exception cref="FlowValidationException">The name or the value is not one a property may hold.</exception>
    public async Task<DeliveryConfigProperty> SetAsync(
        Guid? repoId,
        string name,
        string value,
        string? description,
        string actor,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
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
            .FirstOrDefaultAsync(c => c.RepoId == repoId && c.Name == name, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new DeliveryConfigProperty { Id = Guid.NewGuid(), RepoId = repoId, Name = name };
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
    /// Removes <paramref name="name"/> at <paramref name="repoId"/> (null for the control plane's own). False when it was
    /// not set there, which is not an error: removing what is already absent leaves the same state.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid? repoId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var db = Open();
        var removed = await db.DeliveryConfigProperties
            .Where(c => c.RepoId == repoId && c.Name == name)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return removed > 0;
    }
}
