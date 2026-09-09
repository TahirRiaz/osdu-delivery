using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The document-envelope parsing of the <c>schedule:</c> block: it is read once, alongside <c>flowType</c>, and
/// attached to every kind of flow document (the kind parser ignores the key). Pure parsing, no database.
/// </summary>
public sealed class YamlDocumentScheduleTests
{
    private static readonly YamlDocumentLoader Loader = TestFlowKind.Loader();

    [Fact]
    public void Parse_CronScheduleWithTimezone_IsCaptured()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              cron: "0 6 * * *"
              timezone: "Europe/Oslo"
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.NotNull(doc.Schedule);
        Assert.Equal("0 6 * * *", doc.Schedule!.Cron);
        Assert.Equal("Europe/Oslo", doc.Schedule.Timezone);
        Assert.Null(doc.Schedule.IntervalSeconds);
        Assert.True(doc.Schedule.Enabled);
    }

    [Fact]
    public void Parse_ScheduleValues_CarryTheFlowParametersAFireSupplies()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              cron: "0 6 * * *"
              values:
                logSource: STAT_COMP
                region: north
            source: ./orders.csv
            target: dbo.Orders
            """);

        // Without these a flow that declares a required parameter can never be scheduled: the fire supplies
        // nothing and every run fails validation before it reads anything.
        Assert.Equal(2, doc.Schedule!.Values.Count);
        Assert.Equal("STAT_COMP", doc.Schedule.Values["logSource"]);
        Assert.Equal("north", doc.Schedule.Values["region"]);
    }

    [Fact]
    public void Parse_ScheduleValues_AreValidatedLikeATriggersOwn()
    {
        const string Body = """
            flowType: test
            name: orders
            schedule:
              cron: "0 6 * * *"
              values:
                "not an identifier": x
            source: ./orders.csv
            target: dbo.Orders
            """;

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Body));
        Assert.Contains("schedule.values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ScheduleWithoutValues_HasNone()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              cron: "0 6 * * *"
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.Empty(doc.Schedule!.Values);
    }

    [Fact]
    public void Parse_ScheduleOperation_DefaultsToDeliver_AndRejectsAnUnknownOne()
    {
        const string Body = """
            flowType: test
            name: orders
            schedule:
              cron: "0 6 * * *"
              operation: {0}
            source: ./orders.csv
            target: dbo.Orders
            """;

        var verify = Loader.Parse(Body.Replace("{0}", "Verify", StringComparison.Ordinal));
        Assert.Equal("verify", verify.Schedule!.Operation);

        var plain = Loader.Parse(Body.Replace("operation: {0}", "enabled: true", StringComparison.Ordinal));
        Assert.Equal("deliver", plain.Schedule!.Operation);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Body.Replace("{0}", "reindex", StringComparison.Ordinal)));
        Assert.Contains("schedule.operation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IntervalSchedule_DefaultsTimezoneToUtc_AndHonorsEnabled()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              intervalSeconds: 900
              enabled: false
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.NotNull(doc.Schedule);
        Assert.Equal(900, doc.Schedule!.IntervalSeconds);
        Assert.Null(doc.Schedule.Cron);
        Assert.Equal("UTC", doc.Schedule.Timezone);
        Assert.False(doc.Schedule.Enabled);
    }

    [Fact]
    public void Parse_NoScheduleBlock_LeavesScheduleNull()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.Null(doc.Schedule);
    }

    [Fact]
    public void Parse_EmptyScheduleBlock_IsTreatedAsNoSchedule()
    {
        // A schedule block with neither cron nor interval declares nothing to fire, so it is not a (broken) schedule.
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              timezone: "UTC"
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.Null(doc.Schedule);
    }

    [Fact]
    public void Parse_ScalarSchedule_IsCapturedAsAMembershipReference()
    {
        // `schedule: nightly` is a bare scalar: this flow JOINS the shared schedule named nightly. It carries the
        // name and never a cadence (joining is membership, not a copy); the repo scan binds it to the definition.
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule: nightly
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.NotNull(doc.Schedule);
        Assert.True(doc.Schedule!.IsReference);
        Assert.Equal(["nightly"], doc.Schedule.Refs);
        Assert.Null(doc.Schedule.Cron);
        Assert.Null(doc.Schedule.IntervalSeconds);
    }

    [Fact]
    public void Parse_SequenceSchedule_JoinsEveryNamedSchedule()
    {
        // `schedule: [a, b]` lets one flow sit in more than one schedule: a nightly full refresh and an hourly
        // subset, say. Each named schedule fires on its own cadence and runs this flow as one of its members.
        var doc = Loader.Parse("""
            flowType: test
            name: dim_currency
            schedule: [dwh_nightly, dwh_small_hourly]
            source: ./dim_currency.csv
            target: dbo.DimCurrency
            """);

        Assert.NotNull(doc.Schedule);
        Assert.True(doc.Schedule!.IsReference);
        Assert.Equal(["dwh_nightly", "dwh_small_hourly"], doc.Schedule.Refs);
        Assert.Null(doc.Schedule.Cron);
    }

    [Fact]
    public void Parse_NamedInlineSchedule_CapturesTheNameAlongsideTheCadence()
    {
        // An inline block may carry a name: to publish itself for other flows to reference by that name.
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule:
              name: nightly
              cron: "0 6 * * *"
              timezone: "Europe/Oslo"
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.NotNull(doc.Schedule);
        Assert.Equal("nightly", doc.Schedule!.Name);
        Assert.Equal("0 6 * * *", doc.Schedule.Cron);
        Assert.Equal("Europe/Oslo", doc.Schedule.Timezone);
        Assert.False(doc.Schedule.IsReference);
        Assert.Empty(doc.Schedule.Refs);
    }

    [Fact]
    public void Parse_BlankScalarSchedule_LeavesScheduleNull()
    {
        var doc = Loader.Parse("""
            flowType: test
            name: orders
            schedule: ""
            source: ./orders.csv
            target: dbo.Orders
            """);

        Assert.Null(doc.Schedule);
    }
}
