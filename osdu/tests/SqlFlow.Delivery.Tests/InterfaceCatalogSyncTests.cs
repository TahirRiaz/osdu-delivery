using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The read model of sources and interfaces (docs/interfaces-design.md section 4): the repository sync describes every
/// interface of every delivery flow with the ledger identity it keeps, the route it goes by and the kind its mapping fills;
/// an interface the repository no longer declares stays findable as inactive; and a ledger kept by two flows is reported.
/// </summary>
public sealed class InterfaceCatalogSyncTests : IDisposable
{
    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-interface-sync-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string Source = """
        flowType: delivery
        name: petrel
        source:
          connection: ${env:PETREL_DB}
          work: ../.work
        target:
          endpoint: ${env:OSDU_URL}
          headers: { data-partition-id: dev }
        interfaces:
          wellbores:
            record: { object: Petrel.ing.Wellbore, key: [facility_name] }
            mapping: Wellbore@1.0.0
          logs:
            ledger: wells-welllog-03-header-delivery
            record: { object: Petrel.ing.WellLog, key: [uwi, log_id] }
            files: { root: ../data/logs, locationColumn: log_folder, hashColumn: log_hash }
            mapping: WellLog@1.4.0
            after: [wellbores]
        """;

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<(IReadOnlyList<string> Warnings, SqlFlow.Catalog.CatalogSyncExtensionResult Result)> SyncAsync(DateTime? now = null)
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        var result = await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(
            db, _repo, _root, now ?? new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return (warnings, result);
    }

    private async Task<List<Data.DeliveryInterface>> RowsAsync()
    {
        await using var db = _module.CreateDbContext();
        return await db.DeliveryInterfaces.AsNoTracking().OrderBy(i => i.FlowName).ThenBy(i => i.Ordinal).ToListAsync();
    }

    [Fact]
    public async Task Every_interface_of_every_delivery_flow_is_described_with_the_ledger_it_keeps()
    {
        Write("flows/petrel.yaml", Source);
        Write("flows/wells-wellbore-03-header-delivery.yaml", File.ReadAllText(Samples.WellboreFlowFile));
        Write("mappings/Wellbore@1.0.0.yaml", File.ReadAllText(Path.Combine(Samples.Mappings, "Wellbore@1.0.0.yaml")));

        var (warnings, result) = await SyncAsync();

        Assert.Empty(warnings);
        var rows = await RowsAsync();
        Assert.Equal(3, rows.Count);
        var wellbores = rows.Single(r => r.FlowName == "petrel" && r.Interface == "wellbores");
        Assert.Equal((0, "storage", FlowId.Of("petrel/wellbores"), "petrel/wellbores"), (wellbores.Ordinal, wellbores.Route, wellbores.LedgerFlowId, wellbores.LedgerName));
        Assert.Equal(Samples.WellboreKind, wellbores.Kind);
        Assert.Equal("Petrel.ing.Wellbore", wellbores.RecordObject);
        Assert.Equal("flows/petrel.yaml", wellbores.RelativePath);
        Assert.Contains("storage service", wellbores.RouteReason, StringComparison.Ordinal);
        Assert.True(wellbores.Active);

        var logs = rows.Single(r => r.Interface == "logs");
        Assert.Equal((1, "file", FlowId.Of("wells-welllog-03-header-delivery"), "wells-welllog-03-header-delivery"), (logs.Ordinal, logs.Route, logs.LedgerFlowId, logs.LedgerName));
        // The repository holds no WellLog mapping, so its kind is not known yet.
        Assert.Equal(string.Empty, logs.Kind);
        Assert.Equal(["wellbores"], JsonSerializer.Deserialize<string[]>(logs.AfterJson)!);

        // A flow in the single form is one row with no interface name, keeping the ledger of its own name.
        var single = rows.Single(r => r.FlowName == "wells-wellbore-03-header-delivery");
        Assert.Equal((string.Empty, FlowId.Of("wells-wellbore-03-header-delivery"), "storage"), (single.Interface, single.LedgerFlowId, single.Route));
        Assert.Null(single.RouteReason);
        Assert.True(result.Added >= 3);

        // Syncing the same tree again changes nothing.
        var (_, again) = await SyncAsync(new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.Updated);
        Assert.True((await RowsAsync()).All(r => r.LastSeenUtc == new DateTime(2026, 9, 16, 13, 0, 0, DateTimeKind.Utc)));

        await using var db = _module.CreateDbContext();
        var keeping = await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("wells-welllog-03-header-delivery"), CancellationToken.None);
        Assert.Equal(new InterfaceLocation(_repo, "petrel", "logs", FlowId.Of("wells-welllog-03-header-delivery"), "wells-welllog-03-header-delivery", true), Assert.Single(keeping));
        Assert.Equal(["wellbores", "logs"], (await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", CancellationToken.None)).Select(i => i.Interface));
    }

    [Fact]
    public async Task An_interface_the_repository_no_longer_declares_stays_findable_as_inactive()
    {
        Write("flows/petrel.yaml", Source);
        await SyncAsync();

        // The logs interface goes, then the whole document.
        Write("flows/petrel.yaml", Source[..Source.IndexOf("  logs:", StringComparison.Ordinal)]);
        var (_, removedOne) = await SyncAsync();
        Assert.Equal(1, removedOne.Removed);
        var rows = await RowsAsync();
        Assert.True(rows.Single(r => r.Interface == "wellbores").Active);
        Assert.False(rows.Single(r => r.Interface == "logs").Active);

        await using (var db = _module.CreateDbContext())
        {
            // Its records still lead to their flow, and the flow's own listing leaves it out.
            var keeping = Assert.Single(await DeliveryInterfaceCatalog.KeepingAsync(db, FlowId.Of("wells-welllog-03-header-delivery"), CancellationToken.None));
            Assert.False(keeping.Active);
            Assert.Equal(["wellbores"], (await DeliveryInterfaceCatalog.OfFlowAsync(db, _repo, "petrel", CancellationToken.None)).Select(i => i.Interface));
        }

        File.Delete(Path.Combine(_root, "flows", "petrel.yaml"));
        await SyncAsync();
        Assert.All(await RowsAsync(), r => Assert.False(r.Active));
        await using (var db = _module.CreateDbContext())
        {
            Assert.Contains("petrel", await DeliveryInterfaceCatalog.DescribedFlowsAsync(db, _repo, CancellationToken.None));
        }

        // Declared again, it is active again.
        Write("flows/petrel.yaml", Source);
        await SyncAsync();
        Assert.All(await RowsAsync(), r => Assert.True(r.Active));
    }

    [Fact]
    public async Task A_ledger_kept_by_two_flows_is_reported_and_a_document_that_does_not_parse_describes_nothing()
    {
        Write("flows/petrel.yaml", Source);
        Write("flows/wells-welllog-03-header-delivery.yaml", File.ReadAllText(Samples.Flow));
        Write("flows/broken.yaml", "flowType: delivery\nname: broken\n");
        Write("flows/zz-again.yaml", Source.Replace("wellbores:", "cores:", StringComparison.Ordinal).Replace("after: [wellbores]", "after: [cores]", StringComparison.Ordinal));

        var (warnings, result) = await SyncAsync();

        Assert.Contains(warnings, w => w.StartsWith("flows/zz-again.yaml: delivery flow 'petrel' is already declared by flows/petrel.yaml", StringComparison.Ordinal));
        var shared = Assert.Single(warnings, w => w.Contains("the ledger 'wells-welllog-03-header-delivery' is kept by", StringComparison.Ordinal));
        Assert.Contains("interface 'logs' of flow 'petrel'", shared, StringComparison.Ordinal);
        Assert.Contains("flow 'wells-welllog-03-header-delivery'", shared, StringComparison.Ordinal);
        Assert.Equal(1, result.Invalid);
        var rows = await RowsAsync();
        Assert.DoesNotContain(rows, r => r.FlowName == "broken");
        Assert.DoesNotContain(rows, r => r.Interface == "cores");
    }
}
