using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SqlFlow.Delivery.Engine.Protocols.Etp;

namespace SqlFlow.Delivery.Tests.Etp;

/// <summary>
/// The dataspace, transaction, store and array behaviour of the ETP server, as osdu/specs/reservoir-ddms/INTEGRATION.md
/// reads its code: sections 4.3 to 4.8 for the writes, 6 for the identities, 7 for the reads, and the per-item error
/// shapes of section 8.1.
/// </summary>
internal sealed partial class FakeEtpServer
{
    private readonly Dictionary<string, Dataspace> _spaces = new(StringComparer.Ordinal);
    private readonly Dictionary<long, PendingPut> _chunked = [];
    private Dictionary<string, Dataspace>? _beforeTransaction;
    private Guid? _transaction;
    private IReadOnlyList<string> _transactionSpaces = [];

    /// <summary>The dataspaces the server holds, for a test to read what a delivery left behind.</summary>
    public IReadOnlyDictionary<string, Dataspace> Spaces => _spaces;

    /// <summary>Whether a write transaction is open, which a test asserts is not the case once a delivery is done.</summary>
    public bool InTransaction => _transaction is not null;

    /// <summary>The object of a dataspace under its URI, or null when it holds none.</summary>
    public StoredObject? Object(string uri)
        => _spaces.Values.SelectMany(space => space.Objects.Values).FirstOrDefault(o => string.Equals(o.Uri, uri, StringComparison.Ordinal));

    private async Task<bool> HandleStoreAsync(EtpFrame frame, Peer peer, CancellationToken ct)
    {
        if (frame.Body is { } body && FailNext.TryRemove(body.MessageName, out var planned))
        {
            await peer.FailAsync(frame, planned.Code, planned.Message, ct).ConfigureAwait(false);
            return true;
        }

        switch (frame.Body)
        {
            case GetDataspaceInfo info:
                await DataspaceInfoAsync(info, frame, peer, ct).ConfigureAwait(false);
                return true;
            case PutDataspaces put:
                await PutDataspacesAsync(put, frame, peer, ct).ConfigureAwait(false);
                return true;
            case LockDataspaces @lock:
                await LockAsync(@lock, frame, peer, ct).ConfigureAwait(false);
                return true;
            case StartTransaction start:
                await StartAsync(start, frame, peer, ct).ConfigureAwait(false);
                return true;
            case CommitTransaction commit:
                await CommitAsync(commit, frame, peer, ct).ConfigureAwait(false);
                return true;
            case RollbackTransaction rollback:
                await RollbackAsync(rollback, frame, peer, ct).ConfigureAwait(false);
                return true;
            case PutDataObjects objects:
                await PutObjectsAsync(objects, frame, peer, ct).ConfigureAwait(false);
                return true;
            case Chunk chunk:
                await ChunkAsync(chunk, frame, peer, ct).ConfigureAwait(false);
                return true;
            case GetDataObjects get:
                await GetObjectsAsync(get, frame, peer, ct).ConfigureAwait(false);
                return true;
            case DeleteDataObjects delete:
                await DeleteObjectsAsync(delete, frame, peer, ct).ConfigureAwait(false);
                return true;
            case PutDataArrays arrays:
                await PutArraysAsync(arrays, frame, peer, ct).ConfigureAwait(false);
                return true;
            case PutUninitializedDataArrays declared:
                await DeclareArraysAsync(declared, frame, peer, ct).ConfigureAwait(false);
                return true;
            case PutDataSubarrays slices:
                await PutSubarraysAsync(slices, frame, peer, ct).ConfigureAwait(false);
                return true;
            case GetDataArrayMetadata metadata:
                await ArrayMetadataAsync(metadata, frame, peer, ct).ConfigureAwait(false);
                return true;
            case GetResources resources:
                await ResourcesAsync(resources, frame, peer, ct).ConfigureAwait(false);
                return true;
            default:
                await peer.FailAsync(frame, EtpErrorCodes.InvalidMessageType, $"Unexpected message: {frame.Name}", ct).ConfigureAwait(false);
                return true;
        }
    }

    private async Task DataspaceInfoAsync(GetDataspaceInfo request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var found = new Dictionary<string, Dataspace>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, uri) in request.Uris)
        {
            if (_spaces.TryGetValue(uri, out var space))
            {
                found[key] = space;
            }
            else
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = $"Dataspace {uri} not found" };
            }
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(
            new GetDataspaceInfoResponse
            {
                Dataspaces = found.ToDictionary(e => e.Key, e => e.Value.Described(), StringComparer.Ordinal),
            },
            frame.Header.MessageId,
            ct).ConfigureAwait(false);
    }

    private async Task PutDataspacesAsync(PutDataspaces request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, space) in request.Dataspaces)
        {
            if (_spaces.ContainsKey(space.Uri))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidArgument, Message = $"Space already exists: URI conflict with {space.Uri}" };
                continue;
            }

            var missing = new[] { "viewers", "owners", "legaltags", "otherRelevantDataCountries" }
                .Where(name => !space.CustomData.TryGetValue(name, out var value) || (value.Strings?.Count ?? 0) == 0)
                .ToList();
            if (missing.Count > 0)
            {
                errors[key] = new ErrorInfo
                {
                    Code = EtpErrorCodes.RequestDenied,
                    Message = $"Could not register dataspace {space.Uri}: one or more required fields are empty. Provide all of: viewers, owners, legaltags, otherRelevantDataCountries",
                };
                continue;
            }

            var now = EtpTime.Microseconds(DateTimeOffset.UtcNow);
            _spaces[space.Uri] = new Dataspace
            {
                Uri = space.Uri,
                Path = string.IsNullOrEmpty(space.Path) ? space.Uri : space.Path,
                CustomData = new Dictionary<string, DataValue>(space.CustomData, StringComparer.Ordinal),
                Created = now,
                LastWrite = now,
            };
            success[key] = string.Empty;
            StorageRecords.Add(RecordId(_spaces[space.Uri].Path));
        }

        if (success.Count == 0 && errors.Count > 0)
        {
            // Every entry failed: the errors go alone, with FIN and no response (section 8.1).
            await peer.SendAsync(new ProtocolFailure { Errors = errors }, frame.Header.MessageId, ct).ConfigureAwait(false);
            return;
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new PutDataspacesResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    /// <summary>The <c>dataset--ETPDataspace</c> record the server writes for a dataspace it creates (section 6.3).</summary>
    public List<string> StorageRecords { get; } = [];

    private static string RecordId(string path) => $"opendes:dataset--ETPDataspace:{path.Replace('/', '-')}";

    private async Task LockAsync(LockDataspaces request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, uri) in request.Uris)
        {
            if (!_spaces.TryGetValue(uri, out var space))
            {
                await peer.SendAsync(
                    new ProtocolFailure
                    {
                        Errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal)
                        {
                            [key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = $"Dataspace not found {uri}" },
                        },
                    },
                    frame.Header.MessageId,
                    ct).ConfigureAwait(false);
                return;
            }

            if (space.Locked == request.Lock)
            {
                await peer.SendAsync(
                    new ProtocolFailure
                    {
                        Errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal)
                        {
                            [key] = new ErrorInfo { Code = EtpErrorCodes.InvalidArgument, Message = $"Dataspace is already {(request.Lock ? "locked" : "unlocked")}" },
                        },
                    },
                    frame.Header.MessageId,
                    ct).ConfigureAwait(false);
                return;
            }

            space.Locked = request.Lock;
            success[key] = string.Empty;
        }

        await peer.SendAsync(new LockDataspacesResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task StartAsync(StartTransaction request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        if (_transaction is not null)
        {
            await peer.FailAsync(frame, EtpErrorCodes.MaxTransactionsExceeded, "Transaction already active", ct).ConfigureAwait(false);
            return;
        }

        var invalid = request.DataspaceUris.Where(uri => uri.Length < 5).ToList();
        if (invalid.Count > 0)
        {
            await peer.FailAsync(frame, EtpErrorCodes.InvalidArgument, $"Cannot start transaction, invalid dataspace URI(s): {string.Join(", ", invalid)}", ct).ConfigureAwait(false);
            return;
        }

        _transaction = Guid.NewGuid();
        _transactionSpaces = request.DataspaceUris;
        _beforeTransaction = Snapshot();
        await peer.SendAsync(
            new StartTransactionResponse { TransactionUuid = _transaction.Value, Successful = true },
            frame.Header.MessageId,
            ct).ConfigureAwait(false);
    }

    private async Task CommitAsync(CommitTransaction request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        if (_transaction != request.TransactionUuid)
        {
            await peer.SendAsync(
                new CommitTransactionResponse { TransactionUuid = request.TransactionUuid, Successful = false, FailureReason = _transaction is null ? "No active transaction" : "Unknown transaction uuid" },
                frame.Header.MessageId,
                ct).ConfigureAwait(false);
            return;
        }

        // Arrays an object names but nobody supplied, and arrays nobody named (section 4.7, steps 6 and 7).
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var space in _spaces.Values)
        {
            foreach (var stored in space.Objects.Values)
            {
                foreach (var path in ArrayPaths(stored.Xml))
                {
                    named.Add(path);
                }
            }
        }

        var missing = named.Where(path => !_spaces.Values.Any(s => s.Arrays.ContainsKey(path))).ToList();
        var orphan = _spaces.Values.SelectMany(s => s.Arrays.Keys).Where(path => !named.Contains(path)).ToList();
        if (missing.Count > 0 || orphan.Count > 0)
        {
            await peer.SendAsync(
                new CommitTransactionResponse
                {
                    TransactionUuid = request.TransactionUuid,
                    Successful = false,
                    FailureReason = missing.Count > 0 ? $"{missing.Count} Missing array(s)" : $"{orphan.Count} Orphan array(s)",
                },
                frame.Header.MessageId,
                ct).ConfigureAwait(false);
            return;
        }

        _transaction = null;
        _beforeTransaction = null;
        _transactionSpaces = [];
        await peer.SendAsync(
            new CommitTransactionResponse { TransactionUuid = request.TransactionUuid, Successful = true },
            frame.Header.MessageId,
            ct).ConfigureAwait(false);
    }

    private async Task RollbackAsync(RollbackTransaction request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var known = _transaction == request.TransactionUuid;
        if (known)
        {
            Restore(_beforeTransaction!);
            _transaction = null;
            _beforeTransaction = null;
            _transactionSpaces = [];
        }

        await peer.SendAsync(
            new RollbackTransactionResponse
            {
                TransactionUuid = request.TransactionUuid,
                Successful = known,
                FailureReason = known ? string.Empty : "No active transaction",
            },
            frame.Header.MessageId,
            ct).ConfigureAwait(false);
    }

    private async Task PutObjectsAsync(PutDataObjects request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        // An object whose XML follows in chunks parks the whole request until the chunk carrying FIN (section 4.5).
        if (request.DataObjects.Values.Any(o => o.BlobId is not null))
        {
            _chunked[frame.Header.MessageId] = new PendingPut(request, frame);
            return;
        }

        await StoreObjectsAsync(request, frame, peer, ct).ConfigureAwait(false);
    }

    private async Task ChunkAsync(Chunk chunk, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        if (frame.Header.CorrelationId == 0 || !_chunked.TryGetValue(frame.Header.CorrelationId, out var parked))
        {
            await peer.FailAsync(
                frame,
                EtpErrorCodes.InvalidArgument,
                frame.Header.CorrelationId == 0 ? "Chunk must contains parent ID as correllation ID 0" : $"Chunk must be associated with existing parent {frame.Header.CorrelationId}",
                ct).ConfigureAwait(false);
            return;
        }

        parked.Append(chunk);
        if (!frame.IsFinal)
        {
            return;
        }

        _chunked.Remove(frame.Header.CorrelationId);
        await StoreObjectsAsync(parked.Assembled(), parked.Request, peer, ct).ConfigureAwait(false);
    }

    private async Task StoreObjectsAsync(PutDataObjects request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, PutResponse>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, dataObject) in request.DataObjects)
        {
            if (!string.Equals(dataObject.Format, "xml", StringComparison.Ordinal))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "Expected XML format" };
                continue;
            }

            if (dataObject.Data.IsEmpty)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "No XML content" };
                continue;
            }

            var uri = EtpUri.Parse(dataObject.Resource.Uri);
            if (uri is null)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidUri, Message = "Expected eOBJ_INSTANCE URI" };
                continue;
            }

            if (!_spaces.TryGetValue(uri.Dataspace, out var space))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = $"Dataspace not found: {uri.Dataspace}" };
                continue;
            }

            if (space.Locked)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.RequestDenied, Message = $"Dataspace {uri.Dataspace} is read-only" };
                continue;
            }

            if (_transaction is not null && !_transactionSpaces.Contains(uri.Dataspace, StringComparer.Ordinal))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.RequestDenied, Message = "Writing to dataspaces not specified in `StartTransaction` is not allowed" };
                continue;
            }

            // Identity comes from the XML, not from the resource (section 5.1).
            var identity = Identity(dataObject.Data.Span);
            if (identity is null)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "Invalid UUID" };
                continue;
            }

            space.Objects[$"{identity.Value.Type}({identity.Value.Uuid:D})"] = new StoredObject(
                $"{uri.Dataspace}/{identity.Value.Type}({identity.Value.Uuid:D})",
                identity.Value.Type,
                identity.Value.Uuid,
                dataObject.Resource.Name,
                dataObject.Data.ToArray(),
                EtpTime.Microseconds(DateTimeOffset.UtcNow));
            success[key] = new PutResponse();
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new PutDataObjectsResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task GetObjectsAsync(GetDataObjects request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var found = new Dictionary<string, DataObject>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, uri) in request.Uris)
        {
            var stored = Object(uri);
            if (stored is null)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = "Object not Found" };
                continue;
            }

            found[key] = new DataObject
            {
                Resource = Described(stored),
                Format = "xml",
                Data = stored.Xml,
            };
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new GetDataObjectsResponse { DataObjects = found }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task DeleteObjectsAsync(DeleteDataObjects request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var deleted = new Dictionary<string, ArrayOfString>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, uri) in request.Uris)
        {
            var parsed = EtpUri.Parse(uri);
            var space = parsed is null ? null : _spaces.GetValueOrDefault(parsed.Dataspace);
            var name = parsed is null ? null : $"{parsed.Type}({parsed.Uuid:D})";
            if (space is null || name is null || !space.Objects.Remove(name))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = "Object not Found" };
                continue;
            }

            deleted[key] = new ArrayOfString { Values = [uri] };
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new DeleteDataObjectsResponse { DeletedUris = deleted }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task PutArraysAsync(PutDataArrays request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, put) in request.DataArrays)
        {
            var space = Space(put.Uid, errors, key);
            if (space is null)
            {
                continue;
            }

            if (put.Array.Dimensions.Length == 0)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "No array dimensions" };
                continue;
            }

            if (put.Array.Dimensions.Length > 4)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.LimitExceeded, Message = $"Array rank > 4: {put.Array.Dimensions.Length}" };
                continue;
            }

            var expected = put.Array.Dimensions.ToArray().Aggregate(1L, (a, b) => a * b);
            if (put.Array.Data.ElementCount != expected)
            {
                errors[key] = new ErrorInfo
                {
                    Code = EtpErrorCodes.InvalidObject,
                    Message = $"Array data - dimensions mismatch: {put.Array.Data.ElementCount} != {expected}",
                };
                continue;
            }

            space.Arrays[Path(put.Uid)] = new StoredArray(put.Array.Dimensions.ToArray(), put.Array.Data.Kind, put.Array.Data);
            success[key] = string.Empty;
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new PutDataArraysResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task DeclareArraysAsync(PutUninitializedDataArrays request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, declared) in request.DataArrays)
        {
            var space = Space(declared.Uid, errors, key);
            if (space is null)
            {
                continue;
            }

            space.Arrays[Path(declared.Uid)] = new StoredArray(
                declared.Metadata.Dimensions.ToArray(),
                declared.Metadata.TransportArrayType,
                Data: null);
            success[key] = string.Empty;
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new PutUninitializedDataArraysResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task PutSubarraysAsync(PutDataSubarrays request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, slice) in request.DataSubarrays)
        {
            var space = Space(slice.Uid, errors, key);
            if (space is null)
            {
                continue;
            }

            if (!space.Arrays.TryGetValue(Path(slice.Uid), out var array))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = $"Array not found: {Path(slice.Uid)}" };
                continue;
            }

            if (array.Type != slice.Data.Kind)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "Unexpected sub-array type" };
                continue;
            }

            if (slice.Starts.Length != array.Dimensions.Count || slice.Counts.Length != array.Dimensions.Count)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = "Unexpected sub-array rank" };
                continue;
            }

            var counted = slice.Counts.ToArray().Aggregate(1L, (a, b) => a * b);
            if (slice.Data.ElementCount != counted)
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidObject, Message = $"Sub-array data - counts mismatch: {slice.Data.ElementCount} != {counted}" };
                continue;
            }

            array.Slices.Add(new StoredSlice(slice.Starts.ToArray(), slice.Counts.ToArray(), slice.Data));
            success[key] = string.Empty;
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new PutDataSubarraysResponse { Success = success }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task ArrayMetadataAsync(GetDataArrayMetadata request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var found = new Dictionary<string, DataArrayMetadata>(StringComparer.Ordinal);
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        foreach (var (key, uid) in request.DataArrays)
        {
            var space = Space(uid, errors, key);
            if (space is null)
            {
                continue;
            }

            if (!space.Arrays.TryGetValue(Path(uid), out var array))
            {
                errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = $"Array not found: {Path(uid)}" };
                continue;
            }

            found[key] = new DataArrayMetadata
            {
                Dimensions = array.Dimensions.ToArray(),
                TransportArrayType = array.Type,
                LogicalArrayType = AnyLogicalArrayType.ArrayOfDouble64LE,
                StoreLastWrite = EtpTime.Microseconds(DateTimeOffset.UtcNow),
                StoreCreated = EtpTime.Microseconds(DateTimeOffset.UtcNow),
            };
        }

        if (errors.Count > 0)
        {
            await peer.PartlyFailedAsync(frame, errors, ct).ConfigureAwait(false);
        }

        await peer.SendAsync(new GetDataArrayMetadataResponse { ArrayMetadata = found }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private async Task ResourcesAsync(GetResources request, EtpFrame frame, Peer peer, CancellationToken ct)
    {
        var uri = request.Context.Uri;
        if (!_spaces.TryGetValue(uri, out var space))
        {
            await peer.FailAsync(frame, EtpErrorCodes.NotFound, $"Dataspace {uri} not found", ct).ConfigureAwait(false);
            return;
        }

        var wanted = request.Context.DataObjectTypes;
        var resources = space.Objects.Values
            .Where(o => wanted.Count == 0 || wanted.Contains(o.Type, StringComparer.Ordinal))
            .Select(Described)
            .ToList();
        await peer.SendAsync(new GetResourcesResponse { Resources = resources }, frame.Header.MessageId, ct).ConfigureAwait(false);
    }

    private Dataspace? Space(DataArrayIdentifier uid, Dictionary<string, ErrorInfo> errors, string key)
    {
        var parsed = EtpUri.Parse(uid.Uri);
        if (parsed is null)
        {
            errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidUri, Message = "Invalid Object UID" };
            return null;
        }

        if (string.IsNullOrEmpty(uid.PathInResource))
        {
            errors[key] = new ErrorInfo { Code = EtpErrorCodes.InvalidUri, Message = "Invalid empty pathInResource" };
            return null;
        }

        if (!_spaces.TryGetValue(parsed.Dataspace, out var space))
        {
            errors[key] = new ErrorInfo { Code = EtpErrorCodes.NotFound, Message = "Dataspace not found" };
            return null;
        }

        return space;
    }

    private static string Path(DataArrayIdentifier uid) => uid.PathInResource.TrimStart('/');

    private static Resource Described(StoredObject stored) => new()
    {
        Uri = stored.Uri,
        Name = stored.Name,
        LastChanged = stored.LastWrite,
        StoreLastWrite = stored.LastWrite,
        StoreCreated = stored.LastWrite,
        ActiveStatus = ActiveStatusKind.Active,
    };

    /// <summary>The (type, uuid) the server reads out of the XML itself (section 5.1).</summary>
    private static (string Type, Guid Uuid)? Identity(ReadOnlySpan<byte> xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(xml));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null || !Guid.TryParse(root.Attribute("uuid")?.Value, out var uuid))
        {
            return null;
        }

        if (!root.Elements().Any(e => string.Equals(e.Name.LocalName, "Citation", StringComparison.Ordinal)))
        {
            return null;
        }

        var xsi = root.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value;
        var type = xsi is null ? root.Name.LocalName : xsi[(xsi.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var version = root.Attribute("schemaVersion")?.Value ?? "2.0";
        var parts = version.Split('.');
        var minor = parts.Length > 1 ? $"{parts[0]}.{parts[1]}" : version;
        var ml = (root.Name.NamespaceName, minor) switch
        {
            var (space, v) when space.EndsWith("/resqmlv2", StringComparison.Ordinal) && v == "2.0" => "resqml20",
            var (space, v) when space.EndsWith("/resqmlv2", StringComparison.Ordinal) && v == "2.2" => "resqml22",
            var (space, v) when space.EndsWith("/commonv2", StringComparison.Ordinal) && v == "2.0" => "eml20",
            var (space, v) when space.EndsWith("/commonv2", StringComparison.Ordinal) && v == "2.3" => "eml23",
            _ => "resqml20",
        };

        return ($"{ml}.{type}", uuid);
    }

    /// <summary>The array paths an object's XML names, which the commit checks were supplied (section 4.7).</summary>
    private static IEnumerable<string> ArrayPaths(byte[] xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(xml));
        }
        catch (System.Xml.XmlException)
        {
            yield break;
        }

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is "PathInHdfFile" or "PathInExternalFile" && !string.IsNullOrWhiteSpace(element.Value))
            {
                yield return element.Value.TrimStart('/');
            }
        }
    }

    private Dictionary<string, Dataspace> Snapshot()
        => _spaces.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal);

    private void Restore(Dictionary<string, Dataspace> snapshot)
    {
        _spaces.Clear();
        foreach (var (key, space) in snapshot)
        {
            _spaces[key] = space;
        }
    }

    /// <summary>One dataspace the server holds.</summary>
    internal sealed class Dataspace
    {
        public required string Uri { get; init; }

        public required string Path { get; init; }

        public required Dictionary<string, DataValue> CustomData { get; init; }

        public long Created { get; init; }

        public long LastWrite { get; set; }

        public bool Locked { get; set; }

        public Dictionary<string, StoredObject> Objects { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, StoredArray> Arrays { get; init; } = new(StringComparer.Ordinal);

        public Dataspace Copy() => new()
        {
            Uri = Uri,
            Path = Path,
            CustomData = new Dictionary<string, DataValue>(CustomData, StringComparer.Ordinal),
            Created = Created,
            LastWrite = LastWrite,
            Locked = Locked,
            Objects = new Dictionary<string, StoredObject>(Objects, StringComparer.Ordinal),
            Arrays = Arrays.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal),
        };

        /// <summary>The dataspace as <c>GetDataspaceInfo</c> returns it, with the entries the server adds (section 7.1).</summary>
        public SqlFlow.Delivery.Engine.Protocols.Etp.Dataspace Described()
        {
            var custom = new Dictionary<string, DataValue>(CustomData, StringComparer.Ordinal)
            {
                ["read-only"] = DataValue.Of(Locked),
                ["locked"] = DataValue.Of(Locked),
                ["size"] = DataValue.Of((long)Objects.Count),
            };
            return new SqlFlow.Delivery.Engine.Protocols.Etp.Dataspace
            {
                Uri = Uri,
                Path = Path,
                StoreCreated = Created,
                StoreLastWrite = LastWrite,
                CustomData = custom,
            };
        }
    }

    /// <summary>One object the server holds, identified by the (type, uuid) its XML carries.</summary>
    internal sealed record StoredObject(string Uri, string Type, Guid Uuid, string Name, byte[] Xml, long LastWrite);

    /// <summary>One array the server holds, whole or declared and then filled by slices.</summary>
    internal sealed record StoredArray(IReadOnlyList<long> Dimensions, AnyArrayType Type, AnyArray? Data)
    {
        public List<StoredSlice> Slices { get; init; } = [];

        public StoredArray Copy() => new(Dimensions, Type, Data) { Slices = [.. Slices] };
    }

    internal sealed record StoredSlice(IReadOnlyList<long> Starts, IReadOnlyList<long> Counts, AnyArray Data);

    /// <summary>A put parked until the chunk carrying FIN arrives (section 4.5).</summary>
    private sealed class PendingPut
    {
        private readonly Dictionary<Guid, List<byte>> _blobs = [];

        public PendingPut(PutDataObjects request, EtpFrame frame)
        {
            Original = request;
            Request = frame;
        }

        public PutDataObjects Original { get; }

        public EtpFrame Request { get; }

        public void Append(Chunk chunk)
        {
            if (!_blobs.TryGetValue(chunk.BlobId, out var bytes))
            {
                bytes = [];
                _blobs[chunk.BlobId] = bytes;
            }

            bytes.AddRange(chunk.Data.ToArray());
        }

        /// <summary>The request as it would have arrived whole, each parked object carrying the bytes its chunks brought.</summary>
        public PutDataObjects Assembled() => new()
        {
            DataObjects = Original.DataObjects.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.BlobId is { } blob && _blobs.TryGetValue(blob, out var bytes)
                    ? entry.Value with { BlobId = null, Data = bytes.ToArray() }
                    : entry.Value,
                StringComparer.Ordinal),
            PruneContainedObjects = Original.PruneContainedObjects,
        };
    }
}

/// <summary>An ETP object URI, as both sides read it (osdu/specs/reservoir-ddms/INTEGRATION.md section 4.2).</summary>
internal sealed record EtpUri(string Dataspace, string Type, Guid Uuid)
{
    public static EtpUri? Parse(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        var slash = uri.LastIndexOf('/');
        var open = uri.LastIndexOf('(');
        if (slash < 0 || open < slash || !uri.EndsWith(')'))
        {
            return null;
        }

        var dataspace = uri[..slash];
        var type = uri[(slash + 1)..open];
        return Guid.TryParse(uri[(open + 1)..^1], CultureInfo.InvariantCulture, out var uuid) && type.Contains('.', StringComparison.Ordinal)
            ? new EtpUri(dataspace, type, uuid)
            : null;
    }
}
