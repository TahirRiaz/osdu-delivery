using System.Globalization;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>One cached type as one cache flow declares it, the way the catalog holds it after the repository sync.</summary>
public sealed record CacheTypeDeclaration(
    string FlowName,
    string TypeName,
    string EntityType,
    string Kind,
    string Query,
    IReadOnlyList<ReferenceFieldSpec> Fields,
    CacheChangeMode OnChange);

/// <summary>
/// What one partition's cache holds as every synced cache flow declares it (design.md section 6.2). Several flows may declare
/// the same type: the cache holds one type under that name, the union of what they declare. Every declared path is kept for
/// every record whichever flow captured it, so a value one project asks for is there for every pipeline reading the
/// partition, and a type's changes wait for approval when any flow declaring it asks for that.
/// </summary>
public sealed class CacheDeclaration
{
    private readonly Dictionary<string, List<CacheTypeDeclaration>> _byType;

    public CacheDeclaration(string scope, IEnumerable<CacheTypeDeclaration> declarations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(declarations);
        Scope = scope;
        _byType = declarations
            .OrderBy(d => d.FlowName, StringComparer.Ordinal)
            .GroupBy(d => d.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A partition no synced cache flow declares anything for.</summary>
    public static CacheDeclaration None(string scope) => new(scope, []);

    public string Scope { get; }

    public bool IsEmpty => _byType.Count == 0;

    /// <summary>Every type some flow declares for the partition.</summary>
    public IReadOnlyCollection<string> TypeNames => _byType.Keys;

    /// <summary>Every flow's declaration of one type, in flow name order.</summary>
    public IReadOnlyList<CacheTypeDeclaration> Of(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _byType.TryGetValue(typeName, out var declared) ? declared : [];
    }

    /// <summary>The paths the cache keeps for a type: every name any flow declares, the first flow in name order giving its path.</summary>
    public IReadOnlyList<ReferenceFieldSpec> FieldsOf(string typeName)
    {
        var fields = new List<ReferenceFieldSpec>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in Of(typeName))
        {
            fields.AddRange(declaration.Fields.Where(field => names.Add(field.Name)));
        }

        return fields;
    }

    /// <summary>What a change to a type does: approval when any flow declaring it asks for that; <paramref name="undeclared"/> when none declares it.</summary>
    public CacheChangeMode ModeOf(string typeName, CacheChangeMode undeclared)
    {
        var declared = Of(typeName);
        if (declared.Count == 0)
        {
            return undeclared;
        }

        return declared.Any(d => d.OnChange == CacheChangeMode.Approve) ? CacheChangeMode.Approve : CacheChangeMode.Auto;
    }

    /// <summary>
    /// What makes <paramref name="type"/>, as <paramref name="flowName"/> declares it, disagree with another flow's declaration
    /// of the same type in the partition: a different entity type, or a name cached from a different path. Merged, either
    /// would hold two meanings under one name.
    /// </summary>
    public IReadOnlyList<string> Conflicts(string flowName, ReferenceTypeSpec type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(type);
        var problems = new List<string>();
        foreach (var other in Of(type.Name).Where(d => !string.Equals(d.FlowName, flowName, StringComparison.Ordinal)))
        {
            if (!string.Equals(other.EntityType, type.EntityType, StringComparison.Ordinal))
            {
                problems.Add($"cache flow '{other.FlowName}' declares {type.Name} as {other.EntityType}, and '{flowName}' declares it as {type.EntityType}");
            }

            foreach (var field in type.Fields)
            {
                if (other.Fields.FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)) is { } theirs
                    && !string.Equals(ReferenceField.Normalize(theirs.Path), ReferenceField.Normalize(field.Path), StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{type.Name}.{field.Name} is cached from {theirs.Path} by cache flow '{other.FlowName}' and from {field.Path} by '{flowName}'");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Refuses a flow whose types disagree with another synced flow of the partition about what a name holds. Merged, the
    /// cache would hold two meanings under one name, and whichever flow wrote last would decide which one every pipeline
    /// reads. A refresh and an import check the same way before they write anything.
    /// </summary>
    /// <exception cref="DeliveryException">A type of <paramref name="types"/> conflicts with another flow's declaration.</exception>
    public void ThrowOnConflicts(string flowName, IEnumerable<ReferenceTypeSpec> types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(types);
        var problems = types.SelectMany(type => Conflicts(flowName, type)).ToList();
        if (problems.Count > 0)
        {
            throw new DeliveryException(
                $"Cache flow '{flowName}' disagrees with another cache flow of partition '{Scope}' about what the cache holds, so nothing was captured or imported: {string.Join("; ", problems)}. Make the declarations agree, or give one of the types another name.");
        }
    }

    /// <summary>A flow's type widened to every path the partition keeps for it, so its capture fills them all.</summary>
    public ReferenceTypeSpec Widen(ReferenceTypeSpec type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var fields = type.Fields.ToList();
        var names = fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        fields.AddRange(FieldsOf(type.Name).Where(field => names.Add(field.Name)));
        return type with { Fields = fields };
    }
}

/// <summary>A cached record of a type, as the membership of the partition's cache names it.</summary>
public readonly record struct CacheMemberKey(string TypeName, string RecordId)
{
    /// <summary>Type names compare as mappings read them (ignoring case); record ids exactly, as OSDU holds them.</summary>
    public static IEqualityComparer<CacheMemberKey> Comparer { get; } = new KeyComparer();

    private sealed class KeyComparer : IEqualityComparer<CacheMemberKey>
    {
        public bool Equals(CacheMemberKey x, CacheMemberKey y)
            => string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase) && string.Equals(x.RecordId, y.RecordId, StringComparison.Ordinal);

        public int GetHashCode(CacheMemberKey obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeName), StringComparer.Ordinal.GetHashCode(obj.RecordId));
    }
}

/// <summary>The partition's cache after one flow's capture is merged in, and the types it no longer holds.</summary>
public sealed record CacheMergePlan(ReferenceSnapshot Snapshot, IReadOnlyList<string> RemovedTypes);

/// <summary>
/// How one cache flow's capture enters its partition's cache. A captured record replaces what the cache held for it, so the
/// newest capture of a record is what every pipeline reads, whichever flow made it. A record the cache holds that the capture
/// did not find leaves only when no other flow's last capture still holds it, because another project's query may be what
/// keeps it. Types the capture does not cover are untouched, except a type no synced flow declares any more.
/// </summary>
public static class CacheMerge
{
    private static readonly IReadOnlySet<string> NoFlows = new HashSet<string>(StringComparer.Ordinal);

    /// <param name="current">The partition's current version, or null when the cache holds none.</param>
    /// <param name="members">For every record of the captured types, the flows whose last capture held it.</param>
    /// <param name="flowName">The cache flow whose capture is merged.</param>
    /// <param name="captured">The captured types, each holding every record the flow's query matched.</param>
    /// <param name="declaredTypes">The types synced flows declare for the partition; empty when none are synced, which removes no type.</param>
    /// <param name="version">The label of the version the merge would write.</param>
    /// <param name="capturedUtc">When the capture was made.</param>
    public static CacheMergePlan Apply(
        ReferenceSnapshot? current,
        IReadOnlyDictionary<CacheMemberKey, IReadOnlySet<string>> members,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        IReadOnlyCollection<string> declaredTypes,
        string version,
        DateTimeOffset capturedUtc)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(declaredTypes);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var types = new Dictionary<string, ReferenceType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in current?.Types ?? [])
        {
            types[type.Name] = type;
        }

        foreach (var capture in captured)
        {
            var existing = types.GetValueOrDefault(capture.Name);
            if (existing is not null && !string.Equals(existing.EntityType, capture.EntityType, StringComparison.Ordinal))
            {
                throw new DeliveryException(
                    $"The cache holds {existing.Name} as {existing.EntityType}, and cache flow '{flowName}' captured it as {capture.EntityType}. One name cannot hold both, so nothing was written; give one of the types another name.");
            }

            var items = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
            foreach (var item in existing?.Items ?? [])
            {
                items[item.Id] = item;
            }

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in capture.Items)
            {
                items[item.Id] = item;
                found.Add(item.Id);
            }

            foreach (var id in items.Keys.Where(id => !found.Contains(id)).ToList())
            {
                var holders = members.TryGetValue(new CacheMemberKey(capture.Name, id), out var flows) ? flows : NoFlows;
                if (!holders.Any(flow => !string.Equals(flow, flowName, StringComparison.Ordinal)))
                {
                    items.Remove(id);
                }
            }

            types[capture.Name] = new ReferenceType(existing?.Name ?? capture.Name, capture.EntityType, items.Values);
        }

        var removed = new List<string>();
        if (declaredTypes.Count > 0)
        {
            var declared = new HashSet<string>(declaredTypes, StringComparer.OrdinalIgnoreCase);
            declared.UnionWith(captured.Select(t => t.Name));
            foreach (var name in types.Keys.Where(name => !declared.Contains(name)).ToList())
            {
                removed.Add(types[name].Name);
                types.Remove(name);
            }
        }

        return new CacheMergePlan(new ReferenceSnapshot(version, capturedUtc, types.Values).Normalized(), removed);
    }
}

/// <summary>The label of a cache version: the capture instant, sortable; the store appends the sequence when two captures share a second.</summary>
public static class CacheVersionLabel
{
    public static string Mint(DateTimeOffset capturedUtc)
        => capturedUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
