using System.Net;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A cache version's label enters the render context, so writing one for a capture that found nothing new would change
/// every record's metadata hash and redeliver everything built from the cache. Template versions are content-addressed and
/// already immune; these cover the same property for cache versions, and what an import has to agree with.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class SnapshotVersioningTests : IDisposable
{
    private const string Scope = "dev";

    private const string Flow = "units";

    private static readonly CacheCapture Capture = new(null, "tests", "files");

    private static readonly ReferenceTypeSpec Units = new()
    {
        Name = "UnitOfMeasure",
        EntityType = "reference-data--UnitOfMeasure",
        Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
        Fields = [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name"), new ReferenceFieldSpec("data.ID")],
    };

    private static readonly ReferenceTypeSpec Wellbores = new()
    {
        Name = "Wellbore",
        EntityType = "master-data--Wellbore",
        Kind = "osdu:wks:master-data--Wellbore:*",
        Fields = [new ReferenceFieldSpec("data.FacilityName")],
    };

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private (SnapshotBuilder Builder, OsduCacheStore Store, TestClock Clock) NewBuilder()
    {
        var clock = new TestClock();
        var store = _catalog.Caches();
        return (new SnapshotBuilder(store, Scope, Flow, clock, Samples.Logger<SnapshotBuilder>()), store, clock);
    }

    private static string ReferenceDirectory(params (string Type, string Body)[] files)
    {
        var directory = Samples.NewTempDirectory();
        foreach (var (type, body) in files)
        {
            File.WriteAllText(Path.Combine(directory, type + ".json"), body);
        }

        return directory;
    }

    private const string OneItem = """
        {
          "entityType": "reference-data--UnitOfMeasure",
          "items": [ { "id": "dev:reference-data--UnitOfMeasure:m", "Code": "m", "Name": "metre", "ID": "m" } ]
        }
        """;

    private const string TwoItems = """
        {
          "entityType": "reference-data--UnitOfMeasure",
          "items": [
            { "id": "dev:reference-data--UnitOfMeasure:m", "Code": "m", "Name": "metre", "ID": "m" },
            { "id": "dev:reference-data--UnitOfMeasure:ft", "Code": "ft", "Name": "foot", "ID": "ft" }
          ]
        }
        """;

    private const string OneWellbore = """
        { "entityType": "master-data--Wellbore", "items": [ { "id": "dev:master-data--Wellbore:A", "FacilityName": "NO 1/1-A" } ] }
        """;

    [Fact]
    public async Task Recapturing_identical_reference_data_keeps_the_current_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ImportDirectoryAsync(directory, [Units], Capture);
        Assert.True(first.Written);
        Assert.Equal(first.Snapshot.Version, await store.CurrentVersionAsync(Scope));

        // A later recapture of identical content must not write a version: the version is a timestamp, and a new one
        // would move the render context and re-render every record that reads the cache.
        clock.Advance(TimeSpan.FromHours(6));
        var again = await builder.ImportDirectoryAsync(directory, [Units], Capture);
        Assert.False(again.Written);
        Assert.Equal(first.Snapshot.Version, again.Snapshot.Version);
        Assert.Equal(first.Snapshot.ContentHash(), again.Snapshot.ContentHash());
        Assert.Equal(first.Snapshot.Version, await store.CurrentVersionAsync(Scope));
        Assert.Single(await store.ListVersionsAsync(Scope));
    }

    [Fact]
    public async Task A_reference_change_does_write_a_new_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ImportDirectoryAsync(directory, [Units], Capture);

        clock.Advance(TimeSpan.FromHours(6));
        File.WriteAllText(Path.Combine(directory, "UnitOfMeasure.json"), TwoItems);
        var second = await builder.ImportDirectoryAsync(directory, [Units], Capture);

        Assert.True(second.Written);
        Assert.NotEqual(first.Snapshot.Version, second.Snapshot.Version);
        Assert.NotEqual(first.Snapshot.ContentHash(), second.Snapshot.ContentHash());
        Assert.Equal(second.Snapshot.Version, await store.CurrentVersionAsync(Scope));
        var versions = await store.ListVersionsAsync(Scope);
        Assert.Equal(2, versions.Count);
        Assert.Equal(first.Snapshot.Version, versions[0].PreviousVersion);
    }

    [Fact]
    public async Task A_type_no_synced_cache_flow_declares_any_more_leaves_with_the_next_version()
    {
        // The partition's cache holds what its synced cache flows declare: a type taken out of every flow is not carried forward.
        var (builder, store, clock) = NewBuilder();
        await _catalog.DeclareCacheAsync(Scope, Flow, Units, Wellbores);
        var both = ReferenceDirectory(("UnitOfMeasure", OneItem), ("Wellbore", OneWellbore));
        var first = await builder.ImportDirectoryAsync(both, [Units, Wellbores], Capture);
        Assert.True(first.Snapshot.HasType("Wellbore"));

        clock.Advance(TimeSpan.FromHours(1));
        await _catalog.DeclareCacheAsync(Scope, Flow, Units);
        var unitsOnly = ReferenceDirectory(("UnitOfMeasure", OneItem));
        var second = await builder.ImportDirectoryAsync(unitsOnly, [Units], Capture);

        Assert.True(second.Written);
        var current = await store.LoadAsync(Scope, (await store.CurrentVersionAsync(Scope))!);
        Assert.False(current!.HasType("Wellbore"));
        Assert.True(current.HasType("UnitOfMeasure"));
    }

    [Fact]
    public async Task A_type_a_capture_does_not_cover_stays_while_a_synced_flow_still_declares_it()
    {
        // Another project's flow may be what fills a type, so a capture that does not cover it leaves it as it is.
        var (builder, store, clock) = NewBuilder();
        await _catalog.DeclareCacheAsync(Scope, Flow, Units);
        await _catalog.DeclareCacheAsync(Scope, "wellbores", Wellbores);
        var wellbores = new SnapshotBuilder(store, Scope, "wellbores", clock, Samples.Logger<SnapshotBuilder>());
        await wellbores.ImportDirectoryAsync(ReferenceDirectory(("Wellbore", OneWellbore)), [Wellbores], Capture);

        clock.Advance(TimeSpan.FromHours(1));
        var units = await builder.ImportDirectoryAsync(ReferenceDirectory(("UnitOfMeasure", OneItem)), [Units], Capture);

        Assert.True(units.Written);
        Assert.True(units.Snapshot.HasType("Wellbore"));
        Assert.True(units.Snapshot.HasType("UnitOfMeasure"));
        Assert.Equal(["units", "wellbores"], (await store.ListVersionsAsync(Scope)).Select(v => v.FlowName));
    }

    [Fact]
    public async Task A_capture_widened_to_the_partition_s_declaration_fills_every_path_any_flow_of_the_partition_keeps()
    {
        // Project A caches a wellbore's facility name; project B also asks for its aliases. A's refresh fetches both, so a
        // wellbore A captured answers B's mapping too.
        var facility = new ReferenceFieldSpec("data.FacilityName");
        var alias = new ReferenceFieldSpec("data.NameAlias.AliasName", "Alias");
        var projectA = Wellbores with { Fields = [facility] };
        await _catalog.DeclareCacheAsync(Scope, "project-a", projectA);
        await _catalog.DeclareCacheAsync(Scope, "project-b", Wellbores with { Fields = [facility, alias] });
        var store = _catalog.Caches();
        var declaration = await store.DeclarationAsync(Scope);
        Assert.Equal(["FacilityName", "Alias"], declaration.FieldsOf("Wellbore").Select(f => f.Name));
        Assert.Equal(["project-a", "project-b"], declaration.Of("Wellbore").Select(d => d.FlowName));

        var widened = declaration.Widen(projectA);
        Assert.Equal(["data.FacilityName", "data.NameAlias.AliasName"], widened.Fields.Select(f => f.Path));

        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/query_with_cursor",
            HttpStatusCode.OK,
            """{"results":[{"id":"dev:master-data--Wellbore:A","data":{"FacilityName":"NO 1/1-A","NameAlias":[{"AliasName":"1/1-A"},{"AliasName":"WELL A"}]}}]}""");
        using var osdu = await OsduConnection.CreateAsync(
            "http://localhost/osdu",
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = Scope },
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]),
            handler,
            allowLoopback: true);
        var builder = new SnapshotBuilder(store, Scope, "project-a", new TestClock(), Samples.Logger<SnapshotBuilder>());
        var write = await builder.CaptureAsync(osdu, new ReferenceCaptureSpec { Types = [widened] }, Capture);

        // The type's search, beside the two info requests every capture makes for the partition's system properties.
        var search = Assert.Single(handler.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
        Assert.Contains("data.NameAlias.AliasName", search.Body, StringComparison.Ordinal);
        Assert.True(write.Written);
        var wellbore = (await _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version))!.Type("Wellbore")!.Items.Single();
        Assert.Equal("NO 1/1-A", wellbore.Fields["FacilityName"].Text);
        Assert.Equal(["1/1-A", "WELL A"], wellbore.Fields["Alias"].Terms.Order(StringComparer.Ordinal));
        Assert.Equal("project-a", (await store.ListVersionsAsync(Scope)).Single().FlowName);
    }

    [Fact]
    public async Task An_import_holding_what_the_cache_does_not_declare_writes_nothing()
    {
        var (builder, store, _) = NewBuilder();

        var undeclaredType = ReferenceDirectory(("UnitOfMeasure", OneItem), ("Wellbore", OneWellbore));
        var type = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(undeclaredType, [Units], Capture));
        Assert.Contains("holds a type cache flow 'units' does not declare", type.Message, StringComparison.Ordinal);

        var missing = ReferenceDirectory(("UnitOfMeasure", OneItem));
        var absent = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(missing, [Units, Wellbores], Capture));
        Assert.Contains("Wellbore.json is missing", absent.Message, StringComparison.Ordinal);

        var uncaptured = ReferenceDirectory(("UnitOfMeasure", OneItem.Replace("\"ID\": \"m\"", "\"ID\": \"m\", \"Symbol\": \"m\"", StringComparison.Ordinal)));
        var field = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(uncaptured, [Units], Capture));
        Assert.Contains("values under 'Symbol'", field.Message, StringComparison.Ordinal);

        Assert.Empty(await store.ListVersionsAsync(Scope));
    }

    [Fact]
    public async Task An_import_holds_only_records_of_the_declared_entity_type_in_the_partition_and_never_a_lookup_table()
    {
        var (builder, store, _) = NewBuilder();
        var lookup = new ReferenceTypeSpec { Name = "RecallUnits", EntityType = ReferenceType.LookupEntityType("RecallUnits"), Origin = CacheOrigin.Dictionary, Dictionary = "RecallUnits" };

        // A lookup table the flow declares is filled from its origin, so an import neither needs nor takes it.
        var without = await builder.ImportDirectoryAsync(ReferenceDirectory(("UnitOfMeasure", OneItem)), [Units, lookup], Capture);
        Assert.True(without.Written);
        var offered = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(
            ReferenceDirectory(("UnitOfMeasure", OneItem), ("RecallUnits", """{ "entityType": "lookup--RecallUnits", "key": "key", "items": [ { "id": "M", "key": "M", "value": "m" } ] }""")),
            [Units, lookup],
            Capture));
        Assert.Contains("RecallUnits.json holds RecallUnits, which cache flow 'units' fills from dictionary RecallUnits; a lookup table is captured from its origin, never imported", offered.Message, StringComparison.Ordinal);

        // Records that are not OSDU records of the declared entity type in this partition are hand-made rows, and refused.
        foreach (var id in new[] { "M", "other:reference-data--UnitOfMeasure:m", "dev:reference-data--UnitQuantity:length", "dev:reference-data--UnitOfMeasure:" })
        {
            var foreign = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(
                ReferenceDirectory(("UnitOfMeasure", OneItem.Replace("dev:reference-data--UnitOfMeasure:m", id, StringComparison.Ordinal))), [Units], Capture));
            Assert.Contains($"whose ids are not ids of reference-data--UnitOfMeasure records in partition 'dev' ({id})", foreign.Message, StringComparison.Ordinal);
        }

        Assert.Single(await store.ListVersionsAsync(Scope));
    }

    private const string AliasedWellbore = """
        {
          "entityType": "master-data--Wellbore",
          "items": [ { "id": "dev:master-data--Wellbore:A", "FacilityName": "NO 1/1-A", "Alias": ["1/1-A", "WELL A"] } ]
        }
        """;

    [Fact]
    public async Task An_import_that_lacks_a_value_another_flow_of_the_partition_keeps_is_refused_and_one_carrying_it_keeps_it()
    {
        // Project B keeps the aliases of the wellbores both projects cache. Project A imports wellbores: a record it imports
        // replaces the cached record whole, so files without the aliases would drop B's values from every wellbore.
        var facility = new ReferenceFieldSpec("data.FacilityName");
        var projectA = Wellbores with { Fields = [facility] };
        await _catalog.DeclareCacheAsync(Scope, "project-a", projectA);
        await _catalog.DeclareCacheAsync(Scope, "project-b", Wellbores with { Fields = [facility, new ReferenceFieldSpec("data.NameAlias.AliasName", "Alias")] });
        var store = _catalog.Caches();
        var builder = new SnapshotBuilder(store, Scope, "project-a", new TestClock(), Samples.Logger<SnapshotBuilder>());

        var lacking = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(ReferenceDirectory(("Wellbore", OneWellbore)), [projectA], Capture));
        Assert.Contains(
            "Wellbore.json holds no values under 'Alias' (from data.NameAlias.AliasName, kept by cache flow 'project-b'), which the cache of partition 'dev' keeps for Wellbore",
            lacking.Message,
            StringComparison.Ordinal);
        Assert.Empty(await store.ListVersionsAsync(Scope));

        // Files carrying the value another flow keeps are what the partition's cache holds, and the merge keeps it.
        var carrying = await builder.ImportDirectoryAsync(ReferenceDirectory(("Wellbore", AliasedWellbore)), [projectA], Capture);
        Assert.True(carrying.Written);
        var wellbore = (await _catalog.Caches().LoadAsync(Scope, carrying.Snapshot.Version))!.Type("Wellbore")!.Items.Single();
        Assert.Equal("NO 1/1-A", wellbore.Fields["FacilityName"].Text);
        Assert.Equal(["1/1-A", "WELL A"], wellbore.Fields["Alias"].Terms.Order(StringComparer.Ordinal));
        Assert.Equal("project-a", Assert.Single(await store.ListVersionsAsync(Scope)).FlowName);

        // A type the files hold no record of replaces nothing, so it needs no value.
        var empty = await builder.ImportDirectoryAsync(ReferenceDirectory(("Wellbore", """{ "entityType": "master-data--Wellbore", "items": [] }""")), [projectA], Capture);
        Assert.Empty(empty.Snapshot.Type("Wellbore")!.Items);
    }

    [Fact]
    public async Task An_import_by_a_flow_that_disagrees_with_another_flow_of_the_partition_writes_nothing()
    {
        await _catalog.DeclareCacheAsync(Scope, "project-b", Wellbores);
        var disagreeing = Wellbores with { Fields = [new ReferenceFieldSpec("data.Name", "FacilityName")] };
        var builder = new SnapshotBuilder(_catalog.Caches(), Scope, "project-c", new TestClock(), Samples.Logger<SnapshotBuilder>());

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(ReferenceDirectory(("Wellbore", OneWellbore)), [disagreeing], Capture));
        Assert.Contains("Cache flow 'project-c' disagrees with another cache flow of partition 'dev'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Wellbore.FacilityName is cached from data.FacilityName by cache flow 'project-b' and from data.Name by 'project-c'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await _catalog.Caches().ListVersionsAsync(Scope));
    }

    [Fact]
    public async Task A_template_version_is_the_content_hash_so_capturing_the_same_schema_again_changes_nothing()
    {
        var clock = new TestClock();
        var kind = "osdu:wks:work-product-component--WellLog:1.4.0";
        var root = Samples.NewTempDirectory();
        var directory = Path.Combine(root, "work-product-component");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "WellLog.1.4.0.json"),
            """{"$id":"WellLog.1.4.0.json","type":"object","properties":{"data":{"type":"object","properties":{"Name":{"type":"string"}}}}}""");

        var first = await TemplateSources.FromDirectoryAsync(root, kind, clock);
        clock.Advance(TimeSpan.FromDays(1));
        var again = await TemplateSources.FromDirectoryAsync(root, kind, clock);

        Assert.Equal(first.Version, again.Version);
    }
}

/// <summary>
/// Paging the search index for a cache capture. The service hands back a cursor for the page after the last one as well,
/// and that page is empty, so ending only on a null cursor is how a capture pages forever.
/// </summary>
public class ReferenceCaptureCursorTests
{
    private static readonly ReferenceTypeSpec Spec = new()
    {
        Name = "UnitOfMeasure",
        Kind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0",
        EntityType = "reference-data--UnitOfMeasure",
        Fields = [new ReferenceFieldSpec("data.Code", "Code")],
    };

    private static async Task<(SnapshotBuilder Builder, OsduConnection Osdu)> BuildAsync(FakeHttpHandler handler)
    {
        // Capturing a type reads OSDU only; the store is not written until a version is.
        var builder = new SnapshotBuilder(Samples.SampleCache, "dev", "cursor-test", new TestClock(), Samples.Logger<SnapshotBuilder>());
        var osdu = await OsduConnection.CreateAsync(
            "http://localhost/osdu",
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]),
            handler,
            allowLoopback: true);
        return (builder, osdu);
    }

    [Fact]
    public async Task An_empty_page_ends_the_capture_even_though_the_service_still_offers_a_cursor()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query_with_cursor", hit => hit switch
        {
            0 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c1","results":[{"id":"dev:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}"""),
            1 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c2","results":[{"id":"dev:reference-data--UnitOfMeasure:ft","data":{"Code":"ft"}}]}"""),

            // Elasticsearch keeps handing out a scroll id past the end. The page is empty, and that is the end.
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c3","results":[]}"""),
        });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(2, captured.Items.Count);
            Assert.Equal(3, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_record_the_index_returns_twice_is_cached_once()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query_with_cursor", hit => hit switch
        {
            0 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c1","results":[{"id":"dev:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}"""),
            1 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c2","results":[{"id":"dev:reference-data--UnitOfMeasure:m","data":{"Code":"m"}},{"id":"dev:reference-data--UnitOfMeasure:ft","data":{"Code":"ft"}}]}"""),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c3","results":[]}"""),
        });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(["dev:reference-data--UnitOfMeasure:ft", "dev:reference-data--UnitOfMeasure:m"], captured.Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task A_service_that_hands_back_the_same_page_for_ever_ends_the_type_rather_than_paging_for_ever()
    {
        // Nothing is new after the first page, so the capture is going in circles whatever the cursor says. It keeps
        // what it read, warns, and closes the cursor.
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query_with_cursor", HttpStatusCode.OK, Page("stuck", 1000));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(1000, captured.Items.Count);

            // The first page, then the three that brought nothing new, then the DELETE releasing the scroll.
            Assert.Equal(4, handler.Calls.Count(c => c.Method == HttpMethod.Post));
            var closed = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Delete);
            Assert.EndsWith("/query_with_cursor/stuck", closed.Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_same_cursor_on_every_page_is_still_paging_while_the_pages_bring_new_records()
    {
        // The contract says the cursor is null when there are no more results; a deployment answered with the same
        // handle on every page while the context behind it advanced (Azure Data Manager for Energy, dev,
        // 2026-09-18). A capture that took the repeated handle for a stuck service would refuse a refresh that is
        // working, so what it reads is the records, not the handle.
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query_with_cursor", hit => hit switch
        {
            0 => FakeHttpHandler.Json(HttpStatusCode.OK, Page("c1", 1000)),
            1 => FakeHttpHandler.Json(HttpStatusCode.OK, Page("c1", 1000, from: 1000)),
            2 => FakeHttpHandler.Json(HttpStatusCode.OK, Page("c1", 4, from: 2000)),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, Page("c1", 0)),
        });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(2004, captured.Items.Count);
            Assert.Equal(4, handler.Calls.Count(c => c.Method == HttpMethod.Post));
        }
    }

    /// <summary>One search page of <paramref name="count"/> units of measure, offered with <paramref name="cursor"/>.</summary>
    private static string Page(string cursor, int count, int from = 0)
    {
        var results = string.Join(",", Enumerable.Range(from, count).Select(
            i => "{\"id\":\"dev:reference-data--UnitOfMeasure:u" + i + "\",\"data\":{\"Code\":\"u" + i + "\"}}"));
        return "{\"cursor\":\"" + cursor + "\",\"results\":[" + results + "]}";
    }
}
