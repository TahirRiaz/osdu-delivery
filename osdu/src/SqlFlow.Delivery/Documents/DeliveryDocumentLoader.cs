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

        var protocol = ParseEnum<DeliveryProtocol>(Require(target.Protocol, "target.protocol", source), "target.protocol", source);
        var mapping = Require(render.Mapping, paths.Mapping, source);
        if (!mapping.Contains('@', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: {paths.Mapping} '{mapping}' must be pinned as 'Name@version'; floating references are not allowed.");
        }

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
            Source = MapSource(src, source, paths),
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
                ProtocolOptions = MapOptions(target.ProtocolOptions),
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

            if (flow.Target.Protocol == DeliveryProtocol.OsduWellLog && flow.Target.ProtocolOptions is { DdmsRoot: null, RecordPath: null })
            {
                throw new FlowValidationException(
                    $"{source}: {at} is delivered through a DDMS, and the source's endpoint is the platform every interface reaches its services under. "
                    + $"Say where the DDMS is under it with target.protocolOptions.ddmsRoot or {at}.protocolOptions.ddmsRoot (the wellbore DDMS is usually deployed under /api/os-wellbore-ddms).");
            }

            flows.Add(flow);
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
        if (route.Protocol != DeliveryProtocol.OsduWellLog)
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

    /// <summary>How an interface is delivered, the payload part its route sends, and why.</summary>
    private sealed record InterfaceRoute(DeliveryProtocol Protocol, string? Payload, string Reason);

    /// <summary>
    /// The route of an interface, from what its records carry (docs/interfaces-design.md section 5.2): nothing beside the
    /// record goes through the storage service, files through the file service before the record, DDMS bulk data through a
    /// DDMS after the record. <c>route:</c> names one outright, and a route that cannot deliver what the interface declares
    /// is refused, so nothing it declares is silently left unsent.
    /// </summary>
    private static InterfaceRoute ResolveRoute(InterfaceYaml i, string at, string source)
    {
        var files = i.Files is not null;
        var bulk = i.Bulk is not null;
        var named = string.IsNullOrWhiteSpace(i.Route) ? null : (InterfaceRouteName?)ParseEnum<InterfaceRouteName>(i.Route!.Trim(), at + ".route", source);
        var filesKey = $"{at}.{FilesPayload}";
        var bulkKey = $"{at}.{BulkPayload}";

        void Refuse(bool refused, string why)
        {
            if (refused)
            {
                throw new FlowValidationException($"{source}: {why}");
            }
        }

        switch (named)
        {
            case InterfaceRouteName.Storage:
                Refuse(files || bulk, $"{at}.route is storage, which writes the record alone, so {(files ? filesKey : bulkKey)} would never be sent. Remove it, or choose the route that delivers it.");
                return new InterfaceRoute(DeliveryProtocol.OsduRecord, null, $"{at}.route names the storage route: each record is written through the storage service");
            case InterfaceRouteName.File:
                Refuse(!files, $"{at}.route is file, which uploads and registers each record's files, and the interface declares none under {filesKey}.");
                Refuse(bulk, $"{at}.route is file, which does not write DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(DeliveryProtocol.OsduFile, FilesPayload, $"{at}.route names the file route: each record's files are uploaded and registered through the file service before the record is written");
            case InterfaceRouteName.Manifest:
                Refuse(bulk, $"{at}.route is manifest, which sends records through the ingestion workflow and writes no DDMS bulk data, so {bulkKey} would never be sent.");
                return new InterfaceRoute(
                    DeliveryProtocol.OsduManifest,
                    files ? FilesPayload : null,
                    $"{at}.route names the manifest route: the records go through the ingestion workflow in manifests" + (files ? ", their files registered first" : string.Empty));
            case InterfaceRouteName.Ddms:
                Refuse(files, $"{at}.route is ddms, which writes the record and its bulk data through a DDMS and registers no files, so {filesKey} would never be sent. Deliver the files through an interface of their own.");
                return new InterfaceRoute(
                    DeliveryProtocol.OsduWellLog,
                    bulk ? BulkPayload : null,
                    $"{at}.route names the ddms route: each record is written through its DDMS" + (bulk ? ", then its bulk data" : string.Empty));
        }

        Refuse(files && bulk, $"{at} declares both {FilesPayload} and {BulkPayload}. A record's files and its DDMS bulk data are delivered by different routes; deliver them through two interfaces, one for each.");
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

    /// <summary>The routes an interface can name with <c>route:</c>.</summary>
    private enum InterfaceRouteName
    {
        Storage,
        File,
        Manifest,
        Ddms,
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

        if (DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
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
                var payload = src.Payloads[payloadName];
                if (payload.LocationColumn is null)
                {
                    throw new FlowValidationException(
                        $"{source}: the {flow.Target.Protocol} protocol streams payload '{payloadName}', so {paths.Payload(payloadName)}.locationColumn must name the record column holding each record's payload folder.");
                }

                if (payload.HashColumn is null && flow.Change.PayloadDetect != ChangeDetection.LastModified)
                {
                    throw new FlowValidationException(
                        $"{source}: the flow decides payload changes by content hash, so {paths.Payload(payloadName)}.hashColumn must name the record column holding it; or take the files' modified times instead with {paths.Shared("change.payloadDetect")}: lastModified.");
                }
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

        if (flow.Change.PayloadDetect == ChangeDetection.LastModified && !DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
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
            if (flow.Target.Protocol != DeliveryProtocol.OsduWellLog)
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

    private static ProtocolOptions MapOptions(ProtocolOptionsYaml? o)
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
            PayloadContentType = o.PayloadContentType ?? "application/x-parquet",
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

    private static IReadOnlyDictionary<string, string> Trimmed(Dictionary<string, string>? declared)
        => (declared ?? []).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value?.Trim() ?? string.Empty, StringComparer.Ordinal);
}
