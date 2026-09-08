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
