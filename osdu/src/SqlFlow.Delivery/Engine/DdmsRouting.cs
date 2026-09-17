using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>Where the records of one entity type go on the ddms route: the DDMS serving it and the collection it serves it under.</summary>
public sealed record DdmsRoute(DdmsService Service, DdmsCollectionEntry Collection)
{
    /// <summary>The token a Well Delivery path takes the entity id in: the id's part after its type, never the whole id.</summary>
    public const string EntityIdToken = "{entityId}";

    /// <summary>The token a typed RAFS content path takes the content type in.</summary>
    public const string ContentTypeToken = "{contentType}";

    /// <summary>
    /// The collection below the flow's endpoint: <c>/api/os-wellbore-ddms/ddms/v3/welllogs</c>,
    /// <c>/api/well-delivery/storage/v1/wellbore</c>, <c>/api/rafs-ddms/v2/samplesanalysis</c>.
    /// </summary>
    public string CollectionPath => (Service.Root ?? string.Empty) + Service.Shape switch
    {
        DdmsShape.WellboreDdmsV3 => DdmsCatalog.WellboreDdmsV3Prefix,
        DdmsShape.WellDeliveryV1 => DdmsCatalog.WellDeliveryPrefix,
        DdmsShape.RafsV2 => DdmsCatalog.RafsV2Prefix,
        _ => throw new InvalidOperationException($"The DDMS '{Service.Name}' has the shape {Service.Shape}, which has no paths."),
    } + Collection.Segment;

    /// <summary>One record: read, verified and deleted here. The Well Delivery DDMS names it by its entity id alone.</summary>
    public string RecordPath => CollectionPath + (Service.Shape == DdmsShape.WellDeliveryV1 ? "/" + EntityIdToken : "/{id}");

    /// <summary>
    /// The whole bulk of a record, written at once (on the Wellbore DDMS, with <c>describe=true</c>, its description); on
    /// RAFS, one content table, under its content type when the collection holds several. Null where the DDMS keeps none.
    /// </summary>
    public string? DataPath => Service.Shape switch
    {
        DdmsShape.WellboreDdmsV3 => CollectionPath + "/{id}/data",
        DdmsShape.RafsV2 => CollectionPath + "/{id}/data" + (Collection.TypedContent ? "/" + ContentTypeToken : string.Empty),
        _ => null,
    };

    /// <summary>Where a bulk session of a record is opened; only the Wellbore DDMS has sessions.</summary>
    public string? SessionsPath => Service.Shape == DdmsShape.WellboreDdmsV3 ? CollectionPath + "/{id}/sessions" : null;

    /// <summary>One chunk of a session.</summary>
    public string? SessionDataPath => Service.Shape == DdmsShape.WellboreDdmsV3 ? CollectionPath + "/{id}/sessions/{sessionId}/data" : null;

    /// <summary>A session: committed or abandoned with PATCH, its state read with GET.</summary>
    public string? SessionPath => Service.Shape == DdmsShape.WellboreDdmsV3 ? CollectionPath + "/{id}/sessions/{sessionId}" : null;

    /// <summary>How a message names the collection: <c>welllogs of the DDMS 'wellbore' (/api/os-wellbore-ddms)</c>.</summary>
    public string Describe() => $"the {Collection.Segment} collection of {DdmsRouting.Describe(Service)}";
}

/// <summary>
/// The calls a ddms-route flow makes for the records of one entity type: the paths the flow names itself, and every
/// other one from the collection serving the entity type. A path is null when the flow has nowhere to send that call.
/// </summary>
public sealed record DdmsRecordPaths
{
    /// <summary>The entity type these paths serve.</summary>
    public required string EntityType { get; init; }

    /// <summary>The collection serving the entity type, or null when the flow names its own paths and no DDMS it reaches serves the type.</summary>
    public DdmsRoute? Route { get; init; }

    /// <summary>Where the record itself is written (POST with an array of records).</summary>
    public required string Records { get; init; }

    public required string Record { get; init; }

    public required string Delete { get; init; }

    public string? Data { get; init; }

    public string? Sessions { get; init; }

    public string? SessionData { get; init; }

    public string? Session { get; init; }

    /// <summary>Whether the records carry bulk data beside them. A flow naming its own paths for an unknown type is taken at its word.</summary>
    public bool Bulk => Route?.Collection.Bulk ?? true;

    /// <summary>What the bulk data's columns are checked against before they are sent.</summary>
    public DdmsBulkColumns Columns => Route?.Collection.Columns ?? DdmsBulkColumns.Unchecked;

    /// <summary>The call pattern the records go by: their DDMS's, or the Wellbore DDMS's for paths the flow names itself.</summary>
    public DdmsShape Shape => Route?.Service.Shape ?? DdmsShape.WellboreDdmsV3;

    /// <summary>How a message names where the records go.</summary>
    public string Describe() => Route?.Describe() ?? $"the paths the flow names ({Records})";
}

/// <summary>
/// Where the records of a flow on the ddms route go (docs/interfaces-design.md section 5.4): the DDMSs the flow declares
/// under <c>target.ddms</c>, then the Wellbore DDMS, whose collections OSDU Delivery knows (<see cref="DdmsCatalog"/>).
/// The collection a record goes to is the one serving its entity type, which every target id names
/// (<c>{partition}:{entityType}:{key}</c>), so a verify, a read or a removal routes from the id alone. A flow that names
/// its own paths (<c>protocolOptions.recordPath</c>) has them used as written, and the collection supplies the rest.
/// </summary>
public sealed class DdmsRouting
{
    private readonly ProtocolOptions _options;
    private readonly bool _declaresDdms;
    private readonly HashSet<string> _declared;
    private readonly bool _wellboreUnrooted;
    private readonly string _where;
    private readonly KeyPaths _keys;

    private DdmsRouting(ProtocolOptions options, IReadOnlyList<DdmsService> declared, bool interfaceForm, string where, KeyPaths keys)
    {
        _options = options;
        _declaresDdms = declared.Count > 0;
        _declared = declared.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        _where = where;
        _keys = keys;
        Unread = declared.Where(d => d.AwaitsDiscovery).Select(d => d.Name).ToList();

        // A DDMS whose registration has not been read has no known root, so nothing is routed to it yet.
        var services = declared.Where(d => !d.AwaitsDiscovery).ToList();
        if (options.DdmsRoot is { Length: > 0 } root)
        {
            services.Add(DdmsCatalog.WellboreDdms(root));
        }
        else if (!interfaceForm && declared.Count == 0)
        {
            // A single-form flow that says nothing about where its DDMS is has the DDMS itself as its endpoint.
            services.Add(DdmsCatalog.WellboreDdms(null));
            _wellboreUnrooted = true;
        }

        Services = services;
        PlatformEndpoint = options.DdmsRoot is { Length: > 0 } || interfaceForm || declared.Any(d => d.Root is not null || d.Registration is not null);
    }

    /// <summary>The routing of a flow on the ddms route.</summary>
    public static DdmsRouting Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return new DdmsRouting(flow.Target.ProtocolOptions, flow.Target.Ddms, flow.Interface is not null, KeyPaths.Where(flow), KeyPaths.Of(flow));
    }

    /// <summary>
    /// The routing of a flow in the single form that declares no DDMS of its own: the Wellbore DDMS, under
    /// <c>ddmsRoot</c> when the options name one and at the endpoint otherwise.
    /// </summary>
    public static DdmsRouting Of(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DdmsRouting(options, [], interfaceForm: false, "the flow", KeyPaths.Single);
    }

    /// <summary>The DDMSs the flow reaches, in the order a record's entity type is looked up in.</summary>
    public IReadOnlyList<DdmsService> Services { get; }

    /// <summary>
    /// The DDMSs the flow names by a registration that has not been read (<see cref="DdmsDiscovery"/>): what they serve
    /// is not known yet, so an entity type no other DDMS serves may still be theirs.
    /// </summary>
    public IReadOnlyList<string> Unread { get; }

    /// <summary>
    /// Whether the flow's endpoint is the OSDU platform root rather than a DDMS itself, so the services every record
    /// also lives in (storage, legal) resolve under it by their default paths.
    /// </summary>
    public bool PlatformEndpoint { get; }

    /// <summary>Whether the flow names its own DDMS paths.</summary>
    public bool NamesPaths => _options.RecordPath is not null;

    /// <summary>The route of <paramref name="entityType"/>, or null when no DDMS the flow reaches serves it.</summary>
    public DdmsRoute? Find(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        foreach (var service in Services)
        {
            if (service.CollectionFor(entityType) is { } collection)
            {
                return new DdmsRoute(service, collection);
            }
        }

        return null;
    }

    /// <summary>The entity type an OSDU record id names (<c>{partition}:{entityType}:{key}</c>), or null when it names none.</summary>
    public static string? EntityTypeOf(string targetId)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        var parts = targetId.Split(':');
        return parts.Length >= 3 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>The calls for the record <paramref name="targetId"/>. Throws a <see cref="DeliveryException"/> when the flow cannot route it.</summary>
    public DdmsRecordPaths ForRecord(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var entityType = EntityTypeOf(targetId)
            ?? throw new DeliveryException($"{_where}: the record id '{targetId}' names no entity type ({{partition}}:{{entityType}}:{{key}}), so the ddms route cannot tell which collection holds it.");
        return For(entityType);
    }

    /// <summary>The calls for records of <paramref name="entityType"/>. Throws a <see cref="DeliveryException"/> when the flow cannot route them.</summary>
    public DdmsRecordPaths For(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        var route = Find(entityType);
        if (route is { Service.Shape: not DdmsShape.WellboreDdmsV3 } shaped)
        {
            // Paths a flow names are those of a Wellbore DDMS facade; every other shape says each call it takes.
            var named = NamedPathOptions();
            if (named.Count > 0)
            {
                throw new DeliveryException(
                    $"{_where}: the flow names DDMS paths of its own ({string.Join(", ", named)} under {_keys.Shared("target.protocolOptions")}), which only a DDMS of the "
                    + $"wellboreDdmsV3 shape takes, and {entityType} records go to {shaped.Describe()}, whose shape ({DdmsCatalog.ShapeName(shaped.Service.Shape)}) "
                    + "says every call they take. Remove those paths, or leave that entity type out of the DDMS's collections.");
            }

            return new DdmsRecordPaths
            {
                EntityType = entityType,
                Route = shaped,
                Records = shaped.CollectionPath,
                Record = shaped.RecordPath,
                Delete = shaped.RecordPath,
                Data = shaped.Collection.Bulk ? shaped.DataPath : null,
            };
        }

        if (!NamesPaths)
        {
            if (route is null)
            {
                throw new DeliveryException(Unread.Count > 0
                    ? $"{_where}: no DDMS this flow reaches serves {entityType} unless {string.Join(" or ", Unread.Select(n => $"target.ddms.{n}"))} does, "
                        + "and its registration in the Register service has not been read yet. It is read when the flow's protocol is built."
                    : Unserved(entityType));
            }

            return new DdmsRecordPaths
            {
                EntityType = entityType,
                Route = route,
                Records = route.CollectionPath,
                Record = _options.VerifyPath ?? route.RecordPath,
                Delete = _options.DeletePath ?? route.RecordPath,
                Data = _options.DataPath ?? (route.Collection.Bulk ? route.DataPath : null),
                Sessions = _options.SessionPath ?? (route.Collection.Bulk ? route.SessionsPath : null),
                SessionData = _options.SessionDataPath ?? (route.Collection.Bulk ? route.SessionDataPath : null),
                Session = _options.SessionCommitPath ?? (route.Collection.Bulk ? route.SessionPath : null),
            };
        }

        var bulk = route?.Collection.Bulk ?? true;
        return new DdmsRecordPaths
        {
            EntityType = entityType,
            Route = route,
            Records = _options.RecordPath!,
            Record = _options.VerifyPath ?? route?.RecordPath ?? throw new DeliveryException(Unnamed(entityType, "verifyPath")),
            Delete = _options.DeletePath ?? route?.RecordPath ?? throw new DeliveryException(Unnamed(entityType, "deletePath")),
            Data = _options.DataPath ?? (bulk ? route?.DataPath : null),
            Sessions = _options.SessionPath ?? (bulk ? route?.SessionsPath : null),
            SessionData = _options.SessionDataPath ?? (bulk ? route?.SessionDataPath : null),
            Session = _options.SessionCommitPath ?? (bulk ? route?.SessionPath : null),
        };
    }

    /// <summary>
    /// Why the flow cannot deliver records of <paramref name="entityType"/>, with bulk data when
    /// <paramref name="sendsBulk"/>, or null when it can: no DDMS it reaches serves the type, a path it would need is
    /// nowhere, or the collection holds records alone.
    /// </summary>
    public string? Problem(string entityType, bool sendsBulk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        if (!NamesPaths && Unread.Count > 0 && Find(entityType) is null)
        {
            // Whether a DDMS named by its registration serves the type is known once the registration is read.
            return null;
        }

        DdmsRecordPaths paths;
        try
        {
            paths = For(entityType);
        }
        catch (DeliveryException ex)
        {
            return ex.Message;
        }

        if (!sendsBulk)
        {
            return null;
        }

        if (paths.Route is { Collection.Bulk: false } route && (paths.Data is null || paths.Sessions is null))
        {
            var payload = _keys.Payload(_options.Payload ?? FlowMapper.BulkPayload);
            var how = route.Service.Shape == DdmsShape.WellboreDdmsV3
                ? "Deliver those records without it, or name the DDMS paths that take their bulk data."
                : _declared.Contains(route.Service.Name) && !route.Service.DeclaresCollections
                    ? $"target.ddms.{route.Service.Name} serves the type because its shape ({DdmsCatalog.ShapeName(route.Service.Shape)}) serves it unless the flow lists "
                        + $"other collections: list the ones it is to serve under target.ddms.{route.Service.Name}.collections, leaving {entityType} to the DDMS that keeps its bulk data, "
                        + "or deliver the records without it."
                    : "Deliver those records without it.";
            return $"{_where}: {entityType} records go to {route.Describe()}, which holds records alone and takes no bulk data, so {payload} would never be sent. {how}";
        }

        if (paths.Shape != DdmsShape.WellboreDdmsV3)
        {
            return null;
        }

        var missing = new[] { ("dataPath", paths.Data), ("sessionPath", paths.Sessions), ("sessionDataPath", paths.SessionData), ("sessionCommitPath", paths.Session) }
            .Where(p => p.Item2 is null)
            .Select(p => p.Item1)
            .ToList();
        return missing.Count == 0
            ? null
            : $"{_where}: the flow names its own DDMS paths, and no DDMS it reaches serves {entityType}, so there is nowhere to send its bulk data. "
                + $"Name {string.Join(", ", missing)} under {_keys.Shared("target.protocolOptions")} as well.";
    }

    /// <summary>
    /// Where records of <paramref name="kind"/> go, as the flow's explanation says it (<c>sqlflow check</c>, the API's
    /// target view): the collection and the DDMS, the registration still to be read, or why no DDMS takes them.
    /// </summary>
    public string Explain(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (OsduKind.EntityType(kind) is not { } entityType)
        {
            return $"{kind} names no entity type, so no DDMS collection takes its records.";
        }

        if (!NamesPaths && Unread.Count > 0 && Find(entityType) is null)
        {
            return $"{entityType} records go to the DDMS {string.Join(" or ", Unread.Select(n => $"target.ddms.{n}"))} registered for them, "
                + "which is read from the Register service when the flow runs.";
        }

        try
        {
            return $"{entityType} records go to {For(entityType).Describe()}.";
        }
        catch (DeliveryException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The service descriptions the probe asks: the flow's own probe path, or each DDMS it reaches, as its shape describes itself.</summary>
    public IReadOnlyList<string> ProbePaths
        => _options.ProbePath is { } probe
            ? [probe]
            : Services.SelectMany(DdmsCatalog.ProbePaths).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Where the storage service's reversible delete is, for the records a DDMS writes into storage beside its own (the
    /// Well Delivery DDMS's copies, the datasets RAFS registers for content): the storage default under a platform
    /// endpoint; null when the flow's endpoint is a DDMS itself.
    /// </summary>
    public string? StorageDeletePath => PlatformEndpoint ? OsduRecordProtocol.DefaultDeletePath : null;

    /// <summary>The path options a flow sets that name DDMS calls of a Wellbore DDMS facade.</summary>
    private List<string> NamedPathOptions()
    {
        var named = new List<string>();
        foreach (var (name, value) in new[]
        {
            ("recordPath", _options.RecordPath), ("verifyPath", _options.VerifyPath), ("deletePath", _options.DeletePath), ("dataPath", _options.DataPath),
            ("sessionPath", _options.SessionPath), ("sessionDataPath", _options.SessionDataPath), ("sessionCommitPath", _options.SessionCommitPath),
        })
        {
            if (value is not null)
            {
                named.Add(name);
            }
        }

        return named;
    }

    /// <summary>
    /// Where the storage service's purge of a record's earlier versions is (versions belong to storage for every kind of
    /// record): the flow's own path, or the storage default under a platform endpoint; null when neither is known.
    /// </summary>
    public string? HistoryPath
        => _options.PurgeVersionsPath is { Length: > 0 } explicitPath ? explicitPath
            : PlatformEndpoint ? OsduRecordProtocol.DefaultPurgeVersionsPath
            : null;

    /// <summary>
    /// Where the storage service's purge of a whole record is, for a collection whose DDMS does not purge (a record-only
    /// collection's DELETE is logical only): the flow's own <c>purgePath</c>, or the storage default under a platform
    /// endpoint; null when neither is known.
    /// </summary>
    public string? StoragePurgePath
        => _options.PurgePath is { Length: > 0 } explicitPath ? explicitPath
            : PlatformEndpoint ? OsduRecordProtocol.DefaultPurgePath
            : null;

    /// <summary>Where the legal service's validation is, or null when the flow does not ask or does not reach it.</summary>
    public string? LegalValidatePath => LegalTagValidator.PathFor(_options, PlatformEndpoint);

    /// <summary>How a message names a DDMS: its name, and where it is under the endpoint.</summary>
    internal static string Describe(DdmsService service)
        => $"the DDMS '{service.Name}' ({(service.Root is { } root ? root : "the endpoint itself")})";

    private string Unserved(string entityType)
    {
        var reached = Services.Count == 0
            ? "it reaches no DDMS"
            : "it reaches " + string.Join("; ", Services.Select(s => $"{Describe(s)}, serving {string.Join(", ", s.Collections.Select(c => c.EntityType))}"));
        var how = _declaresDdms || _wellboreUnrooted || _options.DdmsRoot is not null
            ? $"Declare the DDMS that serves it under target.ddms, add the collection to one declared there, or name its paths under {_keys.Shared("target.protocolOptions")}."
            : $"Say where the Wellbore DDMS is with {_keys.Shared("target.protocolOptions.ddmsRoot")}, declare the DDMS that serves it under target.ddms, or name its paths under {_keys.Shared("target.protocolOptions")}.";
        return $"{_where}: no DDMS this flow reaches serves {entityType}: {reached}. {how}";
    }

    private string Unnamed(string entityType, string option)
        => $"{_where}: the flow names its own DDMS paths but not {option}, and no DDMS it reaches serves {entityType} to take it from. "
            + $"Name {option} under {_keys.Shared("target.protocolOptions")}.";
}
