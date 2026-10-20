using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Ordering;

/// <summary>
/// The module's side of the OSDU order (osdu/docs/reference/concepts/osdu-order.md): a template as a node of OSDU's schema
/// graph, and a delivery flow (or an interface of a source) as a delivery of a partition, with the properties its mapping
/// fills. Lineage, <c>sqlflow order</c>, the control plane and a run all describe deliveries here, so they see one order.
/// </summary>
public static class OrderDeliveries
{
    /// <summary>
    /// A template as the schema graph holds it: its kind, its entity type, and every variable whose schema declares an
    /// <c>x-osdu-relationship</c>, by the path a mapping targets it with. Null for a template whose kind names no entity type.
    /// </summary>
    public static SchemaNode? Node(OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (OsduEntity.OfKind(template.Kind) is not { } entity)
        {
            return null;
        }

        var relationships = template.Variables
            .Where(v => v.Relationships.Count > 0)
            .Select(v => new SchemaRelationship(v.Path.Text, v.Relationships))
            .ToList();
        return new SchemaNode(template.Kind, entity, relationships);
    }

    /// <summary>
    /// The partition <paramref name="flow"/>'s records reach, as the OSDU order groups deliveries: the one its
    /// <c>target.headers</c> name (a flow bound to a partition names that one), or <see cref="OsduOrder.EveryPartition"/> for
    /// a source that leaves its partitions to the registry and is not bound to one. Null when the headers name no partition
    /// a cache could be kept under, which its own description reports.
    /// </summary>
    public static string? PartitionOf(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FollowsRegistry && flow.Partition is null)
        {
            return OsduOrder.EveryPartition;
        }

        try
        {
            return Snapshots.CacheScope.Of(flow.Target.Headers, "target.headers");
        }
        catch (SqlFlow.Core.FlowValidationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="flow"/> as a delivery of <paramref name="partition"/>: the kind its mapping fills, the template paths
    /// the mapping fills (null when the template is not at hand, so every relationship of the kind counts), the ones it
    /// fills by a search, and whether its records wait for what they refer to.
    /// </summary>
    public static OrderDelivery Of(FlowDefinition flow, string partition, MappingDefinition mapping, OsduTemplate? template, SchemaNode? node = null, string repository = "")
    {
        ArgumentNullException.ThrowIfNull(flow);
        return Of(flow.Name, flow.Interface, partition, mapping, template, node, repository);
    }

    /// <summary>
    /// A delivery of <paramref name="partition"/> by flow <paramref name="flowName"/> (interface <paramref name="interfaceName"/>)
    /// filling <paramref name="mapping"/>: the template paths it fills are read against <paramref name="template"/> when it is
    /// at hand, else against <paramref name="node"/> (a kind's relationships from a built release of OSDU's lineage), and
    /// are not known when neither is, so every relationship of the kind counts.
    /// </summary>
    public static OrderDelivery Of(
        string flowName, string? interfaceName, string partition, MappingDefinition mapping, OsduTemplate? template, SchemaNode? node, string repository = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(repository);
        List<string>? filled = null;
        var searched = new List<string>();
        if (template is not null || node is not null)
        {
            filled = [];
            var known = template is not null
                ? template.Variables.Select(v => v.Path.Text).ToHashSet(StringComparer.Ordinal)
                : node!.Relationships.Select(r => r.Property).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in mapping.Entries.SelectMany(e => e.Fillers))
            {
                var path = template?.Find(entry.Target)?.Path.Text ?? entry.Target.Text;
                Add(filled, path);

                // A value written as a literal (a list of objects, an object) fills every property it holds, at whatever
                // depth: a literal technical assurance refers to its type as a mapped one does.
                foreach (var inside in LiteralPaths(path, entry.Static).Concat(entry.Parts.SelectMany(p => LiteralPaths(path + "[]", p.Static))))
                {
                    if (known.Contains(inside))
                    {
                        Add(filled, inside);
                    }
                }

                if (entry.ValueNodes.Any(n => n.Source?.Kind == MappingSourceKind.Search))
                {
                    Add(searched, path);
                }
            }
        }

        return new OrderDelivery
        {
            Partition = partition,
            Repository = repository,
            Flow = flowName,
            Interface = string.IsNullOrEmpty(interfaceName) ? null : interfaceName,
            Kind = mapping.Kind,
            Filled = filled,
            Searched = searched,
        };
    }

    /// <summary>The template paths a literal value written at <paramref name="path"/> fills: each property of an object, each item of a list, at any depth.</summary>
    private static IEnumerable<string> LiteralPaths(string path, JsonNode? value)
    {
        var pending = new Stack<(string Path, JsonNode? Node, int Depth)>();
        pending.Push((path, value, 0));
        while (pending.Count > 0)
        {
            var (at, node, depth) = pending.Pop();
            if (depth > OsduTemplate.MaxDepth)
            {
                continue;
            }

            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj)
                    {
                        var inside = $"{at}.{name}";
                        yield return inside;
                        pending.Push((inside, child, depth + 1));
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        pending.Push(($"{at}[]", item, depth + 1));
                    }

                    break;
            }
        }
    }

    private static void Add(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.Ordinal))
        {
            paths.Add(path);
        }
    }

    /// <summary>The delivery <paramref name="flow"/> is in <paramref name="partition"/>, as a plan looks deliveries up by: repository, flow and interface.</summary>
    public static OrderDelivery Identity(FlowDefinition flow, string partition, string kind, string repository = "")
    {
        ArgumentNullException.ThrowIfNull(flow);
        return new OrderDelivery { Partition = partition, Repository = repository, Flow = flow.Name, Interface = flow.Interface, Kind = kind };
    }

    /// <summary>The pattern a mapping node reads an entity type the order waits for by: every kind of it, any authority, source or version.</summary>
    public static string Pattern(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return $"*:*:{entityType}:*";
    }
}
