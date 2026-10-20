namespace SqlFlow.Delivery.Order;

/// <summary>
/// A property of a schema that refers to other records: its path, as a template names it (<c>osdu.data.WellboreID</c>,
/// <c>osdu.data.Curves[].CurveUnit</c>), and the entity types its <c>x-osdu-relationship</c> names
/// (<c>master-data--Wellbore</c>), a group alone (<c>dataset</c>) or an entity alone.
/// </summary>
public sealed record SchemaRelationship(string Property, IReadOnlyList<string> Targets);

/// <summary>One kind's schema as the order reads it: the kind, its entity type, and every property that refers to other records.</summary>
public sealed record SchemaNode(string Kind, string EntityType, IReadOnlyList<SchemaRelationship> Relationships);

/// <summary>
/// The relationships OSDU's schemas declare, kind by kind: OSDU's own lineage, read from its schema definitions and
/// nothing else (osdu/docs/reference/concepts/osdu-order.md). Which records a delivery writes and which properties its
/// mapping fills are the delivery's; what each property may refer to is the schema's, the same for every repository
/// and every partition, which is what lets the order of a partition span repositories.
/// </summary>
public sealed class OsduSchemaGraph
{
    private readonly Dictionary<string, SchemaNode> _byKind;

    private OsduSchemaGraph(Dictionary<string, SchemaNode> byKind) => _byKind = byKind;

    /// <summary>A graph holding no schema.</summary>
    public static OsduSchemaGraph Empty { get; } = new(new Dictionary<string, SchemaNode>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The schemas the graph holds, in kind order.</summary>
    public IReadOnlyList<SchemaNode> Nodes => [.. _byKind.Values.OrderBy(n => n.Kind, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// A graph of <paramref name="nodes"/>. A kind given twice must say the same of its relationships; its entity type must
    /// be the one its kind names. Relationships are kept in the order given, each property once.
    /// </summary>
    public static OsduSchemaGraph Of(IEnumerable<SchemaNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var byKind = new Dictionary<string, SchemaNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            var entity = OsduEntity.OfKind(node.Kind)
                ?? throw new ArgumentException($"'{node.Kind}' is not an OSDU kind (authority:source:group--Entity:version).", nameof(nodes));
            if (!string.Equals(entity, node.EntityType, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The schema of {node.Kind} is said to be of {node.EntityType}, but its kind names {entity}.", nameof(nodes));
            }

            var normalized = Normalize(node with { Kind = node.Kind.Trim(), EntityType = entity });
            if (byKind.TryGetValue(normalized.Kind, out var known))
            {
                if (!SameRelationships(known, normalized))
                {
                    throw new ArgumentException($"The schema of {normalized.Kind} was given twice, and the two disagree on what its properties refer to.", nameof(nodes));
                }

                continue;
            }

            byKind[normalized.Kind] = normalized;
        }

        return new OsduSchemaGraph(byKind);
    }

    /// <summary>The schema of <paramref name="kind"/>, or null when the graph does not hold it.</summary>
    public SchemaNode? Find(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return _byKind.GetValueOrDefault(kind.Trim());
    }

    /// <summary>
    /// The relationships of <paramref name="kind"/> that <paramref name="filled"/> fills, or every relationship when
    /// <paramref name="filled"/> is null: a mapping refers to what the properties it writes point to, and a delivery whose
    /// mapping is not known is taken to refer to everything its schema allows. Null when the graph does not hold the kind.
    /// </summary>
    public IReadOnlyList<SchemaRelationship>? RelationshipsOf(string kind, IReadOnlyCollection<string>? filled)
    {
        if (Find(kind) is not { } node)
        {
            return null;
        }

        if (filled is null)
        {
            return node.Relationships;
        }

        var wanted = new HashSet<string>(filled, StringComparer.Ordinal);
        return [.. node.Relationships.Where(r => wanted.Contains(r.Property))];
    }

    /// <summary>
    /// The entity types each entity type of the graph may refer to, over every kind of it the graph holds: OSDU's lineage
    /// at the level the order works at, a reference to the entity type itself left out.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> EntityReferences()
    {
        var entities = _byKind.Values.Select(n => n.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            var targets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in _byKind.Values.Where(n => string.Equals(n.EntityType, entity, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var target in node.Relationships.SelectMany(r => r.Targets))
                {
                    foreach (var other in entities.Where(e => !string.Equals(e, entity, StringComparison.OrdinalIgnoreCase) && OsduEntity.Names(target, e)))
                    {
                        targets.Add(other);
                    }
                }
            }

            result[entity] = targets;
        }

        return result;
    }

    private static SchemaNode Normalize(SchemaNode node)
    {
        var relationships = new List<SchemaRelationship>();
        foreach (var relationship in node.Relationships ?? [])
        {
            ArgumentNullException.ThrowIfNull(relationship);
            if (string.IsNullOrWhiteSpace(relationship.Property))
            {
                throw new ArgumentException($"A relationship of {node.Kind} names no property.", nameof(node));
            }

            var targets = (relationship.Targets ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0 || relationships.Any(r => string.Equals(r.Property, relationship.Property, StringComparison.Ordinal)))
            {
                continue;
            }

            relationships.Add(new SchemaRelationship(relationship.Property, targets));
        }

        return node with { Relationships = relationships };
    }

    private static bool SameRelationships(SchemaNode a, SchemaNode b)
        => a.Relationships.Count == b.Relationships.Count
            && a.Relationships.Zip(b.Relationships).All(p =>
                string.Equals(p.First.Property, p.Second.Property, StringComparison.Ordinal)
                && p.First.Targets.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(p.Second.Targets.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase));
}
