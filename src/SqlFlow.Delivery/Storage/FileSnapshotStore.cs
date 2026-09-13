using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// The snapshot store as files under one root (a local directory or a blob prefix):
/// <code>
/// references/{version}/manifest.json { version, capturedUtc, contentHash, types }
/// references/{version}/{Type}.json   { entityType, items: [...] }
/// references/current                 the version 'pinned' resolves to
/// </code>
/// Versions are never rewritten: a refresh mints a new version and moves the pin.
/// </summary>
public sealed class FileSnapshotStore : ISnapshotStore
{
    private readonly string _root;
    private readonly FileStoreRegistry _stores;
    private readonly IFileStore _store;

    public FileSnapshotStore(string root, FileStoreRegistry stores)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(stores);
        _root = root.TrimEnd('/', '\\');
        _stores = stores;
        _store = stores.For(root);
    }

    public string Root => _root;

    public async Task<string?> CurrentReferenceVersionAsync(CancellationToken ct = default)
    {
        var text = await ReadTextAsync(Join("references", "current"), ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public async Task<ReferenceSnapshot?> LoadReferencesAsync(string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var manifestText = await ReadTextAsync(Join("references", version, "manifest.json"), ct).ConfigureAwait(false);
        if (manifestText is null)
        {
            return null;
        }

        var manifest = JsonNode.Parse(manifestText) as JsonObject ?? throw new DeliveryException($"Reference snapshot '{version}' has an invalid manifest.");
        var captured = manifest["capturedUtc"]?.GetValue<string>() is { } c ? DateTimeOffset.Parse(c, CultureInfo.InvariantCulture) : DateTimeOffset.UnixEpoch;
        var types = new List<ReferenceType>();
        if (manifest["types"] is JsonArray names)
        {
            foreach (var name in names.Select(n => n?.GetValue<string>()).Where(n => n is not null))
            {
                var typeText = await ReadTextAsync(Join("references", version, name + ".json"), ct).ConfigureAwait(false)
                    ?? throw new DeliveryException($"Reference snapshot '{version}' lists type '{name}' but the file is missing.");
                var node = JsonNode.Parse(typeText) as JsonObject ?? throw new DeliveryException($"Reference type file '{name}.json' in snapshot '{version}' is not a JSON object.");
                types.Add(ReferenceType.FromJson(name!, node));
            }
        }

        var snapshot = new ReferenceSnapshot(version, captured, types);
        if (manifest["contentHash"]?.GetValue<string>() is { } expected && !expected.Equals(snapshot.ContentHash(), StringComparison.Ordinal))
        {
            throw new DeliveryException($"Reference snapshot '{version}' content does not match its recorded hash; snapshots are immutable and this one was edited in place.");
        }

        return snapshot;
    }

    public async Task<IReadOnlyList<string>> ListReferenceVersionsAsync(CancellationToken ct = default)
    {
        var files = await _store.ListAsync(Join("references"), new FileDiscovery { Pattern = "manifest.json", Recursive = true }, ct).ConfigureAwait(false);
        return files
            .Select(f => f.Path.Replace('\\', '/'))
            .Select(p => p.Split('/'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[^2])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    public async Task SaveReferencesAsync(ReferenceSnapshot snapshot, bool makeCurrent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var manifestPath = Join("references", snapshot.Version, "manifest.json");
        if (await _stores.ExistsAsync(manifestPath, ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Reference snapshot version '{snapshot.Version}' already exists; snapshots are immutable. Mint a new version.");
        }

        foreach (var type in snapshot.Types)
        {
            await WriteTextAsync(Join("references", snapshot.Version, type.Name + ".json"), CanonicalJson.Pretty(type.ToJson()), ct).ConfigureAwait(false);
        }

        var manifest = new JsonObject
        {
            ["version"] = snapshot.Version,
            ["capturedUtc"] = CanonicalJson.FormatDateTime(snapshot.CapturedUtc),
            ["contentHash"] = snapshot.ContentHash(),
            ["types"] = new JsonArray(snapshot.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
        };
        await WriteTextAsync(manifestPath, CanonicalJson.Pretty(manifest), ct).ConfigureAwait(false);

        if (makeCurrent)
        {
            await WriteTextAsync(Join("references", "current"), snapshot.Version, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Mints a reference version label from a capture time: sortable, unique per second.</summary>
    public static string MintVersion(DateTimeOffset capturedUtc) => capturedUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Slug(string kind) => kind.Replace(':', '_').Replace('/', '_');

    private string Join(params string[] parts)
    {
        var rel = string.Join('/', parts);
        return _root.Contains("://", StringComparison.Ordinal)
            ? _root + "/" + rel
            : Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<string?> ReadTextAsync(string path, CancellationToken ct)
    {
        var files = await _store.ListAsync(path, new FileDiscovery { Pattern = "*" }, ct).ConfigureAwait(false);
        var file = files.FirstOrDefault(f => f.Path.Replace('\\', '/').EndsWith(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return null;
        }

        await using var stream = await _store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteTextAsync(string path, string text, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await _stores.WriteAsync(path, stream, ct).ConfigureAwait(false);
    }
}
