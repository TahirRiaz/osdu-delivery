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

    /// <summary>One more captured version of the Wellbore type, optionally made the one <c>pinned</c> resolves to.</summary>
    private void WriteVersion(string version, string capturedUtc, string facilityName, bool current)
    {
        Write($"snapshots/references/{version}/manifest.json", $$"""
            { "version": "{{version}}", "capturedUtc": "{{capturedUtc}}", "types": ["Wellbore"] }
            """);
        Write($"snapshots/references/{version}/Wellbore.json", $$"""
            {
              "entityType": "master-data--Wellbore",
              "items": [
                { "id": "opendes:master-data--Wellbore:1", "FacilityName": "{{facilityName}}" },
                { "id": "opendes:master-data--Wellbore:2", "FacilityName": "NO 2/2-B" }
              ]
            }
            """);
        if (current)
        {
            Write("snapshots/references/current", version);
        }
    }

    [Fact]
    public async Task Every_carried_version_keeps_its_own_records()
    {
        // The whole point of carrying more than the current version: reading the cache as it stood then. Two
        // versions of the same cached record must sit side by side, each under its own snapshot, or the listing
        // cannot tell them apart and shows both as if they were one cache.
        BuildRepository();
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);

        WriteVersion("20260201T000000Z", "2026-02-01T00:00:00Z", "NO 1/1-A RENAMED", current: true);
        Assert.Empty(await SyncAsync(repoId));

        await using var db = _catalog.CreateDbContext();
        var snapshots = await db.DeliverySnapshots
            .Where(x => x.RepoId == repoId && x.Kind == "references")
            .OrderBy(x => x.Version)
            .ToListAsync();
        Assert.Equal(["20260101T000000Z", "20260201T000000Z"], snapshots.Select(x => x.Version));
        Assert.False(snapshots[0].Current);
        Assert.True(snapshots[1].Current);

        var items = await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).ToListAsync();
        Assert.Equal(4, items.Count);
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(2, items.Count(i => i.SnapshotId == snapshot.Id));
        }

        // The same cached record, at both versions, with the value that moved between them.
        var history = items.Where(i => i.RecordId == "opendes:master-data--Wellbore:1").ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, i => i.SnapshotId == snapshots[0].Id && i.FieldsJson.Contains("NO 1/1-A\"", StringComparison.Ordinal));
        Assert.Contains(history, i => i.SnapshotId == snapshots[1].Id && i.FieldsJson.Contains("NO 1/1-A RENAMED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unchanged_version_is_not_re_read_from_the_store()
    {
        // A re-sync must not re-open every carried version's files: the catalog already holds them, and the cost
        // of a sync would otherwise grow with the retention window.
        BuildRepository();
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);

        // A sentinel only a re-read would overwrite: the version has not changed, so the sync must leave the rows
        // it already carries exactly as they are rather than deleting and rebuilding them.
        await using (var seed = _catalog.CreateDbContext())
        {
            foreach (var row in await seed.DeliverySnapshotItems.Where(i => i.RepoId == repoId).ToListAsync())
            {
                row.Terms = "sentinel";
            }

            await seed.SaveChangesAsync();
        }

        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        var items = await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("sentinel", i.Terms));
    }

    [Fact]
    public async Task Only_the_newest_versions_are_carried_and_the_current_one_always_is()
    {
        // Twelve versions against a window of ten, with the OLDEST pinned as current: the window keeps the newest,
        // and the current version is carried whatever its age, because it is the one deliveries resolve against.
        BuildRepository();
        var repoId = Guid.NewGuid();
        for (var month = 2; month <= 12; month++)
        {
            WriteVersion($"2026{month:00}01T000000Z", $"2026-{month:00}-01T00:00:00Z", $"NO 1/1-A v{month}", current: false);
        }

        Write("snapshots/references/current", "20260101T000000Z");
        Assert.Empty(await SyncAsync(repoId));

        await using var db = _catalog.CreateDbContext();
        var snapshots = await db.DeliverySnapshots.Where(x => x.RepoId == repoId && x.Kind == "references").ToListAsync();
        Assert.Equal(12, snapshots.Count);

        var carried = (await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).Select(i => i.SnapshotId).Distinct().ToListAsync())
            .ToHashSet();
        var byVersion = snapshots.ToDictionary(x => x.Version, x => x.Id);
        Assert.Equal(10, carried.Count);

        // The current version, and the nine newest captures behind it.
        Assert.Contains(byVersion["20260101T000000Z"], carried);
        for (var month = 4; month <= 12; month++)
        {
            Assert.Contains(byVersion[$"2026{month:00}01T000000Z"], carried);
        }

        // The two that fell out of the window keep their snapshot row and their counts, and lose their items only.
        Assert.DoesNotContain(byVersion["20260201T000000Z"], carried);
        Assert.DoesNotContain(byVersion["20260301T000000Z"], carried);
    }

    [Fact]
    public async Task A_version_that_ages_out_of_the_window_has_its_items_dropped()
    {
        BuildRepository();
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);
        Assert.Equal(2, await CountItemsAsync(repoId));

        // Ten newer captures push the first version out of the window on the next sync.
        for (var month = 2; month <= 12; month++)
        {
            WriteVersion($"2026{month:00}01T000000Z", $"2026-{month:00}-01T00:00:00Z", $"NO 1/1-A v{month}", current: month == 12);
        }

        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        var first = await db.DeliverySnapshots.SingleAsync(x => x.RepoId == repoId && x.Version == "20260101T000000Z");
        Assert.Empty(await db.DeliverySnapshotItems.Where(i => i.SnapshotId == first.Id).ToListAsync());
        Assert.Equal(20, await db.DeliverySnapshotItems.CountAsync(i => i.RepoId == repoId));
    }

    private async Task<int> CountItemsAsync(Guid repoId)
    {
        await using var db = _catalog.CreateDbContext();
        return await db.DeliverySnapshotItems.CountAsync(i => i.RepoId == repoId);
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
