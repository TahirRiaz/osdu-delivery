using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.Lineage.Extraction;

// Probe A: ScriptDom shape of Synapse-style CTAS (WITH clause then AS SELECT).
const string ctasSynapse =
    "CREATE TABLE dbo.Fact WITH (DISTRIBUTION = HASH(id)) AS SELECT id, amt FROM dbo.Stage;";

// Probe B: Fabric-style CTAS (no WITH clause).
const string ctasFabric = "CREATE TABLE dbo.Fact AS SELECT id, amt FROM dbo.Stage;";

// Probe C: CTAS with a CTE body.
const string ctasCte =
    "CREATE TABLE dbo.Fact WITH (DISTRIBUTION = ROUND_ROBIN) AS WITH s AS (SELECT id FROM dbo.Stage) SELECT id FROM s;";

foreach (var (label, sql) in new[] { ("Synapse CTAS", ctasSynapse), ("Fabric CTAS", ctasFabric), ("CTE CTAS", ctasCte) })
{
    Console.WriteLine($"==== PARSE: {label} ====");
    var parser = new TSql160Parser(initialQuotedIdentifiers: true);
    using var reader = new StringReader(sql);
    var fragment = parser.Parse(reader, out var errors);
    foreach (var e in errors) Console.WriteLine($"  PARSE ERROR line {e.Line}: {e.Message}");
    if (errors.Count == 0) Console.WriteLine("  (no parse errors)");
    if (fragment is TSqlScript script && script.Batches.Count > 0 && script.Batches[0].Statements.Count > 0)
    {
        var statement = script.Batches[0].Statements[0];
        Console.WriteLine($"  Statement type: {statement.GetType().Name}");
        if (statement is CreateTableStatement ct)
        {
            Console.WriteLine($"  SelectStatement null: {ct.SelectStatement is null}");
            Console.WriteLine($"  Definition null     : {ct.Definition is null}");
            Console.WriteLine($"  CtasColumns count   : {ct.CtasColumns?.Count ?? -1}");
        }
    }

    Console.WriteLine($"==== EXTRACT: {label} ====");
    Dump(TSqlLineageExtractor.Extract(sql, context: "probe", defaultDatabase: "db1"));
}

// Probe D: the SELECT INTO equivalent for comparison (what the file calls T-SQL's CTAS).
Console.WriteLine("==== EXTRACT: SELECT INTO equivalent ====");
Dump(TSqlLineageExtractor.Extract(
    "SELECT id, amt INTO dbo.Fact FROM dbo.Stage;", context: "probe", defaultDatabase: "db1"));

static void Dump(ScriptDependencies deps)
{
    foreach (var d in deps.Outbound)
        Console.WriteLine($"  OUT  {d.Table.Key}  [{string.Join(",", d.Operations)}]");
    foreach (var d in deps.Inbound)
        Console.WriteLine($"  IN   {d.Table.Key}  [{string.Join(",", d.Operations)}]  kind={d.Kind}");
    foreach (var p in deps.DataFlowPairs)
        Console.WriteLine($"  PAIR {p.Source.Key} -> {p.Target.Key}");
    foreach (var kv in deps.LocalDeps)
        Console.WriteLine($"  LDEP {kv.Key} <- [{string.Join(", ", kv.Value)}]");
    Console.WriteLine($"  CTAS set: [{string.Join(", ", deps.CtasCreated)}]");
    foreach (var w in deps.Warnings)
        Console.WriteLine($"  WARN {w}");
    if (deps.Warnings.Count == 0)
        Console.WriteLine("  (no warnings)");
}
