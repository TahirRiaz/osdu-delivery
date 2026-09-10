using System.Net;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A reference snapshot's version enters the render context, so minting one for a capture that found nothing new
/// would change every record's metadata hash and redeliver the whole estate. Schema snapshots are content-addressed
/// and already immune; these cover the same property for reference snapshots.
/// </summary>
public class SnapshotVersioningTests
{
    private static (SnapshotBuilder Builder, FileSnapshotStore Store, TestClock Clock) NewBuilder()
    {
        var clock = new TestClock();
        var store = new FileSnapshotStore(Samples.NewTempDirectory(), Samples.Stores());
        return (new SnapshotBuilder(store, clock, Samples.Logger<SnapshotBuilder>()), store, clock);
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

    [Fact]
    public async Task Recapturing_identical_reference_data_keeps_the_current_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ReferencesFromDirectoryAsync(directory, makeCurrent: true);
        Assert.Equal(first.Version, await store.CurrentReferenceVersionAsync());

        // A later recapture of byte-identical content must not mint a version: the version is a timestamp, and a
        // new one would move the render context and re-render every record that resolves against it.
        clock.Advance(TimeSpan.FromHours(6));
        var again = await builder.ReferencesFromDirectoryAsync(directory, makeCurrent: true);
        Assert.Equal(first.Version, again.Version);
        Assert.Equal(first.ContentHash(), again.ContentHash());
        Assert.Equal(first.Version, await store.CurrentReferenceVersionAsync());
        Assert.Single(await store.ListReferenceVersionsAsync());
    }

    [Fact]
    public async Task A_reference_change_does_mint_a_new_version()
    {
        var (builder, store, clock) = NewBuilder();
        var directory = ReferenceDirectory(("UnitOfMeasure", OneItem));

        var first = await builder.ReferencesFromDirectoryAsync(directory, makeCurrent: true);

        clock.Advance(TimeSpan.FromHours(6));
        File.WriteAllText(Path.Combine(directory, "UnitOfMeasure.json"), TwoItems);
        var second = await builder.ReferencesFromDirectoryAsync(directory, makeCurrent: true);

        Assert.NotEqual(first.Version, second.Version);
        Assert.NotEqual(first.ContentHash(), second.ContentHash());
        Assert.Equal(second.Version, await store.CurrentReferenceVersionAsync());
        Assert.Equal(2, (await store.ListReferenceVersionsAsync()).Count);
    }

    [Fact]
    public async Task A_schema_snapshot_version_is_the_content_hash_so_recapture_is_already_free()
    {
        var (builder, _, clock) = NewBuilder();
        var kind = "osdu:wks:work-product-component--WellLog:1.4.0";
        var root = Samples.NewTempDirectory();
        var directory = Path.Combine(root, "work-product-component");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "WellLog.1.4.0.json"),
            """{"$id":"WellLog.1.4.0.json","type":"object","properties":{"data":{"type":"object","properties":{"Name":{"type":"string"}}}}}""");

        var first = await builder.SchemaFromDirectoryAsync(root, kind);
        clock.Advance(TimeSpan.FromDays(1));
        var again = await builder.SchemaFromDirectoryAsync(root, kind);

        Assert.Equal(first.Version, again.Version);
    }
}

/// <summary>
/// Paging the search index for a reference capture. The service hands back a cursor for the page after the last
/// one as well, and that page is empty, so ending only on a null cursor is how a capture pages forever.
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

    private static (SnapshotBuilder Builder, OsduConnection Osdu) Build(FakeHttpHandler handler)
    {
        var store = new FileSnapshotStore(Samples.NewTempDirectory(), Samples.Stores());
        var builder = new SnapshotBuilder(store, new TestClock(), Samples.Logger<SnapshotBuilder>());
        var osdu = new OsduConnection(
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
        var (builder, osdu) = Build(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Spec);

            Assert.Equal(2, captured.Items.Count);
            Assert.Equal(3, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal)));
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
        var (builder, osdu) = Build(handler);
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
