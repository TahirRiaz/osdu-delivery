using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What an interface's records refer to, read from its mapping and the template the mapping fills: every property the
/// mapping writes whose schema declares an <c>x-osdu-relationship</c> (docs/interfaces-design.md section 6). A property is
/// counted however it is filled (a column, the cache, a static value), because the record carries the reference either
/// way. The run's preflight, <c>sqlflow check</c> and the control plane's listing of a flow's interfaces all describe an
/// interface here, so they order a source the same way.
/// </summary>
public static class InterfaceSchemas
{
    public static InterfaceSchema Describe(string interfaceName, MappingDefinition mapping, OsduTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(template);
        var references = new List<InterfaceReference>();
        foreach (var entry in mapping.Entries)
        {
            if (template.Find(entry.Target) is not { Relationships.Count: > 0 } variable
                || references.Any(r => string.Equals(r.Property, entry.Target.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            references.Add(new InterfaceReference(entry.Target.Text, variable.Relationships));
        }

        return new InterfaceSchema(interfaceName, mapping.Kind, references);
    }
}
