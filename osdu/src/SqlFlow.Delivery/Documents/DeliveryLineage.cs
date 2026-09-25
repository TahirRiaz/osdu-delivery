using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a delivery flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3). It reads its record table and
/// every child dataset table on the server its <c>source.connection</c> names, so an ingestion flow writing those tables
/// through the same reference is ordered before it. It reads the payload files under the root of the payload set its
/// protocol streams, every partition cache type its mapping resolves against, and every OSDU kind its mapping's searches
/// look in, so the flow that delivers the records it searches for is ordered before it. It writes the OSDU type its
/// mapping fills in the partition it delivers to and, for the file and manifest protocols, the dataset kind it registers
/// each file as.
/// The mapping is the one the flow pins, read from the checkout being scanned and nowhere else; a mapping that cannot be read
/// costs the flow its OSDU nodes with a warning, never the rest of its lineage.
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

    /// <summary>
    /// Everything a source contributes: what each of its interfaces reads and writes, in document order, each declaration
    /// once however many interfaces make it.
    /// </summary>
    public static RegisteredFlowLineage Describe(SourceDefinition source, RegisteredLineageContext context, DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(source);
        var described = source.Interfaces.Select(i => Describe(i, context, documents)).ToList();
        return new RegisteredFlowLineage
        {
            Objects = described.SelectMany(d => d.Objects).Distinct().ToList(),
            Files = described.SelectMany(d => d.Files).Distinct().ToList(),
            Datasets = described.SelectMany(d => d.Datasets).Distinct().ToList(),
            Warnings = described.SelectMany(d => d.Warnings).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Everything one flow (an interface of its source) contributes, in a stable order.</summary>
    public static RegisteredFlowLineage Describe(FlowDefinition flow, RegisteredLineageContext context, DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        var who = $"delivery flow '{flow.Label}'";
        var warnings = new List<string>();

        var files = new List<DeclaredFileLocation>();
        var sent = PayloadParts.Of(flow)?.Select(p => p.Payload).ToList()
            ?? (PayloadParts.Streamed(flow) is { } payloadName ? [payloadName] : []);
        foreach (var name in sent)
        {
            if (flow.Source.Payloads.TryGetValue(name, out var payload))
            {
                files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = payload.Root, FilePattern = payload.Pattern });
            }
        }

        var datasets = new List<DeclaredDataset>();
        var partition = OsduLineage.Partition(flow.Target.Headers, who, "target.headers", warnings);
        var mapping = PinnedMapping(flow, context, documents, who, warnings);
        if (partition is not null)
        {
            var endpoint = flow.Target.Endpoint;
            if (mapping is not null)
            {
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, mapping.Kind, who, $"the kind of mapping '{mapping.Reference}'", warnings));
                foreach (var type in CacheTypes(mapping))
                {
                    Add(datasets, OsduLineage.CacheType(LineageRelation.Reads, partition, type, who, warnings));
                }

                // A search reads the platform's records of its kind as the delivery renders, not a capture of them.
                foreach (var search in mapping.Searches.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
                {
                    Add(datasets, OsduLineage.Type(
                        LineageRelation.Reads, endpoint, partition, search.Kind, who, $"search '{search.Name}' of mapping '{mapping.Reference}'", warnings));
                }
            }

            // The routes that register datasets beside the record write the dataset kind too; the workflow route writes
            // each input's own kind.
            if (flow.Target.Protocol is DeliveryProtocol.File or DeliveryProtocol.Manifest or DeliveryProtocol.Dataset
                or DeliveryProtocol.FileAndDdms or DeliveryProtocol.ManifestAndDdms
                || (flow.Target.Protocol == DeliveryProtocol.Workflow && flow.Target.Workflow?.Anchor == WorkflowAnchor.Storage && flow.Source.Payloads.ContainsKey(PayloadParts.Files)))
            {
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, flow.Target.ProtocolOptions.DatasetKind, who,
                    "target.protocolOptions.datasetKind", warnings));
            }

            foreach (var input in flow.Target.Workflow?.Inputs ?? [])
            {
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, input.DatasetKind, who, $"workflow.inputs.{input.Name}.datasetKind", warnings));
            }
        }

        return new RegisteredFlowLineage
        {
            Objects = DeclaredObjects(flow),
            Files = files,
            Datasets = datasets,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The partition cache types a mapping reads, from its cache sources and their lookups and from the tables its replaces
    /// read, in name order (<see cref="MappingDefinition.CacheTypesRead"/>). A search source's lookups name a search, not a
    /// cache type, and are not among them.
    /// </summary>
    public static IReadOnlyList<string> CacheTypes(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.CacheTypesRead();
    }

    /// <summary>
    /// The mapping the flow pins, read from the mappings directory the flow's layout names inside the scanned checkout, or
    /// null with a warning saying why it could not be.
    /// </summary>
    private static MappingDefinition? PinnedMapping(
        FlowDefinition flow, RegisteredLineageContext context, DeliveryDocumentLoader documents, string who, List<string> warnings)
    {
        var reference = flow.Render.Mapping;
        var layout = DeliveryLayout.ResolveWithin(flow, context.DocumentFolder, context.Contains);
        if (layout is null)
        {
            warnings.Add(
                $"{who} shows no OSDU type in lineage: render.mappings '{flow.Render.MappingsDirectory}' lies outside the repository, so mapping '{reference}' is not read.");
            return null;
        }

        try
        {
            return new MappingCatalog(layout.MappingsDirectory, documents).Load(reference, path => Shown(context, path));
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
        {
            var reason = ex is SqlFlowException ? ex.Message : $"{ex.GetType().Name} reading it";
            warnings.Add($"{who} shows no OSDU type in lineage: mapping '{reference}' could not be read ({SecretHygiene.RedactedMessage(reason)}).");
            return null;
        }
    }

    /// <summary>A path as a warning names it: relative to the checkout, with forward slashes.</summary>
    private static string Shown(RegisteredLineageContext context, string path)
        => Path.GetRelativePath(context.EstateRoot, path).Replace('\\', '/');

    private static void Add(List<DeclaredDataset> datasets, DeclaredDataset? dataset)
    {
        if (dataset is not null)
        {
            datasets.Add(dataset);
        }
    }
}
