using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Loads the kind's documents: delivery flows and retrieval flows (behind the platform's envelope probe, which
/// dispatches on flowType) and mappings (which the platform never sees). Unknown keys are a hard parse error
/// (design.md section 10.4); every failure is a <see cref="FlowValidationException"/> prefixed with the file path.
/// </summary>
public sealed class DeliveryDocumentLoader
{
    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Strict: no IgnoreUnmatchedProperties. A misspelled key fails here rather than being silently ignored.
    private readonly IDeserializer _strict = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // Where a document holds a value of any shape (a mapping entry's static value, a modifier's settings), an
        // unquoted scalar keeps the type YAML reads it as, so static: 5 is a number and static: "5" is text.
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    /// <summary>Loads a delivery flow document as a source: every interface it declares, or the one its single form is.</summary>
    public SourceDefinition LoadSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseSource(File.ReadAllText(path), path);
    }

    /// <summary>Loads a delivery flow that delivers one interface: a document in the single form, or one declaring one interface.</summary>
    public FlowDefinition LoadFlow(string path) => LoadFlow(path, null);

    /// <summary>Loads one interface of a delivery flow document; a null name takes the document's only one.</summary>
    public FlowDefinition LoadFlow(string path, string? interfaceName) => OneInterface(LoadSource(path), interfaceName, path);

    public RetrievalDefinition LoadRetrieval(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseRetrieval(File.ReadAllText(path), path);
    }

    public CacheDefinition LoadCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Cache flow file not found: '{path}'.");
        }

        return ParseCache(File.ReadAllText(path), path);
    }

    public MappingDefinition LoadMapping(string path) => LoadMapping(path, path);

    /// <summary>Loads the mapping file at <paramref name="path"/>, naming it <paramref name="source"/> in every message.</summary>
    public MappingDefinition LoadMapping(string path, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Mapping file not found: '{source}'.");
        }

        return ParseMapping(File.ReadAllText(path), source);
    }

    /// <summary>The discriminator of a document: "delivery", "retrieval" or "cache" for a flow, "mapping" for a mapping.</summary>
    public string Probe(string yaml, string source = "<inline>")
    {
        var probe = Deserialize<DocumentProbeYaml>(_probe, yaml, source);
        if (!string.IsNullOrWhiteSpace(probe?.FlowType))
        {
            return probe!.FlowType!;
        }

        if (!string.IsNullOrWhiteSpace(probe?.DocumentType))
        {
            return probe!.DocumentType!;
        }

        throw new FlowValidationException($"{source}: the document declares no 'flowType' (delivery, retrieval, cache) and no 'documentType: mapping'.");
    }

    /// <summary>Parses a delivery flow document as a source: every interface it declares, or the one its single form is.</summary>
    public SourceDefinition ParseSource(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {FlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<FlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return FlowMapper.Map(y, source);
    }

    /// <summary>Parses a delivery flow that delivers one interface: a document in the single form, or one declaring one interface.</summary>
    public FlowDefinition ParseFlow(string yaml, string source = "<inline>") => ParseFlow(yaml, source, null);

    /// <summary>Parses one interface of a delivery flow document; a null name takes the document's only one.</summary>
    public FlowDefinition ParseFlow(string yaml, string source, string? interfaceName)
        => OneInterface(ParseSource(yaml, source), interfaceName, source);

    private static FlowDefinition OneInterface(SourceDefinition parsed, string? interfaceName, string source)
    {
        try
        {
            return parsed.Interface(interfaceName);
        }
        catch (DeliveryException ex)
        {
            throw new FlowValidationException($"{source}: {ex.Message}", ex);
        }
    }

    public RetrievalDefinition ParseRetrieval(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {RetrievalDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<RetrievalYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return RetrievalMapper.Map(y, source);
    }

    public CacheDefinition ParseCache(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {CacheDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<CacheYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return CacheMapper.Map(y, source);
    }

    public MappingDefinition ParseMapping(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(MappingDefinition.DocumentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'documentType: {MappingDefinition.DocumentTypeName}', found '{kind}'.");
        }

        var y = Deserialize<MappingYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return MappingMapper.Map(y, source);
    }

    /// <summary>
    /// The template a mapping document names in its <c>template</c> block, read without validating anything else, or
    /// null when the document is not readable YAML or names no complete template. A document that fails to load still
    /// pins what it names, so the template cannot be deleted from under a mapping that is only waiting for a fix.
    /// </summary>
    public TemplateReference? TemplateNamedBy(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        TemplateProbeYaml? probe;
        try
        {
            probe = _probe.Deserialize<TemplateProbeYaml>(yaml);
        }
        catch (YamlException)
        {
            // Unreadable YAML, or a template block of the wrong shape, names no template. ParseMapping reports why.
            return null;
        }

        var kind = probe?.Template?.Kind?.Trim();
        var version = probe?.Template?.Version?.Trim();
        return string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(version) ? null : new TemplateReference(kind, version);
    }

    private static T? Deserialize<T>(IDeserializer deserializer, string yaml, string source)
    {
        try
        {
            return deserializer.Deserialize<T>(yaml);
        }
        catch (YamlException ex)
        {
            var detail = ex.InnerException?.Message is { } inner && !ex.Message.Contains(inner, StringComparison.Ordinal)
                ? $"{ex.Message} ({inner})"
                : ex.Message;
            throw new FlowValidationException($"{source}: invalid YAML at line {ex.Start.Line}, column {ex.Start.Column} - {detail}", ex);
        }
    }

    /// <summary>Only the template block of a mapping document; every other key is ignored.</summary>
    private sealed class TemplateProbeYaml
    {
        public MappingTemplateYaml? Template { get; set; }
    }
}

/// <summary>
/// How a message names a key of the document: in the single form as written (<c>source.record</c>), for an interface
/// under the interface (<c>interfaces.wells.record</c>), and for a key the source declares for every interface and an
/// interface may override, with the interface it applies to.
/// </summary>
internal sealed record KeyPaths(string? Interface)
{
    public static KeyPaths Single { get; } = new((string?)null);

    public static KeyPaths ForInterface(string name) => new(name);

    private string At => $"interfaces.{Interface}";

    public string Record => Interface is null ? "source.record" : At + ".record";

    public string Datasets => Interface is null ? "source.datasets" : At + ".datasets";

    public string Mapping => Interface is null ? "render.mapping" : At + ".mapping";

    /// <summary>A payload set: <c>source.payloads.name</c>, or the interface's <c>files</c> or <c>bulk</c>.</summary>
    public string Payload(string name) => Interface is null ? $"source.payloads.{name}" : $"{At}.{name}";

    /// <summary>A key the single form writes as <paramref name="key"/>, and the interface form at the source or the interface.</summary>
    public string Shared(string key) => Interface is null ? key : $"{key} of interface '{Interface}'";

    /// <summary>The key paths of the document a flow definition was read from.</summary>
    public static KeyPaths Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Interface is null ? Single : ForInterface(flow.Interface);
    }

    /// <summary>
    /// The key a message names for what the single form writes as <paramref name="singleFormKey"/>: the record table, the
    /// child tables, the payload sets and the mapping under the interface, and any other key with the interface it applies to.
    /// </summary>
    public string Name(string singleFormKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(singleFormKey);
        if (Interface is null)
        {
            return singleFormKey;
        }

        foreach (var (single, own) in new[] { ("source.record", ".record"), ("source.datasets", ".datasets"), ("render.mapping", ".mapping") })
        {
            if (singleFormKey.Equals(single, StringComparison.Ordinal) || singleFormKey.StartsWith(single + ".", StringComparison.Ordinal))
            {
                return At + own + singleFormKey[single.Length..];
            }
        }

        const string payloads = "source.payloads.";
        return singleFormKey.StartsWith(payloads, StringComparison.Ordinal)
            ? $"{At}.{singleFormKey[payloads.Length..]}"
            : Shared(singleFormKey);
    }

    /// <summary>Where a message about a flow says it comes from: the file, and the interface when the flow is one.</summary>
    public static string Where(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = flow.SourcePath ?? flow.Name;
        return flow.Interface is null ? where : $"{where} (interface '{flow.Interface}')";
    }
}

internal static partial class FlowMapper
{
    /// <summary>The tenant header every OSDU service requires on every request.</summary>
    public const string PartitionHeader = "data-partition-id";

    /// <summary>The longest column name SQL Server accepts.</summary>
    private const int MaxColumnLength = 128;

    /// <summary>
    /// True for a kind the storage service accepts on a record (openapi storage v2, Record.kind:
    /// <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$</c>). A kind that only looks like four colon-separated
    /// parts, with a space in the entity type or a two-part version, would be refused on every record of a run.
    /// </summary>
    public static bool IsRecordKind(string kind) => RecordKindPattern().IsMatch(kind);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex RecordKindPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial System.Text.RegularExpressions.Regex DatasetName();

    /// <summary>The names of the parts an interface's record carries, as its route reads them from the source's payload sets.</summary>
    public const string FilesPayload = "files";

    public const string BulkPayload = "bulk";

    public static SourceDefinition Map(FlowYaml y, string source)
    {
        var name = Require(y.Name, "name", source);
        var description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim();
        var batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim();
        var path = source == "<inline>" ? null : source;
        if (y.Interfaces is null)
        {
            if (y.Reliability?.ParallelInterfaces is not null)
            {
                throw new FlowValidationException($"{source}: reliability.parallelInterfaces says how many interfaces run at once, and the document declares no interfaces.");
            }

            var flow = MapFlow(y, source, KeyPaths.Single);
            CheckLedgerName(flow.Name, "name", source);
            return new SourceDefinition { SourcePath = path, Name = name, Description = description, Batch = batch, Interfaces = [flow] };
        }

        return new SourceDefinition
        {
            SourcePath = path,
            Name = name,
            Description = description,
            Batch = batch,
            DeclaresInterfaces = true,
            Interfaces = MapInterfaces(y, name, source),
            ParallelInterfaces = MapParallelInterfaces(y.Reliability, source),
        };
    }

    /// <summary>
    /// One flow definition out of a document shaped as the single form: the document itself, or the view of one interface
    /// the interface form builds (<see cref="InterfaceView"/>), whose messages name the interface's keys.
    /// </summary>
    private static FlowDefinition MapFlow(FlowYaml y, string source, KeyPaths paths)
    {
        var name = Require(y.Name, "name", source);
        var src = y.Source ?? throw Missing("source", source);
        var render = y.Render ?? throw Missing("render", source);
        var target = y.Target ?? throw Missing("target", source);

        var protocol = ParseProtocol(Require(target.Protocol, "target.protocol", source), source);
        var options = MapOptions(target.ProtocolOptions, source);
        var mapping = Require(render.Mapping, paths.Mapping, source);
        if (!mapping.Contains('@', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {paths.Mapping} '{mapping}' must be pinned as 'Name@version'; floating references are not allowed.");
        }

        var workflow = MapWorkflow(target.Workflow, protocol, src.Payloads?.ContainsKey(FilesPayload) == true, source, paths);
        var mappedSource = WithInputs(MapSource(src, source, paths), target.Workflow, source, paths);
        var flow = new FlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Interface = paths.Interface,
            Parameters = (y.Parameters ?? []).ToDictionary(
                kv => kv.Key,
                kv => new FlowParameter { Required = kv.Value?.Required ?? false, Default = kv.Value?.Default, Description = kv.Value?.Description },
                StringComparer.Ordinal),
            Source = mappedSource,
            Render = new FlowRender
            {
                Mapping = mapping,
                CacheVersion = MapCacheVersion(render, source),
                Parameters = render.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                MappingsDirectory = string.IsNullOrWhiteSpace(render.Mappings) ? null : render.Mappings!.Trim(),
            },
            Change = new FlowChange
            {
                Detect = ParseEnum(y.Change?.Detect, ChangeDetection.RenderedHash, paths.Shared("change.detect"), source),
                PayloadDetect = ParseEnum(y.Change?.PayloadDetect, ChangeDetection.ContentHash, paths.Shared("change.payloadDetect"), source),
                OnUnchanged = ParseEnum(y.Change?.OnUnchanged, UnchangedAction.Skip, paths.Shared("change.onUnchanged"), source),
                UseSourceVersions = y.Change?.UseSourceVersions ?? true,
            },
            Target = new FlowTarget
            {
                Endpoint = Require(target.Endpoint, "target.endpoint", source),
                Auth = MapAuth(target.Auth, source),
                Headers = new Dictionary<string, string>(target.Headers ?? [], StringComparer.OrdinalIgnoreCase),
                Protocol = protocol,
                ProtocolOptions = options,
                Ddms = MapDdms(target.Ddms, protocol, paths.Interface is not null, options.DdmsRoot, source),
                Workflow = workflow,
                Airflow = MapAirflow(target.Airflow, source),
                Eds = MapEds(target.Eds, source),
            },
            Reliability = MapReliability(y.Reliability, source, paths),
            Verify = new FlowVerify { Reconcile = y.Verify?.Reconcile ?? false },
            FailWhen = MapFailWhen(y.FailWhen, source, paths),
        };

        Validate(flow, source, paths);
        return flow;
    }

    /// <summary>
    /// The interfaces of a document in the interface form, in document order: each one's view of the source mapped as a
    /// flow, its route resolved from what it declares, its ledger identity, and what it waits for. Refuses what belongs to
    /// an interface written at the source level, two interfaces sharing a ledger identity, and interfaces that wait for
    /// each other.
    /// </summary>
    private static List<FlowDefinition> MapInterfaces(FlowYaml y, string name, string source)
    {
        var interfaces = y.Interfaces!;
        var src = y.Source ?? throw Missing("source", source);
        var target = y.Target ?? throw Missing("target", source);
        RefuseAtSourceLevel(src, y.Render, target, source);
        if (y.Render is { } shared)
        {
            // What the source declares for every interface is checked once, whatever the interfaces make of it.
            MapCacheVersion(shared, source);
        }

        if (interfaces.Count == 0)
        {
            throw new FlowValidationException($"{source}: interfaces lists no interface. List each OSDU type the source delivers under it, or write the document in the single form.");
        }

        if (interfaces.Count > SourceDefinition.MaxInterfaces)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: interfaces lists {interfaces.Count} interfaces; one document declares at most {SourceDefinition.MaxInterfaces}. Split the source into several documents."));
        }

        var flows = new List<FlowDefinition>(interfaces.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, declared) in interfaces)
        {
            var interfaceName = key ?? string.Empty;
            if (!SourceDefinition.IsInterfaceName(interfaceName))
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: interfaces names an interface '{interfaceName}'; an interface name is a letter followed by letters, digits, '_' and '-', at most {SourceDefinition.MaxInterfaceNameLength} characters."));
            }

            if (!names.Add(interfaceName))
            {
                throw new FlowValidationException($"{source}: interfaces declares '{interfaceName}' more than once (interface names are compared ignoring case, as the ledger identities made from them are).");
            }

            var at = $"interfaces.{interfaceName}";
            var i = declared ?? throw new FlowValidationException($"{source}: {at} declares nothing; an interface needs at least its record and its mapping.");
            if (i.Reliability?.ParallelInterfaces is not null)
            {
                throw new FlowValidationException($"{source}: {at}.reliability.parallelInterfaces is the source's setting: how many of its interfaces run at once. Set it under reliability.");
            }

            var paths = KeyPaths.ForInterface(interfaceName);
            var route = ResolveRoute(i, at, source);
            var flow = MapFlow(InterfaceView(y, i, route), source, paths) with
            {
                Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description.Trim(),
                AdoptedLedger = MapLedger(i.Ledger, at, source),
                After = (i.After ?? []).Select(a => a?.Trim() ?? string.Empty).ToList(),
                RouteReason = route.Reason,
            };

            if (DeliveryProtocols.ReachesDdms(flow.Target.Protocol) && flow.Target.ProtocolOptions is { DdmsRoot: null, RecordPath: null } && flow.Target.Ddms.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: {at} is delivered through a DDMS, and the source's endpoint is the platform every interface reaches its services under. "
                    + $"Say where the DDMS is under it: declare it under target.ddms with its root, or set target.protocolOptions.ddmsRoot or {at}.protocolOptions.ddmsRoot "
                    + "for the Wellbore DDMS (usually deployed under /api/os-wellbore-ddms).");
            }

            flows.Add(flow);
        }

        if (target.Ddms is not null && !flows.Any(f => DeliveryProtocols.ReachesDdms(f.Target.Protocol)))
        {
            throw new FlowValidationException(
                $"{source}: target.ddms declares the DDMSs the source's interfaces are delivered to, and no interface is delivered through one "
                + "(an interface with a bulk part, or route: ddms). Remove target.ddms, or route the interfaces it is meant for through their DDMS.");
        }

        CheckAfter(flows, source);
        CheckLedgers(flows, source);
        return flows;
    }

    /// <summary>What belongs to an interface, written at the source level, is refused rather than applied to every interface.</summary>
    private static void RefuseAtSourceLevel(FlowSourceYaml src, FlowRenderYaml? render, FlowTargetYaml target, string source)
    {
        var misplaced = new List<string>();
        if (src.Record is not null)
        {
            misplaced.Add("source.record (each interface's record table is interfaces.<name>.record)");
        }

        if (src.Datasets is not null)
        {
            misplaced.Add("source.datasets (each interface's child tables are interfaces.<name>.datasets)");
        }

        if (src.Payloads is not null)
        {
            misplaced.Add($"source.payloads (an interface's files are interfaces.<name>.{FilesPayload}, its DDMS bulk data interfaces.<name>.{BulkPayload})");
        }

        if (!string.IsNullOrWhiteSpace(render?.Mapping))
        {
            misplaced.Add("render.mapping (each interface pins its own mapping as interfaces.<name>.mapping)");
        }

        if (!string.IsNullOrWhiteSpace(target.Protocol))
        {
            misplaced.Add("target.protocol (each interface's route follows from what it declares, or is named with interfaces.<name>.route)");
        }

        if (!string.IsNullOrWhiteSpace(target.ProtocolOptions?.Payload))
        {
            misplaced.Add("target.protocolOptions.payload (an interface's route sends its own files or bulk data)");
        }

        if (target.Workflow is not null)
        {
            misplaced.Add("target.workflow (the workflow an interface runs is interfaces.<name>.workflow)");
        }

        if (misplaced.Count > 0)
        {
            throw new FlowValidationException($"{source}: the document declares interfaces, so these belong to an interface rather than the source: {string.Join("; ", misplaced)}.");
        }
    }

    /// <summary>
    /// One interface as a document in the single form: the source's shared blocks with the interface's own settings laid
    /// over them, its record table, child tables and payload parts as the source's, and the route it resolved to as the
    /// target's protocol.
    /// </summary>
    private static FlowYaml InterfaceView(FlowYaml y, InterfaceYaml i, InterfaceRoute route)
    {
        var src = y.Source!;
        var target = y.Target!;
        var payloads = new Dictionary<string, FlowPayloadYaml>(StringComparer.Ordinal);
        if (i.Files is not null)
        {
            payloads[FilesPayload] = i.Files;
        }

        if (i.Bulk is not null)
        {
            payloads[BulkPayload] = i.Bulk;
        }

        var options = YamlOverlay.Apply(target.ProtocolOptions, i.ProtocolOptions) ?? new ProtocolOptionsYaml();
        options.Payload = route.Payload;

        // Where a DDMS sits under the endpoint is a setting of the interfaces delivered through one; an interface delivered
        // another way leaves the source's value alone rather than being refused for it.
        if (!DeliveryProtocols.ReachesDdms(route.Protocol))
        {
            options.DdmsRoot = i.ProtocolOptions?.DdmsRoot;
        }

        var render = y.Render ?? new FlowRenderYaml();
        var parameters = new Dictionary<string, string>(render.Parameters ?? [], StringComparer.Ordinal);
        foreach (var (key, value) in i.Render?.Parameters ?? [])
        {
            parameters[key] = value;
        }

        return new FlowYaml
        {
            FlowType = y.FlowType,
            Name = y.Name,
            Batch = y.Batch,
            Parameters = y.Parameters,
            Source = new FlowSourceYaml
            {
                Connection = src.Connection,
                Record = i.Record,
                Datasets = i.Datasets,
                Payloads = payloads,
                LastModified = string.IsNullOrWhiteSpace(i.LastModified) ? src.LastModified : i.LastModified,
                SystemColumns = OverlaySystemColumns(src.SystemColumns, i.SystemColumns),
                Incremental = YamlOverlay.Apply(src.Incremental, i.Incremental),
                Work = src.Work,
            },
            Render = new FlowRenderYaml
            {
                Mapping = i.Mapping,
                CacheVersion = string.IsNullOrWhiteSpace(i.Render?.CacheVersion) ? render.CacheVersion : i.Render.CacheVersion,
                Parameters = parameters,
                Mappings = render.Mappings,
            },
            Change = YamlOverlay.Apply(y.Change, i.Change),
            Target = new FlowTargetYaml
            {
                Endpoint = target.Endpoint,
                Auth = target.Auth,
                Headers = target.Headers,
                Protocol = route.Protocol.ToString(),
                ProtocolOptions = options,

                // The DDMSs the source declares are where its ddms interfaces go; the other interfaces have no use for them.
                Ddms = DeliveryProtocols.ReachesDdms(route.Protocol) ? target.Ddms : null,
                Workflow = i.Workflow,
                Airflow = target.Airflow,
                Eds = target.Eds,
            },
            Reliability = YamlOverlay.Apply(y.Reliability, i.Reliability),
            Verify = YamlOverlay.Apply(y.Verify, i.Verify),
            FailWhen = YamlOverlay.Apply(y.FailWhen, i.FailWhen),
        };
    }

    /// <summary>The system columns an interface names over the ones its source names; a column opted out of stays opted out.</summary>
    private static FlowSystemColumnsYaml? OverlaySystemColumns(FlowSystemColumnsYaml? shared, FlowSystemColumnsYaml? over)
    {
        if (over is null || shared is null)
        {
            return over ?? shared;
        }

        var merged = new FlowSystemColumnsYaml();
        if (over.HasUpdated || shared.HasUpdated)
        {
            merged.Updated = over.HasUpdated ? over.Updated : shared.Updated;
        }

        if (over.HasFileName || shared.HasFileName)
        {
            merged.FileName = over.HasFileName ? over.FileName : shared.FileName;
        }

        if (over.HasRowNumber || shared.HasRowNumber)
        {
            merged.RowNumber = over.HasRowNumber ? over.RowNumber : shared.RowNumber;
        }

        if (over.HasDeleted || shared.HasDeleted)
        {
            merged.Deleted = over.HasDeleted ? over.Deleted : shared.Deleted;
        }

        return merged;
    }

    /// <summary>How an interface is delivered, the payload set its route sends (null for none, or for a route that sends parts), and why.</summary>
    private sealed record InterfaceRoute(DeliveryProtocol Protocol, string? Payload, string Reason);

    /// <summary>
    /// The route of an interface, from what its records carry (docs/interfaces-design.md section 5.2): nothing beside the
    /// record goes through the storage service, files through the file service before the record, DDMS bulk data through a
    /// DDMS after the record, both through the composed route, and a workflow declaration through the workflow route.
    /// <c>route:</c> names one outright; a named route and a part only another route sends make the composed route of the
    /// two (manifest or file with bulk data, ddms with files), and a route that cannot deliver what the interface declares
    /// is refused, so nothing it declares is silently left unsent.
    /// </summary>
    private static InterfaceRoute ResolveRoute(InterfaceYaml i, string at, string source)
    {
        var files = i.Files is not null;
        var bulk = i.Bulk is not null;
        var workflow = i.Workflow is not null;
        var named = string.IsNullOrWhiteSpace(i.Route) ? null : (InterfaceRouteName?)ParseEnum<InterfaceRouteName>(i.Route!.Trim(), at + ".route", source);
        var filesKey = $"{at}.{FilesPayload}";
        var bulkKey = $"{at}.{BulkPayload}";
        var workflowKey = $"{at}.workflow";

        void Refuse(bool refused, string why)
        {
            if (refused)
            {
                throw new FlowValidationException($"{source}: {why}");
            }
        }

        Refuse(
            workflow && named is not (null or InterfaceRouteName.Workflow),
            $"{at}.route is {i.Route?.Trim()}, and {workflowKey} declares a workflow, which only the workflow route runs. Remove {at}.route, or name workflow.");
        switch (named)
        {
            case InterfaceRouteName.Storage:
                Refuse(files || bulk, $"{at}.route is storage, which writes the record alone, so {(files ? filesKey : bulkKey)} would never be sent. Remove it, or choose the route that delivers it.");
                return new InterfaceRoute(DeliveryProtocol.OsduRecord, null, $"{at}.route names the storage route: each record is written through the storage service");
            case InterfaceRouteName.File when bulk:
            case InterfaceRouteName.Ddms when files:
            case InterfaceRouteName.FileAndDdms:
                Refuse(!files || !bulk, $"{at}.route is fileAndDdms, which registers each record's files and writes its bulk data through its DDMS, and the interface declares no {(files ? bulkKey : filesKey)}.");
                return new InterfaceRoute(
                    DeliveryProtocol.OsduFileAndDdms,
                    null,
                    $"{at} is delivered by the fileAndDdms route ("
                    + (named == InterfaceRouteName.FileAndDdms ? $"{at}.route names it" : $"{at}.route names {i.Route!.Trim()} and the interface declares both {FilesPayload} and {BulkPayload}")
                    + "): each record's files are uploaded and registered through the file service, the record is written through its DDMS referring to them, then its bulk data");
            case InterfaceRouteName.File:
                Refuse(!files, $"{at}.route is file, which uploads and registers each record's files, and the interface declares none under {filesKey}.");
                return new InterfaceRoute(DeliveryProtocol.OsduFile, FilesPayload, $"{at}.route names the file route: each record's files are uploaded and registered through the file service before the record is written");
            case InterfaceRouteName.Dataset:
                Refuse(!files, $"{at}.route is dataset, which stores each record's files and registers them through the dataset service, and the interface declares none under {filesKey}.");
                Refuse(bulk, $"{at}.route is dataset, which writes no DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(
                    DeliveryProtocol.OsduDataset,
                    FilesPayload,
                    $"{at}.route names the dataset route: each record's files are stored where the dataset service says and registered with it, a record of a dataset kind as that dataset");
            case InterfaceRouteName.Manifest when bulk:
            case InterfaceRouteName.ManifestAndDdms:
                Refuse(!bulk, $"{at}.route is manifestAndDdms, which writes each record's bulk data through its DDMS after the manifest, and the interface declares none under {bulkKey}.");
                return new InterfaceRoute(
                    DeliveryProtocol.OsduManifestAndDdms,
                    null,
                    $"{at} is delivered by the manifestAndDdms route ("
                    + (named == InterfaceRouteName.ManifestAndDdms ? $"{at}.route names it" : $"{at}.route names manifest and the interface declares {BulkPayload}")
                    + "): the records go through the ingestion workflow in manifests" + (files ? ", their files registered first," : string.Empty) + " then each record's bulk data through its DDMS");
            case InterfaceRouteName.Manifest:
                return new InterfaceRoute(
                    DeliveryProtocol.OsduManifest,
                    files ? FilesPayload : null,
                    $"{at}.route names the manifest route: the records go through the ingestion workflow in manifests" + (files ? ", their files registered first" : string.Empty));
            case InterfaceRouteName.Ddms:
                return new InterfaceRoute(
                    DeliveryProtocol.OsduWellLog,
                    bulk ? BulkPayload : null,
                    $"{at}.route names the ddms route: each record is written through its DDMS" + (bulk ? ", then its bulk data" : string.Empty));
            case InterfaceRouteName.Workflow:
                Refuse(!workflow, $"{at}.route is workflow, and the interface declares no workflow under {workflowKey}.");
                Refuse(bulk, $"{at}.route is workflow, which writes no DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(DeliveryProtocol.OsduWorkflow, null, $"{at}.route names the workflow route: {WorkflowReason(i)}");
        }

        if (workflow)
        {
            Refuse(bulk, $"{workflowKey} declares a workflow, and the workflow route writes no DDMS bulk data, so {bulkKey} would never be sent.");
            return new InterfaceRoute(DeliveryProtocol.OsduWorkflow, null, $"{at} declares a workflow, so it goes by the workflow route: {WorkflowReason(i)}");
        }

        if (files && bulk)
        {
            return new InterfaceRoute(
                DeliveryProtocol.OsduFileAndDdms,
                null,
                $"{at} declares {FilesPayload} and {BulkPayload}, so each record's files are uploaded and registered through the file service, the record is written through its DDMS referring to them, then its bulk data");
        }

        if (files)
        {
            return new InterfaceRoute(DeliveryProtocol.OsduFile, FilesPayload, $"{at} declares {FilesPayload}, so each record's files are uploaded and registered through the file service before the record is written through the storage service");
        }

        if (bulk)
        {
            return new InterfaceRoute(DeliveryProtocol.OsduWellLog, BulkPayload, $"{at} declares {BulkPayload}, so each record is written through its DDMS and its bulk data after it");
        }

        return new InterfaceRoute(DeliveryProtocol.OsduRecord, null, $"{at} declares no {FilesPayload} and no {BulkPayload}, so each record is written through the storage service");
    }

    /// <summary>What the workflow route does for an interface, in the words its route reason gives.</summary>
    private static string WorkflowReason(InterfaceYaml i)
    {
        var stages = (i.Workflow?.Stages ?? []).Select(s => s?.Workflow?.Trim()).OfType<string>().ToList();
        var anchor = AnchorOf(i.Workflow?.Anchor, i.Files is not null) == WorkflowAnchor.Storage
            ? "each record is written through the storage service"
            : "each record is registered with its files through the dataset service";
        return $"{anchor}, its inputs registered, then {(stages.Count == 0 ? "its workflow" : string.Join(" and then ", stages))} run and what it created read back";
    }

    /// <summary>How the workflow route writes the record: as declared, or as a dataset when the record carries files and as a storage record otherwise.</summary>
    private static WorkflowAnchor AnchorOf(string? declared, bool files)
    {
        if (string.Equals(declared?.Trim(), "storage", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowAnchor.Storage;
        }

        if (string.Equals(declared?.Trim(), "dataset", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowAnchor.Dataset;
        }

        return files ? WorkflowAnchor.Dataset : WorkflowAnchor.Storage;
    }

    /// <summary>The routes an interface can name with <c>route:</c>, and a document in the single form with <c>target.protocol</c>.</summary>
    private enum InterfaceRouteName
    {
        Storage,
        File,
        Dataset,
        Manifest,
        Ddms,
        FileAndDdms,
        ManifestAndDdms,
        Workflow,
    }

    /// <summary>The protocol that delivers a route type.</summary>
    private static DeliveryProtocol RouteProtocol(InterfaceRouteName route) => route switch
    {
        InterfaceRouteName.Storage => DeliveryProtocol.OsduRecord,
        InterfaceRouteName.File => DeliveryProtocol.OsduFile,
        InterfaceRouteName.Dataset => DeliveryProtocol.OsduDataset,
        InterfaceRouteName.Manifest => DeliveryProtocol.OsduManifest,
        InterfaceRouteName.Ddms => DeliveryProtocol.OsduWellLog,
        InterfaceRouteName.FileAndDdms => DeliveryProtocol.OsduFileAndDdms,
        InterfaceRouteName.ManifestAndDdms => DeliveryProtocol.OsduManifestAndDdms,
        InterfaceRouteName.Workflow => DeliveryProtocol.OsduWorkflow,
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "not a route type"),
    };

    /// <summary>
    /// The protocol <c>target.protocol</c> names: a route type (<c>storage</c>, <c>file</c>, <c>dataset</c>,
    /// <c>manifest</c>, <c>ddms</c>, <c>fileAndDdms</c>, <c>manifestAndDdms</c>, <c>workflow</c>), or the protocol a route
    /// type maps onto (<c>osduRecord</c>, <c>osduFile</c>, <c>osduManifest</c>, <c>osduWellLog</c> and the others), as
    /// documents written before the route types name it.
    /// </summary>
    private static DeliveryProtocol ParseProtocol(string value, string source)
    {
        foreach (var route in Enum.GetValues<InterfaceRouteName>())
        {
            if (string.Equals(value, route.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return RouteProtocol(route);
            }
        }

        foreach (var protocol in Enum.GetValues<DeliveryProtocol>())
        {
            if (string.Equals(value, protocol.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return protocol;
            }
        }

        throw new FlowValidationException(
            $"{source}: 'target.protocol' value '{value}' is not one of {string.Join(", ", Enum.GetNames<InterfaceRouteName>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))} "
            + $"(or the protocols they map onto: {string.Join(", ", Enum.GetNames<DeliveryProtocol>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}).");
    }

    private static string? MapLedger(string? ledger, string at, string source)
    {
        if (ledger is null)
        {
            return null;
        }

        var name = ledger.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            throw new FlowValidationException($"{source}: {at}.ledger must name the flow whose ledger the interface adopts.");
        }

        return name;
    }

    /// <summary>Every <c>after:</c> names another interface of the document, once, and the interfaces wait for no cycle.</summary>
    private static void CheckAfter(IReadOnlyList<FlowDefinition> flows, string source)
    {
        var names = flows.Select(f => f.Interface!).ToList();
        foreach (var flow in flows)
        {
            var at = $"interfaces.{flow.Interface}.after";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var after in flow.After)
            {
                if (!names.Contains(after, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{source}: {at} names '{after}', which is not an interface of this document; it declares {string.Join(", ", names)}.");
                }

                if (string.Equals(after, flow.Interface, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException($"{source}: {at} names the interface itself.");
                }

                if (!seen.Add(after))
                {
                    throw new FlowValidationException($"{source}: {at} names '{after}' more than once.");
                }
            }
        }

        var declared = flows.SelectMany(f => f.After.Select(a => new InterfaceDependency(f.Interface!, a, string.Empty)));
        if (InterfaceOrder.Cycle(names, declared) is { } cycle)
        {
            throw new FlowValidationException($"{source}: the interfaces {string.Join(" -> ", cycle)} wait for each other through after:, so none of them could run first.");
        }
    }

    /// <summary>Two interfaces of one document never share a ledger, and every ledger name fits where the ledger records it.</summary>
    private static void CheckLedgers(IReadOnlyList<FlowDefinition> flows, string source)
    {
        foreach (var flow in flows)
        {
            CheckLedgerName(flow.Label, $"the interface's name '{flow.Label}'", source);
            if (flow.AdoptedLedger is { } adopted)
            {
                CheckLedgerName(adopted, $"interfaces.{flow.Interface}.ledger", source);
            }
        }

        foreach (var group in flows.GroupBy(f => f.Id))
        {
            var sharing = group.ToList();
            if (sharing.Count > 1)
            {
                throw new FlowValidationException(
                    $"{source}: the interfaces {string.Join(", ", sharing.Select(f => f.Interface))} would keep the same ledger ('{sharing[0].LedgerName}'); each interface keeps a ledger of its own.");
            }
        }
    }

    /// <summary>A name the ledger records a flow under fits the width the ledger keeps it in.</summary>
    private static void CheckLedgerName(string name, string what, string source)
    {
        if (name.Length > FlowDefinition.MaxLedgerNameLength)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {what} is {name.Length} characters; the ledger records a flow under at most {FlowDefinition.MaxLedgerNameLength}."));
        }
    }

    private static int MapParallelInterfaces(FlowReliabilityYaml? reliability, string source)
    {
        var parallel = reliability?.ParallelInterfaces ?? SourceDefinition.DefaultParallelInterfaces;
        return parallel is < 1 or > SourceDefinition.MaxParallelInterfaces
            ? throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: reliability.parallelInterfaces must be between 1 and {SourceDefinition.MaxParallelInterfaces}."))
            : parallel;
    }

    private static FlowFailWhen MapFailWhen(FailWhenYaml? declared, string source, KeyPaths paths)
    {
        var defaults = new FlowFailWhen();
        if (declared is null)
        {
            return defaults;
        }

        var failWhen = new FlowFailWhen
        {
            FailedPercent = declared.FailedPercent,
            MinRecords = declared.MinRecords ?? defaults.MinRecords,
            ConsecutiveFailures = declared.ConsecutiveFailures,
            OutageFailures = declared.OutageFailures ?? defaults.OutageFailures,
        };

        if (failWhen.FailedPercent is { } percent && (double.IsNaN(percent) || percent <= 0 || percent > 100))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.failedPercent")} must be above 0 and at most 100.");
        }

        if (failWhen.MinRecords < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.minRecords")} must be at least 1.");
        }

        if (failWhen.ConsecutiveFailures is < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.consecutiveFailures")} must be at least 1.");
        }

        if (failWhen.OutageFailures < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("failWhen.outageFailures")} must not be negative (0 turns the outage rule off).");
        }

        return failWhen;
    }

    /// <summary>
    /// The cache version a flow pins, <c>current</c> when it pins none. A flow reads the cache of the partition it delivers to,
    /// so there is no cache to name: a document still naming one under <c>render.cache</c> is refused, rather than read against
    /// a cache other than the one its author meant.
    /// </summary>
    private static string MapCacheVersion(FlowRenderYaml render, string source)
    {
        if (!string.IsNullOrWhiteSpace(render.Cache))
        {
            throw new FlowValidationException(
                $"{source}: render.cache is not a setting any more: a flow reads the cache of the partition it delivers to (target.headers.data-partition-id), which every cache flow of that partition fills. Remove render.cache.");
        }

        return string.IsNullOrWhiteSpace(render.CacheVersion) ? FlowRender.CurrentCacheVersion : render.CacheVersion!.Trim();
    }

    private static FlowSource MapSource(FlowSourceYaml src, string source, KeyPaths paths)
    {
        var record = src.Record ?? throw Missing(paths.Record, source);
        var datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal);
        foreach (var (name, dataset) in src.Datasets ?? [])
        {
            var at = $"{paths.Datasets}.{name}";
            var declared = dataset ?? throw Missing(at + ".object", source);
            datasets[name.Trim()] = new FlowSourceDataset
            {
                Object = Require(declared.Object, at + ".object", source),
                Join = Trimmed(declared.Join),
                OrderBy = (declared.OrderBy ?? []).Select(c => c?.Trim() ?? string.Empty).ToList(),
                MaxRowsPerRecord = declared.MaxRowsPerRecord ?? FlowSourceDataset.DefaultMaxRowsPerRecord,
            };
        }

        var payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal);
        foreach (var (name, payload) in src.Payloads ?? [])
        {
            var at = paths.Payload(name);
            var declared = payload ?? throw Missing(at + ".root", source);
            payloads[name.Trim()] = new FlowPayload
            {
                Root = Require(declared.Root, at + ".root", source),
                LocationColumn = Optional(declared.LocationColumn),
                Pattern = Optional(declared.Pattern) ?? FlowPayload.DefaultPattern,
                HashColumn = Optional(declared.HashColumn),
                ChunkCountColumn = Optional(declared.ChunkCountColumn),
            };
        }

        return new FlowSource
        {
            Connection = Require(src.Connection, "source.connection", source),
            Record = new FlowSourceTable
            {
                Object = Require(record.Object, $"{paths.Record}.object", source),
                Key = (record.Key ?? []).Select(k => k?.Trim() ?? string.Empty).ToList(),
                PrimaryKey = Optional(record.PrimaryKey),
                Scope = Trimmed(record.Scope),
            },
            Datasets = datasets,
            Payloads = payloads,
            LastModified = Optional(src.LastModified),
            SystemColumns = MapSystemColumns(src.SystemColumns, source, paths),
            Incremental = new FlowIncremental
            {
                OverlapSeconds = src.Incremental?.OverlapSeconds ?? FlowIncremental.DefaultOverlapSeconds,
                PageSize = src.Incremental?.PageSize ?? FlowIncremental.DefaultPageSize,
                Isolation = ParseEnum(src.Incremental?.Isolation, SourceIsolation.Snapshot, paths.Shared("source.incremental.isolation"), source),
                CommandTimeoutSeconds = src.Incremental?.CommandTimeoutSeconds ?? 0,
            },
            Work = Require(src.Work, "source.work", source),
        };
    }

    /// <summary>System columns: a column the flow names, one it opts out of with <c>~</c>, and the default for one it leaves out.</summary>
    private static FlowSystemColumns MapSystemColumns(FlowSystemColumnsYaml? declared, string source, KeyPaths paths)
    {
        var defaults = new FlowSystemColumns();
        if (declared is null)
        {
            return defaults;
        }

        if (declared.HasUpdated && string.IsNullOrWhiteSpace(declared.Updated))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("source.systemColumns.updated")} cannot be opted out of: it is the column an incremental read windows on and a record's fingerprint is built from.");
        }

        return new FlowSystemColumns
        {
            Updated = declared.HasUpdated ? declared.Updated!.Trim() : defaults.Updated,
            FileName = declared.HasFileName ? Optional(declared.FileName) : defaults.FileName,
            RowNumber = declared.HasRowNumber ? Optional(declared.RowNumber) : defaults.RowNumber,
            Deleted = declared.HasDeleted ? Optional(declared.Deleted) : defaults.Deleted,
            DeletedDeclared = declared.HasDeleted && !string.IsNullOrWhiteSpace(declared.Deleted),
        };
    }

    /// <summary>
    /// What a flow's source must be for a run to read it (docs/stage4-design.md section 1.1). Every rule names the key it is
    /// about; the bindings to the tables' actual columns are checked when a run opens the source.
    /// </summary>
    private static void ValidateSource(FlowDefinition flow, string source, KeyPaths paths)
    {
        var src = flow.Source;
        IngestionConnection.CheckDeclared(src.Connection, source);
        CheckObject(src.Record.Object, $"{paths.Record}.object", source);

        if (src.Record.Key.Count == 0)
        {
            throw new FlowValidationException($"{source}: {paths.Record}.key must name the record table's key columns (the ingestion flow's load.keyColumns).");
        }

        var key = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in src.Record.Key)
        {
            CheckColumn(column, $"{paths.Record}.key", source);
            if (!key.Add(column))
            {
                throw new FlowValidationException($"{source}: {paths.Record}.key names column '{column}' more than once.");
            }
        }

        if (src.Record.PrimaryKey is { } primaryKey)
        {
            CheckColumn(primaryKey, $"{paths.Record}.primaryKey", source);
        }

        foreach (var (column, parameter) in src.Record.Scope)
        {
            CheckColumn(column, $"{paths.Record}.scope", source);
            if (!flow.Parameters.ContainsKey(parameter))
            {
                throw new FlowValidationException($"{source}: {paths.Record}.scope binds column '{column}' to parameter '{parameter}', which is not declared under parameters.");
            }
        }

        foreach (var (name, dataset) in src.Datasets)
        {
            var at = $"{paths.Datasets}.{name}";
            if (!DatasetName().IsMatch(name) || name.Length > MaxColumnLength || name.Equals(SourceDatasets.Record, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Datasets} names a dataset '{name}'; a child dataset is letters, digits and '_', and '{SourceDatasets.Record}' names the record table itself.");
            }

            CheckObject(dataset.Object, at + ".object", source);
            if (dataset.Join.Count == 0)
            {
                throw new FlowValidationException($"{source}: {at}.join must join the child table's columns to the record table's key columns.");
            }

            foreach (var (child, recordColumn) in dataset.Join)
            {
                CheckColumn(child, at + ".join", source);
                if (!key.Contains(recordColumn))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.join joins child column '{child}' to record column '{recordColumn}', which is not a key column of {paths.Record}.key.");
                }
            }

            foreach (var column in src.Record.Key)
            {
                if (!dataset.Join.Values.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    throw new FlowValidationException(
                        $"{source}: {at}.join does not cover key column '{column}' of the record table; every key column must be joined, or a child row could belong to several records.");
                }
            }

            foreach (var column in dataset.OrderBy)
            {
                CheckColumn(column, at + ".orderBy", source);
            }

            if (dataset.MaxRowsPerRecord is < 1 or > FlowSourceDataset.MaxRowsPerRecordCeiling)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.maxRowsPerRecord must be between 1 and {FlowSourceDataset.MaxRowsPerRecordCeiling}."));
            }
        }

        foreach (var (name, payload) in src.Payloads)
        {
            var at = paths.Payload(name);
            CheckTokens(flow, payload.Root, at + ".root", source);
            foreach (var column in new[] { payload.LocationColumn, payload.HashColumn, payload.ChunkCountColumn }.OfType<string>())
            {
                CheckColumn(column, at, source);
            }

            if (payload.Pattern.Contains('/', StringComparison.Ordinal) || payload.Pattern.Contains('\\', StringComparison.Ordinal) || payload.Pattern.Contains("..", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: {at}.pattern '{payload.Pattern}' is a file name glob under the record's payload folder, without a path.");
            }
        }

        if (PayloadParts.Of(flow) is { } parts)
        {
            ValidateParts(flow, parts, source, paths);
        }
        else if (DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
        {
            var selected = flow.Target.ProtocolOptions.Payload;
            if (selected is not null && !src.Payloads.ContainsKey(selected))
            {
                throw new FlowValidationException($"{source}: target.protocolOptions.payload '{selected}' is not declared under source.payloads.");
            }

            if (selected is null && src.Payloads.Count > 1)
            {
                throw new FlowValidationException(
                    $"{source}: source.payloads declares {src.Payloads.Count} payloads; target.protocolOptions.payload must name the one the {flow.Target.Protocol} protocol streams.");
            }

            var payloadName = selected ?? src.Payloads.Keys.FirstOrDefault();
            if (payloadName is not null)
            {
                RequirePartColumns(flow, payloadName, source, paths);
            }
            else if (flow.Target.Protocol == DeliveryProtocol.OsduDataset)
            {
                throw new FlowValidationException($"{source}: the dataset route stores and registers each record's files, and {paths.Payload(FilesPayload)} declares none.");
            }
        }

        if (src.LastModified is { } lastModified)
        {
            CheckColumn(lastModified, paths.Shared("source.lastModified"), source);
        }

        foreach (var column in new[] { src.SystemColumns.Updated, src.SystemColumns.FileName, src.SystemColumns.RowNumber, src.SystemColumns.Deleted }.OfType<string>())
        {
            CheckColumn(column, paths.Shared("source.systemColumns"), source);
        }

        if (src.Incremental.OverlapSeconds is < 0 or > FlowIncremental.MaxOverlapSeconds)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("source.incremental.overlapSeconds")} must be between 0 and {FlowIncremental.MaxOverlapSeconds}."));
        }

        if (src.Incremental.PageSize is < 1 or > FlowIncremental.MaxPageSize)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("source.incremental.pageSize")} must be between 1 and {FlowIncremental.MaxPageSize}."));
        }

        if (src.Incremental.CommandTimeoutSeconds < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("source.incremental.commandTimeoutSeconds")} must not be negative (0 lets a read run as long as the run does).");
        }

        CheckTokens(flow, src.Work, "source.work", source);
    }

    private static void CheckObject(string declared, string key, string source)
    {
        if (!SourceObjectName.TryParse(declared, out _, out var problem))
        {
            throw new FlowValidationException($"{source}: {key} '{declared}' must be a three-part name [database].[schema].[table]: {problem}.");
        }
    }

    private static void CheckColumn(string column, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(column) || column.Length > MaxColumnLength || column.Any(char.IsControl)
            || column.Contains('[', StringComparison.Ordinal) || column.Contains(']', StringComparison.Ordinal) || column.Trim().Length != column.Length)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {key} names a column '{column}'; a column name is 1 to {MaxColumnLength} characters, without brackets, control characters or surrounding space."));
        }
    }

    private static void CheckTokens(FlowDefinition flow, string text, string key, string source)
    {
        foreach (var token in Tokens(text))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: {key} uses '{{{token}}}', which is not declared under parameters.");
            }
        }
    }

    private static void Validate(FlowDefinition flow, string source, KeyPaths paths)
    {
        ValidateSource(flow, source, paths);

        if (flow.Change.Detect == ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.detect")} cannot be lastModified. A document is always decided by the hash of what it renders to; the source row's business version column is source.lastModified.");
        }

        if (flow.Change.PayloadDetect == ChangeDetection.LastModified && !SendsFiles(flow))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("change.payloadDetect")} is lastModified, but the {flow.Target.Protocol} protocol delivers no payload files to take the watermark from.");
        }

        if (flow.Reliability.Concurrency < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.concurrency")} must be at least 1.");
        }

        if (flow.Reliability.Retry.Attempts < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.retry.attempts")} must be at least 1.");
        }

        if (flow.Reliability.BatchRecords is < 1 or > 100_000)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.batchRecords")} must be between 1 and 100000.");
        }

        if (flow.Reliability.FanOut is < 0 or > FlowReliability.MaxFanOut)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.fanOut")} must be between 0 and {FlowReliability.MaxFanOut}.");
        }

        if (flow.Reliability.FanOutMinRecords < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.fanOutMinRecords")} must be at least 1.");
        }

        if (flow.Reliability.FanOut > 0 && flow.Source.Record.PrimaryKey is null)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("reliability.fanOut")} spreads a submission over ranges of the record table's identity primary key, and {paths.Record}.primaryKey names none. "
                + $"Name the column (the ingestion flow creates it with target.identityColumn, for example RecId), or set {paths.Shared("reliability.fanOut")} to 0.");
        }

        if (flow.Reliability.RenderParallelism is < 0 or > 256)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("reliability.renderParallelism")} must be between 0 and 256.");
        }

        // Every OSDU service makes data-partition-id a required header (openapi storage v2, file v2, search v2,
        // workflow v1, schema-service v1). A flow that leaves it out authors a run where every single request comes
        // back 400 with a message about a tenant, which is a slow and confusing way to learn about a typo in the
        // flow. It costs nothing to say so while the document is being read.
        if (!flow.Target.Headers.ContainsKey(PartitionHeader))
        {
            throw new FlowValidationException(
                $"{source}: target.headers must declare '{PartitionHeader}'. Every OSDU service requires it and rejects a request without it.");
        }

        if (string.IsNullOrWhiteSpace(flow.Target.Headers[PartitionHeader]))
        {
            throw new FlowValidationException($"{source}: target.headers.{PartitionHeader} must not be empty.");
        }

        if (flow.Target.ProtocolOptions.BatchSize is < 1 or > ProtocolOptions.MaxBatchSize)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.batchSize")} must be between 1 and {ProtocolOptions.MaxBatchSize}.");
        }

        if (flow.Target.ProtocolOptions.DdmsRoot is { } ddmsRoot)
        {
            if (!DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.ddmsRoot")} only applies to the osduWellLog protocol; {flow.Target.Protocol} reaches its services under the endpoint already.");
            }

            if (ddmsRoot.Length == 0 || ddmsRoot[0] != '/' || ddmsRoot.Contains("://", StringComparison.Ordinal) || ddmsRoot.Any(char.IsWhiteSpace))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.ddmsRoot")} '{ddmsRoot}' must be a path under the endpoint starting with '/', such as /api/os-wellbore-ddms.");
            }
        }

        if (flow.Target.ProtocolOptions.LegalValidatePath is { } legalPath
            && legalPath[0] != '/'
            && !(Uri.TryCreate(legalPath, UriKind.Absolute, out var legalUrl) && legalUrl.Scheme is "http" or "https"))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.legalValidatePath")} '{legalPath}' must be a path under the endpoint starting with '/', or an absolute http(s) URL.");
        }

        if (flow.Target.ProtocolOptions.RegisterPath is { } registerPath)
        {
            if (!flow.Target.Ddms.Any(d => d.Registration is not null))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.registerPath")} says where the Register service reads a DDMS registration, and no DDMS under target.ddms names one with register.");
            }

            if (!registerPath.Contains("{id}", StringComparison.Ordinal)
                || (registerPath[0] != '/' && !(Uri.TryCreate(registerPath, UriKind.Absolute, out var registerUrl) && registerUrl.Scheme is "http" or "https")))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions.registerPath")} '{registerPath}' must be a path under the endpoint starting with '/', or an absolute http(s) URL, with {{id}} where the registration's id goes.");
            }
        }

        // The bulk endpoint replaces the whole bulk, so at most one chunk can go to it; more than one is a session.
        // A flow that asked for a higher threshold was asking for chunks to overwrite each other.
        if (flow.Target.ProtocolOptions.SessionThresholdChunks is < 0 or > 1)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.sessionThresholdChunks")} must be 1 (a single chunk goes straight to the bulk endpoint, more open a session) or 0 (always open a session). "
                + "The bulk endpoint replaces the whole bulk on every write, so several chunks sent to it would overwrite each other.");
        }

        if (flow.Target.ProtocolOptions.MaxChunkValues < 0 || flow.Target.ProtocolOptions.MaxChunkColumns < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.maxChunkValues")} and maxChunkColumns must not be negative (0 does not check the chunk shape).");
        }

        if (flow.Target.ProtocolOptions.WorkflowPollSeconds < 1 || flow.Target.ProtocolOptions.WorkflowTimeoutMinutes < 1)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.workflowPollSeconds")} and workflowTimeoutMinutes must be at least 1.");
        }

        if (flow.Target.ProtocolOptions.DatasetIndexWaitSeconds < 0)
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.datasetIndexWaitSeconds")} must not be negative (0 does not wait).");
        }

        // Every upload states its type in a Content-Type header, which a malformed value cannot be written into.
        foreach (var (key, value) in new[] { ("payloadContentType", flow.Target.ProtocolOptions.PayloadContentType), ("filesContentType", flow.Target.ProtocolOptions.FilesContentType) })
        {
            if (value is not null && !IsMediaType(value))
            {
                throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions." + key)} '{value}' is not a media type such as application/octet-stream.");
            }
        }

        // RAFS reads the version from the query as major.minor or major.minor.patch (osdu/specs/rafs-ddms/INTEGRATION.md section 1.2).
        if (!System.Text.RegularExpressions.Regex.IsMatch(flow.Target.ProtocolOptions.ContentSchemaVersion, @"^\d+\.\d+(?:\.\d+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.contentSchemaVersion")} '{flow.Target.ProtocolOptions.ContentSchemaVersion}' is not a content schema version such as 1.0.0.");
        }

        if (flow.Target.ProtocolOptions.UploadUrlExpiry is { } expiry && !ValidExpiry(expiry))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.uploadUrlExpiry")} '{expiry}' must be a whole number of minutes, hours or days, such as 30M, 12H or 2D.");
        }

        // Both are written onto records the target stores (a dataset record per file, the manifest the workflow
        // ingests), so they answer to the same pattern as a mapping's kind, and are checked while the flow is read
        // rather than on the first delivery that needs them.
        foreach (var (key, value) in new[] { ("datasetKind", flow.Target.ProtocolOptions.DatasetKind), ("manifestKind", flow.Target.ProtocolOptions.ManifestKind) })
        {
            if (!IsRecordKind(value))
            {
                throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions." + key)} '{value}' must be 'authority:source:entityType:major.minor.patch'.");
            }
        }

        if (flow.Target.ProtocolOptions.ManifestSection is { } section && !ProtocolOptions.ManifestSections.Contains(section, StringComparer.Ordinal))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.manifestSection")} '{section}' is not one of {string.Join(", ", ProtocolOptions.ManifestSections)}.");
        }

        if (string.IsNullOrWhiteSpace(flow.Target.ProtocolOptions.WorkflowAppKey))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.workflowAppKey")} must not be empty.");
        }

        ValidateReferenceOptions(flow, source, paths);
        ValidateWorkflow(flow, source, paths);

        if (flow.Target.Auth.Type == TargetAuthType.OAuth2ClientCredentials && flow.Target.Auth.Token is null)
        {
            throw new FlowValidationException($"{source}: target.auth of type oauth2ClientCredentials needs a 'token' block with the token endpoint url and body.");
        }

        if (flow.Target.Auth.Type is TargetAuthType.Bearer or TargetAuthType.ApiKeyHeader or TargetAuthType.Basic && string.IsNullOrWhiteSpace(flow.Target.Auth.SecretRef))
        {
            throw new FlowValidationException($"{source}: target.auth of type {flow.Target.Auth.Type} needs secretRef.");
        }

        if (flow.Target.Auth.Type == TargetAuthType.ApiKeyHeader && string.IsNullOrWhiteSpace(flow.Target.Auth.HeaderName))
        {
            throw new FlowValidationException($"{source}: target.auth of type apiKeyHeader needs headerName.");
        }
    }

    internal static TargetAuth MapAuth(TargetAuthYaml? a, string source, string key = "target.auth")
    {
        if (a is null)
        {
            return new TargetAuth { Type = TargetAuthType.None };
        }

        return new TargetAuth
        {
            Type = ParseEnum(a.Type, TargetAuthType.None, key + ".type", source),
            SecretRef = a.SecretRef,
            SecondarySecretRef = a.SecondarySecretRef,
            HeaderName = a.HeaderName,
            ValuePrefix = a.ValuePrefix,
            Token = a.Token is null ? null : new TargetTokenEndpoint
            {
                Url = a.Token.Url,
                DiscoveryUrl = a.Token.DiscoveryUrl,
                Body = a.Token.Body ?? new Dictionary<string, string>(StringComparer.Ordinal),
                BasicAuthClient = a.Token.BasicAuthClient,
                TokenPath = a.Token.TokenPath ?? "access_token",
                ApplyPrefix = a.Token.ApplyPrefix ?? "Bearer ",
            },
        };
    }

    /// <summary>
    /// The DDMSs a flow declares under <c>target.ddms</c> (docs/documents.md, "The DDMSs a flow delivers to"), in document
    /// order. Each has a name, a root under the endpoint, a shape and the collections it serves (the shape's own when it
    /// lists none). No entity type is served by two of them, since a record goes to one DDMS. A DDMS without a root makes
    /// the endpoint that DDMS itself, so it is the only DDMS such a flow reaches; a source with interfaces reaches every
    /// service under the platform endpoint, so each of its DDMSs names its root.
    /// </summary>
    private static IReadOnlyList<DdmsService> MapDdms(
        OrderedDictionary<string, DdmsYaml?>? declared, DeliveryProtocol protocol, bool interfaceForm, string? ddmsRoot, string source)
    {
        if (declared is null)
        {
            return [];
        }

        if (!DeliveryProtocols.ReachesDdms(protocol))
        {
            throw new FlowValidationException($"{source}: target.ddms declares the DDMSs the ddms route delivers to, and this flow's protocol is {protocol}. Remove target.ddms.");
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException($"{source}: target.ddms declares no DDMS. Name each DDMS the flow delivers to under it, or remove it.");
        }

        var services = new List<DdmsService>(declared.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in declared)
        {
            var name = key?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsName(name))
            {
                throw new FlowValidationException($"{source}: target.ddms names a DDMS '{name}'; a DDMS name is a letter followed by letters, digits, '_' and '-', at most 64 characters.");
            }

            if (!names.Add(name))
            {
                throw new FlowValidationException($"{source}: target.ddms declares '{name}' more than once (DDMS names are compared ignoring case).");
            }

            var at = $"target.ddms.{name}";
            var ddms = value ?? new DdmsYaml();
            var shape = ParseEnum(ddms.Shape, DdmsShape.WellboreDdmsV3, at + ".shape", source);
            var root = MapDdmsRoot(ddms.Root, at + ".root", source);
            var registration = MapRegistration(ddms, shape, at, source);
            if (root is null && shape == DdmsShape.ProductionTimeSeriesV1)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. The historian's records are written through Storage and its points through its ingestion service, so the flow's endpoint is "
                    + $"the platform both are under; say where the ingestion service is under it (usually {DdmsCatalog.UsualRoot(shape)}).");
            }

            if (root is null && shape == DdmsShape.SeismicStoreV3)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. A dataset's record is read and removed through Storage, so the flow's endpoint is the platform Seismic Store is under; "
                    + $"say where Seismic Store is under it, version path included (usually {DdmsCatalog.UsualRoot(shape)}, or /seistore-svc/api/v3 on Azure).");
            }

            if (root is null && shape == DdmsShape.ReservoirManagement)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. A header record is written, read and removed through Storage, so the flow's endpoint is the platform the "
                    + "Reservoir Management DDMS is under; say where the service is under it (its project names no prefix, so it is the deployment's).");
            }

            if (root is null && registration is null && interfaceForm)
            {
                var usual = DdmsCatalog.UsualRoot(shape) is { } known
                    ? $"a DDMS of the {DdmsCatalog.ShapeName(shape)} shape is usually deployed under {known}"
                    : $"a DDMS of the {DdmsCatalog.ShapeName(shape)} shape is deployed under the prefix its deployment gives it";
                throw new FlowValidationException(
                    $"{source}: {at}.root is required. The source's endpoint is the platform its interfaces reach every service under, so say where the DDMS is under it "
                    + $"({usual})"
                    + (shape == DdmsShape.WellboreDdmsV3 ? ", or name its registration in the Register service with register." : "."));
            }

            // A registered DDMS whose collections the flow leaves out serves what its registration says, once it is read.
            var collections = registration is not null && ddms.Collections is null ? [] : MapDdmsCollections(ddms.Collections, shape, at, source);
            var settings = MapWellDelivery(ddms, shape, at, source);
            var timeSeries = MapTimeSeries(ddms, shape, at, source);
            var seismic = MapSeismicStore(ddms, shape, at, source);
            var reservoirManagement = MapReservoirManagement(ddms, shape, at, source);
            foreach (var collection in collections)
            {
                if (!servedBy.TryAdd(collection.EntityType, name))
                {
                    throw new FlowValidationException(
                        $"{source}: target.ddms serves {collection.EntityType} from both '{servedBy[collection.EntityType]}' and '{name}'. A record goes to one DDMS, "
                        + "so list the collections of one of them under its collections, leaving that entity type out.");
                }
            }

            services.Add(new DdmsService(name, root, shape, collections)
            {
                Registration = registration,
                DeclaresCollections = ddms.Collections is not null,
                WellDelivery = settings,
                TimeSeries = timeSeries,
                SeismicStore = seismic,
                ReservoirManagement = reservoirManagement,
            });
        }

        if (services.FirstOrDefault(s => s.Root is null && s.Registration is null) is { } unrooted && (services.Count > 1 || ddmsRoot is not null))
        {
            throw new FlowValidationException(
                $"{source}: target.ddms.{unrooted.Name} names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS"
                + (ddmsRoot is null ? string.Empty : " and target.protocolOptions.ddmsRoot places none under it")
                + ". Give every DDMS its root under the endpoint, or declare that one alone.");
        }

        return services;
    }

    /// <summary>
    /// The deployment settings of a declared Well Delivery DDMS, or null for any other shape, which takes none of them
    /// (osdu/specs/well-delivery-ddms/INTEGRATION.md section 8: whether the deployment copies entities into Storage, the
    /// provider it runs on, and how many writes it takes at a time).
    /// </summary>
    private static WellDeliverySettings? MapWellDelivery(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.WellDeliveryV1)
        {
            var misplaced = new[] { ("mirror", ddms.Mirror is not null), ("provider", ddms.Provider is not null && shape != DdmsShape.SeismicStoreV3), ("concurrency", ddms.Concurrency is not null) }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe a Well Delivery DDMS deployment, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. Remove them, or declare shape: wellDeliveryV1.");
            }

            return null;
        }

        var provider = ParseEnum<DdmsProvider>(ddms.Provider, default, at + ".provider", source);
        if (ddms.Provider is not null && provider == DdmsProvider.Anthos)
        {
            throw new FlowValidationException($"{source}: {at}.provider '{ddms.Provider}' is not a provider the Well Delivery DDMS runs on: azure, aws, gc or ibm.");
        }

        var concurrency = ddms.Concurrency ?? WellDeliverySettings.DefaultConcurrency;
        if (concurrency is < 1 or > WellDeliverySettings.MaxConcurrency)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.concurrency must be between 1 and {WellDeliverySettings.MaxConcurrency}; the Mongo and Cosmos stores are safe with 1 (section 6 of its brief)."));
        }

        return new WellDeliverySettings
        {
            Mirror = ddms.Mirror ?? true,
            Provider = string.IsNullOrWhiteSpace(ddms.Provider) ? null : provider,
            Concurrency = concurrency,
        };
    }

    /// <summary>
    /// The settings of a declared Production DDMS historian, or null for any other shape, which takes none of them
    /// (osdu/specs/production-timeseries/INTEGRATION.md sections 1, 4 and 6: where the query service is, how long accepted
    /// points are read back for, and the largest request the points are sent in).
    /// </summary>
    private static TimeSeriesSettings? MapTimeSeries(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.ProductionTimeSeriesV1)
        {
            var misplaced = new[]
                {
                    ("queryRoot", ddms.QueryRoot is not null),
                    ("settleSeconds", ddms.SettleSeconds is not null && shape != DdmsShape.ReservoirManagement),
                    ("pollSeconds", ddms.PollSeconds is not null && shape != DdmsShape.ReservoirManagement),
                    ("maxRequestBytes", ddms.MaxRequestBytes is not null),
                }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe the Production DDMS historian, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. "
                    + "Remove them, or declare shape: productionTimeSeriesV1.");
            }

            return null;
        }

        var settle = ddms.SettleSeconds ?? TimeSeriesSettings.DefaultSettleSeconds;
        if (settle is < 0 or > TimeSeriesSettings.MaxSettleSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.settleSeconds must be between 0 and {TimeSeriesSettings.MaxSettleSeconds}: how long a delivery reads accepted points back for, 0 not reading them back."));
        }

        var poll = ddms.PollSeconds ?? TimeSeriesSettings.DefaultPollSeconds;
        if (poll is < 1 or > TimeSeriesSettings.MaxPollSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture, $"{source}: {at}.pollSeconds must be between 1 and {TimeSeriesSettings.MaxPollSeconds}."));
        }

        var bytes = ddms.MaxRequestBytes ?? TimeSeriesSettings.DefaultMaxRequestBytes;
        if (bytes is < TimeSeriesSettings.MinRequestBytes or > TimeSeriesSettings.MaxRequestBytes)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.maxRequestBytes must be between {TimeSeriesSettings.MinRequestBytes} and {TimeSeriesSettings.MaxRequestBytes}; "
                + $"the historian's documentation gives {TimeSeriesSettings.DefaultMaxRequestBytes} bytes as its limit, unverified, which is the default."));
        }

        return new TimeSeriesSettings
        {
            QueryRoot = MapDdmsRoot(ddms.QueryRoot, at + ".queryRoot", source) ?? DdmsCatalog.UsualTimeSeriesQueryRoot,
            SettleSeconds = settle,
            PollSeconds = poll,
            MaxRequestBytesPerRequest = bytes,
        };
    }

    /// <summary>
    /// The settings of a declared Reservoir Management DDMS, or null for any other shape
    /// (osdu/specs/reservoir-management-ddms/INTEGRATION.md section 2.7: how long a delivery with rows waits for the service
    /// to take the header record into its database, and how often it asks). The other shapes refuse the two keys, except
    /// the historian, whose own they are too.
    /// </summary>
    private static ReservoirManagementSettings? MapReservoirManagement(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.ReservoirManagement)
        {
            return null;
        }

        var settle = ddms.SettleSeconds ?? ReservoirManagementSettings.DefaultSettleSeconds;
        if (settle is < 0 or > ReservoirManagementSettings.MaxSettleSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.settleSeconds must be between 0 and {ReservoirManagementSettings.MaxSettleSeconds}: how long a delivery with rows waits for the service to take its header record, 0 asking once."));
        }

        var poll = ddms.PollSeconds ?? ReservoirManagementSettings.DefaultPollSeconds;
        if (poll is < 1 or > ReservoirManagementSettings.MaxPollSeconds)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture, $"{source}: {at}.pollSeconds must be between 1 and {ReservoirManagementSettings.MaxPollSeconds}."));
        }

        return new ReservoirManagementSettings { SettleSeconds = settle, PollSeconds = poll };
    }

    /// <summary>
    /// The settings of a declared Seismic Store, or null for any other shape, which takes none of them
    /// (osdu/specs/seismic-ddms/INTEGRATION.md section 9.3: the tenant, subproject and folder the datasets are registered in,
    /// the provider and object store their files go to, the size of each object or part, and the read-only flag).
    /// </summary>
    private static SeismicStoreSettings? MapSeismicStore(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (shape != DdmsShape.SeismicStoreV3)
        {
            var misplaced = new[]
                {
                    ("tenant", ddms.Tenant is not null), ("subproject", ddms.Subproject is not null), ("folder", ddms.Folder is not null),
                    ("objectStore", ddms.ObjectStore is not null), ("region", ddms.Region is not null), ("chunkMiB", ddms.ChunkMiB is not null),
                    ("readOnly", ddms.ReadOnly is not null),
                }
                .Where(k => k.Item2)
                .Select(k => $"{at}.{k.Item1}")
                .ToList();
            if (misplaced.Count > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {string.Join(", ", misplaced)} describe a Seismic Store, and {at} has the {DdmsCatalog.ShapeName(shape)} shape. Remove them, or declare shape: seismicStoreV3.");
            }

            return null;
        }

        var subproject = ddms.Subproject?.Trim();
        if (string.IsNullOrEmpty(subproject))
        {
            throw new FlowValidationException($"{source}: {at}.subproject is required: the Seismic Store subproject the datasets are registered in, which an operator provisions.");
        }

        if (!DdmsCatalog.IsSubproject(subproject))
        {
            throw new FlowValidationException(
                $"{source}: {at}.subproject '{subproject}' is not a Seismic Store subproject name: a lower-case letter, then lower-case letters, digits and '-', not ending in '-'.");
        }

        var tenant = string.IsNullOrWhiteSpace(ddms.Tenant) ? null : ddms.Tenant.Trim();
        if (tenant is not null && !DdmsCatalog.IsSeismicTenant(tenant))
        {
            throw new FlowValidationException($"{source}: {at}.tenant '{tenant}' is not a Seismic Store tenant name (letters, digits, '_', '.' and '-'); on OSDU it is the data partition id.");
        }

        var folder = string.IsNullOrWhiteSpace(ddms.Folder) ? null : ddms.Folder.Trim().Trim('/');
        if (folder is { Length: 0 })
        {
            folder = null;
        }

        if (folder is not null && !DdmsCatalog.IsSeismicFolder(folder))
        {
            throw new FlowValidationException(
                $"{source}: {at}.folder '{ddms.Folder!.Trim()}' is not a Seismic Store folder: segments of letters, digits, '_', '.' and '-', separated by single slashes.");
        }

        DdmsProvider? provider = null;
        if (ddms.Provider is not null)
        {
            provider = ParseEnum<DdmsProvider>(ddms.Provider, default, at + ".provider", source);
            if (provider == DdmsProvider.Aws)
            {
                throw new FlowValidationException($"{source}: {at}.provider '{ddms.Provider}' is not a provider Seismic Store v3 runs on: azure, gc, anthos or ibm.");
            }
        }

        string? objectStore = null;
        if (!string.IsNullOrWhiteSpace(ddms.ObjectStore))
        {
            var written = ddms.ObjectStore.Trim();
            if (!Uri.TryCreate(written, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
                || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            {
                throw new FlowValidationException(
                    $"{source}: {at}.objectStore '{written}' must be the object store's absolute http(s) address, without credentials, query or fragment (https://s3.example.com).");
            }

            objectStore = written.TrimEnd('/');
        }

        if (provider is DdmsProvider.Anthos or DdmsProvider.Ibm && objectStore is null)
        {
            throw new FlowValidationException(
                $"{source}: {at}.objectStore is required on {ddms.Provider}: Seismic Store issues a key triple there for an S3 store it does not name (osdu/specs/seismic-ddms/INTEGRATION.md section 4.3).");
        }

        var region = string.IsNullOrWhiteSpace(ddms.Region) ? SeismicStoreSettings.DefaultRegion : ddms.Region.Trim();
        if (region.Length > 64 || !region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new FlowValidationException($"{source}: {at}.region '{region}' is not a region name (lower-case letters, digits and '-', such as us-east-1).");
        }

        var chunk = ddms.ChunkMiB ?? SeismicStoreSettings.DefaultChunkMiB;
        if (chunk is < 0 or > SeismicStoreSettings.MaxChunkMiB)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at}.chunkMiB must be between 0 and {SeismicStoreSettings.MaxChunkMiB}: the MiB of each object a file is cut into on Azure (0 keeps it whole), and of each part it goes up in."));
        }

        return new SeismicStoreSettings
        {
            Tenant = tenant,
            Subproject = subproject,
            Folder = folder,
            Provider = provider,
            ObjectStore = objectStore,
            Region = region,
            ChunkMiB = chunk,
            ReadOnly = ddms.ReadOnly ?? false,
        };
    }

    /// <summary>
    /// The Register service id a declared DDMS is looked up under, or null. A DDMS that declares both its root and its
    /// collections leaves its registration nothing to supply, so naming one as well is refused. Only the Wellbore DDMS's
    /// registered documents say which collection serves which entity type in a way the route can read.
    /// </summary>
    private static string? MapRegistration(DdmsYaml ddms, DdmsShape shape, string at, string source)
    {
        if (ddms.Register is null)
        {
            return null;
        }

        if (shape != DdmsShape.WellboreDdmsV3)
        {
            throw new FlowValidationException(
                $"{source}: {at}.register reads a DDMS's collections from its Register service registration, which the route reads for DDMSs of the wellboreDdmsV3 shape. "
                + $"Declare the root of a DDMS of the {DdmsCatalog.ShapeName(shape)} shape, and its collections where they differ from the ones its shape serves.");
        }

        var id = ddms.Register.Trim();
        if (!DdmsCatalog.IsRegistration(id))
        {
            throw new FlowValidationException(
                $"{source}: {at}.register '{id}' is not an id the Register service keeps a DDMS under: 2 to 50 letters, digits and '-'.");
        }

        if (!string.IsNullOrWhiteSpace(ddms.Root) && ddms.Collections is not null)
        {
            throw new FlowValidationException(
                $"{source}: {at} declares its root and its collections, so its registration has nothing to supply. Remove register, or leave out what the registration should say.");
        }

        return id;
    }

    /// <summary>Where a declared DDMS is under the endpoint: a path starting with '/', without a trailing '/'; null when the endpoint is the DDMS.</summary>
    private static string? MapDdmsRoot(string? declared, string key, string source)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var written = declared.Trim();
        var root = written.TrimEnd('/');
        if (root.Length == 0 || root[0] != '/' || root.Contains("://", StringComparison.Ordinal) || root.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or '?' or '#'))
        {
            throw new FlowValidationException($"{source}: {key} '{written}' must be a path under the endpoint starting with '/', such as /api/os-wellbore-ddms.");
        }

        return root;
    }

    /// <summary>The collections a declared DDMS serves, or those of its shape when it lists none.</summary>
    private static IReadOnlyList<DdmsCollectionEntry> MapDdmsCollections(
        OrderedDictionary<string, DdmsCollectionYaml?>? declared, DdmsShape shape, string at, string source)
    {
        if (declared is null)
        {
            return DdmsCatalog.DefaultCollections(shape);
        }

        if (shape == DdmsShape.ProductionTimeSeriesV1)
        {
            throw new FlowValidationException(
                $"{source}: {at}.collections lists collections, and the historian serves {DdmsCatalog.ProductionValues} records alone, under production-values. Leave collections out.");
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: {at}.collections lists no collection. List the entity types the DDMS serves, or leave collections out for the ones its shape serves.");
        }

        var collections = new List<DdmsCollectionEntry>(declared.Count);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in declared)
        {
            var entityType = key?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsEntityType(entityType))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.collections names '{entityType}', which is not an OSDU entity type with its group, such as work-product-component--WellLog.");
            }

            if (!types.Add(entityType))
            {
                throw new FlowValidationException($"{source}: {at}.collections lists {entityType} more than once (entity types are compared ignoring case).");
            }

            var entry = $"{at}.collections.{entityType}";
            if (shape == DdmsShape.WellDeliveryV1)
            {
                collections.Add(MapWellDeliveryCollection(entityType, value, entry, source));
                continue;
            }

            if (shape == DdmsShape.SeismicStoreV3)
            {
                collections.Add(MapSeismicCollection(entityType, value, entry, source));
                continue;
            }

            if (shape == DdmsShape.ReservoirManagement)
            {
                collections.Add(MapReservoirManagementCollection(entityType, value, entry, source));
                continue;
            }

            var declaredCollection = value ?? throw new FlowValidationException($"{source}: {entry} declares nothing; name at least the path the DDMS serves it under.");
            var segment = declaredCollection.Path?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsSegment(segment))
            {
                throw new FlowValidationException(
                    $"{source}: {entry}.path '{segment}' must be the collection's path segment, such as welllogs: letters, digits, '.', '_' and '-', at most 100 characters.");
            }

            var bulk = declaredCollection.Bulk ?? false;
            var columns = ParseEnum(declaredCollection.Columns, DdmsBulkColumns.Unchecked, entry + ".columns", source);
            if (!bulk && columns != DdmsBulkColumns.Unchecked)
            {
                throw new FlowValidationException($"{source}: {entry}.columns says what bulk data columns are checked against, and the collection holds records alone (bulk is false).");
            }

            if (shape != DdmsShape.WellboreDdmsV3 && columns != DdmsBulkColumns.Unchecked)
            {
                throw new FlowValidationException(
                    $"{source}: {entry}.columns names the curve and station checks the Wellbore DDMS applies to its bulk data, and {at} has the {DdmsCatalog.ShapeName(shape)} shape.");
            }

            var typed = declaredCollection.TypedContent ?? false;
            if (typed && (shape != DdmsShape.RafsV2 || !bulk))
            {
                throw new FlowValidationException(
                    shape != DdmsShape.RafsV2
                        ? $"{source}: {entry}.typedContent says a RAFS collection holds several content types, and {at} has the {DdmsCatalog.ShapeName(shape)} shape."
                        : $"{source}: {entry}.typedContent says what content the collection holds, and it holds records alone (bulk is false).");
            }

            collections.Add(new DdmsCollectionEntry(entityType, segment, bulk) { Columns = columns, TypedContent = typed });
        }

        return collections;
    }

    /// <summary>
    /// A collection of a declared Well Delivery DDMS: always the entity type lowercased, which is the path the service
    /// takes the type from, and records alone. A declared path must be that segment.
    /// </summary>
    private static DdmsCollectionEntry MapWellDeliveryCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        var collection = DdmsCatalog.WellDeliveryCollection(entityType);
        if (declared is null)
        {
            return collection;
        }

        if (declared.Path is { } path && !string.Equals(path.Trim(), collection.Segment, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: {entry}.path '{path.Trim()}' is not the path the Well Delivery DDMS serves {entityType} under: it takes the type from the record id and requires the path to be that type, "
                + $"so the path is {collection.Segment}. Leave path out.");
        }

        if (declared.Bulk == true || declared.Columns is not null || declared.TypedContent is not null)
        {
            throw new FlowValidationException($"{source}: {entry} describes bulk data, and the Well Delivery DDMS keeps records alone. Remove bulk, columns and typedContent.");
        }

        return collection;
    }

    /// <summary>
    /// A dataset type a declared Seismic Store registers datasets for: a <c>dataset--FileCollection.*</c> type, which always
    /// keeps files. Its path only names it (the type after <c>dataset--FileCollection.</c>, lowercased, when left out),
    /// since every dataset goes to the subproject whatever its type.
    /// </summary>
    private static DdmsCollectionEntry MapSeismicCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        if (!entityType.StartsWith(DdmsCatalog.FileCollectionPrefix, StringComparison.OrdinalIgnoreCase) || entityType.Length == DdmsCatalog.FileCollectionPrefix.Length)
        {
            throw new FlowValidationException(
                $"{source}: {entry} names a type Seismic Store registers no dataset for; a Seismic Store dataset's record is a {DdmsCatalog.FileCollectionPrefix}* record.");
        }

        var segment = declared?.Path?.Trim() is { Length: > 0 } path ? path : entityType[DdmsCatalog.FileCollectionPrefix.Length..].ToLowerInvariant();
        if (!DdmsCatalog.IsSegment(segment))
        {
            throw new FlowValidationException($"{source}: {entry}.path '{segment}' must be a name of letters, digits, '.', '_' and '-', at most 100 characters.");
        }

        if (declared?.Bulk == false || declared?.Columns is not null || declared?.TypedContent is not null)
        {
            throw new FlowValidationException($"{source}: {entry} says what a dataset keeps, and a Seismic Store dataset always keeps its files; remove bulk, columns and typedContent.");
        }

        return new DdmsCollectionEntry(entityType, segment, Bulk: true);
    }

    /// <summary>
    /// An entity type a declared Reservoir Management DDMS serves: under one of its nine header collections, the one whose
    /// records the service keeps being named by <c>path</c> (the collection the service's own entity type has when left
    /// out). The collection's tables decide whether its records keep rows, so bulk, columns and typedContent are not given.
    /// </summary>
    private static DdmsCollectionEntry MapReservoirManagementCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        var known = DdmsCatalog.ReservoirManagementCollections.FirstOrDefault(c => string.Equals(c.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        var written = declared?.Path?.Trim();
        var segment = written is { Length: > 0 } ? written : known?.Segment;
        var segments = string.Join(", ", ReservoirManagementTables.Headers.Select(h => h.Segment));
        if (segment is null)
        {
            throw new FlowValidationException(
                $"{source}: {entry}.path is required: the header collection of the Reservoir Management DDMS the records go to ({segments}); {entityType} is none of its own entity types.");
        }

        var header = ReservoirManagementTables.Header(segment)
            ?? throw new FlowValidationException($"{source}: {entry}.path '{segment}' is not a header collection of the Reservoir Management DDMS: {segments}.");
        if (declared?.Bulk is not null || declared?.Columns is not null || declared?.TypedContent is not null)
        {
            throw new FlowValidationException(
                $"{source}: {entry} says what the collection keeps, and the Reservoir Management DDMS's tables decide that ({header.Segment} keeps "
                + (header.Tables.Count == 0 ? "no rows" : "rows in " + string.Join(", ", header.Tables)) + "); remove bulk, columns and typedContent.");
        }

        return new DdmsCollectionEntry(entityType, header.Segment, Bulk: header.Tables.Count > 0);
    }

    private static ProtocolOptions MapOptions(ProtocolOptionsYaml? o, string source)
    {
        if (o is null)
        {
            return new ProtocolOptions();
        }

        return new ProtocolOptions
        {
            RecordPath = o.RecordPath,
            RecordMethod = o.RecordMethod,
            DataPath = o.DataPath,
            SessionPath = o.SessionPath,
            SessionDataPath = o.SessionDataPath,
            SessionCommitPath = o.SessionCommitPath,
            VerifyPath = o.VerifyPath,
            DeletePath = o.DeletePath,
            PurgePath = o.PurgePath,
            PurgeVersionsPath = o.PurgeVersionsPath,
            BulkDeletePath = o.BulkDeletePath,
            ProbePath = o.ProbePath,
            Payload = o.Payload,
            SessionThresholdChunks = o.SessionThresholdChunks ?? 1,
            MaxChunkValues = o.MaxChunkValues ?? WellboreDdmsBulkLimits.MaxChunkValues,
            MaxChunkColumns = o.MaxChunkColumns ?? WellboreDdmsBulkLimits.MaxChunkColumns,
            PayloadContentType = Optional(o.PayloadContentType) ?? "application/x-parquet",
            FilesContentType = Optional(o.FilesContentType),
            VersionPath = o.VersionPath ?? "recordIdVersions[0]",
            SkipDuplicates = o.SkipDuplicates ?? false,
            VerifyBatchPath = o.VerifyBatchPath,
            DdmsRoot = string.IsNullOrWhiteSpace(o.DdmsRoot) ? null : o.DdmsRoot!.Trim().TrimEnd('/'),
            ValidateLegalTags = o.ValidateLegalTags ?? true,
            LegalValidatePath = string.IsNullOrWhiteSpace(o.LegalValidatePath) ? null : o.LegalValidatePath!.Trim(),
            PreserveDataKeys = o.PreserveDataKeys ?? [],
            BatchSize = o.BatchSize ?? 100,
            UploadUrlPath = o.UploadUrlPath,
            FileMetadataPath = o.FileMetadataPath,
            DatasetKind = string.IsNullOrWhiteSpace(o.DatasetKind) ? "osdu:wks:dataset--File.Generic:1.0.0" : o.DatasetKind!.Trim(),
            UploadHeaders = new Dictionary<string, string>(o.UploadHeaders ?? [], StringComparer.OrdinalIgnoreCase),
            DatasetsProperty = string.IsNullOrWhiteSpace(o.DatasetsProperty) ? "Datasets" : o.DatasetsProperty!.Trim(),
            WorkflowName = string.IsNullOrWhiteSpace(o.WorkflowName) ? "Osdu_ingest" : o.WorkflowName!.Trim(),
            WorkflowRunPath = o.WorkflowRunPath,
            WorkflowStatusPath = o.WorkflowStatusPath,
            WorkflowPollSeconds = o.WorkflowPollSeconds ?? 10,
            WorkflowTimeoutMinutes = o.WorkflowTimeoutMinutes ?? 60,
            DatasetIndexWaitSeconds = o.DatasetIndexWaitSeconds ?? 120,
            ManifestKind = string.IsNullOrWhiteSpace(o.ManifestKind) ? "osdu:wks:Manifest:1.0.0" : o.ManifestKind!.Trim(),
            UploadUrlExpiry = string.IsNullOrWhiteSpace(o.UploadUrlExpiry) ? null : o.UploadUrlExpiry!.Trim(),
            FileDeletePath = o.FileDeletePath,
            ManifestSection = string.IsNullOrWhiteSpace(o.ManifestSection) ? null : o.ManifestSection!.Trim(),
            WorkflowAppKey = string.IsNullOrWhiteSpace(o.WorkflowAppKey) ? "osdu-delivery" : o.WorkflowAppKey!.Trim(),
            WorkflowPayload = new Dictionary<string, string>(o.WorkflowPayload ?? [], StringComparer.Ordinal),
            RecordQueryPath = o.RecordQueryPath,
            SearchQueryPath = o.SearchQueryPath,
            RegisterPath = string.IsNullOrWhiteSpace(o.RegisterPath) ? null : o.RegisterPath!.Trim(),
            DatasetInstructionsPath = Optional(o.DatasetInstructionsPath),
            DatasetRegisterPath = Optional(o.DatasetRegisterPath),
            DatasetRetrievalPath = Optional(o.DatasetRetrievalPath),
            DatasetSoftDeletePath = Optional(o.DatasetSoftDeletePath),
            ManifestByReference = ParseEnum(o.ManifestByReference, ManifestReference.Never, "target.protocolOptions.manifestByReference", source),
            ManifestInlineLimitKb = o.ManifestInlineLimitKb ?? ProtocolOptions.DefaultManifestInlineLimitKb,
            ByReferenceWorkflowName = Optional(o.ByReferenceWorkflowName) ?? ProtocolOptions.DefaultByReferenceWorkflowName,
            WorkflowPath = Optional(o.WorkflowPath),
            ContentSchemaVersion = Optional(o.ContentSchemaVersion) ?? ProtocolOptions.DefaultContentSchemaVersion,
        };
    }

    /// <summary>The reliability block of a retrieval or cache flow, which runs no interfaces.</summary>
    internal static FlowReliability MapReliability(FlowReliabilityYaml? r, string source)
    {
        if (r?.ParallelInterfaces is not null)
        {
            throw new FlowValidationException($"{source}: reliability.parallelInterfaces says how many interfaces of a delivery flow run at once; this flow declares none.");
        }

        return MapReliability(r, source, KeyPaths.Single);
    }

    private static FlowReliability MapReliability(FlowReliabilityYaml? r, string source, KeyPaths paths)
    {
        var defaults = new FlowReliability();
        if (r is null)
        {
            return defaults;
        }

        var retryDefaults = new FlowRetry();
        return new FlowReliability
        {
            Concurrency = r.Concurrency ?? defaults.Concurrency,
            Retry = r.Retry is null ? retryDefaults : new FlowRetry
            {
                Attempts = r.Retry.Attempts ?? retryDefaults.Attempts,
                Backoff = ParseEnum(r.Retry.Backoff, BackoffKind.Exponential, paths.Shared("reliability.retry.backoff"), source),
                BaseDelayMs = r.Retry.BaseDelayMs ?? retryDefaults.BaseDelayMs,
                MaxDelayMs = r.Retry.MaxDelayMs ?? retryDefaults.MaxDelayMs,
                HonorRetryAfter = r.Retry.HonorRetryAfter ?? retryDefaults.HonorRetryAfter,
                RecordBaseDelayMinutes = r.Retry.RecordBaseDelayMinutes ?? retryDefaults.RecordBaseDelayMinutes,
                RecordMaxDelayMinutes = r.Retry.RecordMaxDelayMinutes ?? retryDefaults.RecordMaxDelayMinutes,
            },
            SkipStatusCodes = r.SkipStatusCodes ?? [],
            TimeoutSeconds = r.TimeoutSeconds ?? defaults.TimeoutSeconds,
            RateLimitRps = r.RateLimitRps ?? defaults.RateLimitRps,
            VerifyTls = r.VerifyTls ?? defaults.VerifyTls,
            UrlAllowlist = r.UrlAllowlist ?? [],
            MaxResponseBytes = r.MaxResponseBytes ?? defaults.MaxResponseBytes,
            MaxRequestBodyBytes = r.MaxRequestBodyBytes ?? defaults.MaxRequestBodyBytes,
            LeaseSeconds = r.LeaseSeconds ?? defaults.LeaseSeconds,
            BatchSize = r.BatchSize ?? defaults.BatchSize,
            BatchRecords = r.BatchRecords ?? defaults.BatchRecords,
            FanOut = r.FanOut ?? defaults.FanOut,
            FanOutMinRecords = r.FanOutMinRecords ?? defaults.FanOutMinRecords,
            RenderParallelism = r.RenderParallelism ?? defaults.RenderParallelism,
        };
    }

    /// <summary>
    /// The External Data Services block (<c>target.eds</c>): how the records that configure EDS are checked before they are
    /// sent (osdu/specs/eds-dms/INTEGRATION.md section 2.1). A retrieval setting or a build named while the checks are off
    /// is refused, since nothing would read it.
    /// </summary>
    private static EdsTarget MapEds(EdsYaml? declared, string source)
    {
        if (declared is null)
        {
            return new EdsTarget();
        }

        var checks = declared.Checks ?? true;
        if (!checks && (declared.Retrieval is not null || !string.IsNullOrWhiteSpace(declared.Build)))
        {
            throw new FlowValidationException(
                $"{source}: target.eds.checks is false, so nothing reads target.eds.retrieval or target.eds.build. Remove them, or turn the checks back on.");
        }

        return new EdsTarget
        {
            Checks = checks,
            Retrieval = declared.Retrieval ?? true,
            Build = string.IsNullOrWhiteSpace(declared.Build) ? null : ParseEnum<EdsBuild>(declared.Build.Trim(), "target.eds.build", source),
        };
    }

    /// <summary>The file service's expiryTime shape: a whole number of minutes, hours or days.</summary>
    private static bool ValidExpiry(string expiry)
        => expiry.Length >= 2 && expiry[^1] is 'M' or 'H' or 'D' && expiry[..^1].All(char.IsAsciiDigit);

    internal static IEnumerable<string> Tokens(string text)
        => System.Text.RegularExpressions.Regex.Matches(text, @"\{(?<name>[A-Za-z0-9_]+)\}").Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal);

    internal static string Require(string? value, string key, string source)
        => string.IsNullOrWhiteSpace(value) ? throw Missing(key, source) : value!.Trim();

    internal static FlowValidationException Missing(string key, string source) => new($"{source}: '{key}' is required.");

    internal static T ParseEnum<T>(string value, string key, string source)
        where T : struct, Enum
        => Enum.TryParse<T>(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FlowValidationException($"{source}: '{key}' value '{value}' is not one of {string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");

    internal static T ParseEnum<T>(string? value, T fallback, string key, string source)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? fallback : ParseEnum<T>(value!, key, source);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A bare media type (<c>type/subtype</c>, parameters allowed), as a Content-Type header takes it.</summary>
    private static bool IsMediaType(string value)
    {
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value, out var parsed) || parsed.MediaType is not { } media)
        {
            return false;
        }

        var slash = media.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && slash < media.Length - 1;
    }

    private static IReadOnlyDictionary<string, string> Trimmed(Dictionary<string, string>? declared)
        => (declared ?? []).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value?.Trim() ?? string.Empty, StringComparer.Ordinal);
}
