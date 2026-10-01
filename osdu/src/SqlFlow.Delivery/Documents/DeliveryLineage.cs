using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Templates;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a delivery flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3). It reads its record table and
/// every child dataset table on the server its <c>source.connection</c> names, so an ingestion flow writing those tables
/// through the same reference is ordered before it. It reads the payload files under the root of the payload set its
/// protocol streams, and the mapping it pins, which is a node of its own (section 3.1). It writes the OSDU type its
/// mapping fills in the partition it delivers to and, for the file and manifest protocols, the dataset kind it registers
/// each file as.
/// <para>
/// The mapping reads what a render reads through it: every partition cache type it names, every cache type holding
/// records of an entity type the ids it builds are checked against, and every OSDU kind its searches look in. Those are
/// reads of the mapping's node, so the graph draws each into the mapping and the mapping into the flow, and the flow
/// inherits them: the cache flow filling a type, and the flow delivering the records a search looks for, are ordered
/// before it.
/// </para>
/// The mapping is the one the flow pins, read from the checkout being scanned; the template it pins is read from the
/// module database where the host has one (<see cref="MappingTemplateSource"/>). A mapping that cannot be read costs the
/// flow its OSDU nodes with a warning, never the rest of its lineage.
/// </summary>
public static class DeliveryLineage
{
    /// <summary>How many template variables a warning names before it counts the rest.</summary>
    private const int MaxNamedVariables = 5;

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
    /// once however many interfaces make it. A source that names its partitions contributes for every one of them, each
    /// interface bound to the partition, so its OSDU, cache and mapping nodes are the partition's (docs/partitions-design.md
    /// section 6); one that leaves them to the registry contributes once, under <see cref="PartitionNames.Every"/>.
    /// </summary>
    public static RegisteredFlowLineage Describe(
        SourceDefinition source, RegisteredLineageContext context, DeliveryDocumentLoader documents, MappingTemplateSource? templates = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        var scan = new Scan(context, documents, templates);

        // A source that leaves its partitions to the registry is described once, under the partition that stands for every
        // registered one: lineage is computed from the documents, and the registry is the catalog's.
        var described = (source.FollowsRegistry ? source.Interfaces : source.EveryLedger(RegisteredPartitions.None))
            .Select(i => Describe(i, scan))
            .ToList();
        return new RegisteredFlowLineage
        {
            Objects = described.SelectMany(d => d.Objects).Distinct().ToList(),
            Files = described.SelectMany(d => d.Files).Distinct().ToList(),
            Datasets = described.SelectMany(d => d.Datasets).Distinct().ToList(),

            // Two interfaces pinning one mapping for one partition read one node; what each says it reads adds up.
            Derivations = described.SelectMany(d => d.Derivations)
                .GroupBy(d => d.Dataset)
                .Select(g => new DeclaredDerivation { Dataset = g.Key, From = g.SelectMany(d => d.From).Distinct().ToList() })
                .ToList(),
            Warnings = described.SelectMany(d => d.Warnings).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Everything one flow (an interface of its source) contributes, in a stable order.</summary>
    public static RegisteredFlowLineage Describe(
        FlowDefinition flow, RegisteredLineageContext context, DeliveryDocumentLoader documents, MappingTemplateSource? templates = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(documents);
        return Describe(flow, new Scan(context, documents, templates));
    }

    private static RegisteredFlowLineage Describe(FlowDefinition flow, Scan scan)
    {
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
        var derivations = new List<DeclaredDerivation>();
        var partition = flow.FollowsRegistry && flow.Partition is null
            ? PartitionNames.Every
            : OsduLineage.Partition(flow.Target.Headers, who, "target.headers", warnings);
        var pinned = PinnedMapping(flow, scan, who, warnings);
        if (partition is not null)
        {
            var endpoint = flow.Target.Endpoint;
            if (pinned is { } found)
            {
                var mapping = found.Mapping;
                Add(datasets, OsduLineage.Type(
                    LineageRelation.Writes, endpoint, partition, mapping.Kind, who, $"the kind of mapping '{mapping.Reference}'", warnings));

                var reads = MappingReads(mapping, endpoint, partition, scan, who, warnings);
                if (OsduLineage.Mapping(endpoint, partition, found.Directory, mapping.Reference, who, warnings) is { } node)
                {
                    derivations.Add(new DeclaredDerivation { Dataset = node, From = reads });
                }
                else
                {
                    // A mapping that cannot be a node of its own still orders the flow: the flow reads what it reads.
                    datasets.AddRange(reads);
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
            Derivations = derivations,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// What a mapping reads in a partition of a platform, each node once and in a stable order: the cache types it reads by
    /// name, then the cache types of the partition holding records of an entity type its ids are checked against, in name
    /// order, then the kinds its searches look in, in the searches' name order.
    /// </summary>
    /// <remarks>
    /// Which types hold an entity type is what the cache flows of the scanned estate declare for the partition: a type a
    /// cache flow of another repository holds is not seen here. A search reads the platform's records of its kind as the
    /// delivery renders, not a capture of them.
    /// </remarks>
    private static List<DeclaredDataset> MappingReads(
        MappingDefinition mapping, string endpoint, string partition, Scan scan, string who, List<string> warnings)
    {
        var template = scan.Template(mapping.Template);
        var cache = MappingCacheReads.Of(mapping, template.Schema);
        var reads = new List<DeclaredDataset>();
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in cache.Types)
        {
            if (named.Add(type.Trim()))
            {
                Add(reads, OsduLineage.CacheType(LineageRelation.Reads, partition, type, who, warnings));
            }
        }

        if (cache.EntityTypes.Count > 0)
        {
            var checkedAgainst = cache.EntityTypes.ToHashSet(StringComparer.Ordinal);
            foreach (var held in scan.Held
                         .Where(h => string.Equals(h.Partition, partition, StringComparison.OrdinalIgnoreCase) && checkedAgainst.Contains(h.EntityType))
                         .OrderBy(h => h.Name, StringComparer.Ordinal))
            {
                if (named.Add(held.Name))
                {
                    Add(reads, OsduLineage.CacheType(LineageRelation.Reads, partition, held.Name, who, warnings));
                }
            }
        }

        foreach (var search in mapping.Searches.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            Add(reads, OsduLineage.Type(
                LineageRelation.Reads, endpoint, partition, search.Kind, who, $"search '{search.Name}' of mapping '{mapping.Reference}'", warnings));
        }

        if (cache.Untold.Count > 0)
        {
            var listed = string.Join(", ", cache.Untold.Take(MaxNamedVariables))
                + (cache.Untold.Count > MaxNamedVariables ? $" and {cache.Untold.Count - MaxNamedVariables} more" : string.Empty);
            warnings.Add(
                $"{who} shows fewer cache types in lineage than its mapping reads: mapping '{mapping.Reference}' builds a reference with ref at {listed}, "
                + $"and the entity type of each is told by its template ({mapping.Template}), which {template.Missing}, "
                + "so the cache types those references are checked against are left out.");
        }

        return reads.Distinct().ToList();
    }

    /// <summary>
    /// The partition cache types a mapping reads by name, from its cache sources and their lookups and from the tables its
    /// replaces read, in name order (<see cref="MappingDefinition.CacheTypesRead"/>). A search source's lookups name a
    /// search, not a cache type, and are not among them; neither are the types its ids are checked against, which are read
    /// by the entity type they hold (<see cref="MappingCacheReads"/>).
    /// </summary>
    public static IReadOnlyList<string> CacheTypes(MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.CacheTypesRead();
    }

    /// <summary>
    /// The mapping the flow pins, read from the mappings directory the flow's layout names inside the scanned checkout,
    /// with that directory as the mapping's node is grouped under it; or null with a warning saying why it could not be.
    /// </summary>
    private static (MappingDefinition Mapping, string Directory)? PinnedMapping(FlowDefinition flow, Scan scan, string who, List<string> warnings)
    {
        var reference = flow.Render.Mapping;
        var context = scan.Context;
        var layout = DeliveryLayout.ResolveWithin(flow, context.DocumentFolder, context.Contains);
        if (layout is null)
        {
            warnings.Add(
                $"{who} shows no OSDU type in lineage: render.mappings '{flow.Render.MappingsDirectory}' lies outside the repository, so mapping '{reference}' is not read.");
            return null;
        }

        var (mapping, reason) = scan.Mapping(layout.MappingsDirectory, reference);
        if (mapping is null)
        {
            warnings.Add($"{who} shows no OSDU type in lineage: mapping '{reference}' could not be read ({reason}).");
            return null;
        }

        var relative = Path.GetRelativePath(context.EstateRoot, layout.MappingsDirectory).Replace('\\', '/').Trim('/');
        return (mapping, relative.Length == 0 || relative == "." ? OsduLineage.RootGroup : relative);
    }

    private static void Add(List<DeclaredDataset> datasets, DeclaredDataset? dataset)
    {
        if (dataset is not null)
        {
            datasets.Add(dataset);
        }
    }

    /// <summary>
    /// What describing one document reads once however many interfaces and partitions ask: each mapping, each template,
    /// and what the cache flows of the estate hold.
    /// </summary>
    private sealed class Scan(RegisteredLineageContext context, DeliveryDocumentLoader documents, MappingTemplateSource? templates)
    {
        private readonly Dictionary<(string Directory, string Reference), (MappingDefinition? Mapping, string? Reason)> _mappings = [];
        private readonly Dictionary<TemplateReference, MappingTemplateRead> _templates = [];
        private IReadOnlyList<CacheLineage.HeldType>? _held;

        public RegisteredLineageContext Context => context;

        /// <summary>
        /// Every type the cache flows of the scanned estate hold, by the partition it is held in. A cache document that
        /// cannot say what it holds (a partition it cannot be bound to) holds nothing here; its own description reports it.
        /// </summary>
        public IReadOnlyList<CacheLineage.HeldType> Held => _held ??= context.Estate
            .Select(entry => entry.Document)
            .OfType<CacheFlowDocument>()
            .SelectMany(document =>
            {
                try
                {
                    return CacheLineage.Held(document.Flow);
                }
                catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException)
                {
                    return [];
                }
            })
            .Distinct()
            .ToList();

        /// <summary>The mapping <paramref name="reference"/> pins under <paramref name="directory"/>, or why it could not be read.</summary>
        public (MappingDefinition? Mapping, string? Reason) Mapping(string directory, string reference)
        {
            if (_mappings.TryGetValue((directory, reference), out var known))
            {
                return known;
            }

            (MappingDefinition?, string?) read;
            try
            {
                read = (new MappingCatalog(directory, documents).Load(reference, Shown), null);
            }
            catch (Exception ex) when (ex is SqlFlowException or IOException or UnauthorizedAccessException)
            {
                read = (null, SecretHygiene.RedactedMessage(ex is SqlFlowException ? ex.Message : $"{ex.GetType().Name} reading it"));
            }

            _mappings[(directory, reference)] = read;
            return read;
        }

        /// <summary>The template <paramref name="reference"/> names, or why lineage has none to read.</summary>
        public MappingTemplateRead Template(TemplateReference reference)
        {
            if (!_templates.TryGetValue(reference, out var read))
            {
                read = templates?.Read(reference) ?? new MappingTemplateRead(MappingTemplateOutcome.NoStore);
                _templates[reference] = read;
            }

            return read;
        }

        /// <summary>A path as a warning names it: relative to the checkout, with forward slashes.</summary>
        private string Shown(string path) => Path.GetRelativePath(context.EstateRoot, path).Replace('\\', '/');
    }
}
