using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols.Dspdm;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The dspdm route (osdu/specs/production-dspdm/INTEGRATION.md): each record is a row of a business object of the
/// Production DDMS core service (DSPDM), named by its kind (<c>{authority}:dspdm:{entity}:{version}</c>), and its data block
/// holds the row's attributes. DSPDM keeps rows in its own database under a primary key it draws when it inserts a row, and
/// its save is an upsert by that key, so a save is not idempotent: a row sent without its key is inserted again. The route
/// therefore finds every row before it saves it.
/// <list type="bullet">
/// <item>Each row is checked against the business object's metadata, read once from DSPDM: an attribute it does not have, its
/// primary key, an attribute DSPDM fills or keeps read-only, and a value its data type does not take hold the record, as does
/// a key attribute left empty.</item>
/// <item>The rows of a delivery are found in one read (<c>POST /common</c>, the key's attributes IN the rows' values), and the
/// rows the records were delivered as are read by primary key. A record's row is the row its target state names while it
/// exists, and otherwise the row its key finds. A row its key finds that the record did not write is taken only when the
/// record's earlier try sent a save for that key (the step <c>save-begin</c>, recorded before every save), or when the flow
/// says to take such rows over (<c>target.dspdm.existingRows: update</c>); otherwise the record is held.</item>
/// <item>The rows are saved in one call (<c>POST /save</c> with <c>readBack</c>), one transaction in DSPDM. An update sends the
/// row's primary key and every attribute its mapping fills, with null for one that rendered empty, so a value the source
/// cleared is cleared. A call DSPDM refuses is sent again one row at a time, so one bad row holds only itself.</item>
/// <item>A row's version is when DSPDM last changed it (<c>ROW_CHANGED_DATE</c>, or <c>ROW_CREATED_DATE</c> for a row never
/// changed), in milliseconds; the target state keeps the business object, the row's primary key and what the save did.</item>
/// </list>
/// A verify reads the rows by primary key and compares versions. Removal deletes a row for good
/// (<c>DELETE /delete/{boName}/{id}</c>, the everything scope): DSPDM keeps no deleted rows and no versions, so the record and
/// history scopes are refused.
/// </summary>
public sealed class OsduDspdmProtocol : IDeliveryProtocol
{
    public const string FindStep = "find";
    public const string SaveBeginStep = "save-begin";
    public const string SaveStep = "save";

    /// <summary>The business object a record's row belongs to, in its target state.</summary>
    public const string BusinessObjectValue = "dspdm.businessObject";

    /// <summary>The primary key DSPDM gave a record's row, in its target state.</summary>
    public const string IdValue = "dspdm.id";

    /// <summary>What the last save did to a record's row (inserted, updated, unchanged), in its target state.</summary>
    public const string OperationValue = "dspdm.operation";

    /// <summary>The business object and key a save was sent for, in the step <see cref="SaveBeginStep"/>.</summary>
    public const string FingerprintValue = "fingerprint";

    public const string VersionValue = "version";
    public const string Inserted = "inserted";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";

    /// <summary>The most rows one save sends: the rows one IN condition of a lookup covers.</summary>
    public const int MaxRowsPerSave = DspdmService.MaxInValues;

    /// <summary>Why the record scope is refused.</summary>
    public const string RecordScopeRefused = "DSPDM keeps no deleted rows, so a row has no reversible removal; the everything scope deletes it for good";

    /// <summary>Why the history scope is refused.</summary>
    public const string HistoryScopeRefused = "DSPDM keeps no earlier versions of a row, so there is no history to purge; the everything scope deletes the row for good";

    private readonly DspdmService _service;
    private readonly DspdmCatalog _catalog;
    private readonly DspdmTarget _target;
    private readonly ProtocolOptions _options;
    private readonly ILogger<OsduDspdmProtocol> _logger;
    private readonly TimeProvider _time;

    public OsduDspdmProtocol(OsduHttpClient client, ProtocolOptions options, DspdmTarget target, ILogger<OsduDspdmProtocol> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _target = target;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _service = new DspdmService(client, target.Root, target.Timezone);
        _catalog = new DspdmCatalog(_service, target);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Dspdm;

    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, MaxRowsPerSave);

    int IDeliveryProtocol.MaxVerifyBatch => DspdmService.MaxInValues;

    bool IDeliveryProtocol.VerifiesWithTargetState => true;

    /// <summary>
    /// Checks that DSPDM has a business object the rows of <paramref name="kind"/> can be saved in, and that its rows can be
    /// found again by a key: the route's preflight, before anything is sent.
    /// </summary>
    public async Task CheckKindAsync(string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        (await _catalog.ForKindAsync(kind, ct).ConfigureAwait(false)).RequireKey();
    }

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome[works.Count];
        foreach (var chunk in works.Select((w, i) => (Work: w, Index: i)).Chunk(MaxBatch))
        {
            var delivery = new Delivery(this, chunk.Select(c => c.Work).ToList());
            var results = await delivery.RunAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                outcomes[chunk[i].Index] = results[i];
            }
        }

        return outcomes;
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => VerifyOneAsync(new VerifyRequest(targetId, expectedVersion), ct);

    /// <summary>
    /// Reads the rows the records were delivered as, by primary key (<c>POST /common</c>, up to
    /// <see cref="DspdmService.MaxInValues"/> keys a read), and compares each row's version with the one the ledger holds. A row
    /// DSPDM no longer holds is missing; a record whose target state names no row cannot be verified.
    /// </summary>
    public async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new VerifyResult[requests.Count];
        var groups = new Dictionary<(string Entity, string Name), List<(int Index, long Id)>>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (StoredRow(request.TargetState) is not { } stored)
            {
                results[i] = new VerifyResult(VerifyOutcome.Error, null, "the record's target state names no DSPDM row, so there is no row to read back");
                continue;
            }

            if (EntityOf(request.TargetId) is not { } entity)
            {
                results[i] = new VerifyResult(VerifyOutcome.Error, null, $"{request.TargetId} names no entity type, so the business object of its row cannot be told");
                continue;
            }

            if (!groups.TryGetValue((entity, stored.Name), out var group))
            {
                group = [];
                groups[(entity, stored.Name)] = group;
            }

            group.Add((i, stored.Id));
        }

        foreach (var ((entity, name), group) in groups)
        {
            ct.ThrowIfCancellationRequested();
            DspdmObject business;
            try
            {
                business = await _catalog.ForEntityAsync(entity, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                Settle(group, new VerifyResult(VerifyOutcome.Error, null, HeaderRedaction.RedactMessage(ex.Message)));
                continue;
            }

            if (!string.Equals(business.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Settle(group, new VerifyResult(VerifyOutcome.Error, null, $"the record was delivered as a row of {name}, and the flow now writes the rows of {entity} to {business.Name}"));
                continue;
            }

            foreach (var chunk in group.Chunk(DspdmService.MaxInValues))
            {
                IReadOnlyDictionary<long, JsonObject> rows;
                try
                {
                    rows = await ReadByIdAsync(business, chunk.Select(c => c.Id).ToList(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
                {
                    Settle(chunk, new VerifyResult(VerifyOutcome.Error, null, HeaderRedaction.RedactMessage(ex.Message)));
                    continue;
                }

                foreach (var (index, id) in chunk)
                {
                    results[index] = Compare(business, id, rows.TryGetValue(id, out var row) ? row : null, requests[index].ExpectedVersion);
                }
            }
        }

        return results;

        void Settle(IEnumerable<(int Index, long Id)> members, VerifyResult result)
        {
            foreach (var (index, _) in members)
            {
                results[index] = result;
            }
        }
    }

    /// <summary>The record's row as DSPDM holds it, found by what the record's target state names; the record's row cannot be read back without it.</summary>
    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => ReadAsync(targetId, null, ct);

    /// <summary>
    /// The record's row as DSPDM holds it (<c>POST /common</c> by primary key), laid out as a record: the record's id, the row's
    /// version, the business object and primary key under <c>dspdm</c>, and the row's attributes under <c>data</c>; null when
    /// DSPDM no longer holds the row.
    /// </summary>
    public async Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var stored = StoredRow(targetState)
            ?? throw new DeliveryException($"{targetId}: the record's target state names no DSPDM row, so there is no row to read back; a record has one once it is delivered.");
        var entity = EntityOf(targetId)
            ?? throw new DeliveryException($"{targetId} names no entity type, so the business object of its row cannot be told.");
        var business = await _catalog.ForEntityAsync(entity, ct).ConfigureAwait(false);
        if (!string.Equals(business.Name, stored.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException($"{targetId} was delivered as a row of {stored.Name}, and the flow now writes the rows of {entity} to {business.Name}.");
        }

        var rows = await _service.ReadAsync(
            business.Name, [], [new DspdmFilter(business.PrimaryKey.Name, DspdmFilter.EqualsOperator, [JsonValue.Create(stored.Id)])], business.PrimaryKey.Name, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var data = (JsonObject)rows[0].DeepClone();
        var version = DspdmValues.VersionOf(data);

        // The serializer's own keys are lower case; the business object's attributes are upper case.
        foreach (var written in (string[])["type", "id", "isInserted", "isUpdated", "isDeleted"])
        {
            data.Remove(written);
        }

        return new JsonObject
        {
            ["id"] = targetId,
            ["version"] = version,
            ["dspdm"] = new JsonObject { ["businessObject"] = business.Name, ["id"] = stored.Id },
            ["data"] = data,
        };
    }

    /// <summary>
    /// Deletes the record's row for good (<c>DELETE /delete/{boName}/{id}</c>): the everything scope. DSPDM keeps no deleted rows
    /// and no versions, so the record and history scopes are refused. A row DSPDM no longer holds is gone already.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (scope != RemovalScope.Everything)
        {
            throw new DeliveryException($"{targetId}: {(scope == RemovalScope.Record ? RecordScopeRefused : HistoryScopeRefused)}.");
        }

        var stored = StoredRow(targetState)
            ?? throw new DeliveryException($"{targetId}: the record's target state names no DSPDM row, so there is no row of it to delete.");
        var removal = await _service.DeleteAsync(stored.Name, stored.Id, ct).ConfigureAwait(false);
        return removal == DspdmRemoval.Gone
            ? new DeleteOutcome(false, true, string.Create(CultureInfo.InvariantCulture, $"row {stored.Id} of {stored.Name} not found in DSPDM"))
            : new DeleteOutcome(true, false, string.Create(CultureInfo.InvariantCulture, $"row {stored.Id} of {stored.Name} deleted from DSPDM, for good"));
    }

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default) => _service.ProbeAsync(ct);

    private async Task<VerifyResult> VerifyOneAsync(VerifyRequest request, CancellationToken ct)
        => (await VerifyBatchAsync([request], ct).ConfigureAwait(false))[0];

    /// <summary>What one row's read means for its record: missing, drifted, or matching the version the ledger holds.</summary>
    private static VerifyResult Compare(DspdmObject business, long id, JsonObject? row, long? expected)
    {
        if (row is null)
        {
            return new VerifyResult(VerifyOutcome.Missing, null, string.Create(CultureInfo.InvariantCulture, $"row {id} of {business.Name} not found"));
        }

        var observed = DspdmValues.VersionOf(row);
        if (observed is null)
        {
            return business.VersionAttributes.Count == 0
                ? new VerifyResult(VerifyOutcome.Match, null, $"{business.Name} keeps no change date, so only the row's presence is checked")
                : new VerifyResult(VerifyOutcome.Error, null, string.Create(CultureInfo.InvariantCulture, $"row {id} of {business.Name} carries no change date"));
        }

        if (expected is null)
        {
            return new VerifyResult(VerifyOutcome.Match, observed, "no expected version recorded; observed version adopted");
        }

        return observed == expected
            ? new VerifyResult(VerifyOutcome.Match, observed, null)
            : new VerifyResult(
                VerifyOutcome.Drifted,
                observed,
                string.Create(CultureInfo.InvariantCulture, $"row {id} of {business.Name} changed at {DspdmValues.Moment(observed.Value)}; the ledger holds the save of {DspdmValues.Moment(expected.Value)}"));
    }

    /// <summary>The rows of <paramref name="business"/> with the given primary keys, keyed by primary key.</summary>
    private async Task<IReadOnlyDictionary<long, JsonObject>> ReadByIdAsync(DspdmObject business, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var found = new Dictionary<long, JsonObject>();
        foreach (var chunk in ids.Distinct().Chunk(DspdmService.MaxInValues))
        {
            var select = new[] { business.PrimaryKey.Name }.Concat(business.Key.Select(k => k.Name)).Concat(business.VersionAttributes).Distinct(StringComparer.Ordinal).ToList();
            var rows = await _service.ReadAsync(
                business.Name,
                select,
                [new DspdmFilter(business.PrimaryKey.Name, DspdmFilter.InOperator, chunk.Select(id => (JsonNode?)JsonValue.Create(id)).ToList())],
                business.PrimaryKey.Name,
                ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (IdOf(business, row) is { } id)
                {
                    found[id] = row;
                }
            }
        }

        return found;
    }

    /// <summary>The primary key of a row DSPDM answered with: its own attribute, or the serializer's <c>id</c>.</summary>
    private static long? IdOf(DspdmObject business, JsonObject row)
        => DspdmValues.Key(row[business.PrimaryKey.Name]) ?? DspdmValues.Key(row["id"]);

    /// <summary>The row a record's target state names: its business object and primary key.</summary>
    private static (string Name, long Id)? StoredRow(IReadOnlyDictionary<string, string>? state)
    {
        if (state is null
            || !state.TryGetValue(BusinessObjectValue, out var name) || string.IsNullOrWhiteSpace(name)
            || !state.TryGetValue(IdValue, out var text)
            || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return (name, id);
    }

    /// <summary>The entity type a record's id names (<c>{partition}:{entity}:{key}</c>), which is its business object's entity.</summary>
    private static string? EntityOf(string targetId)
        => targetId.Split(':') is { Length: 3 } parts && parts[1].Length > 0 ? parts[1] : null;

    private static bool Empty(JsonNode? value)
        => value is null || (value.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetValue<string>()));

    /// <summary>A save outcome's failure as the worker is to take it: a refusal about the row it was sent alone holds the record.</summary>
    private static Exception Verdict(Exception failure, bool alone)
        => alone && failure is DspdmRefusal { AboutTheRows: true } refusal
            ? new RecordHeldException($"DSPDM refused the row: {refusal.Message}", refusal)
            : failure;

    /// <summary>Whether a failed save says nothing about the service, so the other rows of the delivery are still worth sending.</summary>
    private static bool AboutTheRow(Exception failure)
        => failure is DspdmRefusal or OsduStatusException { StatusCode: >= 400 and < 500 and not (401 or 403 or 408 or 425 or 429) };

    /// <summary>One record's row in a delivery: what it is to be saved as, and what happened to it.</summary>
    private sealed class Row(int index, DeliveryWork work, DeliverySteps steps)
    {
        public int Index { get; } = index;

        public DeliveryWork Work { get; } = work;

        public DeliverySteps Steps { get; } = steps;

        public required DspdmObject Business { get; init; }

        /// <summary>The attributes the row gives, upper case.</summary>
        public required JsonObject Values { get; init; }

        /// <summary>The attributes the row's mapping fills, upper case.</summary>
        public required IReadOnlySet<string> Owned { get; init; }

        /// <summary>The key's values in the form DSPDM keeps them, one per key attribute.</summary>
        public required IReadOnlyList<string> Tokens { get; init; }

        public required string Fingerprint { get; init; }

        public (string Name, long Id)? Stored { get; init; }

        public string KeyText => string.Join('', Tokens);

        public string KeyDisplay => string.Join(", ", Business.Key.Select((k, i) => $"{k.Name} '{Tokens[i]}'"));

        public bool Insert { get; set; }

        public long? Id { get; set; }

        /// <summary>The row as the find read it, whose version an unchanged save keeps.</summary>
        public JsonObject? Found { get; set; }

        public string? Note { get; set; }

        /// <summary>The body the save sends: the row's values, and for an update its primary key and a null for each attribute its mapping fills that rendered empty.</summary>
        public JsonObject Body()
        {
            var body = new JsonObject();
            if (!Insert)
            {
                body[Business.PrimaryKey.Name] = Id;
            }

            foreach (var (name, value) in Values)
            {
                if (value is not null)
                {
                    body[name] = value.DeepClone();
                }
            }

            if (!Insert)
            {
                foreach (var name in Owned.Where(o => Values[o] is null))
                {
                    body[name] = null;
                }
            }

            return body;
        }
    }

    /// <summary>One chunk of works on its way to DSPDM: prepared, found, decided, saved and settled.</summary>
    private sealed class Delivery(OsduDspdmProtocol protocol, IReadOnlyList<DeliveryWork> works)
    {
        private readonly DeliveryOutcome?[] _outcomes = new DeliveryOutcome?[works.Count];

        public async Task<IReadOnlyList<DeliveryOutcome>> RunAsync(CancellationToken ct)
        {
            var rows = new List<Row>();
            for (var i = 0; i < works.Count; i++)
            {
                var work = works[i];
                if (!work.DeliverMetadata)
                {
                    _outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
                    continue;
                }

                try
                {
                    rows.Add(await PrepareAsync(i, work, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
                {
                    _outcomes[i] = DeliveryOutcome.Failed(ex);
                }
            }

            foreach (var group in rows.GroupBy(r => r.Business.Name, StringComparer.Ordinal))
            {
                await DeliverAsync(group.First().Business, group.ToList(), ct).ConfigureAwait(false);
            }

            return _outcomes.Select((o, i) => o ?? DeliveryOutcome.Failed(new DeliveryException($"{works[i].TargetId}: the dspdm route settled nothing for the record."))).ToList();
        }

        /// <summary>The record's row, checked against its business object: its values, the attributes its mapping fills, its key, and the row it was delivered as.</summary>
        private async Task<Row> PrepareAsync(int index, DeliveryWork work, CancellationToken ct)
        {
            var document = work.Document;
            var kind = document["kind"] is JsonValue k && k.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new RecordHeldException("the rendered row names no kind, so its business object cannot be told");
            var business = await protocol._catalog.ForKindAsync(kind, ct).ConfigureAwait(false);
            var key = business.RequireKey();
            if (document["data"] is not JsonObject data)
            {
                throw new RecordHeldException($"the rendered row of {business.Name} has no data block, which holds its attributes");
            }

            var problems = new List<string>();
            var values = new JsonObject();
            foreach (var (name, value) in data)
            {
                var upper = name.ToUpperInvariant();
                if (values.ContainsKey(upper))
                {
                    problems.Add($"two properties name the attribute {upper}");
                }
                else if (Unwritable(business, upper) is { } reason)
                {
                    problems.Add(reason);
                }
                else if (DspdmValues.Refusal(business.Attributes[upper].Type, value, protocol._service.Zone) is { } refusal)
                {
                    problems.Add($"{upper} {refusal}");
                }
                else
                {
                    values[upper] = value?.DeepClone();
                }
            }

            var owned = new SortedSet<string>(values.Select(v => v.Key), StringComparer.Ordinal);
            foreach (var item in document[DspdmKinds.OwnedProperty] as JsonArray ?? [])
            {
                if (item is JsonValue listed && listed.TryGetValue<string>(out var attribute) && !string.IsNullOrWhiteSpace(attribute)
                    && owned.Add(attribute.Trim().ToUpperInvariant()) && Unwritable(business, attribute.Trim().ToUpperInvariant()) is { } reason)
                {
                    problems.Add($"its mapping fills {reason}");
                }
            }

            var tokens = new List<string>(key.Count);
            foreach (var attribute in key)
            {
                var token = values.ContainsKey(attribute.Name) ? DspdmValues.Token(attribute.Type, values[attribute.Name], protocol._service.Zone) : null;
                if (string.IsNullOrEmpty(token))
                {
                    problems.Add($"{attribute.Name} is empty, and the rows of {business.Name} are found again by {string.Join(", ", key.Select(a => a.Name))}");
                }

                tokens.Add(token ?? string.Empty);
            }

            if (problems.Count > 0)
            {
                throw new RecordHeldException($"the row cannot be saved in {business.Name}: {string.Join("; ", problems.Distinct(StringComparer.Ordinal))}");
            }

            (string Name, long Id)? stored = null;
            if (work.TargetState.ContainsKey(IdValue))
            {
                stored = StoredRow(work.TargetState)
                    ?? throw new RecordHeldException($"the record's target state names the DSPDM row '{work.TargetState[IdValue]}' without a business object or a whole-number key; remove the record before delivering it again");
            }

            return new Row(index, work, new DeliverySteps(protocol._time))
            {
                Business = business,
                Values = values,
                Owned = owned,
                Tokens = tokens,
                Fingerprint = ContentHash.Of(business.Name + "\n" + string.Join('\n', tokens)),
                Stored = stored,
            };
        }

        /// <summary>Why a row cannot give <paramref name="attribute"/>, or null when it can.</summary>
        private static string? Unwritable(DspdmObject business, string attribute)
        {
            if (!business.Attributes.TryGetValue(attribute, out var known))
            {
                return $"{attribute}, which is not an active attribute of {business.Name}";
            }

            if (known.PrimaryKey)
            {
                return $"{attribute}, the primary key of {business.Name}, which DSPDM gives a row when it inserts it";
            }

            if (known.Audit)
            {
                return $"{attribute}, which DSPDM fills on every save";
            }

            return known.ReadOnly ? $"{attribute}, which is read-only in {business.Name}" : null;
        }

        /// <summary>The rows of one business object: found, decided, then saved together.</summary>
        private async Task DeliverAsync(DspdmObject business, List<Row> rows, CancellationToken ct)
        {
            // Two records of one delivery cannot be one row: the later ones are held.
            var active = new List<Row>(rows.Count);
            var byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
            var byStored = new Dictionary<long, Row>();
            foreach (var row in rows)
            {
                if (byKey.TryGetValue(row.KeyText, out var first)
                    || (row.Stored is { } stored && byStored.TryGetValue(stored.Id, out first)))
                {
                    Hold(row, $"another record of this delivery ({first.Work.SourceKey ?? first.Work.TargetId}) is the same row of {business.Name} ({row.KeyDisplay}), and a row is one record's");
                    continue;
                }

                byKey[row.KeyText] = row;
                if (row.Stored is { } owned)
                {
                    byStored[owned.Id] = row;
                }

                active.Add(row);
            }

            if (active.Count == 0)
            {
                return;
            }

            var started = active[0].Steps.Now;
            IReadOnlyDictionary<Row, List<JsonObject>> candidates;
            IReadOnlyDictionary<long, JsonObject> storedRows;
            try
            {
                candidates = await FindAsync(business, active, ct).ConfigureAwait(false);
                var unseen = active
                    .Where(r => r.Stored is { } s && string.Equals(s.Name, business.Name, StringComparison.OrdinalIgnoreCase)
                        && !candidates[r].Any(c => IdOf(business, c) == s.Id))
                    .Select(r => r.Stored!.Value.Id)
                    .ToList();
                storedRows = unseen.Count == 0 ? new Dictionary<long, JsonObject>() : await protocol.ReadByIdAsync(business, unseen, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var row in active)
                {
                    row.Steps.Add(FindStep, started, (ex as OsduStatusException)?.StatusCode, null, HeaderRedaction.RedactMessage(ex.Message));
                    _outcomes[row.Index] = DeliveryOutcome.Failed(ex, row.Steps.Steps);
                }

                return;
            }

            var toSave = new List<Row>(active.Count);
            foreach (var row in active)
            {
                var found = candidates[row];
                row.Steps.Add(FindStep, started, 200, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["found"] = found.Count == 0 ? "none" : string.Join(",", found.Select(f => IdOf(business, f)?.ToString(CultureInfo.InvariantCulture) ?? "?")),
                });
                if (Decide(business, row, found, storedRows) && Complete(business, row))
                {
                    toSave.Add(row);
                }
            }

            if (toSave.Count == 0)
            {
                return;
            }

            // The marker a later try reads when this save's answer is lost: the row it finds by this key is the one this try saved.
            await Task.WhenAll(toSave.Select(row =>
            {
                var begun = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FingerprintValue] = row.Fingerprint,
                    [BusinessObjectValue] = business.Name,
                    [OperationValue] = row.Insert ? "insert" : "update",
                };
                row.Steps.Add(SaveBeginStep, row.Steps.Now, null, begun);
                return row.Work.ReportStepAsync(SaveBeginStep, begun, ct);
            })).ConfigureAwait(false);

            await SaveAsync(business, toSave, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds the rows the records' keys name, in one read: each key attribute IN the values the rows give. The rows found
        /// are matched to the records by their keys in the form DSPDM keeps them; a row found whose key is not among the values
        /// sent means DSPDM compares a value differently, and the records left without a row are then looked up one by one, by
        /// DSPDM's own comparison.
        /// </summary>
        private async Task<IReadOnlyDictionary<Row, List<JsonObject>>> FindAsync(DspdmObject business, IReadOnlyList<Row> rows, CancellationToken ct)
        {
            var key = business.Key;
            var zone = protocol._service.Zone;
            var filters = new List<DspdmFilter>(key.Count);
            var sent = new List<HashSet<string>>(key.Count);
            for (var i = 0; i < key.Count; i++)
            {
                var distinct = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    distinct.TryAdd(row.Tokens[i], Filtered(row.Values[key[i].Name]));
                }

                filters.Add(new DspdmFilter(key[i].Name, DspdmFilter.InOperator, distinct.Values.ToList()));
                sent.Add([.. distinct.Keys]);
            }

            var found = await protocol._service.ReadAsync(business.Name, business.LookupAttributes, filters, business.PrimaryKey.Name, ct).ConfigureAwait(false);
            var byKey = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            var mismatch = false;
            foreach (var candidate in found)
            {
                var tokens = key.Select(k => DspdmValues.Token(k.Type, candidate[k.Name], zone) ?? string.Empty).ToList();
                if (tokens.Where((token, i) => !sent[i].Contains(token)).Any())
                {
                    mismatch = true;
                    continue;
                }

                var text = string.Join('', tokens);
                if (!byKey.TryGetValue(text, out var list))
                {
                    list = [];
                    byKey[text] = list;
                }

                list.Add(candidate);
            }

            var result = new Dictionary<Row, List<JsonObject>>();
            foreach (var row in rows)
            {
                result[row] = byKey.TryGetValue(row.KeyText, out var list) ? list : [];
                if (mismatch && result[row].Count == 0)
                {
                    result[row] = [.. await protocol._service.ReadAsync(
                        business.Name,
                        business.LookupAttributes,
                        key.Select(k => new DspdmFilter(k.Name, DspdmFilter.EqualsOperator, [Filtered(row.Values[k.Name])])).ToList(),
                        business.PrimaryKey.Name,
                        ct).ConfigureAwait(false)];
                }
            }

            if (mismatch)
            {
                protocol._logger.LogWarning(
                    "DSPDM answered rows of {BusinessObject} whose keys ({Key}) the route reads differently from the values it sent; the rows it did not match were looked up one by one.",
                    business.Name, string.Join(", ", key.Select(k => k.Name)));
            }

            return result;
        }

        /// <summary>A value as a filter sends it: text trimmed, as DSPDM trims what it saves.</summary>
        private static JsonNode? Filtered(JsonNode? value)
            => value is JsonValue text && text.GetValueKind() == JsonValueKind.String ? JsonValue.Create(text.GetValue<string>().Trim()) : value?.DeepClone();

        /// <summary>Which row the record is, or why it is held; false when it is held.</summary>
        private bool Decide(DspdmObject business, Row row, List<JsonObject> found, IReadOnlyDictionary<long, JsonObject> storedRows)
        {
            if (found.Count > 1)
            {
                Hold(row, $"{found.Count} rows of {business.Name} ({string.Join(", ", found.Select(f => IdOf(business, f)))}) have {row.KeyDisplay}, so the record's row cannot be told");
                return false;
            }

            var candidate = found.Count == 1 ? found[0] : null;
            var candidateId = candidate is null ? null : IdOf(business, candidate);
            if (candidate is not null && candidateId is null)
            {
                Fail(row, new DeliveryException($"DSPDM answered a row of {business.Name} without its primary key {business.PrimaryKey.Name}."));
                return false;
            }

            if (row.Stored is { } stored)
            {
                if (!string.Equals(stored.Name, business.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Hold(row, string.Create(CultureInfo.InvariantCulture,
                        $"the record was delivered as row {stored.Id} of {stored.Name}, and its kind now writes rows of {business.Name}; remove the record (the everything scope deletes row {stored.Id} of {stored.Name}) before delivering it again"));
                    return false;
                }

                if (candidateId == stored.Id)
                {
                    return Update(row, stored.Id, candidate, null);
                }

                if (storedRows.TryGetValue(stored.Id, out var existing))
                {
                    if (candidate is null)
                    {
                        return Update(row, stored.Id, existing, "its key changed");
                    }

                    Hold(row, string.Create(CultureInfo.InvariantCulture,
                        $"{row.KeyDisplay} is the key of row {candidateId} of {business.Name}, and the record's row is {stored.Id}; two rows cannot share a key"));
                    return false;
                }

                if (candidate is null)
                {
                    row.Insert = true;
                    row.Note = string.Create(CultureInfo.InvariantCulture, $"row {stored.Id} it was delivered as is no longer in DSPDM");
                    return true;
                }
            }
            else if (candidate is null)
            {
                row.Insert = true;
                return true;
            }

            // A row the record did not write holds its key, unless the record's earlier try saved it and its answer was lost.
            if (row.Work.Completed(SaveBeginStep) is { } begun && begun.GetValueOrDefault(FingerprintValue) == row.Fingerprint)
            {
                return Update(row, candidateId!.Value, candidate, "the row an earlier try saved, whose answer was lost");
            }

            if (protocol._target.ExistingRows == DspdmExistingRows.Update)
            {
                return Update(row, candidateId!.Value, candidate, "an existing row taken over (target.dspdm.existingRows: update)");
            }

            Hold(row, string.Create(CultureInfo.InvariantCulture,
                $"{business.Name} already holds row {candidateId} with {row.KeyDisplay}, which this record did not write. Set target.dspdm.existingRows to update to take such rows over, or remove the row."));
            return false;
        }

        private static bool Update(Row row, long id, JsonObject? found, string? note)
        {
            row.Insert = false;
            row.Id = id;
            row.Found = found;
            row.Note = note;
            return true;
        }

        /// <summary>Whether the row gives what DSPDM requires of it: the mandatory attributes of an insert, and no mandatory attribute cleared by an update.</summary>
        private bool Complete(DspdmObject business, Row row)
        {
            var missing = row.Insert
                ? business.MandatoryOnInsert.Where(a => Empty(row.Values[a.Name])).Select(a => a.Name).ToList()
                : row.Owned.Where(o => business.Attributes[o].Mandatory && Empty(row.Values[o])).ToList();
            if (missing.Count == 0)
            {
                return true;
            }

            Hold(row, row.Insert
                ? $"DSPDM requires {string.Join(", ", missing)} to insert a row of {business.Name}, and the row renders {(missing.Count == 1 ? "it" : "them")} empty"
                : $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} mandatory in {business.Name}, and the row renders {(missing.Count == 1 ? "it" : "them")} empty, which would clear {(missing.Count == 1 ? "it" : "them")}");
            return false;
        }

        /// <summary>Saves the rows in one call; a call DSPDM refuses is sent again one row at a time.</summary>
        private async Task SaveAsync(DspdmObject business, List<Row> rows, CancellationToken ct)
        {
            var started = rows[0].Steps.Now;
            DspdmAnswer answer;
            try
            {
                answer = await protocol._service.SaveAsync(business.Name, rows.Select(r => r.Body()).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (rows.Count > 1 && AboutTheRow(ex))
            {
                protocol._logger.LogInformation(
                    "DSPDM refused a save of {Count} rows of {BusinessObject}; saving them one at a time to find the rows it refuses: {Message}",
                    rows.Count, business.Name, HeaderRedaction.RedactMessage(ex.Message));
                await SaveOneByOneAsync(business, rows, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var row in rows)
                {
                    FailSave(row, started, ex, rows.Count == 1);
                }

                return;
            }

            await SettleAsync(business, rows, answer, started, ct).ConfigureAwait(false);
        }

        private async Task SaveOneByOneAsync(DspdmObject business, List<Row> rows, CancellationToken ct)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[i];
                var started = row.Steps.Now;
                DspdmAnswer answer;
                try
                {
                    answer = await protocol._service.SaveAsync(business.Name, [row.Body()], ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
                {
                    FailSave(row, started, ex, alone: true);
                    if (!AboutTheRow(ex))
                    {
                        // The service failed, not the row: the rows after it are not sent, and share the failure.
                        foreach (var rest in rows.Skip(i + 1))
                        {
                            rest.Steps.Add(SaveStep, started, null, null, "not sent: " + HeaderRedaction.RedactMessage(ex.Message));
                            _outcomes[rest.Index] = DeliveryOutcome.Failed(ex, rest.Steps.Steps);
                        }

                        return;
                    }

                    continue;
                }

                await SettleAsync(business, [row], answer, started, ct).ConfigureAwait(false);
            }
        }

        private void FailSave(Row row, DateTime started, Exception failure, bool alone)
        {
            var status = failure switch
            {
                OsduStatusException http => http.StatusCode,
                DspdmRefusal refusal => refusal.HttpStatus,
                _ => (int?)null,
            };
            row.Steps.Add(SaveStep, started, status, null, HeaderRedaction.RedactMessage(failure.Message));
            _outcomes[row.Index] = DeliveryOutcome.Failed(Verdict(failure, alone), row.Steps.Steps);
        }

        /// <summary>
        /// Settles each saved row from the rows DSPDM answered with, in the order they were sent: its primary key, what the save
        /// did, and its version. A row the answer does not settle (an insert answered without its key, an unchanged row DSPDM
        /// did not read back, an answer that updated nothing) is read again.
        /// </summary>
        private async Task SettleAsync(DspdmObject business, List<Row> rows, DspdmAnswer answer, DateTime started, CancellationToken ct)
        {
            var returned = answer.Rows(business.Name);
            var aligned = returned.Count == rows.Count;
            var settled = new List<(Row Row, string Operation, long? Version)>(rows.Count);
            var unsettled = new List<Row>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var back = aligned && answer.StatusCode != DspdmAnswer.Info ? returned[i] : null;
                var id = back is null ? null : IdOf(business, back);
                var operation = back?["isInserted"] is JsonValue inserted && inserted.GetValueKind() == JsonValueKind.True ? Inserted
                    : back?["isUpdated"] is JsonValue updated && updated.GetValueKind() == JsonValueKind.True ? Updated
                    : null;
                if (row.Insert)
                {
                    if (id is null || operation != Inserted)
                    {
                        unsettled.Add(row);
                        continue;
                    }

                    row.Id = id;
                    settled.Add((row, Inserted, DspdmValues.VersionOf(back!)));
                    continue;
                }

                if (back is null || id != row.Id)
                {
                    unsettled.Add(row);
                    continue;
                }

                // DSPDM reads back only when a save changed something; a row it left unchanged keeps the version the find read.
                var version = operation == Updated ? DspdmValues.VersionOf(back) : row.Found is { } found ? DspdmValues.VersionOf(found) : null;
                if (version is null && business.VersionAttributes.Count > 0)
                {
                    unsettled.Add(row);
                    continue;
                }

                settled.Add((row, operation ?? Unchanged, version));
            }

            if (unsettled.Count > 0)
            {
                try
                {
                    var byId = await protocol.ReadByIdAsync(business, unsettled.Where(r => !r.Insert).Select(r => r.Id!.Value).ToList(), ct).ConfigureAwait(false);
                    var inserted = unsettled.Where(r => r.Insert).ToList();
                    var byKey = inserted.Count == 0 ? new Dictionary<Row, List<JsonObject>>() : await FindAsync(business, inserted, ct).ConfigureAwait(false);
                    foreach (var row in unsettled)
                    {
                        if (row.Insert)
                        {
                            var found = byKey[row];
                            if (found.Count != 1 || IdOf(business, found[0]) is not { } id)
                            {
                                FailSave(row, started, new DeliveryException(
                                    $"DSPDM answered the save of a row of {business.Name} with {row.KeyDisplay} without the row it inserted, and {found.Count} rows have that key now."), alone: false);
                                continue;
                            }

                            row.Id = id;
                            settled.Add((row, Inserted, DspdmValues.VersionOf(found[0])));
                        }
                        else if (byId.TryGetValue(row.Id!.Value, out var stored))
                        {
                            var version = DspdmValues.VersionOf(stored);
                            var before = row.Found is { } found ? DspdmValues.VersionOf(found) : null;
                            settled.Add((row, version is not null && version == before ? Unchanged : Updated, version));
                        }
                        else
                        {
                            FailSave(row, started, new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                                $"DSPDM answered the save of row {row.Id} of {business.Name}, and the row is no longer there; it was deleted while it was saved.")), alone: false);
                        }
                    }
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
                {
                    foreach (var row in unsettled)
                    {
                        FailSave(row, started, new DeliveryException(
                            $"DSPDM answered the save of a row of {business.Name}, and the row could not be read to settle it: {HeaderRedaction.RedactMessage(ex.Message)}", ex), alone: false);
                    }
                }
            }

            var reports = new List<Task>(settled.Count);
            foreach (var (row, operation, version) in settled)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [BusinessObjectValue] = business.Name,
                    [IdValue] = row.Id!.Value.ToString(CultureInfo.InvariantCulture),
                    [OperationValue] = operation,
                };
                if (version is { } v)
                {
                    values[VersionValue] = v.ToString(CultureInfo.InvariantCulture);
                }

                row.Steps.Add(SaveStep, started, 200, values);
                reports.Add(row.Work.ReportStepAsync(SaveStep, values, ct));
                var detail = operation switch
                {
                    Inserted => string.Create(CultureInfo.InvariantCulture, $"inserted as row {row.Id} of {business.Name}"),
                    Updated => string.Create(CultureInfo.InvariantCulture, $"updated row {row.Id} of {business.Name}"),
                    _ => string.Create(CultureInfo.InvariantCulture, $"row {row.Id} of {business.Name} unchanged in DSPDM"),
                };
                _outcomes[row.Index] = new DeliveryOutcome
                {
                    MetadataDelivered = true,
                    PayloadDelivered = false,
                    TargetVersion = version,
                    Returned = values,
                    Steps = row.Steps.Steps,
                    Detail = row.Note is null ? detail : $"{detail} ({row.Note})",
                };
            }

            await Task.WhenAll(reports).ConfigureAwait(false);
        }

        private void Hold(Row row, string reason)
            => _outcomes[row.Index] = DeliveryOutcome.Failed(new RecordHeldException(reason), row.Steps.Steps);

        private void Fail(Row row, Exception failure)
            => _outcomes[row.Index] = DeliveryOutcome.Failed(failure, row.Steps.Steps);
    }
}
