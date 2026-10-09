using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The catalog of flows that name their partitions (docs/partitions-design.md sections 4 to 6), on SQL Server: the cache
/// declarations the repository sync records per partition, a flow moving from its header to named partitions without losing
/// its cache, the interfaces described once per partition with each partition's ledger, and the central configuration read
/// in its two layers.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class PartitionCatalogTests : IDisposable
{
    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-partitions-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
    }

    private async Task<IReadOnlyList<string>> SyncAsync()
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(
            db, _repo, _root, new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return warnings;
    }

    private async Task<List<string>> DefinitionsAsync()
    {
        await using var db = _module.CreateDbContext();
        return (await db.DeliveryCacheDefinitions.AsNoTracking().Where(c => c.RepoId == _repo).ToListAsync())
            .Select(c => $"{c.Scope}/{c.FlowName}/{c.Name}{(c.DeclaresPartitions ? " (named)" : string.Empty)}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private const string Lookups = """
          - table: OsduData.silver.CurveDictionary
            name: CurveDictionary
            key: mnemonic
            fields: [log_curve_type_id]
        """;

    /// <summary>The lookups cache flow, naming <paramref name="partitions"/> or, when null, the partition its header names.</summary>
    private static string LookupsFlow(string? partitions, string types = Lookups) => $$"""
        flowType: cache
        name: welldb-lookups-00-cache
        {{(partitions is null ? string.Empty : $"partitions: {partitions}")}}
        source:
          connection: ${env:OSDU_DATA_DB}
          headers: {{(partitions is null ? "{ data-partition-id: dev }" : "{ }")}}
        types:
        {{types}}
        """;

    /// <summary>The lookups cache flow naming neither partitions nor a header: it builds a cache for every registered partition.</summary>
    private const string RegistryLookupsFlow = """
        flowType: cache
        name: welldb-lookups-00-cache
        source:
          connection: ${env:OSDU_DATA_DB}
        types:
          - table: OsduData.silver.CurveDictionary
            name: CurveDictionary
            key: mnemonic
            fields: [log_curve_type_id]
        """;

    private DeliveryPartitionRegistry Registry() => new(_module.CreateDbContext);

    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_cache_flow_that_names_its_partitions_declares_each_type_once_per_partition_it_is_built_for()
    {
        Write("cache/lookups.yaml", LookupsFlow("[dev, test]", Lookups + """

              - table: OsduData.silver.TestUnits
                name: TestUnits
                key: source_unit
                fields: [osdu_unit]
                partitions: [test]
            """));

        Assert.Empty(await SyncAsync());

        Assert.Equal(
            [
                "dev/welldb-lookups-00-cache/CurveDictionary (named)",
                "test/welldb-lookups-00-cache/CurveDictionary (named)",
                "test/welldb-lookups-00-cache/TestUnits (named)",
            ],
            await DefinitionsAsync());
    }

    [Fact]
    public async Task A_flow_moving_from_its_header_to_named_partitions_keeps_its_partition_s_cache_and_lets_go_of_one_it_drops()
    {
        Write("cache/lookups.yaml", LookupsFlow(null));
        Assert.Empty(await SyncAsync());
        Assert.Equal(["dev/welldb-lookups-00-cache/CurveDictionary"], await DefinitionsAsync());

        await using (var db = _module.CreateDbContext())
        {
            db.DeliveryCacheMembers.Add(new DeliveryCacheMember { Scope = "dev", TypeName = "CurveDictionary", RecordId = "GR", FlowName = "welldb-lookups-00-cache" });
            await db.SaveChangesAsync();
        }

        Write("cache/lookups.yaml", LookupsFlow("[dev, test]"));
        Assert.Empty(await SyncAsync());
        Assert.Equal(
            ["dev/welldb-lookups-00-cache/CurveDictionary (named)", "test/welldb-lookups-00-cache/CurveDictionary (named)"],
            await DefinitionsAsync());

        await using (var db = _module.CreateDbContext())
        {
            Assert.Equal(["GR"], await db.DeliveryCacheMembers.Where(m => m.Scope == "dev" && m.FlowName == "welldb-lookups-00-cache").Select(m => m.RecordId).ToListAsync());
            db.DeliveryCacheMembers.Add(new DeliveryCacheMember { Scope = "test", TypeName = "CurveDictionary", RecordId = "DT", FlowName = "welldb-lookups-00-cache" });
            await db.SaveChangesAsync();
        }

        // Dropping a partition lets go of what the flow held in it, and of nothing it holds in the partitions it keeps.
        Write("cache/lookups.yaml", LookupsFlow("[dev]"));
        Assert.Empty(await SyncAsync());
        Assert.Equal(["dev/welldb-lookups-00-cache/CurveDictionary (named)"], await DefinitionsAsync());
        await using (var db = _module.CreateDbContext())
        {
            Assert.False(await db.DeliveryCacheMembers.AnyAsync(m => m.Scope == "test" && m.FlowName == "welldb-lookups-00-cache"));
            Assert.True(await db.DeliveryCacheMembers.AnyAsync(m => m.Scope == "dev" && m.FlowName == "welldb-lookups-00-cache"));
        }
    }

    [Fact]
    public async Task Every_interface_is_described_once_per_partition_with_the_ledger_it_keeps_there()
    {
        Write("flows/petrel.yaml", """
            flowType: delivery
            name: petrel
            partitions:
              - name: dev
                keepLedger: true
              - test
            source:
              connection: ${env:PETREL_DB}
              lastModified: update_date
              work: work/petrel
            target:
              endpoint: ${env:OSDU_URL}
            interfaces:
              wellbores:
                record: { object: Petrel.ing.Wellbore, key: [uwi] }
                mapping: Wellbore@1.1.0
              logs:
                record: { object: Petrel.ing.WellLog, key: [log_id] }
                mapping: WellLog@1.4.0
            """);

        await SyncAsync();

        await using var db = _module.CreateDbContext();
        var rows = await db.DeliveryInterfaces.AsNoTracking().Where(i => i.RepoId == _repo).ToListAsync();
        Assert.Equal(
            [
                ("logs", "dev", FlowId.Of("petrel/logs"), "petrel/logs", 1),
                ("logs", "test", FlowId.Of("petrel/logs", "test"), "petrel/logs@test", 1),
                ("wellbores", "dev", FlowId.Of("petrel/wellbores"), "petrel/wellbores", 0),
                ("wellbores", "test", FlowId.Of("petrel/wellbores", "test"), "petrel/wellbores@test", 0),
            ],
            rows.Select(r => (r.Interface, r.Partition, r.LedgerFlowId, r.LedgerName, r.Ordinal))
                .OrderBy(r => r.Interface, StringComparer.Ordinal).ThenBy(r => r.Partition, StringComparer.Ordinal).ToList());

        Assert.Equal(["wellbores", "logs"], (await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", "test", CancellationToken.None)).Select(i => i.Interface));
        Assert.Empty(await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", null, CancellationToken.None));
        var keeping = Assert.Single(await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("petrel/logs", "test"), CancellationToken.None));
        Assert.Equal(("logs", "test"), (keeping.Interface, keeping.Partition));
    }

    [Fact]
    public async Task A_registry_driven_cache_flow_declares_its_types_in_every_registered_partition_as_the_registry_stands_at_each_sync()
    {
        Write("cache/lookups.yaml", RegistryLookupsFlow);

        // Nothing registered: the flow builds nothing, and the sync says why.
        var warnings = await SyncAsync();
        Assert.Contains(warnings, w => w.Contains("none is registered yet", StringComparison.Ordinal));
        Assert.Empty(await DefinitionsAsync());

        await Registry().AddAsync("dev", null, makeDefault: false, "tester", Now);
        await Registry().AddAsync("test", null, makeDefault: false, "tester", Now);
        Assert.Empty(await SyncAsync());
        Assert.Equal(
            ["dev/welldb-lookups-00-cache/CurveDictionary (named)", "test/welldb-lookups-00-cache/CurveDictionary (named)"],
            await DefinitionsAsync());

        // A partition registered later is described at the next sync, and one removed is let go of.
        await Registry().AddAsync("prod", null, makeDefault: false, "tester", Now);
        Assert.True(await Registry().RemoveAsync("test"));
        Assert.Empty(await SyncAsync());
        Assert.Equal(
            ["dev/welldb-lookups-00-cache/CurveDictionary (named)", "prod/welldb-lookups-00-cache/CurveDictionary (named)"],
            await DefinitionsAsync());
    }

    [Fact]
    public async Task A_registry_driven_delivery_flow_is_described_in_every_registered_partition_and_keeps_the_ledger_keepLedger_names()
    {
        await Registry().AddAsync("dev", null, makeDefault: false, "tester", Now);
        await Registry().AddAsync("test", null, makeDefault: false, "tester", Now);
        Write("flows/welllog.yaml", """
            flowType: delivery
            name: welldb-welllog
            keepLedger: dev
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.silver.WellLog, key: [log_id] }
              lastModified: update_date
              work: work/welllog
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
            """);

        await SyncAsync();

        await using (var db = _module.CreateDbContext())
        {
            var rows = await db.DeliveryInterfaces.AsNoTracking().Where(i => i.RepoId == _repo).ToListAsync();
            Assert.Equal(
                [("dev", FlowId.Of("welldb-welllog"), "welldb-welllog", true), ("test", FlowId.Of("welldb-welllog", "test"), "welldb-welllog@test", true)],
                rows.Select(r => (r.Partition, r.LedgerFlowId, r.LedgerName, r.Active)).OrderBy(r => r.Partition, StringComparer.Ordinal).ToList());
        }

        // Taken out of the registry, a partition's interface stops answering first; its records still lead to their flow.
        Assert.True(await Registry().RemoveAsync("test"));
        await SyncAsync();
        await using (var db = _module.CreateDbContext())
        {
            var test = await db.DeliveryInterfaces.AsNoTracking().SingleAsync(i => i.RepoId == _repo && i.Partition == "test");
            Assert.False(test.Active);
            var keeping = await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("welldb-welllog", "test"), CancellationToken.None);
            Assert.Equal("welldb-welllog", Assert.Single(keeping).FlowName);
        }
    }

    [Fact]
    public async Task The_central_configuration_is_set_per_partition_and_read_with_a_partition_s_values_first()
    {
        var store = new DeliveryConfigStore(_module.CreateDbContext);
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        // A property is set for a repository the catalog holds; the catalog's rows are this test's to remove.
        await using var catalog = _module.CreateCatalogContext();
        catalog.Repos.Add(new SqlFlow.Catalog.CatalogRepo { Id = _repo, Name = "partitions-" + _repo.ToString("N")[..10], FirstSeenUtc = now, LastSyncUtc = now });
        await catalog.SaveChangesAsync();
        try
        {
            await ReadConfigurationInLayersAsync(store, catalog, now);
        }
        finally
        {
            await catalog.Repos.Where(r => r.Id == _repo).ExecuteDeleteAsync();
        }
    }

    private async Task ReadConfigurationInLayersAsync(DeliveryConfigStore store, SqlFlow.Catalog.CatalogDbContext catalog, DateTime now)
    {
        await store.SetAsync(null, null, "OSDU_URL", "https://osdu.example.com", null, "tests", now, catalog);
        await store.SetAsync(_repo, null, "OSDU_LEGAL_TAG", "estate-legal", null, "tests", now, catalog);
        await store.SetAsync(null, "test", "OSDU_URL", "https://test.osdu.example.com", null, "tests", now, catalog);
        await store.SetAsync(_repo, "test", "OSDU_LEGAL_TAG", "estate-test-legal", null, "tests", now, catalog);
        await store.SetAsync(null, "prod", "OSDU_LEGAL_TAG", "platform-prod-legal", null, "tests", now, catalog);

        var configuration = await store.ConfigurationAsync(_repo);
        Assert.Equal("estate-legal", configuration.Base["OSDU_LEGAL_TAG"]);
        Assert.Equal(["prod", "test"], configuration.Partitions.Keys.Order(StringComparer.Ordinal));

        var test = await store.EffectiveAsync(_repo, "test");
        Assert.Equal(("https://test.osdu.example.com", "estate-test-legal"), (test["OSDU_URL"], test["OSDU_LEGAL_TAG"]));

        // A value set for a partition wins over the estate's own value set for none.
        var prod = await store.EffectiveAsync(_repo, "prod");
        Assert.Equal(("https://osdu.example.com", "platform-prod-legal"), (prod["OSDU_URL"], prod["OSDU_LEGAL_TAG"]));
        var none = await store.EffectiveAsync(_repo, null);
        Assert.Equal(("https://osdu.example.com", "estate-legal"), (none["OSDU_URL"], none["OSDU_LEGAL_TAG"]));

        Assert.Equal(["OSDU_LEGAL_TAG"], (await store.ListAsync(_repo, "test")).Select(p => p.Name));
        Assert.True(await store.RemoveAsync(_repo, "test", "OSDU_LEGAL_TAG"));
        Assert.Equal("https://test.osdu.example.com", (await store.EffectiveAsync(_repo, "test"))["OSDU_URL"]);
        Assert.Equal("estate-legal", (await store.EffectiveAsync(_repo, "test"))["OSDU_LEGAL_TAG"]);

        var ex = await Assert.ThrowsAsync<FlowValidationException>(() => store.SetAsync(null, "${env:PART}", "OSDU_URL", "x", null, "tests", now, catalog));
        Assert.Contains("not a data-partition-id", ex.Message, StringComparison.Ordinal);
    }
}
