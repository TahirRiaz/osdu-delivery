using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The etp route (osdu/specs/reservoir-ddms/INTEGRATION.md): each record is one Energistics data object in a dataspace
/// of the Reservoir DDMS, written over ETP 1.2 on a WebSocket rather than through an OSDU service. The object's
/// identity is the one its own XML carries, because that is the only identity the store reads (section 5.1), and it is
/// recorded in the record's target state as the URI the object lives at.
/// <list type="bullet">
/// <item>The XML comes from the record's <c>files</c> payload, or from <c>data.Xml</c> when the mapping renders it. It is
/// checked before anything is sent: a root uuid and schema version, a Citation directly under the root, a namespace and
/// version the store files, and the <c>obj_</c> spelling references resolve against.</item>
/// <item>The dataspace is the record's <c>data.Dataspace</c> or the flow's <c>target.etp.dataspace</c>. It is created only
/// when it is missing, outside any transaction, carrying the record's own ACLs and legal tags, which is what the server
/// registers its <c>dataset--ETPDataspace</c> record with (section 6.3).</item>
/// <item>Every write of a batch goes inside one explicit transaction per dataspace (section 4.4): the objects, then the
/// arrays that fit whole, then the commit. An array too large for a message is declared in that transaction and filled
/// by subarrays in transactions of their own, each retryable on its own (section 5.4).</item>
/// <item>A commit the server refuses rolls the transaction back and holds the records of that dataspace with the reason it
/// gave, because a missing array or a dangling reference is the estate's to fix.</item>
/// </list>
/// A verify lists the dataspace's resources and compares the store's last write with the one the delivery recorded.
/// Removal deletes the object (the everything scope): ETP keeps no earlier versions of an object and no deleted ones,
/// and the route never deletes a dataspace, because the server purges its OSDU record when it does (section 7.8).
/// </summary>
public sealed class OsduEtpProtocol : IDeliveryProtocol
{
    /// <summary>The steps an attempt records, in the order it takes them.</summary>
    public const string DataspaceStep = "dataspace";

    public const string TransactionStep = "transaction";
    public const string ObjectsStep = "objects";
    public const string ArraysStep = "arrays";
    public const string CommitStep = "commit";
    public const string FillStep = "fill";
    public const string LockStep = "lock";

    /// <summary>The URI the object lives at, in the record's target state.</summary>
    public const string UriValue = "etp.uri";

    /// <summary>The dataspace the object lives in, in the record's target state.</summary>
    public const string DataspaceValue = "etp.dataspace";

    /// <summary>The store's type for the object (<c>resqml20.obj_Grid2dRepresentation</c>), in the record's target state.</summary>
    public const string ObjectTypeValue = "etp.objectType";

    /// <summary>The uuid the object's XML carries, in the record's target state.</summary>
    public const string UuidValue = "etp.uuid";

    /// <summary>Why the record scope is refused.</summary>
    public const string RecordScopeRefused =
        "the Reservoir DDMS keeps no deleted objects, so an object has no reversible removal; the everything scope deletes it for good";

    /// <summary>Why the history scope is refused.</summary>
    public const string HistoryScopeRefused =
        "the Reservoir DDMS keeps only an object's latest content, so there is no history to purge; the everything scope deletes the object for good";

    /// <summary>The four values the server requires on a dataspace it registers (section 4.3).</summary>
    private static readonly string[] Required = ["viewers", "owners", "legaltags", "otherRelevantDataCountries"];

    /// <summary>The share of a message the reference importer fills, which this route fills too (section 8.6).</summary>
    private const double MessageFill = 0.8;

    /// <summary>How many times a transaction another session is writing is waited for (section 4.4).</summary>
    private const int TransactionAttempts = 6;

    private readonly EtpConnection _connection;
    private readonly EtpTarget _target;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    private readonly string? _partition;

    /// <param name="connection">Opens the sessions this route works in.</param>
    /// <param name="target">The flow's Reservoir DDMS block.</param>
    /// <param name="partition">The data partition the flow's headers name, which the dataspace's OSDU record id is built from.</param>
    /// <param name="log">Where a created dataspace and its record id are logged.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public OsduEtpProtocol(EtpConnection connection, EtpTarget target, string? partition, ILogger<OsduEtpProtocol> log, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);
        _connection = connection;
        _target = target;
        _partition = partition;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Etp;

    /// <summary>Objects per <c>PutDataObjects</c> message, which is also the batch the worker hands over.</summary>
    public int MaxBatch => _target.ObjectsPerMessage;

    /// <summary>Objects one verify lists at a time; they are read per dataspace, not one by one.</summary>
    public int MaxVerifyBatch => 200;

    /// <summary>A verify needs the URI the delivery recorded, because the store knows nothing of the ledger's ids.</summary>
    public bool VerifiesWithTargetState => true;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcomes = await DeliverBatchAsync([work], ct).ConfigureAwait(false);
        return outcomes[0];
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome?[works.Count];
        var prepared = new List<Prepared>(works.Count);
        for (var i = 0; i < works.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                prepared.Add(await PrepareAsync(i, works[i], ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex);
            }
        }

        if (prepared.Count > 0)
        {
            await using var session = await _connection.OpenAsync(ct).ConfigureAwait(false);
            foreach (var group in prepared.GroupBy(p => p.Dataspace, StringComparer.Ordinal))
            {
                var items = group.ToList();
                try
                {
                    await WriteAsync(session, group.Key, items, outcomes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    foreach (var item in items.Where(item => outcomes[item.Index] is null))
                    {
                        outcomes[item.Index] = DeliveryOutcome.Failed(ex);
                    }
                }
            }

            await session.CloseAsync("delivery done", CancellationToken.None).ConfigureAwait(false);
        }

        for (var i = 0; i < outcomes.Length; i++)
        {
            outcomes[i] ??= DeliveryOutcome.Failed(new DeliveryException("The delivery ended without an outcome for this record."));
        }

        return outcomes!;
    }

    /// <summary>Everything one record needs before a session is opened, so a bad record never starts a transaction.</summary>
    private async Task<Prepared> PrepareAsync(int index, DeliveryWork work, CancellationToken ct)
    {
        var what = $"the rendered object {work.SourceKey ?? work.TargetId}";
        var data = work.Document["data"] as JsonObject;
        var path = Text(data, "Dataspace")
            ?? _target.Dataspace
            ?? throw new RecordHeldException($"{what} names no data.Dataspace, and the flow declares no target.etp.dataspace");
        EtpObjectXml.CheckPath(path);

        var xml = await XmlAsync(work, data, what, ct).ConfigureAwait(false);
        var identity = EtpObjectXml.Read(xml, what);
        var uri = EtpObjectXml.ObjectUri(path, identity.ObjectType, identity.Uuid);
        var arrays = EtpArrays.Read(data, what);

        var named = identity.ArrayPaths;
        var declared = arrays.Select(a => a.Path).ToList();
        if (named.Except(declared, StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
        {
            throw new RecordHeldException(
                $"{what} names the array path(s) {string.Join(", ", missing)} in its XML and declares no array for them; the Reservoir DDMS refuses the commit of a transaction whose objects name arrays nobody supplied");
        }

        if (declared.Except(named, StringComparer.Ordinal).ToList() is { Count: > 0 } orphans)
        {
            throw new RecordHeldException(
                $"{what} declares the array(s) {string.Join(", ", orphans)} that its XML names nowhere; the Reservoir DDMS refuses the commit of a transaction that leaves an array no object claims");
        }

        return new Prepared(index, work, path, uri, identity, xml, arrays);
    }

    /// <summary>The object's XML: the record's files payload, or the document when the mapping renders it.</summary>
    private static async Task<ReadOnlyMemory<byte>> XmlAsync(DeliveryWork work, JsonObject? data, string what, CancellationToken ct)
    {
        if (work.Parts.FirstOrDefault(p => string.Equals(p.Role, PayloadParts.Files, StringComparison.Ordinal))?.Source is { } source)
        {
            var files = await source.ListChunksAsync(ct).ConfigureAwait(false);
            if (files.Count > 1)
            {
                throw new RecordHeldException(
                    $"{what} carries {files.Count} files, and one record is one Energistics object, which is one XML document");
            }

            if (files.Count == 1)
            {
                await using var stream = await source.OpenAsync(files[0], ct).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct).ConfigureAwait(false);
                return memory.ToArray();
            }
        }

        var inline = Text(data, "Xml")
            ?? throw new RecordHeldException($"{what} carries no file and no data.Xml, so it has no object to send");
        return Encoding.UTF8.GetBytes(inline);
    }

    /// <summary>Writes one dataspace's share of a batch: the dataspace, one transaction, the objects, the arrays, the commit.</summary>
    private async Task WriteAsync(EtpSession session, string path, IReadOnlyList<Prepared> items, DeliveryOutcome?[] outcomes, CancellationToken ct)
    {
        var steps = new DeliverySteps(_time);
        var dataspace = EtpObjectXml.DataspaceUri(path);
        await EnsureDataspaceAsync(session, path, dataspace, items[0], steps, ct).ConfigureAwait(false);

        var transaction = await StartAsync(session, dataspace, steps, ct).ConfigureAwait(false);
        var committed = false;
        try
        {
            var failed = await PutObjectsAsync(session, items, outcomes, steps, ct).ConfigureAwait(false);
            var live = items.Where(item => !failed.Contains(item.Index)).ToList();
            var fills = await PutArraysAsync(session, live, outcomes, steps, ct).ConfigureAwait(false);

            var commit = await session.CallAsync(new CommitTransaction { TransactionUuid = transaction }, ct).ConfigureAwait(false);
            var result = commit.Part<CommitTransactionResponse>();
            committed = result.Successful;
            steps.Add(CommitStep, steps.Now, null, Values(("transaction", transaction.ToString()), ("committed", result.Successful ? "yes" : "no")), result.Successful ? null : result.FailureReason);
            if (!result.Successful)
            {
                throw Refused(result.FailureReason, path);
            }

            await FillAsync(session, dataspace, fills, steps, ct).ConfigureAwait(false);
            if (_target.Lock)
            {
                await LockAsync(session, dataspace, locked: true, steps, ct).ConfigureAwait(false);
            }

            foreach (var item in live.Where(item => outcomes[item.Index] is null))
            {
                outcomes[item.Index] = new DeliveryOutcome
                {
                    MetadataDelivered = true,
                    PayloadDelivered = item.Work.DeliverPayload,
                    ChunksSent = item.Arrays.Count,
                    Detail = $"{item.Identity.ObjectType} in {path}",
                    Returned = Values(
                        (UriValue, item.Uri),
                        (DataspaceValue, path),
                        (ObjectTypeValue, item.Identity.ObjectType),
                        (UuidValue, item.Identity.Uuid.ToString("D", CultureInfo.InvariantCulture)),
                        (PayloadParts.StateKey(PayloadParts.Bulk), item.BulkHash ?? string.Empty)),
                    Steps = steps.Steps,
                };
            }
        }
        finally
        {
            if (!committed)
            {
                await RollbackAsync(session, transaction).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the dataspace when it is missing, with the record's own ACLs and legal tags (section 4.3).</summary>
    private async Task EnsureDataspaceAsync(EtpSession session, string path, string dataspace, Prepared first, DeliverySteps steps, CancellationToken ct)
    {
        var began = steps.Now;
        var info = await session.CallAsync(new GetDataspaceInfo { Uris = One(dataspace) }, ct).ConfigureAwait(false);
        if (info.Errors.TryGetValue("0", out var refusal) && refusal.Code != EtpErrorCodes.NotFound)
        {
            // Only "not found" means the dataspace is this delivery's to create; anything else (no permission, a URI the
            // server will not take) would fail the create with a less useful message.
            throw new EtpProtocolException(refusal.Code, refusal.Message, $"the dataspace {path}");
        }

        var found = info.All<GetDataspaceInfoResponse>().SelectMany(r => r.Dataspaces.Values).FirstOrDefault();
        if (found is not null)
        {
            if (_target.Lock && (found.CustomData.TryGetValue("locked", out var locked) && locked.Flag == true))
            {
                await LockAsync(session, dataspace, locked: false, steps, ct).ConfigureAwait(false);
            }

            steps.Add(DataspaceStep, began, null, Values(("dataspace", path), ("created", "no")));
            return;
        }

        var created = await session.CallAsync(
            new PutDataspaces
            {
                Dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal)
                {
                    ["0"] = new Dataspace
                    {
                        Uri = dataspace,
                        Path = path,
                        StoreCreated = 0,
                        StoreLastWrite = 0,
                        CustomData = Access(first),
                    },
                },
            },
            ct).ConfigureAwait(false);
        created.Failed();

        // The server registers the dataspace's OSDU record itself; its id is logged, as every id this engine causes is.
        var record = EtpDataspaceRecord.Id(_connection.Endpoint, path, _partition);
        _log.LogInformation(
            "The Reservoir DDMS created the dataspace {Dataspace} at {Endpoint} and registered its storage record {Record}",
            path, _connection.Endpoint, record);
        steps.Add(DataspaceStep, began, null, Values(("dataspace", path), ("created", "yes"), ("record", record)));
    }

    /// <summary>The ACLs and legal tags of the record that first needed the dataspace, which is where a flow declares them.</summary>
    private static IReadOnlyDictionary<string, DataValue> Access(Prepared first)
    {
        var acl = first.Work.Document["acl"] as JsonObject;
        var legal = first.Work.Document["legal"] as JsonObject;
        var values = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        Add(values, "viewers", acl?["viewers"] as JsonArray);
        Add(values, "owners", acl?["owners"] as JsonArray);
        Add(values, "legaltags", legal?["legaltags"] as JsonArray);
        Add(values, "otherRelevantDataCountries", legal?["otherRelevantDataCountries"] as JsonArray);
        var missing = Required.Where(name => !values.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new RecordHeldException(
                $"the dataspace {first.Dataspace} does not exist and the rendered object carries no {string.Join(", ", missing)}; the Reservoir DDMS registers a dataspace with exactly the flow's ACLs and legal tags, so a record that carries none cannot create one");
        }

        return values;
    }

    private static void Add(Dictionary<string, DataValue> values, string name, JsonArray? entries)
    {
        var list = entries?.Select(e => e?.GetValue<string>()).OfType<string>().Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list is { Count: > 0 })
        {
            values[name] = DataValue.Of(list);
        }
    }

    /// <summary>Starts the write transaction, waiting for a dataspace another session is writing (section 4.4).</summary>
    private async Task<Guid> StartAsync(EtpSession session, string dataspace, DeliverySteps steps, CancellationToken ct)
    {
        var began = steps.Now;
        var wait = TimeSpan.FromMilliseconds(400);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var reply = await session.CallAsync(
                    new StartTransaction { ReadOnly = false, Message = "OSDU Delivery", DataspaceUris = [dataspace] },
                    ct).ConfigureAwait(false);
                var started = reply.Part<StartTransactionResponse>();
                if (!started.Successful)
                {
                    throw new DeliveryException($"The Reservoir DDMS would not start a write transaction on {dataspace}: {started.FailureReason}");
                }

                steps.Add(TransactionStep, began, null, Values(("transaction", started.TransactionUuid.ToString()), ("attempts", attempt.ToString(CultureInfo.InvariantCulture))));
                return started.TransactionUuid;
            }
            catch (EtpProtocolException ex) when (ex.Code == EtpErrorCodes.MaxTransactionsExceeded && attempt < TransactionAttempts)
            {
                _log.LogInformation(
                    "The dataspace {Dataspace} is being written by another session; waiting {Wait} before try {Next} of {Attempts}",
                    dataspace, wait, attempt + 1, TransactionAttempts);
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                wait = TimeSpan.FromMilliseconds(Math.Min(2000, wait.TotalMilliseconds * 2));
            }
        }
    }

    /// <summary>Puts the objects, in messages under the negotiated size, and reports the ones the server refused.</summary>
    private async Task<HashSet<int>> PutObjectsAsync(EtpSession session, IReadOnlyList<Prepared> items, DeliveryOutcome?[] outcomes, DeliverySteps steps, CancellationToken ct)
    {
        var failed = new HashSet<int>();
        var budget = Budget(session);
        var batch = new List<Prepared>();
        var bytes = 0L;
        foreach (var item in items)
        {
            if (item.Xml.Length > budget)
            {
                // One object larger than a message goes alone, its XML following in chunks (section 4.5).
                await SendObjectsAsync(session, batch, failed, outcomes, steps, ct).ConfigureAwait(false);
                batch.Clear();
                bytes = 0;
                await SendChunkedAsync(session, item, failed, outcomes, steps, budget, ct).ConfigureAwait(false);
                continue;
            }

            if (batch.Count == _target.ObjectsPerMessage || bytes + item.Xml.Length > budget)
            {
                await SendObjectsAsync(session, batch, failed, outcomes, steps, ct).ConfigureAwait(false);
                batch.Clear();
                bytes = 0;
            }

            batch.Add(item);
            bytes += item.Xml.Length;
        }

        await SendObjectsAsync(session, batch, failed, outcomes, steps, ct).ConfigureAwait(false);
        return failed;
    }

    private static async Task SendObjectsAsync(EtpSession session, IReadOnlyList<Prepared> batch, HashSet<int> failed, DeliveryOutcome?[] outcomes, DeliverySteps steps, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var began = steps.Now;
        var reply = await session.CallAsync(
            new PutDataObjects
            {
                DataObjects = batch.ToDictionary(item => Key(item), item => Object(item), StringComparer.Ordinal),
            },
            ct).ConfigureAwait(false);
        Report(reply, batch, failed, outcomes);
        steps.Add(ObjectsStep, began, null, Values(("objects", batch.Count.ToString(CultureInfo.InvariantCulture)), ("refused", failed.Count.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>One object whose XML is larger than a message: the put without FIN, then the XML in chunks (section 4.5).</summary>
    private static async Task SendChunkedAsync(EtpSession session, Prepared item, HashSet<int> failed, DeliveryOutcome?[] outcomes, DeliverySteps steps, long budget, CancellationToken ct)
    {
        var began = steps.Now;
        var blob = Guid.NewGuid();
        var chunks = new List<IEtpMessage>();
        for (var at = 0; at < item.Xml.Length; at += (int)budget)
        {
            var length = (int)Math.Min(budget, item.Xml.Length - at);
            chunks.Add(new Chunk { BlobId = blob, Data = item.Xml.Slice(at, length), Final = at + length >= item.Xml.Length });
        }

        var reply = await session.CallAsync(
            new PutDataObjects
            {
                DataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal)
                {
                    [Key(item)] = Object(item) with { BlobId = blob, Data = ReadOnlyMemory<byte>.Empty },
                },
            },
            chunks,
            ct).ConfigureAwait(false);
        Report(reply, [item], failed, outcomes);
        steps.Add(ObjectsStep, began, null, Values(("objects", "1"), ("chunks", chunks.Count.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>Writes each record's arrays, and returns the fills the large ones need once the transaction commits.</summary>
    private async Task<List<Fill>> PutArraysAsync(EtpSession session, IReadOnlyList<Prepared> items, DeliveryOutcome?[] outcomes, DeliverySteps steps, CancellationToken ct)
    {
        var fills = new List<Fill>();
        var whole = new Dictionary<string, PutDataArraysType>(StringComparer.Ordinal);
        var declared = new Dictionary<string, PutUninitializedDataArrayType>(StringComparer.Ordinal);
        var budget = Budget(session);
        var began = steps.Now;
        var count = 0;
        foreach (var item in items)
        {
            foreach (var array in item.Arrays)
            {
                ct.ThrowIfCancellationRequested();
                var values = array.Values is not null
                    ? EtpArrays.Inline(array, item.What)
                    : await EtpArrays.FromParquetAsync(array, Bulk(item), _target.MaxArrayBytes, item.What, ct).ConfigureAwait(false);
                if (values.ElementCount != array.ElementCount)
                {
                    throw new RecordHeldException(
                        $"{item.What}: the array {array.Path} is shaped {string.Join(" x ", array.Dimensions)}, which is {array.ElementCount} elements, and it holds {values.ElementCount}");
                }

                var uid = new DataArrayIdentifier { Uri = array.OwnerUri ?? item.Uri, PathInResource = array.Path };
                count++;
                if (values.EstimatedBytes <= budget)
                {
                    whole[Key(item, count)] = new PutDataArraysType
                    {
                        Uid = uid,
                        Array = new DataArray { Dimensions = array.Dimensions.ToArray(), Data = values },
                    };
                    continue;
                }

                declared[Key(item, count)] = new PutUninitializedDataArrayType
                {
                    Uid = uid,
                    Metadata = new DataArrayMetadata
                    {
                        Dimensions = array.Dimensions.ToArray(),
                        TransportArrayType = values.Kind,
                        LogicalArrayType = Logical(values.Kind),
                        StoreLastWrite = 0,
                        StoreCreated = 0,
                    },
                };
                fills.Add(new Fill(uid, array, values));
            }
        }

        if (whole.Count > 0)
        {
            (await session.CallAsync(new PutDataArrays { DataArrays = whole }, ct).ConfigureAwait(false)).Failed();
        }

        if (declared.Count > 0)
        {
            (await session.CallAsync(new PutUninitializedDataArrays { DataArrays = declared }, ct).ConfigureAwait(false)).Failed();
        }

        if (count > 0)
        {
            steps.Add(ArraysStep, began, null, Values(
                ("whole", whole.Count.ToString(CultureInfo.InvariantCulture)),
                ("declared", declared.Count.ToString(CultureInfo.InvariantCulture))));
        }

        return fills;
    }

    /// <summary>Fills the arrays that were only declared, each in a transaction of its own, so one can be retried alone (section 5.4).</summary>
    private async Task FillAsync(EtpSession session, string dataspace, IReadOnlyList<Fill> fills, DeliverySteps steps, CancellationToken ct)
    {
        if (fills.Count == 0)
        {
            return;
        }

        var budget = Budget(session);
        foreach (var fill in fills)
        {
            ct.ThrowIfCancellationRequested();
            var began = steps.Now;
            var width = fill.Values.EstimatedBytes / Math.Max(1, fill.Values.ElementCount);
            var slices = EtpArrays.Slices(fill.Declaration.Dimensions, Math.Max(1, width), budget);
            var transaction = await StartAsync(session, dataspace, steps, ct).ConfigureAwait(false);
            var committed = false;
            try
            {
                foreach (var slice in slices)
                {
                    var reply = await session.CallAsync(
                        new PutDataSubarrays
                        {
                            DataSubarrays = new Dictionary<string, PutDataSubarraysType>(StringComparer.Ordinal)
                            {
                                ["0"] = new PutDataSubarraysType
                                {
                                    Uid = fill.Uid,
                                    Data = fill.Values.Slice((int)slice.Offset, (int)slice.Length),
                                    Starts = slice.Starts.ToArray(),
                                    Counts = slice.Counts.ToArray(),
                                },
                            },
                        },
                        ct).ConfigureAwait(false);
                    reply.Failed();
                }

                var commit = (await session.CallAsync(new CommitTransaction { TransactionUuid = transaction }, ct).ConfigureAwait(false)).Part<CommitTransactionResponse>();
                committed = commit.Successful;
                if (!commit.Successful)
                {
                    throw Refused(commit.FailureReason, dataspace);
                }

                steps.Add(FillStep, began, null, Values(("array", fill.Declaration.Path), ("slices", slices.Count.ToString(CultureInfo.InvariantCulture))));
            }
            finally
            {
                if (!committed)
                {
                    await RollbackAsync(session, transaction).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task LockAsync(EtpSession session, string dataspace, bool locked, DeliverySteps steps, CancellationToken ct)
    {
        var began = steps.Now;
        var reply = await session.CallAsync(new LockDataspaces { Uris = One(dataspace), Lock = locked }, ct).ConfigureAwait(false);
        reply.Failed();
        steps.Add(LockStep, began, null, Values(("dataspace", dataspace), ("locked", locked ? "yes" : "no")));
    }

    private async Task RollbackAsync(EtpSession session, Guid transaction)
    {
        try
        {
            if (session.IsOpen)
            {
                await session.CallAsync(new RollbackTransaction { TransactionUuid = transaction }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The session ends with the delivery either way, and an uncommitted transaction rolls back when it does.
            _log.LogDebug("The transaction {Transaction} could not be rolled back: {Reason}", transaction, ex.Message);
        }
    }

    /// <summary>What the server's refusal of a commit means: the estate's to fix, or worth another try (sections 4.7 and 8.4).</summary>
    private static DeliveryException Refused(string reason, string dataspace)
    {
        var held = reason.Contains("Missing", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Orphan", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("dangling", StringComparison.OrdinalIgnoreCase);
        var message = $"The Reservoir DDMS refused the commit of the transaction on {dataspace}: {reason}";
        return held ? new RecordHeldException(message) : new DeliveryException(message);
    }

    /// <summary>Records the per-object failures of a put, leaving the rest of the batch to go on (section 8.1).</summary>
    private static void Report(EtpReply reply, IReadOnlyList<Prepared> batch, HashSet<int> failed, DeliveryOutcome?[] outcomes)
    {
        reply.Failed();
        var stored = reply.All<PutDataObjectsResponse>().SelectMany(r => r.Success.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var item in batch)
        {
            var key = Key(item);
            if (stored.Contains(key))
            {
                continue;
            }

            failed.Add(item.Index);
            var error = reply.Errors.TryGetValue(key, out var info)
                ? new EtpProtocolException(info.Code, info.Message, $"the object {item.Uri}")
                : new DeliveryException($"The Reservoir DDMS neither stored nor refused the object {item.Uri}.");
            outcomes[item.Index] = DeliveryOutcome.Failed(
                EtpErrorCodes.Retryable(error is EtpProtocolException typed ? typed.Code : EtpErrorCodes.InvalidState)
                    ? error
                    : new RecordHeldException(error.Message, error));
        }
    }

    public async Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
    {
        var results = await VerifyBatchAsync([new VerifyRequest(targetId, expectedVersion)], ct).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new VerifyResult?[requests.Count];
        var wanted = new List<(int Index, string Dataspace, string Uri, string Type, long? Expected)>();
        for (var i = 0; i < requests.Count; i++)
        {
            var state = requests[i].TargetState;
            if (state is null || !state.TryGetValue(UriValue, out var uri) || !state.TryGetValue(DataspaceValue, out var dataspace))
            {
                results[i] = new VerifyResult(VerifyOutcome.Missing, null, "the record records no ETP object, so nothing was delivered to read back");
                continue;
            }

            wanted.Add((i, dataspace, uri, state.GetValueOrDefault(ObjectTypeValue, string.Empty), requests[i].ExpectedVersion));
        }

        if (wanted.Count > 0)
        {
            await using var session = await _connection.OpenAsync(ct).ConfigureAwait(false);
            foreach (var group in wanted.GroupBy(w => w.Dataspace, StringComparer.Ordinal))
            {
                var types = group.Select(w => w.Type).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                var reply = await session.CallAsync(
                    new GetResources
                    {
                        Context = new ContextInfo
                        {
                            Uri = EtpObjectXml.DataspaceUri(group.Key),
                            Depth = 1,
                            DataObjectTypes = types,
                            NavigableEdges = RelationshipKind.Primary,
                        },
                        Scope = ContextScopeKind.TargetsOrSelf,
                    },
                    ct).ConfigureAwait(false);
                reply.Failed();
                var found = reply.All<GetResourcesResponse>()
                    .SelectMany(r => r.Resources)
                    .ToDictionary(r => r.Uri, r => r, StringComparer.Ordinal);
                foreach (var (index, _, uri, _, expected) in group)
                {
                    results[index] = found.TryGetValue(uri, out var resource)
                        ? new VerifyResult(
                            expected is null || expected == resource.StoreLastWrite ? VerifyOutcome.Match : VerifyOutcome.Drifted,
                            resource.StoreLastWrite,
                            expected is null || expected == resource.StoreLastWrite ? null : $"the Reservoir DDMS last wrote {uri} at {EtpTime.At(resource.StoreLastWrite):O}")
                        : new VerifyResult(VerifyOutcome.Missing, null, $"the dataspace {group.Key} holds no object at {uri}");
                }
            }

            await session.CloseAsync("verify done", CancellationToken.None).ConfigureAwait(false);
        }

        return results.Select(r => r ?? new VerifyResult(VerifyOutcome.Error, null, "the object was not read back")).ToList();
    }

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default) => ReadAsync(targetId, null, ct);

    public async Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        if (targetState is null || !targetState.TryGetValue(UriValue, out var uri))
        {
            return null;
        }

        await using var session = await _connection.OpenAsync(ct).ConfigureAwait(false);
        var reply = await session.CallAsync(new GetDataObjects { Uris = One(uri), Format = "xml" }, ct).ConfigureAwait(false);
        reply.Failed();
        var found = reply.All<GetDataObjectsResponse>().SelectMany(r => r.DataObjects.Values).FirstOrDefault();
        await session.CloseAsync("read done", CancellationToken.None).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["uri"] = found.Resource.Uri,
            ["name"] = found.Resource.Name,
            ["storeLastWrite"] = found.Resource.StoreLastWrite,
            ["storeCreated"] = found.Resource.StoreCreated,
            ["lastChanged"] = found.Resource.LastChanged,
            ["xml"] = Encoding.UTF8.GetString(found.Data.Span),
        };
    }

    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        if (scope == RemovalScope.Record)
        {
            throw new DeliveryException(RecordScopeRefused);
        }

        if (scope == RemovalScope.History)
        {
            throw new DeliveryException(HistoryScopeRefused);
        }

        if (targetState is null || !targetState.TryGetValue(UriValue, out var uri))
        {
            return new DeleteOutcome(false, true, "the record records no ETP object, so there is nothing to remove");
        }

        await using var session = await _connection.OpenAsync(ct).ConfigureAwait(false);
        var reply = await session.CallAsync(new DeleteDataObjects { Uris = One(uri) }, ct).ConfigureAwait(false);
        await session.CloseAsync("removal done", CancellationToken.None).ConfigureAwait(false);
        if (reply.Errors.TryGetValue("0", out var error))
        {
            return error.Code == EtpErrorCodes.NotFound
                ? new DeleteOutcome(false, true, $"the Reservoir DDMS no longer holds {uri}")
                : throw new EtpProtocolException(error.Code, error.Message, $"the removal of {uri}");
        }

        reply.Failed();
        return new DeleteOutcome(true, false, $"the object {uri} was deleted from its dataspace");
    }

    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var session = await _connection.OpenAsync(ct).ConfigureAwait(false);
            var detail = $"{session.Server}, session {session.SessionId}, {session.MaxMessageBytes} byte messages, compression {(session.Compressed ? "gzip" : "off")}";
            await session.CloseAsync("probe done", CancellationToken.None).ConfigureAwait(false);
            return new ProbeOutcome(true, 101, detail, _connection.Endpoint.AbsolutePath);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlFlowException)
        {
            return new ProbeOutcome(false, 0, ex.Message, _connection.Endpoint.AbsolutePath);
        }
    }

    private static AnyLogicalArrayType Logical(AnyArrayType transport) => transport switch
    {
        AnyArrayType.ArrayOfBoolean => AnyLogicalArrayType.ArrayOfBoolean,
        AnyArrayType.ArrayOfInt => AnyLogicalArrayType.ArrayOfInt32LE,
        AnyArrayType.ArrayOfLong => AnyLogicalArrayType.ArrayOfInt64LE,
        AnyArrayType.ArrayOfFloat => AnyLogicalArrayType.ArrayOfFloat32LE,
        AnyArrayType.ArrayOfDouble => AnyLogicalArrayType.ArrayOfDouble64LE,
        AnyArrayType.ArrayOfString => AnyLogicalArrayType.ArrayOfString,
        _ => AnyLogicalArrayType.ArrayOfUInt8,
    };

    private static long Budget(EtpSession session) => (long)(session.MaxMessageBytes * MessageFill);

    private static IPayloadSource Bulk(Prepared item)
        => item.Work.Parts.FirstOrDefault(p => string.Equals(p.Role, PayloadParts.Bulk, StringComparison.Ordinal))?.Source
            ?? throw new RecordHeldException($"{item.What}: an array reads a column of the record's bulk payload, and the record carries none");

    private static DataObject Object(Prepared item) => new()
    {
        Resource = new Resource
        {
            Uri = item.Uri,
            Name = item.Identity.Title,
            LastChanged = item.Identity.LastChanged is { } changed ? EtpTime.Microseconds(changed) : 0,
            StoreLastWrite = 0,
            StoreCreated = 0,
            ActiveStatus = ActiveStatusKind.Active,
        },
        Format = "xml",
        Data = item.Xml,
    };

    private static string Key(Prepared item) => item.Index.ToString(CultureInfo.InvariantCulture);

    private static string Key(Prepared item, int array) => $"{item.Index.ToString(CultureInfo.InvariantCulture)}-{array.ToString(CultureInfo.InvariantCulture)}";

    private static IReadOnlyDictionary<string, string> One(string uri)
        => new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = uri };

    private static IReadOnlyDictionary<string, string> Values(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static string? Text(JsonObject? data, string name)
        => data?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>One record ready to send: where it goes, what it is, and the arrays it brings.</summary>
    private sealed record Prepared(
        int Index,
        DeliveryWork Work,
        string Dataspace,
        string Uri,
        EtpObjectIdentity Identity,
        ReadOnlyMemory<byte> Xml,
        IReadOnlyList<EtpArrayDeclaration> Arrays)
    {
        public string What => $"the rendered object {Work.SourceKey ?? Work.TargetId}";

        /// <summary>The content hash of the bulk payload this delivery sent, which the record keeps.</summary>
        public string? BulkHash => Work.Parts.FirstOrDefault(p => string.Equals(p.Role, PayloadParts.Bulk, StringComparison.Ordinal))?.Hash;
    }

    /// <summary>One array declared in the write transaction and filled slice by slice after it commits.</summary>
    private sealed record Fill(DataArrayIdentifier Uid, EtpArrayDeclaration Declaration, AnyArray Values);
}
