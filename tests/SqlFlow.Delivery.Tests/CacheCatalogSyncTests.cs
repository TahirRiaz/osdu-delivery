using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cache reaching the catalog: what a retrieval flow declares it caches, and what the current snapshot holds,
/// both written by the repository sync so the GUI can show them without opening the repository.
/// </summary>
public sealed class CacheCatalogSyncTests : IDisposable
{
    private readonly SqliteCatalog _catalog = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-cache-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _catalog.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repository with a caching retrieval flow and a captured, current reference snapshot.</summary>
    private void BuildRepository()
    {
        Write("flows/osdu-metadata-sync.yaml", """
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              kinds: [osdu:wks:master-data--Wellbore:1.0.0]
            target:
              location: lake/metadata
            cache:
              types:
                - fields:
                    - data.FacilityName
                    - path: data.NameAlias.AliasName
                      as: Alias
            """);

        Write("snapshots/references/current", "20260101T000000Z");
        Write("snapshots/references/20260101T000000Z/manifest.json", """
            { "version": "20260101T000000Z", "capturedUtc": "2026-01-01T00:00:00Z", "types": ["Wellbore"] }
            """);
        Write("snapshots/references/20260101T000000Z/Wellbore.json", """
            {
              "entityType": "master-data--Wellbore",
              "items": [
                { "id": "opendes:master-data--Wellbore:1", "FacilityName": "NO 1/1-A", "Alias": ["1/1-A", "WELL A"] },
                { "id": "opendes:master-data--Wellbore:2", "FacilityName": "NO 2/2-B" }
              ]
            }
            """);
    }

    private async Task<IReadOnlyList<string>> SyncAsync(Guid repoId)
    {
        var warnings = new List<string>();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        await using var db = _catalog.CreateDbContext();
        await sync.SyncAsync(db, repoId, _root, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return warnings;
    }

    [Fact]
    public async Task Definitions_and_cached_records_reach_the_catalog()
    {
        BuildRepository();
        var repoId = Guid.NewGuid();
        var warnings = await SyncAsync(repoId);
        Assert.Empty(warnings);

        await using var db = _catalog.CreateDbContext();
        var definition = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId);
        Assert.Equal("Wellbore", definition.Name);
        Assert.Equal("master-data--Wellbore", definition.EntityType);
        Assert.Equal("osdu:wks:master-data--Wellbore:1.0.0", definition.Kind);
        Assert.Equal("osdu-metadata-sync", definition.FlowName);
        Assert.True(definition.MakeCurrent);
        using var fields = JsonDocument.Parse(definition.FieldsJson);
        Assert.Equal(["FacilityName", "Alias"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        Assert.Equal(
            ["data.FacilityName", "data.NameAlias.AliasName"],
            fields.RootElement.EnumerateArray().Select(f => f.GetProperty("path").GetString()));

        var items = await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).OrderBy(i => i.RecordId).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal("Wellbore", items[0].TypeName);
        Assert.Equal("opendes:master-data--Wellbore:1", items[0].RecordId);
        Assert.Contains("\"Alias\":[\"1/1-A\",\"WELL A\"]", items[0].FieldsJson, StringComparison.Ordinal);

        // The terms are what a search over the cache matches, one line per value the record holds.
        Assert.Equal(["NO 1/1-A", "1/1-A", "WELL A"], items[0].Terms.Split('\n'));
    }

    [Fact]
    public async Task A_second_sync_leaves_one_row_per_record()
    {
        BuildRepository();
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);
        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        Assert.Equal(2, await db.DeliverySnapshotItems.CountAsync(i => i.RepoId == repoId));
        Assert.Equal(1, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
    }

    [Fact]
    public async Task A_cache_that_leaves_the_repository_leaves_the_catalog()
    {
        BuildRepository();
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);

        File.Delete(Path.Combine(_root, "flows", "osdu-metadata-sync.yaml"));
        Directory.Delete(Path.Combine(_root, "snapshots"), recursive: true);
        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
        Assert.Empty(await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task An_invalid_cache_declaration_is_reported_and_skipped()
    {
        Write("flows/broken.yaml", """
            flowType: retrieval
            name: broken-sync
            source:
              endpoint: https://osdu.example.com
              kinds:
                - osdu:wks:master-data--Wellbore:1.0.0
                - osdu:wks:reference-data--UnitOfMeasure:1.0.0
            target:
              location: lake/metadata
            cache:
              types:
                - fields: [data.FacilityName]
            """);

        var warnings = await SyncAsync(Guid.NewGuid());
        Assert.Contains(warnings, w => w.Contains("needs a kind", StringComparison.Ordinal));
    }
}
