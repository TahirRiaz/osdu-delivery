using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>
/// The Well Delivery DDMS shape (osdu/specs/well-delivery-ddms/INTEGRATION.md). Each entity goes alone to
/// <c>PUT /storage/v1/{type}</c>, the type being its id's type lowercased, under a version the route chooses: a 13-digit
/// epoch-millisecond value above the one the ledger holds, recorded as a step before the write, so every retry of the
/// same revision sends it again and the DDMS replaces that version in place rather than adding one (section 4). A
/// forced redelivery of content the ledger already delivered writes the delivered version again, which is how the DDMS
/// takes references to entities written later, except on IBM, whose store refuses a second write of a version.
///
/// The DDMS indexes only references that end in a version, and its domain queries and reference trees work only
/// through that index (section 3), so a reference the mapping renders in the usual form, ending in a colon, to an entity
/// this DDMS serves is sent with the version the DDMS holds for that entity; a reference to an entity it does not hold
/// yet is sent as rendered, and the attempt names it. Every rule the service applies before it writes, and the ones that
/// silently lose data (a date it cannot parse is stored as null), is checked before the request.
///
/// The service returns 201 whether or not the entity passed its schema check; the findings of a failed check are kept on
/// the attempt, since nothing reads them later. Where the deployment copies entities into Storage (<c>mirror</c>), the
/// copy's id is recorded, and a removal takes the copy through Storage as well, since the DDMS never deletes it. Writes to
/// one DDMS go through a gate sized by the DDMS's <c>concurrency</c>: the Mongo and Cosmos stores keep the current
/// collection in shared state (section 6).
/// </summary>
internal sealed partial class WellDeliveryShape(DdmsShapeContext context) : IDdmsShape
{
    /// <summary>The step that records the version a revision is written under, before the write.</summary>
    public const string VersionStep = "version";

    /// <summary>The target state entry holding the hash of the content the ledger last delivered, which an in-place rewrite compares.</summary>
    public const string ContentKey = "wellDelivery.content";

    /// <summary>The target state entry holding the id of the entity's copy in Storage.</summary>
    public const string StorageIdKey = "wellDelivery.storageId";

    /// <summary>The most legal tags one entity may carry: the DDMS sends them all in one Legal validation, which takes 25 names.</summary>
    public const int MaxLegalTags = 25;

    private const int MaxReturnedText = 4000;

    private static readonly TimeSpan ReferenceLifetime = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-ddzzz", "yyyy-M-d",
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "HH:mm:ss", "HH:mm:sszzz", "H:m:s",
    ];

    private readonly OsduHttpClient _client = context.Client;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly TimeProvider _time = context.Time;
    private readonly ConcurrentDictionary<string, (long? Version, DateTimeOffset ReadAt)> _references = new(StringComparer.Ordinal);

    public Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var route = RouteOf(paths);
        if (work.DeliverPayload)
        {
            throw new RecordHeldException($"{work.TargetId} goes to {route.Describe()}, which holds records alone and takes no bulk data; deliver the record without a payload");
        }

        if (work.DeliverMetadata && work.Completed(OsduDdmsProtocol.MetadataStep) is null && RecordProblem(work.TargetId, work.Document) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        return Task.FromResult<object?>(null);
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var route = RouteOf(paths);
        if (!work.DeliverMetadata)
        {
            return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
        }

        var steps = new DeliverySteps(_time);
        if (work.Completed(OsduDdmsProtocol.MetadataStep) is { } done)
        {
            // An earlier try wrote the entity and failed afterwards; nothing is written again.
            steps.Resumed(OsduDdmsProtocol.MetadataStep, done);
            var resumed = done.TryGetValue("version", out var text) ? RecordWriter.ParseVersion(text) : null;
            return new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = false,
                TargetVersion = resumed ?? work.ExistingVersion,
                Returned = done,
                Steps = steps.Steps,
            };
        }

        var entityId = DdmsShapeValues.EntityId(work.TargetId)
            ?? throw new RecordHeldException($"the record id '{work.TargetId}' names no entity id after its type, which the Well Delivery DDMS keys the entity by");
        var content = ContentOf(work.Document);
        var version = await VersionAsync(work, content, route, steps, ct).ConfigureAwait(false);
        var (document, pinned, unpinned) = await PinAsync(work.Document, route.Service, work.TargetId, ct).ConfigureAwait(false);
        document["version"] = version;

        var started = steps.Now;
        var url = _client.Url(route.CollectionPath);
        var (status, valid, findings) = await WriteAsync(route, url, document, entityId, version, ct).ConfigureAwait(false);
        Remember(route.Service, route.Collection.Segment, entityId, version);

        var returned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recordId"] = work.TargetId,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
            ["entityType"] = route.Collection.Segment,
            ["entityId"] = entityId,
            [ContentKey] = content,
            ["wellDelivery.pinned"] = pinned.ToString(CultureInfo.InvariantCulture),
        };
        if (valid is { } isValid)
        {
            returned["wellDelivery.valid"] = isValid ? "true" : "false";
        }

        if (findings is not null)
        {
            returned["wellDelivery.schemaFindings"] = Clip(findings);
        }

        if (unpinned.Count > 0)
        {
            returned["wellDelivery.unpinned"] = Clip(DdmsShapeValues.Bounded(unpinned));
        }

        if ((route.Service.WellDelivery ?? new WellDeliverySettings()).Mirror)
        {
            returned[StorageIdKey] = StorageId(work.TargetId);
        }

        steps.Add(OsduDdmsProtocol.MetadataStep, started, status, returned);
        await work.ReportStepAsync(OsduDdmsProtocol.MetadataStep, returned, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        if (valid == false)
        {
            warnings.Add("stored with the JSON-schema findings the DDMS reported (wellDelivery.schemaFindings)");
        }

        if (unpinned.Count > 0)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{unpinned.Count} reference(s) to entities the DDMS does not hold were sent without a version, so its queries do not find the entity through them: {DdmsShapeValues.Bounded(unpinned, 5)}"));
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = true,
            PayloadDelivered = false,
            TargetVersion = version,
            Detail = warnings.Count == 0 ? null : string.Join("; ", warnings),
            Returned = returned,
            Steps = steps.Steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
        => RecordWriter.VerifyAsync(_client, EntityUrl(RouteOf(paths).RecordPath, targetId), expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, EntityUrl(RouteOf(paths).RecordPath, targetId), ct);

    /// <summary>
    /// The reversible scope soft-deletes every version of the entity (<c>DELETE /storage/v1/{type}/{id}</c>, which the
    /// DDMS reverses only by a write of the same version) and its Storage copy (<c>POST /records/{id}:delete</c>);
    /// everything purges both (<c>DELETE /storage/v1/{type}/{id}:purge</c>, an admin operation, and storage's
    /// <c>DELETE /records/{id}</c>). The history scope is refused: the DDMS keys every version by the value other
    /// entities' references cite, so purging earlier versions would cut those references.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        var route = RouteOf(paths);
        if (scope == RemovalScope.History)
        {
            throw new RecordHeldException(
                $"{targetId} is in {route.Describe()}, which keys every version of an entity by the value other entities' references cite, so purging its earlier versions would cut those references; "
                + "remove the record, or purge everything, instead");
        }

        var mirror = (route.Service.WellDelivery ?? new WellDeliverySettings()).Mirror;
        var storageId = targetState is not null && targetState.TryGetValue(StorageIdKey, out var recorded) && recorded.Length > 0 ? recorded : StorageId(targetId);
        Uri? copyUrl = null;
        if (mirror)
        {
            var storagePath = (scope == RemovalScope.Record ? _routing.StorageDeletePath : _routing.StoragePurgePath)
                ?? throw new RecordHeldException(
                    $"{route.Describe()} copies {targetId} into Storage and never deletes the copy, and this flow does not say where the storage service is. "
                    + "Give the DDMS its root under target.ddms when the endpoint is the OSDU platform root"
                    + (scope == RemovalScope.Everything ? ", or name purgePath (an absolute URL such as https://<host>/api/storage/v2/records/{id})" : string.Empty)
                    + ", or declare mirror: false for a deployment that keeps no copy.");
            copyUrl = _client.Url(storagePath, storageId);
        }

        var entity = EntityUrl(scope == RemovalScope.Everything ? route.RecordPath + ":purge" : route.RecordPath, targetId);
        var removedEntity = await RecordWriter.RemoveAtAsync(_client, HttpMethod.Delete, entity, ct).ConfigureAwait(false);
        var removedCopy = copyUrl is not null
            && await RecordWriter.RemoveAtAsync(_client, scope == RemovalScope.Record ? HttpMethod.Post : HttpMethod.Delete, copyUrl, ct).ConfigureAwait(false);
        if (!removedEntity && !removedCopy)
        {
            return new DeleteOutcome(false, true, "record not found in the Well Delivery DDMS" + (mirror ? " or in Storage" : string.Empty));
        }

        var what = !removedEntity ? "already gone from the Well Delivery DDMS"
            : scope == RemovalScope.Record ? "removed from the Well Delivery DDMS (reversible: a write of the same version restores it)"
            : "purged from the Well Delivery DDMS (every version)";
        var copy = !mirror ? string.Empty
            : !removedCopy ? $"; its Storage copy {storageId} was already gone"
            : scope == RemovalScope.Record ? $", and its Storage copy {storageId} removed (reversible)"
            : $", and its Storage copy {storageId} purged";
        return new DeleteOutcome(true, false, what + copy);
    }

    /// <summary>The Well Delivery DDMS keeps no bulk data, so a record carries no link to any.</summary>
    public bool CarryLink(JsonObject? stored, JsonObject document) => true;

    /// <summary>
    /// Why the Well Delivery DDMS would refuse <paramref name="document"/>, or store less of it than was sent, or null
    /// when it would not (osdu/specs/well-delivery-ddms/INTEGRATION.md sections 2 to 4 and 8).
    /// </summary>
    internal static string? RecordProblem(string targetId, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(document);
        if (!EntityIdPattern().IsMatch(targetId))
        {
            return $"the record id '{targetId}' does not match the id the Well Delivery DDMS takes (<namespace>:<group>--<Type>:<entityId>)";
        }

        var entityType = DdmsRouting.EntityTypeOf(targetId)!;
        var type = entityType[(entityType.IndexOf("--", StringComparison.Ordinal) + 2)..];
        if (!ReadableType().IsMatch(type))
        {
            return $"the type {type} of '{targetId}' has characters the Well Delivery DDMS takes on a write and refuses on every read and delete (letters, digits and '-' only)";
        }

        if (DdmsShapeValues.EntityId(targetId) is not { } entityId || !EntityIdSegment().IsMatch(entityId))
        {
            return $"the entity id of '{targetId}' has characters other than letters, digits, '-', '_' and '.'; the Well Delivery DDMS's reference trees and queries lose an entity whose id has a ':' or '%'";
        }

        if (Text(document["kind"]) is null)
        {
            return "the record has no kind, which the Well Delivery DDMS requires";
        }

        if (AclProblem(document["acl"]) is { } acl)
        {
            return acl;
        }

        if (LegalProblem(document["legal"]) is { } legal)
        {
            return legal;
        }

        if (document["data"] is not JsonObject data)
        {
            return "the record has no data object, which the Well Delivery DDMS requires";
        }

        if (Text(data["ExistenceKind"]) is not { } existence)
        {
            return "data.ExistenceKind is not given; the Well Delivery DDMS requires it of every entity, in reference form (<namespace>:reference-data--ExistenceKind:Planned:)";
        }

        if (!ReferenceForm().IsMatch(existence))
        {
            return $"data.ExistenceKind '{existence}' is not in the reference form the Well Delivery DDMS requires, which ends in ':' or a version (<namespace>:reference-data--ExistenceKind:Planned:)";
        }

        if (document["meta"] is { } meta && (meta is not JsonArray items || items.Any(item => item is not JsonObject)))
        {
            return "meta is not an array of objects, which the Well Delivery DDMS refuses";
        }

        foreach (var property in new[] { "StartDateTime", "EndDateTime" })
        {
            if (data[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } date && !SupportedDate(date))
            {
                return $"data.{property} '{date}' is in a form the Well Delivery DDMS cannot parse (fractional seconds are not among them), and it would store the date as null; "
                    + "render it as yyyy-MM-ddTHH:mm:ss, with an offset or Z, or as a date alone";
            }
        }

        return null;
    }

    /// <summary>
    /// The id of an entity's copy in Storage: the record id with its namespace replaced by the partition, every occurrence
    /// of it, as the service's <c>String.replace</c> does (osdu/specs/well-delivery-ddms/INTEGRATION.md section 4).
    /// </summary>
    internal string StorageId(string targetId)
    {
        var partition = _client.Header(FlowMapper.PartitionHeader);
        return DdmsShapeValues.Namespace(targetId) is { } ns && !string.IsNullOrEmpty(partition) && !string.Equals(ns, partition, StringComparison.Ordinal)
            ? targetId.Replace(ns, partition, StringComparison.Ordinal)
            : targetId;
    }

    /// <summary>
    /// The version this revision is written under. An earlier try's version is sent again; content the ledger delivered
    /// before goes back under the version it holds, so references to that version see the rewrite; anything else gets a
    /// new epoch-millisecond version above the one the ledger holds. The version is recorded before the write.
    /// </summary>
    private async Task<long> VersionAsync(DeliveryWork work, string content, DdmsRoute route, DeliverySteps steps, CancellationToken ct)
    {
        if (work.Completed(VersionStep) is { } minted
            && minted.TryGetValue("version", out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var chosen))
        {
            steps.Resumed(VersionStep, minted);
            return chosen;
        }

        var ibm = route.Service.WellDelivery?.Provider == DdmsProvider.Ibm;
        long version;
        string write;
        if (work.ExistingVersion is { } existing && IsEpochMillis(existing)
            && work.TargetState.TryGetValue(ContentKey, out var delivered) && string.Equals(delivered, content, StringComparison.Ordinal)
            && !ibm)
        {
            version = existing;
            write = "in place";
        }
        else
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var floor = work.ExistingVersion is { } previous && IsEpochMillis(previous) ? previous + 1 : 0;
            version = Math.Max(now, floor);
            if (!IsEpochMillis(version))
            {
                throw new RecordHeldException(
                    string.Create(CultureInfo.InvariantCulture, $"the next version of {work.TargetId} would be {version}, which is not a 13-digit epoch-millisecond value; the Well Delivery DDMS orders versions as text"));
            }

            write = "new";
        }

        var started = steps.Now;
        var returned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
            ["write"] = write,
        };
        await work.ReportStepAsync(VersionStep, returned, ct).ConfigureAwait(false);
        steps.Add(VersionStep, started, null, returned);
        return version;
    }

    /// <summary>
    /// Writes the entity through the gate of its DDMS. An answer lost to a server error or a dropped connection is settled
    /// by reading the version back: when the DDMS holds it, the write landed (an IBM store refuses the repeat of a write
    /// that landed with a server error).
    /// </summary>
    private async Task<(int Status, bool? Valid, string? Findings)> WriteAsync(DdmsRoute route, Uri url, JsonObject document, string entityId, long version, CancellationToken ct)
    {
        var gate = Gate(route.Service);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _client.SendJsonAsync(HttpMethod.Put, url, document, null, ct).ConfigureAwait(false);
            if (result.Body.Length == 0)
            {
                return ((int)result.Status, null, null);
            }

            var body = OsduHttpClient.ParseJson(result, url);
            var valid = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("valid", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean()
                : (bool?)null;
            var findings = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null
                ? errors.GetRawText()
                : null;
            return ((int)result.Status, valid, findings);
        }
        catch (Exception ex) when (ex is OsduStatusException { StatusCode: >= 500 } || ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException })
        {
            var versionUrl = _client.Url(route.RecordPath + "/{version}", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entityId"] = entityId,
                ["version"] = version.ToString(CultureInfo.InvariantCulture),
            });
            var held = await _client.SendJsonAsync(HttpMethod.Get, versionUrl, null, new HashSet<int> { 400, 404 }, ct).ConfigureAwait(false);
            if ((int)held.Status is >= 200 and < 300)
            {
                return ((int)held.Status, null, null);
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The rendered document with every reference to an entity this DDMS serves given the version the DDMS holds for it,
    /// as the DDMS's index needs (osdu/specs/well-delivery-ddms/INTEGRATION.md section 3, reference indexing): every
    /// string in <c>data</c>, in nested objects and in arrays of strings or objects, as the index scans them. Returns how
    /// many were pinned and the ones the DDMS holds no entity for.
    /// </summary>
    private async Task<(JsonObject Document, int Pinned, List<string> Unpinned)> PinAsync(JsonObject rendered, DdmsService service, string targetId, CancellationToken ct)
    {
        var document = (JsonObject)rendered.DeepClone();
        var unpinned = new List<string>();
        if (document["data"] is not JsonObject data)
        {
            return (document, 0, unpinned);
        }

        var slots = new List<(JsonNode Parent, string? Key, int Index, Match Reference)>();
        Collect(data, slots, inArray: false);
        var self = DdmsRouting.EntityTypeOf(targetId) is { } selfType ? DdmsCatalog.WellDeliveryType(selfType) + ":" + DdmsShapeValues.EntityId(targetId) : null;
        var pinned = 0;
        foreach (var (parent, key, index, reference) in slots)
        {
            var entityType = reference.Groups["type"].Value;
            var type = DdmsCatalog.WellDeliveryType(entityType);
            var entityId = reference.Groups["id"].Value;
            if (service.CollectionFor(entityType) is null || string.Equals(type + ":" + entityId, self, StringComparison.Ordinal))
            {
                continue;
            }

            if (await ReferenceVersionAsync(service, type, entityId, ct).ConfigureAwait(false) is not { } version)
            {
                unpinned.Add(reference.Value);
                continue;
            }

            var versioned = reference.Value + version.ToString(CultureInfo.InvariantCulture);
            if (key is not null)
            {
                ((JsonObject)parent)[key] = versioned;
            }
            else
            {
                ((JsonArray)parent)[index] = versioned;
            }

            pinned++;
        }

        return (document, pinned, unpinned.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>The unversioned references under <paramref name="node"/>, as the DDMS's index scans it: arrays inside arrays are not read.</summary>
    private static void Collect(JsonNode? node, List<(JsonNode Parent, string? Key, int Index, Match Reference)> slots, bool inArray)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (Unversioned(value) is { } match)
                    {
                        slots.Add((obj, key, -1, match));
                    }
                    else if (value is JsonObject or JsonArray)
                    {
                        Collect(value, slots, inArray: false);
                    }
                }

                break;
            case JsonArray array when !inArray:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Unversioned(array[i]) is { } match)
                    {
                        slots.Add((array, null, i, match));
                    }
                    else if (array[i] is JsonObject item)
                    {
                        Collect(item, slots, inArray: false);
                    }
                }

                break;
        }
    }

    private static Match? Unversioned(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && UnversionedReference().Match(value.GetValue<string>()) is { Success: true } match
            ? match
            : null;

    /// <summary>
    /// The latest version the DDMS holds for an entity, read once and kept for a few minutes; null when it holds none (a
    /// 404, or the 400 a Mongo store answers for a type never written). An entity the DDMS does not hold is asked for again
    /// by the next record, since the interface that delivers it may have landed it meanwhile.
    /// </summary>
    private async Task<long?> ReferenceVersionAsync(DdmsService service, string type, string entityId, CancellationToken ct)
    {
        var key = Key(service, type, entityId);
        var now = _time.GetUtcNow();
        if (_references.TryGetValue(key, out var known) && now - known.ReadAt < ReferenceLifetime)
        {
            return known.Version;
        }

        var url = _client.Url((service.Root ?? string.Empty) + DdmsCatalog.WellDeliveryPrefix + "{type}/{entityId}", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["entityId"] = entityId,
        });
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 400, 404 }, ct).ConfigureAwait(false);
        long? version = null;
        if ((int)result.Status is >= 200 and < 300 && result.Body.Length > 0)
        {
            version = RecordWriter.ParseVersion(JsonPathReader.SelectValue(OsduHttpClient.ParseJson(result, url), "version"));
        }

        if (version is not null)
        {
            _references[key] = (version, now);
        }

        return version;
    }

    private void Remember(DdmsService service, string type, string entityId, long version)
        => _references[Key(service, type, entityId)] = (version, _time.GetUtcNow());

    private static string Key(DdmsService service, string type, string entityId) => service.Name + "|" + type + "|" + entityId;

    /// <summary>The gate every write to one Well Delivery deployment from this process goes through.</summary>
    private SemaphoreSlim Gate(DdmsService service)
    {
        var size = Math.Clamp((service.WellDelivery ?? new WellDeliverySettings()).Concurrency, 1, WellDeliverySettings.MaxConcurrency);
        return Gates.GetOrAdd(_client.Url(service.Root ?? "/").GetLeftPart(UriPartial.Path).TrimEnd('/'), _ => new SemaphoreSlim(size, size));
    }

    private Uri EntityUrl(string template, string targetId)
    {
        var entityId = DdmsShapeValues.EntityId(targetId)
            ?? throw new RecordHeldException($"the record id '{targetId}' names no entity id after its type, which the Well Delivery DDMS keys the entity by");
        return _client.Url(template, new Dictionary<string, string>(StringComparer.Ordinal) { ["entityId"] = entityId });
    }

    private static DdmsRoute RouteOf(DdmsRecordPaths paths)
        => paths.Route ?? throw new InvalidOperationException($"{paths.EntityType} records have no Well Delivery collection to go to.");

    /// <summary>The hash of what a revision says, the version it is written under aside.</summary>
    private static string ContentOf(JsonObject document)
    {
        var copy = (JsonObject)document.DeepClone();
        copy.Remove("version");
        return Hashing.ContentHash.Of(CanonicalJson.ToString(copy));
    }

    private static bool IsEpochMillis(long value) => value is >= 1_000_000_000_000 and <= 9_999_999_999_999;

    private static string Clip(string text) => text.Length <= MaxReturnedText ? text : text[..MaxReturnedText] + "...";

    private static string? AclProblem(JsonNode? acl)
    {
        if (acl is not JsonObject groups || groups.Count == 0)
        {
            return "the record has no acl, which the Well Delivery DDMS requires";
        }

        foreach (var role in new[] { "owners", "viewers" })
        {
            var members = Strings(groups[role]);
            if (members is null || members.Count == 0)
            {
                return $"acl.{role} is empty; the Well Delivery DDMS requires owners and viewers";
            }

            if (members.FirstOrDefault(m => !m.Contains('@', StringComparison.Ordinal)) is { } bare)
            {
                return $"acl.{role} names '{bare}' without a domain; the Well Delivery DDMS checks every group's domain and fails on one without '@'";
            }
        }

        return null;
    }

    private static string? LegalProblem(JsonNode? legal)
    {
        if (legal is not JsonObject block || block.Count == 0)
        {
            return "the record has no legal block, which the Well Delivery DDMS requires";
        }

        var tags = Strings(block["legaltags"]);
        if (tags is null || tags.Count == 0)
        {
            return "legal.legaltags is empty; the Well Delivery DDMS requires at least one legal tag";
        }

        if (tags.Distinct(StringComparer.Ordinal).Count() > MaxLegalTags)
        {
            return string.Create(CultureInfo.InvariantCulture, $"the record carries {tags.Count} legal tags; the Well Delivery DDMS validates them in one Legal request, which takes at most {MaxLegalTags}");
        }

        var countries = Strings(block["otherRelevantDataCountries"]);
        return countries is null || countries.Count == 0
            ? "legal.otherRelevantDataCountries is empty; the Well Delivery DDMS requires at least one country"
            : null;
    }

    /// <summary>The strings of a list, or null when it is not a list of non-blank strings.</summary>
    private static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (Text(item) is not { } text)
            {
                return null;
            }

            values.Add(text);
        }

        return values;
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>
    /// Whether the service's date parser reads <paramref name="value"/> (its <c>DateTimeUtil</c>, section 3): local and
    /// offset date-times without fractional seconds, RFC 1123, basic and extended dates, and times.
    /// </summary>
    private static bool SupportedDate(string value)
        => DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
           || DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // EntityStorageService.java:66, the id the service takes.
    [GeneratedRegex(@"^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdPattern();

    // EntityStorageService.java:67, the type every read and delete route takes.
    [GeneratedRegex(@"^[0-9a-zA-Z\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReadableType();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityIdSegment();

    // EntityStorageService.java:70, the reference form data.ExistenceKind takes.
    [GeneratedRegex(@"^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+:[0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceForm();

    // A reference as mappings render it, ending in ':', to an entity whose id the DDMS can key.
    [GeneratedRegex(@"^(?<ns>[\w\-\.]+):(?<type>[0-9a-zA-Z\-]+\-\-[\w\-]*):(?<id>[A-Za-z0-9_.\-]+):$", RegexOptions.CultureInvariant)]
    private static partial Regex UnversionedReference();
}
