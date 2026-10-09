using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A type's own content hash: a version of a partition's cache moves whenever anything in the cache does, and the hash of
/// each type it holds is what says whether that type moved with it. It is taken over exactly the bytes the type contributes
/// to the whole hash, so the two can never disagree, and it follows what the type holds and nothing else.
/// </summary>
public sealed class CacheTypeHashTests
{
    private static ReferenceItem Item(string id, params (string Name, string Json)[] fields)
        => new(id, fields.ToDictionary(f => f.Name, f => ReferenceValue.From(JsonNode.Parse(f.Json)!), StringComparer.OrdinalIgnoreCase));

    private static ReferenceType Units(params (string Code, string Name)[] units) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        units.Select(u => Item("dev:reference-data--UnitOfMeasure:" + u.Code, ("Code", $"\"{u.Code}\""), ("Name", $"\"{u.Name}\""))));

    /// <summary>A type of every shape a capture keeps: text, a set, a nested object, a number, a flag, and text outside ASCII.</summary>
    private static ReferenceType Wellbores() => new(
        "Wellbore", "master-data--Wellbore",
        [
            Item("dev:master-data--Wellbore:1", ("FacilityName", "\"Wellbore Æøå Nord\""), ("NameAlias", """[{"AliasName":"A-1","AliasNameTypeID":"x"}]""")),
            Item("dev:master-data--Wellbore:2", ("FacilityName", "\"Wellbore A/1-19 ST\""), ("Depth", "1234.50"), ("Active", "true"), ("Aliases", """["b","a"]""")),
        ]);

    private static ReferenceType Lookup(params (string Key, string Value)[] rows) => new(
        "UnitAlias", ReferenceType.LookupEntityType("UnitAlias"),
        rows.Select(r => Item(r.Key, ("key", $"\"{r.Key}\""), ("value", $"\"{r.Value}\""))), key: "key");

    /// <summary>The same type built afresh, so nothing it computed before answers for it.</summary>
    private static ReferenceType Fresh(ReferenceType type) => new(type.Name, type.EntityType, type.Items.ToList(), type.Key);

    /// <summary>
    /// The whole hash as it was always taken: the canonical JSON of the whole document at once, which every stored version
    /// was written with, so a change to how it is streamed shows up here and not as versions that no longer load.
    /// </summary>
    private static string WholeAsAlways(ReferenceSnapshot snapshot)
    {
        var types = new JsonObject();
        foreach (var type in snapshot.Normalized().Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            types[type.Name] = type.ToJson();
        }

        if (snapshot.SystemProperties.Count == 0)
        {
            return ContentHash.Of(CanonicalJson.ToBytes(types));
        }

        var properties = new JsonArray(snapshot.SystemProperties
            .Select(p => (JsonNode)new JsonObject { ["service"] = p.Service, ["name"] = p.Name, ["state"] = p.State.ToString() })
            .ToArray());
        return ContentHash.Of(CanonicalJson.ToBytes(new JsonObject { ["systemProperties"] = properties, ["types"] = types }));
    }

    [Fact]
    public void A_type_hash_is_taken_over_exactly_the_bytes_the_type_contributes_to_the_whole()
    {
        // A type long enough to be drained part way through, beside one of every value shape, a lookup table, and a type
        // that holds nothing.
        var many = new ReferenceType("LogCurveType", "reference-data--LogCurveType",
            Enumerable.Range(0, 2600).Select(i => Item($"dev:reference-data--LogCurveType:{i:D5}", ("Code", $"\"C{i}\""))));
        var empty = new ReferenceType("LogType", "reference-data--LogType", []);
        ReferenceType[] types = [Units(("m", "metre"), ("ft", "foot")), Wellbores(), Lookup(("M", "m"), ("FT", "ft")), many, empty];
        var flags = new[] { new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, SystemPropertyState.Enabled, "dataPartition", null) };

        foreach (var snapshot in new[] { new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, types), new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, types.Select(Fresh), flags) })
        {
            var hashes = snapshot.Normalized().Hashes();
            Assert.Equal(WholeAsAlways(snapshot), hashes.Content);
            Assert.Equal(hashes.Content, snapshot.Normalized().ContentHash());
            Assert.Equal(types.Length, hashes.Types.Count);
            foreach (var type in types)
            {
                Assert.Equal(Fresh(type).ContentHash(), hashes.Of(type.Name));
                Assert.Equal(ContentHash.HexLength, hashes.Of(type.Name)!.Length);
            }
        }

        // The type is named as mappings name it, whatever the case.
        Assert.Equal(Fresh(types[0]).ContentHash(), new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, types).Hashes().Of("unitofmeasure"));
        Assert.Null(new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, types).Hashes().Of("Nothing"));
    }

    [Fact]
    public void A_type_hash_follows_what_the_type_holds_and_not_the_order_it_was_found_in_or_its_name()
    {
        var units = Units(("m", "metre"), ("ft", "foot"));
        var hash = units.ContentHash();

        // The order a capture found the records in, and the name the version keeps the type under, do not move it.
        Assert.Equal(hash, new ReferenceType(units.Name, units.EntityType, units.Items.Reverse()).ContentHash());
        Assert.Equal(hash, new ReferenceType("Units", units.EntityType, units.Items).ContentHash());
        Assert.Equal(
            new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [units]).ContentHash(),
            new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [new ReferenceType(units.Name, units.EntityType, units.Items.Reverse())]).ContentHash());

        // Everything the type holds does.
        Assert.NotEqual(hash, Units(("m", "Metre"), ("ft", "foot")).ContentHash());
        Assert.NotEqual(hash, Units(("m", "metre")).ContentHash());
        Assert.NotEqual(hash, Units(("m", "metre"), ("ft", "foot"), ("km", "kilometre")).ContentHash());
        Assert.NotEqual(hash, new ReferenceType(units.Name, "reference-data--UnitQuantity", units.Items).ContentHash());
        Assert.NotEqual(hash, new ReferenceType(units.Name, units.EntityType, []).ContentHash());
        Assert.NotEqual(
            Lookup(("M", "m")).ContentHash(),
            new ReferenceType("UnitAlias", ReferenceType.LookupEntityType("UnitAlias"), Lookup(("M", "m")).Items, key: "code").ContentHash());

        // A value's case is a change of it: OSDU codes that differ by case are different records.
        Assert.NotEqual(Lookup(("M", "m")).ContentHash(), Lookup(("M", "M")).ContentHash());
    }

    [Fact]
    public void Two_versions_compare_type_by_type_as_added_changed_unchanged_and_removed()
    {
        var units = Units(("m", "metre"));
        var wellbores = Wellbores();
        var first = CacheTypeChanges.Compare(null, new ReferenceSnapshot("v1", DateTimeOffset.UnixEpoch, [units, wellbores]));
        Assert.Equal(CacheTypeChange.Added, first.Of("UnitOfMeasure"));
        Assert.Equal(CacheTypeChange.Added, first.Of("Wellbore"));
        Assert.Empty(first.Removed);

        var previous = new ReferenceSnapshot("v1", DateTimeOffset.UnixEpoch, [units, wellbores, Lookup(("M", "m"))]);
        // The same content built afresh, as a later capture finds it, is unchanged.
        var current = new ReferenceSnapshot(
            "v2", DateTimeOffset.UnixEpoch.AddHours(1), [Fresh(units), Wellbores(), new ReferenceType("LogType", "reference-data--LogType", [])]);
        var changes = CacheTypeChanges.Compare(previous, current);
        Assert.Equal(CacheTypeChange.Unchanged, changes.Of("UnitOfMeasure"));
        Assert.Equal(CacheTypeChange.Unchanged, changes.Of("wellbore"));
        Assert.Equal(CacheTypeChange.Added, changes.Of("LogType"));
        Assert.Null(changes.Of("UnitAlias"));
        Assert.Equal(["UnitAlias"], changes.Removed);
        Assert.Equal(["LogType"], changes.Moved);

        var moved = CacheTypeChanges.Compare(previous, new ReferenceSnapshot("v2", DateTimeOffset.UnixEpoch, [Units(("m", "meter")), wellbores, Lookup(("M", "m"))]));
        Assert.Equal(CacheTypeChange.Changed, moved.Of("UnitOfMeasure"));
        Assert.Equal(CacheTypeChange.Unchanged, moved.Of("UnitAlias"));
        Assert.Equal(["UnitOfMeasure"], moved.Moved);

        // A name spelled another way is a change, since the records are stored under the name as spelled.
        var respelled = CacheTypeChanges.Compare(previous, new ReferenceSnapshot("v2", DateTimeOffset.UnixEpoch, [new ReferenceType("unitOfMeasure", units.EntityType, units.Items)]));
        Assert.Equal(CacheTypeChange.Changed, respelled.Of("UnitOfMeasure"));

        var none = CacheTypeChanges.None(previous);
        Assert.All(previous.Types, t => Assert.Equal(CacheTypeChange.Unchanged, none.Of(t.Name)));
        Assert.Empty(none.Moved);
        Assert.Empty(none.Removed);
    }

    [Fact]
    public void A_change_reads_back_only_as_it_is_written()
    {
        foreach (var change in Enum.GetValues<CacheTypeChange>())
        {
            Assert.Equal(change, CacheTypeChanges.Parse(CacheTypeChanges.Text(change)));
        }

        Assert.Null(CacheTypeChanges.Parse("Changed"));
        Assert.Null(CacheTypeChanges.Parse("removed"));
        Assert.Null(CacheTypeChanges.Parse(null));
    }
}

/// <summary>
/// What a version of a partition's cache records of each type it holds (its content hash, how it compares with the version
/// before, and the version its content dates from), what a merge leaves alone because a type did not move, how a load checks
/// each type against its own hash, how versions written before types were hashed are read and dated, and the history that
/// names the types each version moved.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class CacheTypeVersionTests : IDisposable
{
    private const string Scope = "dev";

    private const string Flow = "reference-cache";

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CacheCapture Capture = new(null, "manual:tester", "${env:OSDU_URL}");

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private static ReferenceType Units(params (string Code, string Name)[] units) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        units.Select(u => ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:" + u.Code, new Dictionary<string, string> { ["Code"] = u.Code, ["Name"] = u.Name })));

    private static ReferenceType Wellbores(params string[] keys) => new(
        "Wellbore", "master-data--Wellbore",
        keys.Select(key => ReferenceItem.FromText("dev:master-data--Wellbore:" + key, new Dictionary<string, string> { ["FacilityName"] = "Wellbore " + key })));

    private static ReferenceType LogTypes() => new("LogType", "reference-data--LogType", []);

    private static IReadOnlyList<SystemPropertyReading> KeywordLower(SystemPropertyState state)
        => [SystemPropertyReading.Read(SystemProperties.Indexer, [new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, state, "dataPartition", null)])];

    private static CacheVersionType TypeOf(CacheVersionInfo version, string name) => Assert.Single(version.Types, t => t.Name == name);

    [Fact]
    public async Task A_version_records_each_type_s_hash_how_it_moved_and_the_version_its_content_dates_from()
    {
        var store = _catalog.Caches();
        var first = await store.MergeAsync(Scope, Flow, [Units(("m", "metre"), ("ft", "foot")), Wellbores("A")], Capture, T0);
        Assert.Equal(CacheTypeChange.Added, first.Changes.Of("UnitOfMeasure"));
        Assert.Equal(CacheTypeChange.Added, first.Changes.Of("Wellbore"));

        // The metre is renamed: the version moves, and of its types only the units moved with it.
        var second = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre"), ("ft", "foot")), Wellbores("A")], Capture, T0.AddHours(1));
        Assert.True(second.Written);
        Assert.Equal(CacheTypeChange.Changed, second.Changes.Of("UnitOfMeasure"));
        Assert.Equal(CacheTypeChange.Unchanged, second.Changes.Of("Wellbore"));
        Assert.Equal(["UnitOfMeasure"], second.Changes.Moved);

        var third = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre"), ("ft", "foot")), Wellbores("A", "B")], Capture, T0.AddHours(2));
        Assert.Equal(["Wellbore"], third.Changes.Moved);

        var reader = _catalog.Caches();
        var versions = await reader.ListVersionsAsync(Scope);
        Assert.Equal([third.Snapshot.Version, second.Snapshot.Version, first.Snapshot.Version], versions.Select(v => v.Version));
        var (v3, v2, v1) = (versions[0], versions[1], versions[2]);

        Assert.Equal(CacheTypeChange.Added, TypeOf(v1, "UnitOfMeasure").Change);
        Assert.Equal(v1.Version, TypeOf(v1, "Wellbore").Since);
        Assert.Equal(CacheTypeChange.Changed, TypeOf(v2, "UnitOfMeasure").Change);
        Assert.Equal(v2.Version, TypeOf(v2, "UnitOfMeasure").Since);
        Assert.Equal(CacheTypeChange.Unchanged, TypeOf(v2, "Wellbore").Change);
        Assert.Equal(v1.Version, TypeOf(v2, "Wellbore").Since);
        Assert.Equal(CacheTypeChange.Unchanged, TypeOf(v3, "UnitOfMeasure").Change);
        Assert.Equal(v2.Version, TypeOf(v3, "UnitOfMeasure").Since);
        Assert.Equal(CacheTypeChange.Changed, TypeOf(v3, "Wellbore").Change);
        Assert.Equal(v3.Version, TypeOf(v3, "Wellbore").Since);

        // The hash a version records of a type is the type's own, and an unchanged type records the same hash again.
        Assert.Equal(second.Snapshot.Type("UnitOfMeasure")!.ContentHash(), TypeOf(v2, "UnitOfMeasure").Hash);
        Assert.Equal(TypeOf(v1, "Wellbore").Hash, TypeOf(v2, "Wellbore").Hash);
        Assert.Equal(TypeOf(v2, "UnitOfMeasure").Hash, TypeOf(v3, "UnitOfMeasure").Hash);
        Assert.NotEqual(TypeOf(v2, "Wellbore").Hash, TypeOf(v3, "Wellbore").Hash);

        // A type that did not move is not rewritten: its records' ranges run on from the version that last changed it.
        await using (var db = _catalog.CreateDbContext())
        {
            var wellbore = await db.DeliveryCacheItems.SingleAsync(i => i.Scope == Scope && i.RecordId == "dev:master-data--Wellbore:A");
            Assert.Equal(1, wellbore.FromSequence);
            Assert.Null(wellbore.ToSequence);
            Assert.Equal(2, await db.DeliveryCacheItems.CountAsync(i => i.Scope == Scope && i.TypeName == "UnitOfMeasure" && i.ToSequence == null));
            Assert.Equal(0, await db.DeliveryCacheItems.CountAsync(i => i.Scope == Scope && i.TypeName == "UnitOfMeasure" && i.FromSequence == 3));
        }

        // Every version still loads, each type checked against its own hash.
        foreach (var version in versions)
        {
            Assert.NotNull(await _catalog.Caches().LoadAsync(Scope, version.Version));
        }
    }

    [Fact]
    public async Task A_version_written_for_the_partition_s_system_properties_alone_moves_no_type_and_writes_no_record()
    {
        var store = _catalog.Caches();
        await store.MergeAsync(Scope, Flow, [Units(("m", "metre")), Wellbores("A")], Capture, T0, KeywordLower(SystemPropertyState.Disabled));

        var flipped = await store.MergeAsync(Scope, Flow, [Units(("m", "metre")), Wellbores("A")], Capture, T0.AddHours(1), KeywordLower(SystemPropertyState.Enabled));
        Assert.True(flipped.Written);
        Assert.Empty(flipped.Changes.Moved);
        Assert.Empty(flipped.Changes.Removed);
        Assert.All(flipped.Snapshot.Types, t => Assert.Equal(CacheTypeChange.Unchanged, flipped.Changes.Of(t.Name)));

        await using (var db = _catalog.CreateDbContext())
        {
            Assert.Equal(0, await db.DeliveryCacheItems.CountAsync(i => i.Scope == Scope && (i.FromSequence == 2 || i.ToSequence == 2)));
        }

        var version = Assert.Single(await _catalog.Caches().ListVersionsAsync(Scope), v => v.Current);
        Assert.All(version.Types, t => Assert.Equal(CacheTypeChange.Unchanged, t.Change));
        var loaded = await _catalog.Caches().LoadAsync(Scope, version.Version);
        Assert.True(SystemProperties.KeywordLowerOn(loaded!.SystemProperties));
    }

    [Fact]
    public async Task A_type_whose_records_or_recorded_hash_were_altered_is_named_when_its_version_loads()
    {
        var write = await _catalog.Caches().MergeAsync(Scope, Flow, [Units(("m", "metre")), Wellbores("A")], Capture, T0);

        // A record altered in place: the version refuses to load, and names the type the record belongs to.
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryCacheItems
                .Where(i => i.Scope == Scope && i.RecordId == "dev:master-data--Wellbore:A")
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.FieldsJson, """{"FacilityName":"Wellbore B"}"""));
        }

        var altered = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version));
        Assert.Contains("the records of Wellbore no longer match", altered.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitOfMeasure", altered.Message, StringComparison.Ordinal);

        // A recorded hash that is not the type's: the records are as written, but the version no longer vouches for them.
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryCacheItems
                .Where(i => i.Scope == Scope && i.RecordId == "dev:master-data--Wellbore:A")
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.FieldsJson, """{"FacilityName":"Wellbore A"}"""));
            var row = await db.DeliveryCacheVersions.SingleAsync(v => v.Scope == Scope);
            var unitsHash = write.Snapshot.Type("UnitOfMeasure")!.ContentHash();
            row.TypesJson = row.TypesJson.Replace(unitsHash, new string('0', ContentHash.HexLength), StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var misrecorded = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version));
        Assert.Contains("the records of UnitOfMeasure no longer match", misrecorded.Message, StringComparison.Ordinal);

        // Something the store never writes is refused rather than taken on trust.
        await using (var db = _catalog.CreateDbContext())
        {
            var row = await db.DeliveryCacheVersions.SingleAsync(v => v.Scope == Scope);
            row.TypesJson = row.TypesJson.Replace(new string('0', ContentHash.HexLength), "not-a-hash", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var unreadable = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().ListVersionsAsync(Scope));
        Assert.Contains("records a hash for UnitOfMeasure that is not a content hash", unreadable.Message, StringComparison.Ordinal);

        await using (var db = _catalog.CreateDbContext())
        {
            var row = await db.DeliveryCacheVersions.SingleAsync(v => v.Scope == Scope);
            row.TypesJson = row.TypesJson.Replace("\"not-a-hash\"", $"\"{write.Snapshot.Type("UnitOfMeasure")!.ContentHash()}\"", StringComparison.Ordinal)
                .Replace("\"added\"", "\"moved\"", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => _catalog.Caches().ListVersionsAsync(Scope));
        Assert.Contains("which is not a change (added, changed or unchanged)", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_written_before_types_were_hashed_still_loads_and_the_next_one_dates_its_types_from_the_records()
    {
        var store = _catalog.Caches();
        var v1 = await store.MergeAsync(Scope, Flow, [Units(("m", "metre")), LogTypes()], Capture, T0, KeywordLower(SystemPropertyState.Disabled));
        var v2 = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre")), LogTypes(), Wellbores("A")], Capture, T0.AddHours(1), KeywordLower(SystemPropertyState.Disabled));

        // Both versions as they were written before types were hashed: each type its name, entity type and count alone.
        await using (var db = _catalog.CreateDbContext())
        {
            foreach (var row in await db.DeliveryCacheVersions.Where(v => v.Scope == Scope).ToListAsync())
            {
                var legacy = new JsonArray();
                foreach (var type in (JsonNode.Parse(row.TypesJson) as JsonArray)!.OfType<JsonObject>())
                {
                    legacy.Add(new JsonObject { ["name"] = type["name"]!.GetValue<string>(), ["entityType"] = type["entityType"]!.GetValue<string>(), ["items"] = type["items"]!.GetValue<long>() });
                }

                row.TypesJson = legacy.ToJsonString();
            }

            await db.SaveChangesAsync();
        }

        var old = await _catalog.Caches().ListVersionsAsync(Scope);
        Assert.All(old.SelectMany(v => v.Types), t => Assert.True(t.Hash is null && t.Change is null && t.Since is null));
        Assert.NotNull(await _catalog.Caches().LoadAsync(Scope, v2.Snapshot.Version));

        // The history reads them from what the records and the versions hold.
        var legacyHistory = await ReadHistoryAsync(null);
        Assert.Equal([("LogType", "added"), ("UnitOfMeasure", "added")], Moved(legacyHistory[v1.Snapshot.Version]));
        Assert.Equal([("UnitOfMeasure", "changed"), ("Wellbore", "added")], Moved(legacyHistory[v2.Snapshot.Version]));
        Assert.All(legacyHistory.Values.SelectMany(h => h.Types), t => Assert.Null(t.Hash));
        Assert.Equal(1, Assert.Single(legacyHistory[v2.Snapshot.Version].Types, t => t.TypeName == "Wellbore").Items);

        // A version written for the system properties alone: every type is unchanged, and each is dated from what the
        // partition holds, the units from the version that renamed the metre, the wellbores from the version that brought them,
        // and the empty log types, which no record dates, from the version they arrived with.
        var v3 = await _catalog.Caches().MergeAsync(Scope, Flow, [Units(("m", "Metre")), LogTypes(), Wellbores("A")], Capture, T0.AddHours(2), KeywordLower(SystemPropertyState.Enabled));
        Assert.True(v3.Written);
        Assert.Empty(v3.Changes.Moved);
        var current = Assert.Single(await _catalog.Caches().ListVersionsAsync(Scope), v => v.Current);
        Assert.Equal(v2.Snapshot.Version, TypeOf(current, "UnitOfMeasure").Since);
        Assert.Equal(v2.Snapshot.Version, TypeOf(current, "Wellbore").Since);
        Assert.Equal(v1.Snapshot.Version, TypeOf(current, "LogType").Since);
        Assert.All(current.Types, t => Assert.Equal(CacheTypeChange.Unchanged, t.Change));

        // From there on a type carries its date forward without reading the records again.
        var v4 = await _catalog.Caches().MergeAsync(Scope, Flow, [Units(("m", "Metre")), LogTypes(), Wellbores("A", "B")], Capture, T0.AddHours(3), KeywordLower(SystemPropertyState.Enabled));
        var newest = Assert.Single(await _catalog.Caches().ListVersionsAsync(Scope), v => v.Current);
        Assert.Equal(v4.Snapshot.Version, newest.Version);
        Assert.Equal(v2.Snapshot.Version, TypeOf(newest, "UnitOfMeasure").Since);
        Assert.Equal(v4.Snapshot.Version, TypeOf(newest, "Wellbore").Since);
        Assert.Equal(v1.Snapshot.Version, TypeOf(newest, "LogType").Since);
    }

    [Fact]
    public async Task The_history_names_the_types_each_version_moved_and_narrows_to_one_type()
    {
        var units = new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure", EntityType = "reference-data--UnitOfMeasure", Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
            Fields = [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name")],
        };
        var wellbores = new ReferenceTypeSpec
        {
            Name = "Wellbore", EntityType = "master-data--Wellbore", Kind = "osdu:wks:master-data--Wellbore:*",
            Fields = [new ReferenceFieldSpec("data.FacilityName")],
        };
        await _catalog.DeclareCacheAsync(Scope, Flow, units, wellbores);
        var store = _catalog.Caches();
        var v1 = await store.MergeAsync(Scope, Flow, [Units(("m", "metre"), ("ft", "foot")), Wellbores("A")], Capture, T0, KeywordLower(SystemPropertyState.Disabled));
        var v2 = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre"), ("ft", "foot")), Wellbores("A")], Capture, T0.AddHours(1), KeywordLower(SystemPropertyState.Disabled));
        var v3 = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre"), ("ft", "foot")), Wellbores("A")], Capture, T0.AddHours(2), KeywordLower(SystemPropertyState.Enabled));

        // The wellbores are no longer declared, so the next version leaves them behind.
        await _catalog.DeclareCacheAsync(Scope, Flow, units);
        var v4 = await store.MergeAsync(Scope, Flow, [Units(("m", "Metre"), ("ft", "foot"))], Capture, T0.AddHours(3), KeywordLower(SystemPropertyState.Enabled));
        Assert.Equal(["Wellbore"], v4.Changes.Removed);

        var history = await ReadHistoryAsync(null);
        Assert.Equal([("UnitOfMeasure", "added"), ("Wellbore", "added")], Moved(history[v1.Snapshot.Version]));
        Assert.Equal([("UnitOfMeasure", "changed")], Moved(history[v2.Snapshot.Version]));
        Assert.Empty(history[v3.Snapshot.Version].Types);
        Assert.Equal(new CacheChangeCounts(0, 0, 0), history[v3.Snapshot.Version].Changes);
        Assert.Equal([("Wellbore", "removed")], Moved(history[v4.Snapshot.Version]));
        var left = Assert.Single(history[v4.Snapshot.Version].Types);
        Assert.Equal(new CacheChangeCounts(0, 0, 1), left.Counts);
        Assert.Null(left.Hash);
        Assert.Equal(0, left.Items);
        var renamed = Assert.Single(history[v2.Snapshot.Version].Types);
        Assert.Equal(new CacheChangeCounts(1, 0, 0), renamed.Counts);
        Assert.Equal(v1.Snapshot.Version, history[v2.Snapshot.Version].Before);

        // Each version that moved a type is a version of the type: its hash there, never the one the version before held.
        Assert.Equal(v2.Snapshot.Type("UnitOfMeasure")!.ContentHash(), renamed.Hash);
        Assert.Equal(2, renamed.Items);
        var arrived = Assert.Single(history[v1.Snapshot.Version].Types, t => t.TypeName == "UnitOfMeasure");
        Assert.Equal(v1.Snapshot.Type("UnitOfMeasure")!.ContentHash(), arrived.Hash);
        Assert.NotEqual(arrived.Hash, renamed.Hash);

        // Narrowed to the wellbores, only the versions that moved them name anything.
        var narrowed = await ReadHistoryAsync("Wellbore");
        Assert.Equal([("Wellbore", "added")], Moved(narrowed[v1.Snapshot.Version]));
        Assert.Empty(narrowed[v2.Snapshot.Version].Types);
        Assert.Empty(narrowed[v3.Snapshot.Version].Types);
        Assert.Equal([("Wellbore", "removed")], Moved(narrowed[v4.Snapshot.Version]));
        Assert.Equal(new CacheChangeCounts(0, 0, 1), narrowed[v4.Snapshot.Version].Changes);
        Assert.Equal(new CacheChangeCounts(0, 0, 0), narrowed[v2.Snapshot.Version].Changes);
    }

    private async Task<Dictionary<string, CacheHistoryEntry>> ReadHistoryAsync(string? type)
    {
        await using var db = _catalog.CreateDbContext();
        return (await CacheVersions.HistoryAsync(db, Scope, type)).ToDictionary(h => h.Version.Version, StringComparer.Ordinal);
    }

    private static List<(string, string)> Moved(CacheHistoryEntry entry) => entry.Types.Select(t => (t.TypeName, t.Change)).ToList();
}
