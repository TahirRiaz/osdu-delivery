using SqlFlow.Core.Lineage;
using SqlFlow.Lineage;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Offline end-to-end through the real service over real estates on disk: the harness writes documents and
/// canonical run artifacts, the service computes, the tests pin the resulting PLAN: the contract the batch
/// runner will consume. This is the layer that proves the tiers compose, not just that each part works.
/// </summary>
public sealed class LineageServiceTests
{
    [Fact]
    public async Task DeclaredEstate_PlansTheMedallionOrder()
    {
        using var estate = new LineageEstateHarness()
            .Flow("bronze/load-customers.flow.yaml", LineageEstateHarness.Ingestion("load-customers", "Staging.dbo.Customers", "DW.dbo.Customers"))
            .Flow("bronze/load-orders.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("silver/build-facts.flow.yaml", LineageEstateHarness.Ingestion("build-facts", "DW.dbo.Orders", "DW.dbo.FactOrders", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Flow("gold/watch.flow.yaml", LineageEstateHarness.HealthCheck("orders-watch", "DW.dbo.FactOrders", "OrderDate"));

        var report = await estate.ComputeAsync();

        Assert.Equal(
            [["load-customers", "load-orders"], ["build-facts"], ["orders-watch"]],
            LineageEstateHarness.Waves(report));
        Assert.Empty(report.Cycles);
        Assert.Equal([LineageTier.Declared, LineageTier.Observed], report.TiersUsed);
    }

    [Fact]
    public async Task ObservedTier_RevealsWhatTheYamlDoesNot()
    {
        // The post-process hook truth lives only in the executed trace: a side table written at run time.
        // The observed tier connects its reader; without it the reader floats free.
        using var estate = new LineageEstateHarness()
            .Flow("load-orders.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("audit-watch.flow.yaml", LineageEstateHarness.HealthCheck("audit-watch", "DW.audit.LoadLog", "At"))
            .Run("load-orders", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("source.select", "SELECT * FROM [Staging].[dbo].[Orders];"),
                ("staging.create", "CREATE TABLE [DW].[dbo].[stg_orders_99] (Id int);"),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] SELECT * FROM [DW].[dbo].[stg_orders_99];"),
                ("staging.drop", "DROP TABLE [DW].[dbo].[stg_orders_99];"),
                ("postprocess", "INSERT INTO [DW].[audit].[LoadLog] (At) SELECT GETUTCDATE();"));

        var withObserved = await estate.ComputeAsync(includeObserved: true);
        var withoutObserved = await estate.ComputeAsync(includeObserved: false);

        Assert.Equal([["load-orders"], ["audit-watch"]], LineageEstateHarness.Waves(withObserved));
        Assert.Equal([["audit-watch", "load-orders"]], LineageEstateHarness.Waves(withoutObserved));

        // The run-scoped staging table never reaches the graph; the observed edges carry their run stamp.
        Assert.DoesNotContain(withObserved.Objects, o => o.Name.Contains("stg_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(withObserved.Edges, e => e.Tier == LineageTier.Observed && e.ObservedRunId is not null);
    }

    [Fact]
    public async Task StaleDocument_IsWarnedInTheReport()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load-orders.flow.yaml",
                LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"),
                writeUtc: new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc))
            .Run("load-orders", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] SELECT 1;"));

        var report = await estate.ComputeAsync();

        Assert.Contains(report.Warnings, w => w.Contains("changed after its last run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HookLineage_OrdersTheHookTargetsReaders()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load.flow.yaml", LineageEstateHarness.Ingestion(
                "load-orders", "Staging.dbo.Orders", "DW.dbo.Orders",
                postProcess: "INSERT INTO DW.audit.Stamps (At) SELECT GETUTCDATE();"))
            .Flow("stamp-export.flow.yaml", LineageEstateHarness.Export("stamp-export", "DW.audit.Stamps"));

        var report = await estate.ComputeAsync();

        Assert.Equal([["load-orders"], ["stamp-export"]], LineageEstateHarness.Waves(report));
    }

    [Fact]
    public async Task ImpactAndDependencyQueries_WalkTheWholeChain()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load.flow.yaml", LineageEstateHarness.Ingestion("load-orders", "Staging.dbo.Orders", "DW.dbo.Orders"))
            .Flow("facts.flow.yaml", LineageEstateHarness.Ingestion("build-facts", "DW.dbo.Orders", "DW.dbo.FactOrders", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Flow("export.flow.yaml", LineageEstateHarness.Export("fact-export", "DW.dbo.FactOrders"));

        var report = await estate.ComputeAsync();

        var subject = Assert.Single(LineageService.ResolveSubject(report, "DW.dbo.Orders"));
        var downstream = LineageService.Downstream(report, subject);
        Assert.Contains("flow:build-facts", downstream);
        Assert.Contains("flow:fact-export", downstream);
        Assert.Contains(downstream, n => n.EndsWith("|dw|dbo|factorders", StringComparison.Ordinal));

        var upstreamOfExport = LineageService.Upstream(report, "fact-export");
        Assert.Contains("flow:build-facts", upstreamOfExport);
        Assert.Contains(upstreamOfExport, n => n.EndsWith("|dw|dbo|orders", StringComparison.Ordinal));
        Assert.Contains("flow:load-orders", upstreamOfExport);
    }

    [Fact]
    public async Task ResolveSubject_FlowNamesObjectsAndAmbiguity()
    {
        using var estate = new LineageEstateHarness()
            .Flow("a.flow.yaml", LineageEstateHarness.Ingestion("load-a", "SrcA.dbo.Orders", "DwA.dbo.Orders"))
            .Flow("b.flow.yaml", LineageEstateHarness.Ingestion("load-b", "SrcB.dbo.Orders", "DwB.dbo.Orders"));

        var report = await estate.ComputeAsync();

        Assert.Equal(["flow:load-a"], LineageService.ResolveSubject(report, "load-a"));
        Assert.Single(LineageService.ResolveSubject(report, "DwA.dbo.Orders"));

        // A bare name matching several identities comes back as ALL of them, never a silent pick.
        Assert.True(LineageService.ResolveSubject(report, "Orders").Count >= 4);
        Assert.Empty(LineageService.ResolveSubject(report, "NoSuchThing"));
    }

    [Fact]
    public async Task ServiceOutput_IsByteDeterministic()
    {
        using var estate = new LineageEstateHarness()
            .Flow("z.flow.yaml", LineageEstateHarness.Ingestion("zeta", "S.dbo.A", "DW.dbo.B"))
            .Flow("a.flow.yaml", LineageEstateHarness.Ingestion("alpha", "DW.dbo.B", "DW.dbo.C", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
            .Run("zeta", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[B] SELECT * FROM [S].[dbo].[A];"));

        var first = await estate.ComputeAsync();
        var second = await estate.ComputeAsync();

        // Everything except the generation timestamp must be identical.
        var json1 = System.Text.Json.JsonSerializer.Serialize(first with { GeneratedAtUtc = default });
        var json2 = System.Text.Json.JsonSerializer.Serialize(second with { GeneratedAtUtc = default });
        Assert.Equal(json1, json2);
    }

    [Fact]
    public async Task EmptyEstate_PlansNothing_Gracefully()
    {
        using var estate = new LineageEstateHarness();

        var report = await estate.ComputeAsync();

        Assert.Empty(report.Flows);
        Assert.Empty(report.ExecutionPlan.Waves);
        Assert.Empty(report.Cycles);
    }
}
