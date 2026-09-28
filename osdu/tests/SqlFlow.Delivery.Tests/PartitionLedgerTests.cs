using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ledgers of a flow that names its partitions, against the ledger on SQL Server (docs/partitions-design.md section 4):
/// which partitions a ledger's records went to, read from their OSDU ids, and the guards that refuse a run that would
/// deliver records again as new or mix two partitions' records in one ledger. Every test works under flows of its own.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class PartitionLedgerTests
{
    /// <summary>The module's schema, brought up to date once per test run.</summary>
    private static readonly Lazy<Task> Migrated = new(async () =>
    {
        await using var db = Database();
        await db.Database.MigrateAsync();
    });

    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _name = "partition-guard-" + Guid.NewGuid().ToString("N")[..12];
    private readonly DeliveryDocumentLoader _loader = new();

    private static OsduDbContext Database() => new(OsduDbContext.SqlServerOptions(OsduTestServer.ConnectionString));

    private static async Task<OsduLedger> LedgerAsync()
    {
        await Migrated.Value;
        return new OsduLedger(Database, TimeProvider.System);
    }

    /// <summary>The flow of this test naming <paramref name="partitions"/>, bound to <paramref name="partition"/>.</summary>
    private SourceDefinition Flow(string partitions, string partition) => _loader.ParseSource($$"""
        flowType: delivery
        name: {{_name}}
        partitions: {{partitions}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.arc.WellLog, key: [log_id] }
          lastModified: update_date
          work: work/welllog
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
        """.ReplaceLineEndings("\n"), "flows/welllog.yaml").ForPartition(partition);

    /// <summary>
    /// The flow of this test naming neither partitions nor a header, so serving every registered partition, keeping the
    /// ledger it kept before in <paramref name="keepLedger"/> when one is named, and bound to <paramref name="partition"/>.
    /// </summary>
    private SourceDefinition RegistryFlow(string? keepLedger, string partition) => _loader.ParseSource($$"""
        flowType: delivery
        name: {{_name}}
        {{(keepLedger is null ? string.Empty : $"keepLedger: {keepLedger}")}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.arc.WellLog, key: [log_id] }
          lastModified: update_date
          work: work/welllog
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
        """.ReplaceLineEndings("\n"), "flows/welllog.yaml").ForPartition(partition);

    /// <summary>The guards over <paramref name="bound"/>, a flow serving <paramref name="served"/>, or the partitions it names.</summary>
    private static Task CheckAsync(PartitionLedgers guards, ILedger ledger, SourceDefinition bound, params string[] served)
        => guards.CheckAsync(ledger, bound, served.Length > 0 ? served : bound.Served(RegisteredPartitions.None), CancellationToken.None);

    /// <summary>
    /// Records in the ledger of <paramref name="flowId"/>: one per partition named, each with an OSDU id of its own in that
    /// partition (ids are unique across the shared database), delivered, or only claimed when <paramref name="claimedOnly"/>;
    /// a null partition is a record with no id yet. The ledger is one the upgrade to partition keys could not place, since
    /// its records went to several partitions: unassigned in the directory, with its rows under the unassigned partition.
    /// </summary>
    private static async Task SeedAsync(Guid flowId, bool claimedOnly, params string?[] partitions)
    {
        await Migrated.Value;
        await using var db = Database();
        if (!await db.DeliveryLedgers.AnyAsync(l => l.FlowId == flowId))
        {
            db.DeliveryLedgers.Add(new DeliveryLedger
            {
                PartitionId = DeliveryModel.UnassignedPartition,
                FlowId = flowId,
                Kind = LedgerKinds.Delivery,
                FlowName = $"seeded-{flowId:N}",
                LedgerName = $"seeded-{flowId:N}",
                RegisteredUtc = Now,
            });
        }

        foreach (var partition in partitions)
        {
            var id = partition is null ? null : $"{partition}:work-product-component--WellLog:{Guid.NewGuid():N}";
            db.DeliveryRecords.Add(new DeliveryRecord
            {
                FlowId = flowId,
                DeliveryKey = Guid.NewGuid(),
                SourceKey = Guid.NewGuid().ToString("N"),
                MappingName = "WellLog@1.4.0",
                Status = id is null ? "pending" : "delivered",
                TargetId = claimedOnly ? null : id,
                ClaimedTargetId = id,
                CreatedUtc = Now,
                UpdatedUtc = Now,
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_ledger_says_which_partitions_its_records_went_to_by_their_osdu_ids()
    {
        var ledger = await LedgerAsync();
        var flowId = FlowId.Of(_name);
        Assert.False(await ledger.HoldsRecordsAsync(flowId));

        await SeedAsync(flowId, false, "dev", "dev", "test");
        await SeedAsync(flowId, true, "test");
        await SeedAsync(flowId, false, (string?)null);

        Assert.True(await ledger.HoldsRecordsAsync(flowId));
        var delivered = await ledger.DeliveredPartitionsAsync(flowId);
        Assert.Equal([new PartitionRecords("dev", 2), new PartitionRecords("test", 2)], delivered);
    }

    [Fact]
    public async Task A_run_is_refused_while_no_partition_keeps_a_ledger_holding_records_of_a_partition_the_flow_names()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev", "dev");

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => CheckAsync(new PartitionLedgers(), ledger, Flow("[dev, test]", "test")));

        Assert.Contains("no partition keeps the ledger", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 records delivered to 'dev'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("keepLedger: true", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing ran.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ledger_of_records_delivered_to_a_partition_the_flow_no_longer_names_holds_no_run_back()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "sandbox");

        await CheckAsync(new PartitionLedgers(), ledger, Flow("[dev, test]", "dev"));
    }

    [Fact]
    public async Task The_partition_that_keeps_the_ledger_runs_when_the_ledger_holds_its_records_alone()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev");
        var dev = Flow("[{ name: dev, keepLedger: true }, test]", "dev");

        await CheckAsync(new PartitionLedgers(), ledger, dev);

        Assert.Equal(FlowId.Of(_name), dev.First.Id);

        // The other partition keeps a ledger of its own and starts it empty; nothing about the kept ledger holds it back.
        await CheckAsync(new PartitionLedgers(), ledger, Flow("[{ name: dev, keepLedger: true }, test]", "test"));
    }

    [Fact]
    public async Task The_partition_that_keeps_the_ledger_is_refused_when_the_ledger_holds_another_partition_s_records()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev", "test");

        var ex = await Assert.ThrowsAsync<DeliveryException>(
            () => CheckAsync(new PartitionLedgers(), ledger, Flow("[{ name: dev, keepLedger: true }, test]", "dev")));

        Assert.Contains("Partition 'dev' keeps the ledger", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 record delivered to 'test'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_that_passed_is_not_asked_again_by_the_same_host()
    {
        var ledger = await LedgerAsync();
        var guards = new PartitionLedgers();
        var dev = Flow("[{ name: dev, keepLedger: true }, test]", "dev");
        await CheckAsync(guards, ledger, dev);

        // No run bound to its partitions can add another partition's record to the kept ledger; one written behind the
        // engine's back is not looked for again by the host that passed the check, and a host starting afresh finds it.
        await SeedAsync(FlowId.Of(_name), false, "test");
        await CheckAsync(guards, ledger, dev);
        await Assert.ThrowsAsync<DeliveryException>(() => CheckAsync(new PartitionLedgers(), ledger, dev));
    }

    [Fact]
    public async Task A_registry_driven_run_is_refused_while_no_partition_keeps_a_ledger_holding_records_of_a_registered_partition()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev");

        var ex = await Assert.ThrowsAsync<DeliveryException>(
            () => CheckAsync(new PartitionLedgers(), ledger, RegistryFlow(null, "test"), "dev", "test"));

        Assert.Contains("no partition keeps the ledger", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at the top of the flow: keepLedger: dev", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_driven_flow_is_not_held_back_by_records_of_a_partition_the_registry_no_longer_holds()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "sandbox");

        await CheckAsync(new PartitionLedgers(), ledger, RegistryFlow(null, "dev"), "dev", "test");
    }

    [Fact]
    public async Task A_registry_driven_flow_keeps_its_ledger_in_the_partition_keepLedger_names()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev");
        var dev = RegistryFlow("dev", "dev");
        var test = RegistryFlow("dev", "test");

        await CheckAsync(new PartitionLedgers(), ledger, dev, "dev", "test");
        await CheckAsync(new PartitionLedgers(), ledger, test, "dev", "test");

        Assert.Equal(FlowId.Of(_name), dev.First.Id);
        Assert.Equal(FlowId.Of(_name, "test"), test.First.Id);
    }

    [Fact]
    public async Task A_registry_driven_partition_that_keeps_the_ledger_is_refused_when_it_holds_another_partition_s_records()
    {
        var ledger = await LedgerAsync();
        await SeedAsync(FlowId.Of(_name), false, "dev", "test");

        var ex = await Assert.ThrowsAsync<DeliveryException>(
            () => CheckAsync(new PartitionLedgers(), ledger, RegistryFlow("dev", "dev"), "dev", "test"));

        Assert.Contains("Partition 'dev' keeps the ledger", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_that_names_no_partitions_passes_every_guard_untouched()
    {
        var ledger = await LedgerAsync();
        var source = _loader.ParseSource($$"""
            flowType: delivery
            name: {{_name}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.WellLog, key: [log_id] }
              lastModified: update_date
              work: work/welllog
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              protocol: storage
            """.ReplaceLineEndings("\n"), "flows/welllog.yaml");
        await SeedAsync(FlowId.Of(_name), false, "test");

        await CheckAsync(new PartitionLedgers(), ledger, source);
    }
}
