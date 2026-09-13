using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a cache flow declares reaching the catalog through the repository sync, so the GUI can show what a cache holds and
/// which file defines it. The sync only reads the repository: a cache's versions are written by its runs, never by the
/// sync, and snapshot files left in a repository are nothing to it.
/// </summary>
public sealed class CacheCatalogSyncTests : IDisposable
{
    private const string CacheFlow = """
        flowType: cache
        name: osdu-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }
        onChange: approve
        types:
          - kind: osdu:wks:master-data--Wellbore:1.0.0
            onChange: auto
            fields:
              - data.FacilityName
              - path: data.NameAlias.AliasName
                as: Alias
          - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            fields: [data.Code, data.Name]
        """;

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

    private void Write(string relative, string content, string? root = null)
    {
        var path = Path.Combine(root ?? _root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<IReadOnlyList<string>> SyncAsync(Guid repoId, string? root = null)
    {
        var warnings = new List<string>();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        await using var db = _catalog.CreateDbContext();
        await sync.SyncAsync(db, repoId, root ?? _root, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return warnings;
    }

    [Fact]
    public async Task What_a_cache_flow_declares_reaches_the_catalog()
    {
        Write("caches/osdu-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        await using var db = _catalog.CreateDbContext();
        var definitions = await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(["UnitOfMeasure", "Wellbore"], definitions.Select(d => d.Name));

        var wellbore = definitions[1];
        Assert.Equal("osdu-reference-cache", wellbore.CacheName);
        Assert.Equal("caches/osdu-reference-cache.yaml", wellbore.RelativePath);
        Assert.Equal("master-data--Wellbore", wellbore.EntityType);
        Assert.Equal("osdu:wks:master-data--Wellbore:1.0.0", wellbore.Kind);
        Assert.Equal("auto", wellbore.OnChange);
        Assert.Equal("approve", definitions[0].OnChange);
        Assert.True(wellbore.MakeCurrent);
        using var fields = JsonDocument.Parse(wellbore.FieldsJson);
        Assert.Equal(["FacilityName", "Alias"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        Assert.Equal(
            ["data.FacilityName", "data.NameAlias.AliasName"],
            fields.RootElement.EnumerateArray().Select(f => f.GetProperty("path").GetString()));

        // The sync writes no version: a cache is filled by the runs of its cache flow.
        Assert.Empty(await db.DeliveryCacheVersions.ToListAsync());
        Assert.Empty(await db.DeliveryCacheItems.ToListAsync());
    }

    [Fact]
    public async Task A_second_sync_leaves_one_row_per_declared_type_and_reads_a_changed_declaration_again()
    {
        Write("caches/osdu-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);
        await SyncAsync(repoId);

        await using (var db = _catalog.CreateDbContext())
        {
            Assert.Equal(2, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
        }

        Write("caches/osdu-reference-cache.yaml", CacheFlow.Replace("fields: [data.Code, data.Name]", "fields: [data.Code]", StringComparison.Ordinal));
        await SyncAsync(repoId);

        await using (var db = _catalog.CreateDbContext())
        {
            var units = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId && c.Name == "UnitOfMeasure");
            using var fields = JsonDocument.Parse(units.FieldsJson);
            Assert.Equal(["Code"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        }
    }

    [Fact]
    public async Task Snapshot_files_left_in_a_repository_are_nothing_to_the_sync()
    {
        Write("caches/osdu-reference-cache.yaml", CacheFlow);
        Write("snapshots/references/current", "20260101T000000Z");
        Write("snapshots/references/20260101T000000Z/manifest.json", """
            { "version": "20260101T000000Z", "capturedUtc": "2026-01-01T00:00:00Z", "types": ["Wellbore"] }
            """);
        Write("snapshots/references/20260101T000000Z/Wellbore.json", """
            { "entityType": "master-data--Wellbore", "items": [ { "id": "opendes:master-data--Wellbore:1", "FacilityName": "NO 1/1-A" } ] }
            """);

        Assert.Empty(await SyncAsync(Guid.NewGuid()));

        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheVersions.ToListAsync());
        Assert.Empty(await db.DeliveryCacheItems.ToListAsync());
    }

    [Fact]
    public async Task A_cache_that_leaves_the_repository_leaves_the_catalog()
    {
        Write("caches/osdu-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);

        File.Delete(Path.Combine(_root, "caches", "osdu-reference-cache.yaml"));
        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task A_retrieval_flow_is_not_a_cache()
    {
        Write("flows/osdu-metadata-sync.yaml", """
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: opendes }
              kinds: [osdu:wks:master-data--Wellbore:1.0.0]
            target:
              location: lake/metadata
            """);

        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));
        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task An_invalid_cache_declaration_is_reported_and_skipped()
    {
        Write("caches/broken.yaml", """
            flowType: cache
            name: broken-cache
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: opendes }
            types:
              - fields: [data.FacilityName]
            """);

        var repoId = Guid.NewGuid();
        var warnings = await SyncAsync(repoId);
        Assert.Contains(warnings, w => w.Contains("kind is required", StringComparison.Ordinal));
        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task A_cache_declared_by_two_files_keeps_one_and_says_so()
    {
        Write("caches/a.yaml", CacheFlow);
        Write("caches/b.yaml", CacheFlow);

        var repoId = Guid.NewGuid();
        var warnings = await SyncAsync(repoId);
        Assert.Contains(warnings, w => w.Contains("cache 'osdu-reference-cache' is already declared by", StringComparison.Ordinal));
        await using var db = _catalog.CreateDbContext();
        Assert.Equal(2, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
    }

    [Fact]
    public async Task A_cache_another_repository_declares_under_the_same_name_is_reported()
    {
        Write("caches/osdu-reference-cache.yaml", CacheFlow);
        Assert.Empty(await SyncAsync(Guid.NewGuid()));

        var other = Path.Combine(_root, "other");
        Write("osdu-reference-cache.yaml", CacheFlow, other);
        var warnings = await SyncAsync(Guid.NewGuid(), other);
        Assert.Contains(warnings, w => w.Contains("also declared by another repository", StringComparison.Ordinal));
    }
}
