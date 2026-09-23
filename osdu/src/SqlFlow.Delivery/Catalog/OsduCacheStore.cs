using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The cache store over the <c>osdu.CacheVersion</c>, <c>osdu.CacheItem</c> and <c>osdu.CacheMember</c>
/// tables, one cache per partition. A merge runs in one transaction: it reads the partition's current version and who holds
/// each record of the captured types, merges the capture in, and when the cached content moved writes the version row
/// first, which claims the partition's next sequence so a concurrent write fails instead of interleaving, then the records
/// that changed, arrived or left, then the membership and the current flag. A record whose values did not change is not
/// written again: its open range already covers the new version, however many flows captured it. Versions never change once
/// written, so the most recently loaded ones are kept in memory.
/// </summary>
public sealed class OsduCacheStore : ICacheStore
{
    /// <summary>The width of a partition name and of a cache flow name in the catalog.</summary>
    public const int MaxNameLength = 200;

    private const int InsertChunk = 2_000;

    private const int UpdateChunk = 1_000;

    /// <summary>How many loaded versions stay in memory. A run renders against one; the GUI reads a handful.</summary>
    private const int RetainedVersions = 8;

    private readonly Func<OsduDbContext> _factory;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Scope, string Version, ReferenceSnapshot Snapshot)> _recent = new();

    public OsduCacheStore(Func<OsduDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        return await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope && v.Current)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (Recent(scope, version) is { } known)
        {
            return known;
        }

        await using var db = _factory();
        var row = await db.DeliveryCacheVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Scope == scope && v.Version == version, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var snapshot = await ReadAsync(db, row, ct).ConfigureAwait(false);
        Remember(scope, version, snapshot);
        return snapshot;
    }

    public async Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        var rows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => v.Scope == scope)
            .OrderByDescending(v => v.Sequence)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(Info).ToList();
    }

    public async Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        await using var db = _factory();
        var rows = db.DeliveryCacheVersions.AsNoTracking().Where(v => v.Scope == scope);
        rows = version is null ? rows.Where(v => v.Current) : rows.Where(v => v.Version == version);
        var row = await rows.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : Info(row);
    }

    public async Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        await using var db = _factory();
        return await DeclarationAsync(db, scope, ct).ConfigureAwait(false);
    }

    /// <summary>What the synced cache flows declare for a partition, read through an open context.</summary>
    public static async Task<CacheDeclaration> DeclarationAsync(OsduDbContext db, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var rows = await db.DeliveryCacheDefinitions.AsNoTracking()
            .Where(d => d.Scope == scope)
            .OrderBy(d => d.FlowName).ThenBy(d => d.Name).ThenBy(d => d.RepoId)
            .Select(d => new { d.FlowName, d.Name, d.EntityType, d.Kind, d.Query, d.FieldsJson, d.OnChange })
            .ToListAsync(ct).ConfigureAwait(false);

        // A flow is named once per catalog; the sync warns when two repositories declare the same one, and the first row wins here.
        var seen = new HashSet<(string Flow, string Type)>();
        var declarations = new List<CacheTypeDeclaration>(rows.Count);
        foreach (var row in rows)
        {
            if (!seen.Add((row.FlowName, row.Name.ToUpperInvariant())))
            {
                continue;
            }

            declarations.Add(new CacheTypeDeclaration(
                row.FlowName, row.Name, row.EntityType, row.Kind, string.IsNullOrWhiteSpace(row.Query) ? "*" : row.Query,
                ParseFields(row.FieldsJson, row.FlowName, row.Name),
                row.OnChange.Equals("approve", StringComparison.OrdinalIgnoreCase) ? CacheChangeMode.Approve : CacheChangeMode.Auto));
        }

        return new CacheDeclaration(scope, declarations);
    }

    public async Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(capture);
        if (scope.Length > MaxNameLength)
        {
            throw new DeliveryException($"A partition name is at most {MaxNameLength} characters; '{scope[..40]}...' is longer.");
        }

        if (flowName.Length > MaxNameLength)
        {
            throw new DeliveryException($"A cache flow name is at most {MaxNameLength} characters; '{flowName[..40]}...' is longer.");
        }

        if (captured.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != captured.Count)
        {
            throw new DeliveryException($"Cache flow '{flowName}' captured the same type name twice; nothing was written to the cache of partition '{scope}'.");
        }

        foreach (var type in captured)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (type.Items.FirstOrDefault(item => !ids.Add(item.Id)) is { } twice)
            {
                throw new DeliveryException(
                    $"Cache flow '{flowName}': type {type.Name} holds record {twice.Id} more than once, so the capture could not say which values it holds. Nothing was written to the cache of partition '{scope}'.");
            }
        }

        var typeNames = captured.Select(t => t.Name).ToList();
        var label = CacheVersionLabel.Mint(capturedUtc);

        await using var db = _factory();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            var write = await strategy.ExecuteAsync(async () =>
            {
                // A retried attempt rebuilds everything it stages from the capture, never from what a failed attempt tracked.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                var currentRow = await db.DeliveryCacheVersions.AsNoTracking()
                    .Where(v => v.Scope == scope && v.Current)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var current = currentRow is null ? null : Recent(scope, currentRow.Version) ?? await ReadAsync(db, currentRow, ct).ConfigureAwait(false);

                var memberRows = await db.DeliveryCacheMembers.AsNoTracking()
                    .Where(m => m.Scope == scope && typeNames.Contains(m.TypeName))
                    .Select(m => new { m.TypeName, m.RecordId, m.FlowName })
                    .ToListAsync(ct).ConfigureAwait(false);
                var members = new Dictionary<CacheMemberKey, IReadOnlySet<string>>(CacheMemberKey.Comparer);
                foreach (var held in memberRows.GroupBy(m => new CacheMemberKey(m.TypeName, m.RecordId), CacheMemberKey.Comparer))
                {
                    members[held.Key] = held.Select(m => m.FlowName).ToHashSet(StringComparer.Ordinal);
                }

                var declared = await db.DeliveryCacheDefinitions.AsNoTracking()
                    .Where(d => d.Scope == scope)
                    .Select(d => d.Name)
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                var plan = CacheMerge.Apply(current, members, flowName, captured, declared, label, capturedUtc, readings);
                var hash = plan.Snapshot.ContentHash();
                if (current is not null && string.Equals(hash, current.ContentHash(), StringComparison.Ordinal))
                {
                    await WriteMembersAsync(db, scope, flowName, captured, plan, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return new CacheWrite(current, current, Written: false);
                }

                var sequence = (await db.DeliveryCacheVersions
                    .Where(v => v.Scope == scope)
                    .MaxAsync(v => (int?)v.Sequence, ct).ConfigureAwait(false) ?? 0) + 1;
                var version = await db.DeliveryCacheVersions.AnyAsync(v => v.Scope == scope && v.Version == label, ct).ConfigureAwait(false)
                    ? $"{label}-{sequence.ToString(CultureInfo.InvariantCulture)}"
                    : label;
                var snapshot = string.Equals(version, plan.Snapshot.Version, StringComparison.Ordinal)
                    ? plan.Snapshot
                    : new ReferenceSnapshot(version, capturedUtc, plan.Snapshot.Types, plan.Snapshot.SystemProperties);
                var types = snapshot.Types.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

                // The row goes in first: the unique sequence is what a concurrent write of the same partition collides on.
                var row = new DeliveryCacheVersion
                {
                    Id = FlowIdentity.FromName($"delivery-cache-version/{scope}/{version}"),
                    Scope = scope,
                    FlowName = flowName,
                    Version = version,
                    Sequence = sequence,
                    CapturedUtc = capturedUtc.UtcDateTime,
                    ContentHash = hash,
                    PreviousVersion = currentRow?.Version,
                    Current = false,
                    RunId = capture.RunId,
                    CapturedBy = Clip(capture.CapturedBy, 200),
                    Origin = Clip(capture.Origin, 1000),
                    TypesJson = TypesJson(types),
                    SystemPropertiesJson = SystemPropertiesJson(snapshot.SystemProperties),
                    Items = types.Sum(t => (long)t.Items.Count),
                };
                db.DeliveryCacheVersions.Add(row);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                await WriteItemsAsync(db, scope, sequence, types, ct).ConfigureAwait(false);
                await WriteMembersAsync(db, scope, flowName, captured, plan, ct).ConfigureAwait(false);

                await db.DeliveryCacheVersions
                    .Where(v => v.Scope == scope && v.Current)
                    .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, false), ct).ConfigureAwait(false);
                await db.DeliveryCacheVersions
                    .Where(v => v.Id == row.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(v => v.Current, true), ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return new CacheWrite(snapshot, current, Written: true);
            }).ConfigureAwait(false);

            if (write.Written)
            {
                Remember(scope, write.Snapshot.Version, write.Snapshot);
            }

            return write;
        }
        catch (DbUpdateException ex)
        {
            throw new DeliveryException(
                $"Cache flow '{flowName}' could not write a version of the cache of partition '{scope}', most likely because another refresh of the partition wrote one at the same time. Nothing of this capture was kept; run the refresh again.",
                ex);
        }
    }

    /// <summary>A version row as the store and the catalog readers describe it.</summary>
    public static CacheVersionInfo Info(DeliveryCacheVersion row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CacheVersionInfo(
            row.Scope, row.Version, row.Sequence, DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc), row.Current, row.PreviousVersion,
            row.RunId, row.CapturedBy, row.Origin, row.FlowName, row.Items, ParseTypes(row.TypesJson, row.Scope, row.Version),
            ParseSystemProperties(row.SystemPropertiesJson, row.Scope, row.Version));
    }

    /// <summary>The captured values of a record as the catalog stores them: names in ordinal order, each value as captured.</summary>
    public static string FieldsJson(ReferenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = new JsonObject();
        foreach (var (name, value) in item.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            fields[name] = value.Node.DeepClone();
        }

        return fields.ToJsonString();
    }

    /// <summary>A declaration's fields as the sync stores them: <c>[{ "path": "data.Code", "as": "Code" }]</c>.</summary>
    public static IReadOnlyList<ReferenceFieldSpec> ParseFields(string json, string flowName, string typeName)
    {
        try
        {
            var fields = new List<ReferenceFieldSpec>();
            foreach (var field in (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? []).OfType<JsonObject>())
            {
                if (field["path"] is not JsonValue path || !path.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var name = field["as"] is JsonValue alias && alias.TryGetValue<string>(out var given) && !string.IsNullOrWhiteSpace(given) ? given : null;
                fields.Add(new ReferenceFieldSpec(text, name));
            }

            return fields;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException(
                $"The catalog's declaration of {typeName} by cache flow '{flowName}' holds fields that are not valid JSON ({ex.Message}); sync the repository again.", ex);
        }
    }

    /// <summary>One version with every record whose range covers its sequence, checked against the hash it was written with.</summary>
    private static async Task<ReferenceSnapshot> ReadAsync(OsduDbContext db, DeliveryCacheVersion row, CancellationToken ct)
    {
        var scope = row.Scope;
        var sequence = row.Sequence;
        var items = await db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == scope && i.FromSequence <= sequence && (i.ToSequence == null || i.ToSequence > sequence))
            .Select(i => new { i.TypeName, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var byType = items.GroupBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // The version row lists every type, a type that holds no record included, so the loaded version holds exactly the
        // types the merge left and hashes as it did.
        var types = ParseTypes(row.TypesJson, scope, row.Version)
            .Select(type => new ReferenceType(
                type.Name,
                type.EntityType,
                (byType.GetValueOrDefault(type.Name) ?? [])
                    .OrderBy(i => i.RecordId, StringComparer.Ordinal)
                    .Select(i => new ReferenceItem(i.RecordId, Fields(i.FieldsJson, scope, row.Version)))))
            .ToList();
        var snapshot = new ReferenceSnapshot(
            row.Version,
            new DateTimeOffset(DateTime.SpecifyKind(row.CapturedUtc, DateTimeKind.Utc)),
            types,
            ParseSystemProperties(row.SystemPropertiesJson, scope, row.Version));
        if (!string.Equals(snapshot.ContentHash(), row.ContentHash, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"Version {row.Version} of the cache of partition '{scope}' does not match the content hash it was written with: its records were altered after the version was written, so nothing renders against it.");
        }

        return snapshot;
    }

    /// <summary>
    /// Compares the new version against what the newest one holds, record by record over every type: a record that changed
    /// ends its open range and begins another, one that arrived begins one, and one that left ends its range.
    /// </summary>
    private static async Task WriteItemsAsync(OsduDbContext db, string scope, int sequence, IReadOnlyList<ReferenceType> types, CancellationToken ct)
    {
        var open = await db.DeliveryCacheItems.AsNoTracking()
            .Where(i => i.Scope == scope && i.ToSequence == null)
            .Select(i => new { i.ItemId, i.TypeName, i.EntityType, i.RecordId, i.FieldsJson })
            .ToListAsync(ct).ConfigureAwait(false);
        var held = open.ToDictionary(o => (Type: o.TypeName.ToUpperInvariant(), o.RecordId));

        var closing = new List<long>();
        var arriving = new List<DeliveryCacheItem>();
        var seen = new HashSet<(string Type, string RecordId)>();
        foreach (var type in types)
        {
            foreach (var item in type.Items)
            {
                var key = (Type: type.Name.ToUpperInvariant(), item.Id);
                seen.Add(key);
                var fields = FieldsJson(item);
                if (held.TryGetValue(key, out var existing))
                {
                    if (string.Equals(existing.FieldsJson, fields, StringComparison.Ordinal)
                        && string.Equals(existing.EntityType, type.EntityType, StringComparison.Ordinal)
                        && string.Equals(existing.TypeName, type.Name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    closing.Add(existing.ItemId);
                }

                arriving.Add(new DeliveryCacheItem
                {
                    Scope = scope,
                    TypeName = type.Name,
                    EntityType = type.EntityType,
                    RecordId = item.Id,
                    FieldsJson = fields,
                    Terms = Terms(item),
                    FromSequence = sequence,
                });
            }
        }

        closing.AddRange(open.Where(o => !seen.Contains((o.TypeName.ToUpperInvariant(), o.RecordId))).Select(o => o.ItemId));
        foreach (var chunk in closing.Chunk(UpdateChunk))
        {
            var ids = chunk.ToList();
            await db.DeliveryCacheItems
                .Where(i => ids.Contains(i.ItemId))
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.ToSequence, (int?)sequence), ct).ConfigureAwait(false);
        }

        foreach (var chunk in arriving.Chunk(InsertChunk))
        {
            db.DeliveryCacheItems.AddRange(chunk);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// The flow's membership of the captured types becomes exactly what it captured, under the names the cache holds the
    /// types by; a type the merge removed from the partition loses every flow's membership.
    /// </summary>
    private static async Task WriteMembersAsync(
        OsduDbContext db, string scope, string flowName, IReadOnlyList<ReferenceType> captured, CacheMergePlan plan, CancellationToken ct)
    {
        var typeNames = captured.Select(t => t.Name).ToList();
        await db.DeliveryCacheMembers
            .Where(m => m.Scope == scope && m.FlowName == flowName && typeNames.Contains(m.TypeName))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (plan.RemovedTypes.Count > 0)
        {
            var removed = plan.RemovedTypes.ToList();
            await db.DeliveryCacheMembers
                .Where(m => m.Scope == scope && removed.Contains(m.TypeName))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        var rows = captured.SelectMany(type =>
        {
            var name = plan.Snapshot.Type(type.Name)?.Name ?? type.Name;
            return type.Items.Select(item => new DeliveryCacheMember { Scope = scope, TypeName = name, RecordId = item.Id, FlowName = flowName });
        });
        foreach (var chunk in rows.Chunk(InsertChunk))
        {
            db.DeliveryCacheMembers.AddRange(chunk);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Every scalar the record holds, newline separated, so a text search over the cache is one predicate.</summary>
    private static string Terms(ReferenceItem item)
        => string.Join('\n', item.Fields.Values.SelectMany(v => v.Terms).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string TypesJson(IEnumerable<ReferenceType> types)
    {
        var array = new JsonArray();
        foreach (var type in types)
        {
            array.Add(new JsonObject { ["name"] = type.Name, ["entityType"] = type.EntityType, ["items"] = type.Items.Count });
        }

        return array.ToJsonString();
    }

    /// <summary>A version's system properties as the row stores them, in the order they are hashed.</summary>
    private static string SystemPropertiesJson(IEnumerable<SystemProperty> properties)
    {
        var array = new JsonArray();
        foreach (var property in SystemProperties.Ordered(properties))
        {
            var entry = new JsonObject
            {
                ["service"] = property.Service,
                ["name"] = property.Name,
                ["state"] = property.State.ToString(),
            };
            if (property.Source is { } source)
            {
                entry["source"] = source;
            }

            if (property.Detail is { } detail)
            {
                entry["detail"] = detail;
            }

            array.Add(entry);
        }

        return array.ToJsonString();
    }

    /// <summary>A version's system properties, read back; a state the row does not name is refused rather than guessed.</summary>
    private static List<SystemProperty> ParseSystemProperties(string json, string scope, string version)
    {
        try
        {
            var properties = new List<SystemProperty>();
            foreach (var entry in (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray ?? []).OfType<JsonObject>())
            {
                var service = entry["service"]?.GetValue<string>();
                var name = entry["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(name)
                    || SystemProperties.ParseState(entry["state"]?.GetValue<string>()) is not { } state)
                {
                    throw new DeliveryException(
                        $"Version {version} of the cache of partition '{scope}' lists a system property without a service, a name or a known state ({entry.ToJsonString()}).");
                }

                properties.Add(new SystemProperty(service, name, state, entry["source"]?.GetValue<string>(), entry["detail"]?.GetValue<string>()));
            }

            return properties;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeliveryException($"Version {version} of the cache of partition '{scope}' has system properties that are not valid JSON ({ex.Message}).", ex);
        }
    }

    private static List<CacheVersionType> ParseTypes(string json, string scope, string version)
    {
        try
        {
            return (JsonNode.Parse(json) as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(type => new CacheVersionType(
                    type["name"]?.GetValue<string>() ?? throw new DeliveryException($"Version {version} of the cache of partition '{scope}' lists a type without a name."),
                    type["entityType"]?.GetValue<string>() ?? string.Empty,
                    type["items"]?.GetValue<long>() ?? 0))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new DeliveryException($"Version {version} of the cache of partition '{scope}' has a type list that is not valid JSON ({ex.Message}).", ex);
        }
    }

    private static Dictionary<string, ReferenceValue> Fields(string json, string scope, string version)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A record of version {version} of the cache of partition '{scope}' holds values that are not valid JSON ({ex.Message}).", ex);
        }

        var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in node ?? [])
        {
            if (value is not null)
            {
                fields[name] = ReferenceValue.From(value);
            }
        }

        return fields;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    private ReferenceSnapshot? Recent(string scope, string version)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Scope, scope, StringComparison.Ordinal) && string.Equals(node.Value.Version, version, StringComparison.Ordinal))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return node.Value.Snapshot;
                }
            }
        }

        return null;
    }

    private void Remember(string scope, string version, ReferenceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_recent.Any(e => string.Equals(e.Scope, scope, StringComparison.Ordinal) && string.Equals(e.Version, version, StringComparison.Ordinal)))
            {
                return;
            }

            _recent.AddFirst((scope, version, snapshot));
            while (_recent.Count > RetainedVersions)
            {
                _recent.RemoveLast();
            }
        }
    }
}
