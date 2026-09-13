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
public sealed class SnapshotVersioningTests : IDisposable
{
    private const string Cache = "units";

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

    private readonly SqliteCatalog _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private (SnapshotBuilder Builder, CatalogCacheStore Store, TestClock Clock) NewBuilder()
    {
        var clock = new TestClock();
        var store = _catalog.Caches();
        return (new SnapshotBuilder(store, Cache, clock, Samples.Logger<SnapshotBuilder>()), store, clock);
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
          "items": [ { "id": "opendes:reference-data--UnitOfMeasure:m", "Code": "m", "Name": "metre", "ID": "m" } ]
        }
        """;

    private const string TwoItems = """
        {
          "entityType": "reference-data--UnitOfMeasure",
          "items": [
            { "id": "opendes:reference-data--UnitOfMeasure:m", "Code": "m", "Name": "metre", "ID": "m" },
            { "id": "opendes:reference-data--UnitOfMeasure:ft", "Code": "ft", "Name": "foot", "ID": "ft" }
          ]
        }
        """;

    private const string OneWellbore = """
        { "entityType": "master-data--Wellbore", "items": [ { "id": "opendes:master-data--Wellbore:A", "FacilityName": "NO 1/1-A" } ] }
        """;

    [Fact]
    public async Task Recapturing_identical_reference_data_keeps_the_current_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ImportDirectoryAsync(directory, [Units], Capture, makeCurrent: true);
        Assert.True(first.Written);
        Assert.Equal(first.Snapshot.Version, await store.CurrentVersionAsync(Cache));

        // A later recapture of identical content must not write a version: the version is a timestamp, and a new one
        // would move the render context and re-render every record that reads the cache.
        clock.Advance(TimeSpan.FromHours(6));
        var again = await builder.ImportDirectoryAsync(directory, [Units], Capture, makeCurrent: true);
        Assert.False(again.Written);
        Assert.Equal(first.Snapshot.Version, again.Snapshot.Version);
        Assert.Equal(first.Snapshot.ContentHash(), again.Snapshot.ContentHash());
        Assert.Equal(first.Snapshot.Version, await store.CurrentVersionAsync(Cache));
        Assert.Single(await store.ListVersionsAsync(Cache));
    }

    [Fact]
    public async Task A_reference_change_does_write_a_new_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ImportDirectoryAsync(directory, [Units], Capture, makeCurrent: true);

        clock.Advance(TimeSpan.FromHours(6));
        File.WriteAllText(Path.Combine(directory, "UnitOfMeasure.json"), TwoItems);
        var second = await builder.ImportDirectoryAsync(directory, [Units], Capture, makeCurrent: true);

        Assert.True(second.Written);
        Assert.NotEqual(first.Snapshot.Version, second.Snapshot.Version);
        Assert.NotEqual(first.Snapshot.ContentHash(), second.Snapshot.ContentHash());
        Assert.Equal(second.Snapshot.Version, await store.CurrentVersionAsync(Cache));
        var versions = await store.ListVersionsAsync(Cache);
        Assert.Equal(2, versions.Count);
        Assert.Equal(first.Snapshot.Version, versions[0].PreviousVersion);
    }

    [Fact]
    public async Task A_type_the_cache_no_longer_declares_leaves_with_the_next_version()
    {
        // A version holds exactly what one capture found: a type taken out of the cache flow is not carried forward.
        var (builder, store, clock) = NewBuilder();
        var both = ReferenceDirectory(("UnitOfMeasure", OneItem), ("Wellbore", OneWellbore));
        var first = await builder.ImportDirectoryAsync(both, [Units, Wellbores], Capture, makeCurrent: true);
        Assert.True(first.Snapshot.HasType("Wellbore"));

        clock.Advance(TimeSpan.FromHours(1));
        var unitsOnly = ReferenceDirectory(("UnitOfMeasure", OneItem));
        var second = await builder.ImportDirectoryAsync(unitsOnly, [Units], Capture, makeCurrent: true);

        Assert.True(second.Written);
        var current = await store.LoadAsync(Cache, (await store.CurrentVersionAsync(Cache))!);
        Assert.False(current!.HasType("Wellbore"));
        Assert.True(current.HasType("UnitOfMeasure"));
    }

    [Fact]
    public async Task An_import_holding_what_the_cache_does_not_declare_writes_nothing()
    {
        var (builder, store, _) = NewBuilder();

        var undeclaredType = ReferenceDirectory(("UnitOfMeasure", OneItem), ("Wellbore", OneWellbore));
        var type = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(undeclaredType, [Units], Capture, makeCurrent: true));
        Assert.Contains("holds a type cache 'units' does not declare", type.Message, StringComparison.Ordinal);

        var missing = ReferenceDirectory(("UnitOfMeasure", OneItem));
        var absent = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(missing, [Units, Wellbores], Capture, makeCurrent: true));
        Assert.Contains("Wellbore.json is missing", absent.Message, StringComparison.Ordinal);

        var uncaptured = ReferenceDirectory(("UnitOfMeasure", OneItem.Replace("\"ID\": \"m\"", "\"ID\": \"m\", \"Symbol\": \"m\"", StringComparison.Ordinal)));
        var field = await Assert.ThrowsAsync<DeliveryException>(() => builder.ImportDirectoryAsync(uncaptured, [Units], Capture, makeCurrent: true));
        Assert.Contains("values under 'Symbol'", field.Message, StringComparison.Ordinal);

        Assert.Empty(await store.ListVersionsAsync(Cache));
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
        var builder = new SnapshotBuilder(Samples.SampleCache, "cursor-test", new TestClock(), Samples.Logger<SnapshotBuilder>());
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
            0 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c1","results":[{"id":"opendes:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}"""),
            1 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c2","results":[{"id":"opendes:reference-data--UnitOfMeasure:ft","data":{"Code":"ft"}}]}"""),

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
            0 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c1","results":[{"id":"opendes:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}"""),
            1 => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c2","results":[{"id":"opendes:reference-data--UnitOfMeasure:m","data":{"Code":"m"}},{"id":"opendes:reference-data--UnitOfMeasure:ft","data":{"Code":"ft"}}]}"""),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"cursor":"c3","results":[]}"""),
        });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(["opendes:reference-data--UnitOfMeasure:ft", "opendes:reference-data--UnitOfMeasure:m"], captured.Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task A_cursor_that_never_moves_fails_instead_of_paging_forever()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post,
            "/query_with_cursor",
            HttpStatusCode.OK,
            """{"cursor":"stuck","results":[{"id":"opendes:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}""");
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => builder.CaptureTypeAsync(osdu, Spec));

            Assert.Contains("same cursor twice", ex.Message, StringComparison.Ordinal);

            // Two search pages, then one DELETE releasing the scroll the capture walked away from.
            Assert.Equal(2, handler.Calls.Count(c => c.Method == HttpMethod.Post));
            var closed = Assert.Single(handler.Calls, c => c.Method == HttpMethod.Delete);
            Assert.EndsWith("/query_with_cursor/stuck", closed.Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }
}
