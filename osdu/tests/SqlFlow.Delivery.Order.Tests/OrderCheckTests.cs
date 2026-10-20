using Xunit;
using static SqlFlow.Delivery.Order.Tests.OrderTestKit;

namespace SqlFlow.Delivery.Order.Tests;

public sealed class OrderCheckTests
{
    private static RepositoryWaves Waves(string repository, params (string Flow, int Wave)[] waves)
        => new(repository, waves.ToDictionary(w => w.Flow, w => w.Wave), new HashSet<string>());

    private static OrderPlan Estate(string logsRepository = "estate", string wellsRepository = "estate") => Plan(
        Deliver("wells", Well, [], repository: wellsRepository),
        Deliver("wellbores", Wellbore, ["osdu.data.WellID"], repository: "estate"),
        Deliver("logs", WellLog, ["osdu.data.WellboreID"], repository: logsRepository));

    [Fact]
    public void Waves_that_run_every_referred_type_first_keep_the_order()
    {
        var result = OrderCheck.Check(Estate(), Waves("estate", ("wells", 3), ("wellbores", 4), ("logs", 5)));
        Assert.True(result.Holds);
        Assert.Empty(result.CrossRepository);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    public void A_flow_whose_provider_runs_in_the_same_wave_or_later_breaks_the_order(int wells, int wellbores)
    {
        var result = OrderCheck.Check(Estate(), Waves("estate", ("wells", wells), ("wellbores", wellbores), ("logs", 9)));
        var breach = Assert.Single(result.Breaches);
        Assert.Equal((OrderBreachKind.NotEarlier, "wellbores", "wells"), (breach.Kind, breach.Dependent.Flow, breach.Provider.Flow));
        Assert.Contains($"runs wells in wave {wells} and wellbores in wave {wellbores}", breach.Why, StringComparison.Ordinal);
        Assert.Contains("osdu.data.WellID", breach.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_in_a_cycle_s_fallback_wave_breaks_the_order()
    {
        var waves = new RepositoryWaves("estate", new Dictionary<string, int> { ["wells"] = 1, ["wellbores"] = 7, ["logs"] = 7 }, new HashSet<string> { "WELLBORES", "logs" });
        var result = OrderCheck.Check(Estate(), waves);
        Assert.Equal(2, result.Breaches.Count);
        Assert.All(result.Breaches, b => Assert.Equal(OrderBreachKind.Unordered, b.Kind));
        Assert.Contains("fallback wave", result.Breaches[0].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_lineage_gave_no_wave_breaks_the_order()
    {
        var result = OrderCheck.Check(Estate(), Waves("estate", ("wellbores", 2), ("logs", 3)));
        var breach = Assert.Single(result.Breaches);
        Assert.Equal((OrderBreachKind.NoWave, "wells"), (breach.Kind, breach.Provider.Flow));
        Assert.Contains("gave wells no wave", breach.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Interfaces_of_one_source_are_ordered_by_the_source_and_never_breach()
    {
        var plan = Plan(
            Deliver("source", Wellbore, ["osdu.data.WellID"], repository: "estate", @interface: "wellbores"),
            Deliver("source", Well, [], repository: "estate", @interface: "wells"));
        Assert.True(OrderCheck.Check(plan, Waves("estate", ("source", 3))).Holds);
    }

    [Fact]
    public void A_reference_to_records_only_another_repository_delivers_is_listed_and_breaks_nothing()
    {
        var plan = Plan(
            Deliver("wellbores", Wellbore, [], repository: "master"),
            Deliver("logs", WellLog, ["osdu.data.WellboreID"], repository: "logs"));
        var result = OrderCheck.Check(plan, Waves("LOGS", ("logs", 3)));

        // The other repository's flows run in its own schedules: nothing here waits for them, and nothing is breached.
        Assert.True(result.Holds);
        var dependency = Assert.Single(result.CrossRepository);
        Assert.Equal(("logs", "master-data--Wellbore", "wellbores"), (dependency.Dependent.Flow, dependency.EntityType, Assert.Single(dependency.Providers).Flow));
    }

    [Fact]
    public void A_type_delivered_here_and_elsewhere_is_checked_here_and_listed_as_a_dependency_on_the_other_repository()
    {
        var plan = Plan(
            Deliver("wellbores", Wellbore, [], repository: "estate"),
            Deliver("wellbores-legacy", Wellbore, [], repository: "legacy"),
            Deliver("logs", WellLog, ["osdu.data.WellboreID"], repository: "estate"));
        var result = OrderCheck.Check(plan, Waves("estate", ("wellbores", 4), ("logs", 4)));

        Assert.Equal("wellbores", Assert.Single(result.Breaches).Provider.Flow);
        Assert.Equal("legacy", Assert.Single(Assert.Single(result.CrossRepository).Providers).Repository);
    }

    [Fact]
    public void A_reference_the_order_cut_is_not_checked()
    {
        var plan = Plan(
            Deliver("wellbores", Wellbore, ["osdu.data.DefinitiveTrajectoryID"], repository: "estate"),
            Deliver("trajectories", Trajectory, ["osdu.data.WellboreID"], repository: "estate"));
        var result = OrderCheck.Check(plan, Waves("estate", ("wellbores", 3), ("trajectories", 4)));
        Assert.True(result.Holds);
    }

    [Fact]
    public void Another_repository_s_flows_are_not_checked_against_this_one_s_waves()
    {
        var plan = Estate(logsRepository: "other");
        var result = OrderCheck.Check(plan, Waves("other", ("logs", 1)));
        Assert.True(result.Holds);
        Assert.Equal("wellbores", Assert.Single(Assert.Single(result.CrossRepository).Providers).Flow);
    }
}
