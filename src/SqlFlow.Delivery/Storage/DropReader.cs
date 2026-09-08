using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Reads a drop through the file stores: the manifest, the parquet scopes (child scopes first, indexed by parent
/// key), and the payload chunk listings. Payload bytes are never parsed here; <see cref="OpenChunkAsync"/> hands
/// back a raw stream for the protocol to copy.
/// </summary>
public sealed class DropReader : IDropReader
{
    /// <summary>The root-scope column that carries the delivery key when child scopes or payloads exist.</summary>
    public const string DeliveryKeyColumn = "deliveryKey";

    private readonly FileStoreRegistry _stores;

    public DropReader(FileStoreRegistry stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        _stores = stores;
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
            throw new FlowValidationException($"{manifestPath}: manifest not found. Databricks writes it last; a missing manifest means the drop is incomplete or the location is wrong.");
        }

        await using var stream = await store.OpenReadAsync(files[0], ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var manifest = DropManifest.Parse(json, manifestPath);
        return drop with { Manifest = manifest };
    }

    public async IAsyncEnumerable<SourceRecord> ReadRecordsAsync(Drop drop, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drop);
        var manifest = drop.Manifest;
        var store = _stores.For(drop.Location);

        // Child scopes are read fully and indexed by parent key: a record needs all of its children to render.
        var children = new Dictionary<string, Dictionary<Guid, List<SourceRow>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, scope) in manifest.Scopes)
        {
            if (name.Equals(DropManifest.RootScope, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = new Dictionary<Guid, List<SourceRow>>();
            var parentKey = scope.ParentKey!;
            foreach (var file in scope.Files)
            {
                await foreach (var row in ReadScopeFileAsync(store, drop.Resolve(file), ct).ConfigureAwait(false))
                {
                    var keyText = row.GetString(parentKey);
                    if (!Guid.TryParse(keyText, CultureInfo.InvariantCulture, out var key))
                    {
                        throw new FlowValidationException($"{drop.Resolve(file)}: scope '{name}' row has parentKey '{keyText}', which is not a UUID.");
                    }

                    if (!index.TryGetValue(key, out var list))
                    {
                        list = [];
                        index[key] = list;
                    }

                    list.Add(row);
                }
            }

            if (scope.OrderBy is { } orderBy)
            {
                foreach (var list in index.Values)
                {
                    list.Sort((a, b) => CompareValues(a.Get(orderBy), b.Get(orderBy)));
                }
            }

            children[name] = index;
        }

        var hasChildren = children.Count > 0 || manifest.Payloads.Count > 0;
        foreach (var file in manifest.Root.Files)
        {
            var path = drop.Resolve(file);
            await foreach (var row in ReadScopeFileAsync(store, path, ct).ConfigureAwait(false))
            {
                Guid? declared = null;
                var keyText = row.GetString(DeliveryKeyColumn);
                if (keyText is not null)
                {
                    if (!Guid.TryParse(keyText, CultureInfo.InvariantCulture, out var key))
                    {
                        throw new FlowValidationException($"{path}: root row has {DeliveryKeyColumn} '{keyText}', which is not a UUID.");
                    }

                    declared = key;
                }
                else if (hasChildren)
                {
                    throw new FlowValidationException($"{path}: root rows must carry a '{DeliveryKeyColumn}' column when the drop has child scopes or payloads.");
                }

                var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
                if (declared is { } dk)
                {
                    foreach (var (name, index) in children)
                    {
                        scopes[name] = index.TryGetValue(dk, out var rows) ? rows : [];
                    }
                }

                yield return new SourceRecord { Row = row, Scopes = scopes, DeclaredDeliveryKey = declared };
            }
        }
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

    private static async IAsyncEnumerable<SourceRow> ReadScopeFileAsync(IFileStore store, string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var files = await store.ListAsync(path, new FileDiscovery { Pattern = "*" }, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new FlowValidationException($"{path}: declared in the manifest but not found in the drop.");
        }

        foreach (var file in files)
        {
            await using var raw = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
            await using var seekable = await ParquetScopeReader.EnsureSeekableAsync(raw, ct).ConfigureAwait(false);
            await foreach (var row in ParquetScopeReader.ReadRowsAsync(seekable, null, ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    private static int CompareValues(object? a, object? b)
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
}
