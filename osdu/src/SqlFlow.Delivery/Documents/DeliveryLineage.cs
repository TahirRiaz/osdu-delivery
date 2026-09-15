using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a delivery flow contributes to SQLFlow's lineage: reads of its record table and of every child dataset table, on
/// the server its <c>source.connection</c> names. An ingestion flow writing those tables through the same connection
/// reference lands on the same nodes, so the execution plan orders pre-ingestion, then ingestion, then the delivery flow.
/// </summary>
public static class DeliveryLineage
{
    /// <summary>The objects the flow reads, the record table first and the datasets in name order.</summary>
    public static IReadOnlyList<DeclaredDataObject> DeclaredObjects(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var source = flow.Source;
        var objects = new List<DeclaredDataObject>(1 + source.Datasets.Count)
        {
            DeclaredDataObject.Reads(source.Connection, source.Record.Object),
        };

        foreach (var (_, dataset) in source.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            objects.Add(DeclaredDataObject.Reads(source.Connection, dataset.Object));
        }

        return objects;
    }
}
