using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Ordering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A repository's waves checked against the OSDU order of its own deliveries (osdu/docs/reference/concepts/osdu-order.md,
/// Checking a repository's waves), said once however many partitions a flow reaches; and which schedules run best before
/// which (Schedules), from the order of each partition with every repository's deliveries together.
/// </summary>
public sealed class RepositoryOrderTests
{
    private static readonly Guid Here = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid There = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string Well = "osdu:wks:master-data--Well:1.0.0";
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.0.0";
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.0.0";

    private static readonly SchemaNode WellNode = new(Well, "master-data--Well", []);
    private static readonly SchemaNode WellboreNode = new(Wellbore, "master-data--Wellbore", [new SchemaRelationship("osdu.data.WellID", ["master-data--Well"])]);
    private static readonly SchemaNode WellLogNode = new(WellLog, "work-product-component--WellLog", [new SchemaRelationship("osdu.data.WellboreID", ["master-data--Wellbore"])]);

    private static readonly Dictionary<Guid, string> Names = new() { [Here] = "estate", [There] = "masters" };

    [Fact]
    public void Waves_that_run_a_provider_after_the_flow_referring_to_it_are_reported_with_the_partition_and_the_waves()
    {
        var read = Read(
            Held(Here, "wells", WellNode, null, "dev"),
            Held(Here, "wellbores", WellboreNode, ["osdu.data.WellID"], "dev"));

        var report = RepositoryOrder.Check(Here, RepositoryOrder.Plan(read, Names), Waves(("wells", 2), ("wellbores", 1)), read.Problems);

        Assert.False(report.Holds);
        var line = Assert.Single(report.Warnings());
        Assert.StartsWith("The OSDU order of partition 'dev' is not kept: ", line, StringComparison.Ordinal);
        Assert.Contains("lineage runs wells in wave 2 and wellbores in wave 1", line, StringComparison.Ordinal);

        var kept = RepositoryOrder.Check(Here, RepositoryOrder.Plan(read, Names), Waves(("wells", 1), ("wellbores", 2)), read.Problems);
        Assert.True(kept.Holds);
        Assert.Empty(kept.Warnings());
    }

    [Fact]
    public void A_flow_serving_every_partition_breaching_in_each_named_partition_is_reported_once()
    {
        var read = Read(
            Held(Here, "wells", WellNode, null, OsduOrder.EveryPartition),
            Held(Here, "wellbores", WellboreNode, ["osdu.data.WellID"], OsduOrder.EveryPartition),
            Held(Here, "logs-dev", WellLogNode, ["osdu.data.WellboreID"], "dev"),
            Held(Here, "logs-test", WellLogNode, ["osdu.data.WellboreID"], "test"));

        var report = RepositoryOrder.Check(
            Here, RepositoryOrder.Plan(read, Names), Waves(("wells", 3), ("wellbores", 1), ("logs-dev", 4), ("logs-test", 4)), read.Problems);

        // Checked in the partition standing for every one, and in dev and test, where its flows are delivered too.
        Assert.Equal(["*", "dev", "test"], report.Checks.Select(c => c.Plan.Partition));
        Assert.All(report.Checks, c => Assert.Single(c.Result.Breaches));
        var line = Assert.Single(report.Warnings());
        Assert.StartsWith("The OSDU order of every registered partition is not kept: ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_whose_lineage_set_no_wave_is_not_checked_but_what_could_not_be_read_is_still_said()
    {
        var read = new CatalogDeliveries(
            [Held(Here, "wells", WellNode, null, "dev"), Held(Here, "wellbores", WellboreNode, ["osdu.data.WellID"], "dev")],
            ["flow 'logs' (flows/logs.yaml) is left out of the OSDU order: its mapping no longer loads (bad)."]);

        var report = RepositoryOrder.Check(Here, RepositoryOrder.Plan(read, Names), Waves(), read.Problems);

        Assert.False(report.WavesComputed);
        Assert.Empty(report.Checks);
        Assert.Equal(read.Problems, report.Warnings());
    }

    [Fact]
    public void Records_another_repository_delivers_break_nothing_here_and_its_breaches_are_its_own()
    {
        var read = Read(
            Held(There, "wells", WellNode, null, "dev"),
            Held(There, "wellbores", WellboreNode, ["osdu.data.WellID"], "dev"),
            Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev"));

        // The other repository's flows run in its own schedules; only the repository asked about is checked.
        var report = RepositoryOrder.Check(Here, RepositoryOrder.Plan(read, Names), Waves(("logs", 1)), read.Problems);
        Assert.True(report.Holds);
        Assert.Empty(report.Warnings());
        Assert.Equal("wellbores", Assert.Single(Assert.Single(report.Checks).Result.CrossRepository).Providers.Single().Flow);
    }

    [Fact]
    public void A_schedule_whose_flows_refer_to_records_another_repository_s_schedule_delivers_names_it_with_why()
    {
        var plans = RepositoryOrder.Plan(Read(Held(There, "wellbores", WellboreNode, [], "dev"), Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev")), Names);
        var masters = Schedule(There, "masters-nightly");
        var logs = Schedule(Here, "logs-hourly");

        var orders = ScheduleOrders.Of(plans, [masters, logs], [Member(masters, "wellbores"), Member(logs, "logs")], []).Schedules;

        var first = Assert.Single(orders.Single(o => o.Schedule == logs).RunFirst);
        Assert.Equal((masters, false, ScheduleTiming.Unknown), (first.Schedule, first.SameRepository, first.Timing));
        var reason = Assert.Single(first.Because);
        Assert.Equal(
            ("logs", "work-product-component--WellLog", "master-data--Wellbore", "masters", "wellbores"),
            (reason.Flow, reason.EntityType, reason.RefersTo, reason.ProviderRepository, reason.ProviderFlow));
        Assert.Equal(["dev"], reason.Partitions);

        // Nothing the masters deliver refers to the logs.
        Assert.Empty(orders.Single(o => o.Schedule == masters).RunFirst);
    }

    [Fact]
    public void Flows_one_schedule_runs_are_ordered_by_its_waves_and_name_no_other_schedule()
    {
        var plans = RepositoryOrder.Plan(Read(Held(Here, "wellbores", WellboreNode, [], "dev"), Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev")), Names);
        var nightly = Schedule(Here, "nightly");
        var hourly = Schedule(Here, "hourly");

        // The nightly runs both; the hourly runs the logs alone, and the wellbores run in the nightly.
        var orders = ScheduleOrders.Of(plans, [nightly, hourly], [Member(nightly, "wellbores"), Member(nightly, "logs"), Member(hourly, "logs")], []).Schedules;

        Assert.Empty(orders.Single(o => o.Schedule == nightly).RunFirst);
        var first = Assert.Single(orders.Single(o => o.Schedule == hourly).RunFirst);
        Assert.Equal((nightly, true, ScheduleTiming.Unknown), (first.Schedule, first.SameRepository, first.Timing));

        // Chained behind it, directly or through another schedule, the hourly already fires after it.
        var middle = Schedule(Here, "middle");
        var chained = ScheduleOrders.Of(
            plans, [nightly, hourly, middle], [Member(nightly, "wellbores"), Member(hourly, "logs")], [(hourly.Id, "MIDDLE"), (middle.Id, "nightly")]).Schedules;
        Assert.True(Assert.Single(chained.Single(o => o.Schedule == hourly).RunFirst).Chained);
    }

    [Fact]
    public void A_delivery_no_schedule_runs_is_named_as_unscheduled_and_a_chain_declared_in_a_circle_ends()
    {
        var plans = RepositoryOrder.Plan(Read(Held(There, "wellbores", WellboreNode, [], "dev"), Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev")), Names);
        var logs = Schedule(Here, "logs");
        var other = Schedule(Here, "other");

        var orders = ScheduleOrders.Of(plans, [logs, other], [Member(logs, "logs")], [(logs.Id, "other"), (other.Id, "logs")]).Schedules;

        var order = Assert.Single(orders);
        Assert.Empty(order.RunFirst);
        var unscheduled = Assert.Single(order.Unscheduled);
        Assert.Equal(("logs", "wellbores", "masters"), (unscheduled.Flow, unscheduled.ProviderFlow, unscheduled.ProviderRepository));
    }

    [Fact]
    public void A_flow_serving_every_partition_names_each_partition_it_refers_in_once()
    {
        var plans = RepositoryOrder.Plan(
            Read(
                Held(There, "wellbores", WellboreNode, [], OsduOrder.EveryPartition),
                Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], OsduOrder.EveryPartition),
                Held(Here, "test-logs", WellLogNode, ["osdu.data.WellboreID"], "test")),
            Names);
        var masters = Schedule(There, "masters");
        var logs = Schedule(Here, "logs");

        var orders = ScheduleOrders.Of(plans, [masters, logs], [Member(masters, "wellbores"), Member(logs, "logs"), Member(logs, "test-logs")], []).Schedules;

        var because = Assert.Single(orders.Single(o => o.Schedule == logs).RunFirst).Because;
        Assert.Equal(["*", "test"], because.Single(r => r.Flow == "logs").Partitions);
        Assert.Equal(["test"], because.Single(r => r.Flow == "test-logs").Partitions);
    }

    [Theory]
    [InlineData(3, 2, true, ScheduleTiming.FiresAfter)]
    [InlineData(1, 2, true, ScheduleTiming.FiresFirst)]
    [InlineData(2, 2, true, ScheduleTiming.FiresFirst)]
    [InlineData(1, 2, false, ScheduleTiming.NotFiring)]
    public void How_the_fires_of_two_schedules_fall_is_read_from_their_next_fires(int mastersHour, int logsHour, bool mastersFire, ScheduleTiming expected)
    {
        var plans = RepositoryOrder.Plan(Read(Held(There, "wellbores", WellboreNode, [], "dev"), Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev")), Names);
        var day = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var masters = Schedule(There, "masters") with { Cron = "0 * * * *", NextFireUtc = day.AddHours(mastersHour), Paused = !mastersFire };
        var logs = Schedule(Here, "logs") with { Cron = "0 * * * *", NextFireUtc = day.AddHours(logsHour) };

        var overview = ScheduleOrders.Of(plans, [masters, logs], [Member(masters, "wellbores"), Member(logs, "logs")], []);

        var order = overview.Schedules.Single(o => o.Schedule.Id == logs.Id);
        Assert.Equal(expected, Assert.Single(order.RunFirst).Timing);
        Assert.Equal(expected is ScheduleTiming.FiresAfter or ScheduleTiming.NotFiring, order.OutOfOrder.Any());
        Assert.Empty(overview.Circles);
    }

    [Fact]
    public void Schedules_that_each_deliver_what_the_other_refers_to_are_named_as_a_circle()
    {
        var plans = RepositoryOrder.Plan(
            Read(
                Held(Here, "wells", WellNode, [], "dev"),
                Held(There, "wellbores", WellboreNode, ["osdu.data.WellID"], "dev"),
                Held(Here, "logs", WellLogNode, ["osdu.data.WellboreID"], "dev")),
            Names);
        var estate = Schedule(Here, "estate");
        var masters = Schedule(There, "masters");
        var alone = Schedule(Here, "alone");

        // The estate's wells go before the masters' wellbores, which go before the estate's logs.
        var overview = ScheduleOrders.Of(
            plans, [estate, masters, alone], [Member(estate, "wells"), Member(estate, "logs"), Member(masters, "wellbores"), Member(alone, "logs")], []);

        var circle = Assert.Single(overview.Circles);
        Assert.Equal(["estate", "masters"], circle.Select(s => s.Name));
        Assert.Equal(["masters"], overview.Schedules.Single(o => o.Schedule == alone).RunFirst.Select(f => f.Schedule.Name));
    }

    private static ScheduleName Schedule(Guid repo, string name) => new(Guid.NewGuid(), repo, Names[repo], name);

    private static ScheduleMemberFlow Member(ScheduleName schedule, string flow) => new(schedule.Id, schedule.RepoId, flow);

    private static CatalogDelivery Held(Guid repo, string flow, SchemaNode schema, string[]? filled, string partition)
        => new(
            repo,
            $"flows/{flow}.yaml",
            new OrderDelivery { Partition = partition, Repository = repo.ToString("D"), Flow = flow, Kind = schema.Kind, Filled = filled },
            schema);

    private static CatalogDeliveries Read(params CatalogDelivery[] deliveries) => new(deliveries, []);

    private static Dictionary<string, int> Waves(params (string Flow, int Wave)[] waves)
        => waves.ToDictionary(w => w.Flow, w => w.Wave, StringComparer.OrdinalIgnoreCase);
}
