using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The tiered collection: a flow estate on disk scans into declared facts per document kind (with hooks
/// AST-extracted and broken files skipped loudly), the canonical run artifacts replay into observed facts
/// (run-scoped staging dissolved, source steps attributed to the source server, staleness detected, corrupt
/// artifacts tolerated), and the identity rules (server references, node keys) hold everywhere.
/// </summary>
public sealed class LineageCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageCollectorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private const string IngestionYaml = """
        flowType: ing
        name: load-orders
        connections:
          src: ${env:SQLFLOW_CONN_SRC}
          dwh: ${env:SQLFLOW_CONN_DWH}
        source: { server: src, object: Staging.dbo.Orders }
        target: { server: dwh, object: DW.dbo.Orders }
        load:
          keyColumns: [OrderID]
        postProcess: "EXEC DW.audit.usp_Stamp; INSERT INTO DW.audit.LoadLog (At) SELECT GETUTCDATE();"
        """;

    [Fact]
    public void FlowSet_CollectsEveryKind_WithDeclaredEndpoints()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        Write("orders-watch.flow.yaml", """
            flowType: hc
            name: orders-watch
            connections:
              dwh: ${env:SQLFLOW_CONN_DWH}
            target: { server: dwh, object: DW.dbo.Orders }
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);
        Write("refresh.flow.yaml", """
            flowType: sp
            name: refresh-marts
            connections:
              dwh: ${env:SQLFLOW_CONN_DWH}
            procedure: { server: dwh, object: DW.dbo.usp_Refresh }
            """);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Equal(["load-orders", "orders-watch", "refresh-marts"],
            collected.Flows.Select(f => f.Node.Name).OrderBy(n => n, StringComparer.Ordinal));

        // ing: read source, write target, plus the post-process hook's facts via the AST.
        var ingestion = collected.Facts.Where(f => f.Flow == "load-orders").ToList();
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Reads && f.Name == "Orders" && f.Database == "Staging"
            && f.ServerRef == "${env:SQLFLOW_CONN_SRC}");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Writes && f.Name == "Orders" && f.Database == "DW");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Requires && f.Name == "usp_Stamp" && f.Schema == "audit");
        Assert.Contains(ingestion, f => f.Relation == LineageRelation.Writes && f.Name == "LoadLog");

        // hc reads its target; sp requires its procedure.
        Assert.Contains(collected.Facts, f => f.Flow == "orders-watch" && f.Relation == LineageRelation.Reads && f.Name == "Orders");
        Assert.Contains(collected.Facts, f => f.Flow == "refresh-marts" && f.Relation == LineageRelation.Requires && f.Name == "usp_Refresh");

        // The server inventory carries both references for the derived tier.
        Assert.Contains("${env:SQLFLOW_CONN_SRC}", collected.Servers.Keys);
        Assert.Contains("${env:SQLFLOW_CONN_DWH}", collected.Servers.Keys);
    }

    [Fact]
    public void FlowSet_BrokenDocument_WarnsAndContinues()
    {
        Write("good.flow.yaml", IngestionYaml);
        Write("broken.flow.yaml", "flowType: ing\nname: [not, a, name");

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Single(collected.Flows);
        Assert.Contains(collected.Warnings, w => w.StartsWith("broken.flow.yaml: skipped:", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowSet_DuplicateFlowNames_Warn()
    {
        Write("a/one.flow.yaml", IngestionYaml);
        Write("b/two.flow.yaml", IngestionYaml);

        var collected = new FlowSetCollector().Collect(_root);

        Assert.Contains(collected.Warnings, w => w.Contains("'load-orders' is declared by 2 documents", StringComparison.Ordinal));
    }

    private void WriteRunArtifact(string flowName, string resultJson, DateTime writtenUtc)
    {
        var safe = Core.Runs.RunHistoryWriter.SafeName(flowName);
        var folder = Path.Combine(_root, ".sqlflow", "runs", safe, $"{writtenUtc:yyyyMMdd-HHmmss}_abcd1234");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "7e4f4d3b-1111-2222-3333-444455556666",
              "success": true,
              "writtenUtc": "{{writtenUtc:yyyy-MM-ddTHH:mm:ssZ}}",
              "result": {{resultJson}}
            }
            """);
    }

    [Fact]
    public void RunArtifacts_ReplayTheTrace_StagingDissolves_SidesAttribute()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """
            {
              "sqlTrace": [
                { "sequence": 1, "step": "source.select", "sql": "SELECT * FROM [Staging].[dbo].[Orders] WHERE Id > 5;" },
                { "sequence": 2, "step": "staging.create", "sql": "CREATE TABLE [DW].[dbo].[stg_orders_123] (Id int);" },
                { "sequence": 3, "step": "upsert.insert", "sql": "INSERT INTO [DW].[dbo].[Orders] SELECT * FROM [DW].[dbo].[stg_orders_123];" },
                { "sequence": 4, "step": "staging.drop", "sql": "DROP TABLE [DW].[dbo].[stg_orders_123];" },
                { "sequence": 5, "step": "postprocess", "sql": "INSERT INTO [DW].[audit].[LoadLog] (At) SELECT GETUTCDATE();" }
              ]
            }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));

        // The document predates the run: no staleness.
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        // Source reads attribute to the source server; target work to the target server.
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Reads && f.Database == "Staging"
            && f.ServerRef == "${env:SQLFLOW_CONN_SRC}" && f.Tier == LineageTier.Observed && f.RunId is not null);
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Writes && f.Name == "Orders" && f.Database == "DW"
            && f.ServerRef == "${env:SQLFLOW_CONN_DWH}");
        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Writes && f.Name == "LoadLog");

        // The run-scoped staging table dissolved.
        Assert.DoesNotContain(observed.Facts, f => f.Name.Contains("stg_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(observed.Warnings, w => w.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunArtifacts_DocumentNewerThanRun_WarnsStale()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        WriteRunArtifact("load-orders", """{ "sqlTrace": [] }""", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "load-orders.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("changed after its last run", StringComparison.Ordinal));
    }

    [Fact]
    public void RunArtifacts_CorruptJson_WarnsAndContinues()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        var folder = Path.Combine(_root, ".sqlflow", "runs", "load-orders", "20260611-060000_abcd1234");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), "{ not json");

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("run artifact unreadable", StringComparison.Ordinal));
        Assert.Empty(observed.Facts);
    }

    [Fact]
    public void RunArtifacts_UnmatchedRunFolder_Warns()
    {
        Write("load-orders.flow.yaml", IngestionYaml);
        Directory.CreateDirectory(Path.Combine(_root, ".sqlflow", "runs", "deleted-flow"));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Warnings, w => w.Contains("'deleted-flow' has no matching flow document", StringComparison.Ordinal));
    }

    [Fact]
    public void RunArtifacts_FileFlowDdl_IsObservedToo()
    {
        Write("csv-load.flow.yaml", """
            name: csv-load
            source: { type: csv, location: ./data/orders.csv }
            target:
              connection: ${env:SQLFLOW_CONN_DWH}
              schema: raw
              table: Orders
            """);
        WriteRunArtifact("csv-load", """
            { "ddlExecuted": [ "CREATE TABLE [raw].[Orders] ([Id] int NULL);" ] }
            """, new DateTime(2026, 6, 11, 6, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "csv-load.flow.yaml"), new DateTime(2026, 6, 1));

        var declared = new FlowSetCollector().Collect(_root);
        var observed = RunArtifactCollector.Collect(_root, declared.Flows);

        Assert.Contains(observed.Facts, f => f.Relation == LineageRelation.Creates && f.Name == "Orders" && f.Schema == "raw");
    }

    [Fact]
    public void ServerIdentity_ReferencesPassThrough_InlineLiteralsHash()
    {
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", ServerIdentity.From(" ${env:SQLFLOW_CONN_DWH} "));
        Assert.Equal("@warehouse", ServerIdentity.From("@warehouse"));

        var first = ServerIdentity.From("Server=db1;Database=DW;User ID=u;Password=hunter2");
        var second = ServerIdentity.From("server=DB1;database=dw;user id=U;password=HUNTER2");
        Assert.StartsWith("inline:", first, StringComparison.Ordinal);

        // Equal up to case: same identity; the literal (and its secret) never appears.
        Assert.Equal(first, second);
        Assert.DoesNotContain("hunter", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db1", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodeKey_CaseFolds_AndKeepsAbsentPartsDistinct()
    {
        Assert.Equal(NodeKey.For("@DWH", "Dw", "DBO", "Orders"), NodeKey.For("@dwh", "dw", "dbo", "orders"));
        Assert.NotEqual(NodeKey.For("@dwh", null, "dbo", "orders"), NodeKey.For("@dwh", "dw", "dbo", "orders"));
        Assert.NotEqual(NodeKey.For("@a", "dw", "dbo", "x"), NodeKey.For("@b", "dw", "dbo", "x"));
    }
}
