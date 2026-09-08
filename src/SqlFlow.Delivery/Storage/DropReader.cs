using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Reads a drop through the file stores: the manifest, the parquet scopes, and the payload chunk listings. Records
/// stream in bounded memory whatever the drop holds (design.md section 16.1): a drop without child scopes streams
/// its root rows; a partitioned drop merge-joins root file i with child file i, both sorted by key; any other drop
/// is hash-partitioned to disk by key and joined bucket by bucket. Payload bytes are never parsed here;
/// <see cref="OpenChunkAsync"/> hands back a raw stream for the protocol to copy.
/// </summary>
public sealed class DropReader : IDropReader
{
    /// <summary>The root-scope column that carries the delivery key when child scopes or payloads exist.</summary>
    public const string DeliveryKeyColumn = "deliveryKey";

    private readonly FileStoreRegistry _stores;
    private readonly ILogger _logger;

    public DropReader(FileStoreRegistry stores, ILogger<DropReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _stores = stores;
        _logger = logger ?? NullLogger<DropReader>.Instance;
    }

    public async Task<Drop> OpenAsync(string location, string manifestName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var store = _stores.For(location);
        var drop = new Drop(location.TrimEnd('/', '\\'), null!);
        var manifestPath = drop.Resolve(manifestName);
        var files = await store.ListAsync(manifestPath, new FileDiscovery { Pattern = "*" }, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new FlowValidationException($"{manifestPath}: manifest not found. The preparing side writes it last; a missing manifest means the drop is incomplete or the location is wrong.");
        }

        await using var stream = await store.OpenReadAsync(files[0], ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var manifest = DropManifest.Parse(json, manifestPath);
        return drop with { Manifest = manifest };
    }

    public IAsyncEnumerable<SourceRecord> ReadRecordsAsync(Drop drop, IReadOnlyList<int>? partitions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drop);
        var manifest = drop.Manifest;
        var selected = SelectPartitions(manifest, partitions);
        var children = manifest.Scopes.Where(kv => !kv.Key.Equals(DropManifest.RootScope, StringComparison.OrdinalIgnoreCase)).ToList();
        var keyRequired = children.Count > 0 || manifest.Payloads.Count > 0;

        if (children.Count == 0)
        {
            return StreamRootAsync(drop, selected, keyRequired, ct);
        }

        return manifest.Partitioned
            ? MergeJoinAsync(drop, selected, children, ct)
            : SpillJoinAsync(drop, selected, children, ct);
    }

    public Task<IReadOnlyList<PayloadChunk>> ListPayloadChunksAsync(Drop drop, string payloadName, Guid deliveryKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drop);
        return ListPayloadChunksAsync(PayloadLocation(drop, payloadName, deliveryKey), ct);
    }

    public async Task<IReadOnlyList<PayloadChunk>> ListPayloadChunksAsync(string payloadLocation, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadLocation);
        var (directory, pattern) = SplitPattern(payloadLocation);
        var store = _stores.For(directory);
        var files = await store.ListAsync(directory, new FileDiscovery { Pattern = pattern }, ct).ConfigureAwait(false);
        return files
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select((f, i) => new PayloadChunk(i, f.Path, f.Size))
            .ToList();
    }

    public Task<Stream> OpenChunkAsync(PayloadChunk chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var store = _stores.For(chunk.Path);
        return store.OpenReadAsync(new FileRef { Path = chunk.Path, Name = Path.GetFileName(chunk.Path), Size = chunk.Size }, ct);
    }

    /// <summary>The resolved chunk glob for one record: directory plus file pattern, e.g. .../curves/{key}/chunk_*.parquet.</summary>
    public string PayloadLocation(Drop drop, string payloadName, Guid deliveryKey)
    {
        ArgumentNullException.ThrowIfNull(drop);
        if (!drop.Manifest.Payloads.TryGetValue(payloadName, out var payload))
        {
            throw new FlowValidationException($"manifest: payload '{payloadName}' is not declared.");
        }

        var relative = payload.PathTemplate.Replace("{deliveryKey}", deliveryKey.ToString("D"), StringComparison.Ordinal);
        return drop.Resolve(relative);
    }

    /// <summary>The root files to read: every one, or the requested indexes (validated against the manifest).</summary>
    private static IReadOnlyList<(int Index, string File)> SelectPartitions(DropManifest manifest, IReadOnlyList<int>? partitions)
    {
        var files = manifest.Root.Files;
        if (partitions is null)
        {
            return files.Select((f, i) => (i, f)).ToList();
        }

        var selected = new List<(int, string)>(partitions.Count);
        foreach (var index in partitions.Distinct().Order())
        {
            if (index < 0 || index >= files.Count)
            {
                throw new FlowValidationException($"manifest: partition {index} does not exist; the root scope has {files.Count} file(s).");
            }

            selected.Add((index, files[index]));
        }

        return selected;
    }

    private async IAsyncEnumerable<SourceRecord> StreamRootAsync(Drop drop, IReadOnlyList<(int Index, string File)> files, bool keyRequired, [EnumeratorCancellation] CancellationToken ct)
    {
        var store = _stores.For(drop.Location);
        foreach (var (_, file) in files)
        {
            var path = drop.Resolve(file);
            await foreach (var row in ReadScopeFileAsync(store, path, ct).ConfigureAwait(false))
            {
                yield return new SourceRecord { Row = row, DeclaredDeliveryKey = DeclaredKey(row, path, keyRequired) };
            }
        }
    }

    /// <summary>A partitioned drop: root file i and child file i, each sorted by key, joined in lockstep.</summary>
    private async IAsyncEnumerable<SourceRecord> MergeJoinAsync(
        Drop drop,
        IReadOnlyList<(int Index, string File)> partitions,
        IReadOnlyList<KeyValuePair<string, ManifestScope>> children,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var store = _stores.For(drop.Location);
        foreach (var (index, rootFile) in partitions)
        {
            var rootPath = drop.Resolve(rootFile);
            var cursors = new List<ChildCursor>(children.Count);
            try
            {
                foreach (var (name, scope) in children)
                {
                    var childPath = drop.Resolve(scope.Files[index]);
                    cursors.Add(new ChildCursor(name, scope.ParentKey!, scope.OrderBy, childPath, ReadScopeFileAsync(store, childPath, ct).GetAsyncEnumerator(ct)));
                }

                string? previous = null;
                await foreach (var row in ReadScopeFileAsync(store, rootPath, ct).ConfigureAwait(false))
                {
                    var key = DeclaredKey(row, rootPath, keyRequired: true)!.Value;
                    var keyText = key.ToString("D");
                    if (previous is not null && string.CompareOrdinal(keyText, previous) < 0)
                    {
                        throw new FlowValidationException($"{rootPath}: the manifest declares the drop partitioned, but row {keyText} follows {previous}; root rows must be sorted by {DeliveryKeyColumn} within each file.");
                    }

                    previous = keyText;
                    var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cursor in cursors)
                    {
                        scopes[cursor.Scope] = await cursor.TakeAsync(keyText).ConfigureAwait(false);
                    }

                    yield return new SourceRecord { Row = row, Scopes = scopes, DeclaredDeliveryKey = key };
                }

                foreach (var cursor in cursors)
                {
                    var orphans = await cursor.DrainAsync().ConfigureAwait(false);
                    if (orphans > 0)
                    {
                        _logger.LogWarning("{Path}: {Count} child row(s) of scope '{Scope}' reference records that are not in partition {Partition}.", cursor.Path, orphans, cursor.Scope, index);
                    }
                }
            }
            finally
            {
                foreach (var cursor in cursors)
                {
                    await cursor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Any other drop: every scope hashed to disk by key, then joined one bucket at a time.</summary>
    private async IAsyncEnumerable<SourceRecord> SpillJoinAsync(
        Drop drop,
        IReadOnlyList<(int Index, string File)> partitions,
        IReadOnlyList<KeyValuePair<string, ManifestScope>> children,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var store = _stores.For(drop.Location);
        long childBytes = 0;
        var childFiles = new List<(string Scope, ManifestScope Definition, FileRef File)>();
        foreach (var (name, scope) in children)
        {
            foreach (var file in scope.Files)
            {
                var path = drop.Resolve(file);
                var listed = await store.ListAsync(path, new FileDiscovery { Pattern = "*" }, ct).ConfigureAwait(false);
                if (listed.Count == 0)
                {
                    throw new FlowValidationException($"{path}: declared in the manifest but not found in the drop.");
                }

                foreach (var f in listed)
                {
                    childBytes += f.Size;
                    childFiles.Add((name, scope, f));
                }
            }
        }

        await using var spill = new ScopeSpill(ScopeSpill.BucketsFor(childBytes));
        _logger.LogInformation("Joining {Scopes} child scope(s) through {Buckets} disk bucket(s); declare the drop partitioned to stream it instead.", children.Count, spill.Buckets);

        foreach (var (name, scope, file) in childFiles)
        {
            await foreach (var row in ReadParquetAsync(file, ct).ConfigureAwait(false))
            {
                var keyText = row.GetString(scope.ParentKey!);
                if (!Guid.TryParse(keyText, CultureInfo.InvariantCulture, out var key))
                {
                    throw new FlowValidationException($"{file.Path}: scope '{name}' row has parentKey '{keyText}', which is not a UUID.");
                }

                await spill.WriteAsync(name, key, row, ct).ConfigureAwait(false);
            }
        }

        foreach (var (_, rootFile) in partitions)
        {
            var path = drop.Resolve(rootFile);
            await foreach (var row in ReadScopeFileAsync(store, path, ct).ConfigureAwait(false))
            {
                var key = DeclaredKey(row, path, keyRequired: true)!.Value;
                await spill.WriteAsync(DropManifest.RootScope, key, row, ct).ConfigureAwait(false);
            }
        }

        await spill.CompleteAsync(ct).ConfigureAwait(false);

        for (var bucket = 0; bucket < spill.Buckets; bucket++)
        {
            var index = new Dictionary<string, Dictionary<Guid, List<SourceRow>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, scope) in children)
            {
                var byKey = new Dictionary<Guid, List<SourceRow>>();
                await foreach (var (key, row) in spill.ReadAsync(name, bucket, ct).ConfigureAwait(false))
                {
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        byKey[key] = list;
                    }

                    list.Add(row);
                }

                if (scope.OrderBy is { } orderBy)
                {
                    foreach (var list in byKey.Values)
                    {
                        list.Sort((a, b) => CompareValues(a.Get(orderBy), b.Get(orderBy)));
                    }
                }

                index[name] = byKey;
            }

            await foreach (var (key, row) in spill.ReadAsync(DropManifest.RootScope, bucket, ct).ConfigureAwait(false))
            {
                var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, byKey) in index)
                {
                    scopes[name] = byKey.TryGetValue(key, out var rows) ? rows : [];
                }

                yield return new SourceRecord { Row = row, Scopes = scopes, DeclaredDeliveryKey = key };
            }
        }
    }

    private static Guid? DeclaredKey(SourceRow row, string path, bool keyRequired)
    {
        var keyText = row.GetString(DeliveryKeyColumn);
        if (keyText is not null)
        {
            if (!Guid.TryParse(keyText, CultureInfo.InvariantCulture, out var key))
            {
                throw new FlowValidationException($"{path}: root row has {DeliveryKeyColumn} '{keyText}', which is not a UUID.");
            }

            return key;
        }

        if (keyRequired)
        {
            throw new FlowValidationException($"{path}: root rows must carry a '{DeliveryKeyColumn}' column when the drop has child scopes or payloads.");
        }

        return null;
    }

    private static (string Directory, string Pattern) SplitPattern(string location)
    {
        var normalized = location.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        if (slash < 0)
        {
            return (location, "*");
        }

        var last = normalized[(slash + 1)..];
        if (last.Contains('*', StringComparison.Ordinal) || last.Contains('?', StringComparison.Ordinal))
        {
            var directory = location[..slash];
            return (directory, last);
        }

        return (location, "*");
    }

    private async IAsyncEnumerable<SourceRow> ReadScopeFileAsync(IFileStore store, string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var files = await store.ListAsync(path, new FileDiscovery { Pattern = "*" }, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new FlowValidationException($"{path}: declared in the manifest but not found in the drop.");
        }

        foreach (var file in files)
        {
            await foreach (var row in ReadParquetAsync(file, ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    private async IAsyncEnumerable<SourceRow> ReadParquetAsync(FileRef file, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var seekable = await _stores.OpenSeekableAsync(file, ct).ConfigureAwait(false);
        await foreach (var row in ParquetScopeReader.ReadRowsAsync(seekable, null, ct).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    internal static int CompareValues(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        if (a is IComparable ca && a.GetType() == b.GetType())
        {
            return ca.CompareTo(b);
        }

        if (a is long or double && b is long or double)
        {
            return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        }

        return string.CompareOrdinal(SourceRow.Stringify(a), SourceRow.Stringify(b));
    }

    /// <summary>One child scope's sorted rows of one partition, consumed in lockstep with the root rows.</summary>
    private sealed class ChildCursor : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<SourceRow> _rows;
        private readonly string _parentKey;
        private readonly string? _orderBy;
        private (string KeyText, SourceRow Row)? _pending;
        private string? _previous;
        private int _orphans;

        public ChildCursor(string scope, string parentKey, string? orderBy, string path, IAsyncEnumerator<SourceRow> rows)
        {
            Scope = scope;
            Path = path;
            _parentKey = parentKey;
            _orderBy = orderBy;
            _rows = rows;
        }

        public string Scope { get; }

        public string Path { get; }

        /// <summary>The rows whose parent key equals <paramref name="keyText"/>, skipping (and counting) rows for smaller keys.</summary>
        public async Task<IReadOnlyList<SourceRow>> TakeAsync(string keyText)
        {
            var list = new List<SourceRow>();
            while (true)
            {
                if (_pending is null)
                {
                    if (!await _rows.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    _pending = Keyed(_rows.Current);
                }

                var cmp = string.CompareOrdinal(_pending.Value.KeyText, keyText);
                if (cmp < 0)
                {
                    _orphans++;
                    _pending = null;
                    continue;
                }

                if (cmp > 0)
                {
                    break;
                }

                list.Add(_pending.Value.Row);
                _pending = null;
            }

            if (_orderBy is { } orderBy && list.Count > 1)
            {
                list.Sort((a, b) => CompareValues(a.Get(orderBy), b.Get(orderBy)));
            }

            return list;
        }

        /// <summary>Consumes what is left after the last root row; every remaining row is an orphan.</summary>
        public async Task<int> DrainAsync()
        {
            if (_pending is not null)
            {
                _orphans++;
                _pending = null;
            }

            while (await _rows.MoveNextAsync().ConfigureAwait(false))
            {
                Keyed(_rows.Current);
                _orphans++;
            }

            return _orphans;
        }

        private (string KeyText, SourceRow Row) Keyed(SourceRow row)
        {
            var text = row.GetString(_parentKey);
            if (!Guid.TryParse(text, CultureInfo.InvariantCulture, out var key))
            {
                throw new FlowValidationException($"{Path}: scope '{Scope}' row has parentKey '{text}', which is not a UUID.");
            }

            var keyText = key.ToString("D");
            if (_previous is not null && string.CompareOrdinal(keyText, _previous) < 0)
            {
                throw new FlowValidationException($"{Path}: the manifest declares the drop partitioned, but child row {keyText} follows {_previous}; scope '{Scope}' rows must be sorted by {_parentKey} within each file.");
            }

            _previous = keyText;
            return (keyText, row);
        }

        public ValueTask DisposeAsync() => _rows.DisposeAsync();
    }
}
