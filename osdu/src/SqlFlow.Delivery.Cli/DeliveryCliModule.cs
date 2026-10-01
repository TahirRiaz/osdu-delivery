using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.Telemetry;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The OSDU module as the <c>sqlflow</c> CLI composes it: the delivery, retrieval, cache, assertion and dimension flow kinds (so SQLFlow's
/// own <c>validate</c>, <c>run</c> and <c>worker</c> verbs read and execute them), the ledger, templates and caches over the
/// module's database, and the module's own verbs: <c>check</c>, <c>preview</c>, <c>values</c>, <c>fixtures</c>, <c>records</c>, <c>config</c>,
/// <c>partition</c>, <c>cache</c>, <c>template</c>, <c>assertions</c> and <c>dimensions</c>.
/// </summary>
/// <remarks>
/// A command's database is the catalog the command line names (<c>--db</c>, else <c>${env:SQLFLOW_CATALOG_DB}</c>) unless
/// the module has a connection of its own in <c>SQLFLOW_OSDU_DB</c>. A worker node opens no catalog connection at all, so
/// there the module always reads its database through that reference, and says so when it is unset.
/// </remarks>
public sealed class DeliveryCliModule : ICliModule
{
    /// <summary>The module's name: what the CLI calls it in errors, and the name its database is registered under.</summary>
    public string Name => OsduSchema.Module;

    public IReadOnlyList<CliVerb> Verbs =>
    [
        new CliVerb(
            "check",
            [
                "sqlflow check    <flow.yaml> [--interface <name>] [--partition <id>] [--set k=v] [--connect]",
                "                                   The delivery preflight: the flow's documents, its mapping against the",
                "                                   pinned template, and the version of the cache it reads (needs --db:",
                "                                   templates and caches live in the module's database). With --connect it",
                "                                   also opens the flow's ingestion tables and reports what it would read.",
                "                                   A flow that declares interfaces is checked one interface at a time, each",
                "                                   with its route, its ledger and what it waits for; --interface checks one.",
                "                                   A flow that names its partitions is checked in the one --partition names.",
            ],
            DeliveryVerbs.CheckAsync)
        {
            Flags = ["--connect"],
            ValueOptions = ["--interface", "--partition"],
        },
        new CliVerb(
            "preview",
            [
                "sqlflow preview  <flow.yaml> [--interface <name>] [--partition <id>] [--key <key>] [--set k=v] [--out <file.json>]",
                "                                   Render one record as a delivery would, and send nothing: the scope's",
                "                                   first record, or the one --key names (a delivery key, an OSDU id the",
                "                                   ledger holds, a source key, or a JSON array of the key's parts). Says what",
                "                                   the next run would do with it, and shows the document, the files it would",
                "                                   upload and the requests the route would make (needs --db). --out writes",
                "                                   the whole preview as JSON.",
            ],
            DeliveryPreviewVerbs.PreviewAsync)
        {
            ValueOptions = ["--interface", "--partition", "--key", "--out"],
        },
        new CliVerb(
            "values",
            [
                "sqlflow values   <flow.yaml> [--interface <name>] [--partition <id>] [--target <osdu.path>]... [--set k=v]",
                "                 [--max-rows <n>] [--samples <n>] [--skip <n>] [--rows <file.csv>] [--out <file.json>]",
                "                                   Find the rows that will not give the mapping's variables the values the",
                "                                   template expects: every row of the scope rendered as a delivery renders it,",
                "                                   and every value held to the template's rules. Says, variable by variable,",
                "                                   how many rows hold the record, write a value the template does not accept,",
                "                                   or leave the variable out, with each reason, the values behind it and",
                "                                   example records (needs --db). --target checks one variable and what is",
                "                                   inside it (repeat it for more); --max-rows reads that many rows (10,000 by",
                "                                   default, 0 for the whole scope); --samples names that many examples per",
                "                                   reason after the first --skip; --rows writes every failing row to CSV;",
                "                                   --out writes the whole check as JSON. Exits 1 when a row is held or",
                "                                   writes a value the template does not accept. Nothing is written elsewhere.",
            ],
            DeliveryValueCheckVerbs.ValuesAsync)
        {
            ValueOptions = ["--interface", "--partition", "--target", "--max-rows", "--samples", "--skip", "--rows", "--out"],
        },
        new CliVerb(
            "fixtures",
            [
                "sqlflow fixtures update <flow.yaml> [--interface <name>] [--partition <id>] [--dry-run]",
                "                                   Render the fixtures of the flow's mapping as the preflight does and write",
                "                                   each into its expected block, leaving the rest of the file as written",
                "                                   (needs --db: the pinned template and cache live in the module's database).",
                "                                   A fixture whose record would be held is left and named; --dry-run says",
                "                                   what would change and writes nothing.",
            ],
            DeliveryFixtureVerbs.FixturesAsync)
        {
            Subcommands = ["update"],
            Flags = ["--dry-run"],
            ValueOptions = ["--interface", "--partition"],
        },
        new CliVerb(
            "records",
            [
                "sqlflow records list <flow.yaml> [--interface <name>] [--partition <id>] [--search <term>] [--contains]",
                "                     [--status <status>] [--max <n>]",
                "                                   An interface's records from the ledger: the key, the status, the OSDU id",
                "                                   and the last error of each (needs --db). A flow that names its partitions",
                "                                   keeps a ledger per partition: --partition names the one to read",
                "sqlflow records show <flow.yaml> --key <delivery key | source key> [--interface <name>] [--attempts <n>]",
                "                                   One record with every try it took: what each sent, what the target",
                "                                   answered step by step, and why it stopped (needs --db)",
                "sqlflow records release <flow.yaml> [--interface <name>] [--key <delivery key>]...",
                "                                   Release blocked records (held, failed, deleted) back to pending, all",
                "                                   of them or the ones named, once their cause is fixed (needs --db)",
            ],
            DeliveryRecordVerbs.RecordsAsync)
        {
            Subcommands = ["list", "show", "release"],
            Flags = ["--contains"],
            ValueOptions = ["--interface", "--partition", "--search", "--status", "--max", "--key", "--attempts"],
        },
        new CliVerb(
            "config",
            [
                "sqlflow config list [--repo <id>] [--partition <id>]",
                "                                   The central configuration: the values the control plane supplies to the",
                "                                   runs it queues, so a flow's ${env:NAME} resolves from one place rather",
                "                                   than from every node (needs --db)",
                "sqlflow config effective --repo <id> [--partition <id>]",
                "                                   What a run of that repository's flows is given: the control plane's",
                "                                   properties with the repository's own over them, and for a run bound to",
                "                                   --partition, that partition's values over both (needs --db)",
                "sqlflow config set <name> --value <value> [--repo <id>] [--partition <id>] [--description <text>]",
                "                                   Set a property, for every repository or for one, and for every partition",
                "                                   or for one. The value is a non-secret value or a ${env:...} or",
                "                                   ${keyvault:...} reference a node resolves, never a secret (needs --db)",
                "sqlflow config remove <name> [--repo <id>] [--partition <id>]",
            ],
            DeliveryConfigVerbs.ConfigAsync)
        {
            Subcommands = ["list", "effective", "set", "remove"],
            ValueOptions = ["--repo", "--partition", "--value", "--description"],
        },
        new CliVerb(
            "partition",
            [
                "sqlflow partition list",
                "                                   The partition registry: the OSDU partitions a flow that names none serves,",
                "                                   and the default one a run that names none runs in (needs --db)",
                "sqlflow partition add <name> [--description <text>] [--default]",
                "                                   Register a partition; the first one registered becomes the default. Each",
                "                                   repository describes its registry-driven flows in it at its next sync",
                "sqlflow partition describe <name> --description <text> | default <name> | remove <name>",
                "                                   Say what a partition is for, make it the default, or take it out of the",
                "                                   registry. Removing deletes nothing kept under it; the default is removed",
                "                                   only when it is the last partition (needs --db)",
            ],
            DeliveryPartitionVerbs.PartitionAsync)
        {
            Subcommands = ["list", "add", "describe", "default", "remove"],
            Flags = ["--default"],
            ValueOptions = ["--description"],
        },
        new CliVerb(
            "cache",
            [
                "sqlflow cache list <partition | cache.yaml> [--partition <id>]",
                "                                   The versions of a partition's cache: when each was captured, by which",
                "                                   run, and what it holds; for a cache flow that names its partitions, of",
                "                                   each partition it builds, or the one --partition names (needs --db)",
                "sqlflow cache import <cache.yaml> --from-dir <dir> [--partition <id>]",
                "                                   Write type files as a version of the cache, for offline work (needs --db).",
                "                                   A cache is captured from OSDU by running its cache flow: sqlflow run <cache.yaml>",
            ],
            DeliveryVerbs.CacheAsync)
        {
            Subcommands = ["list", "import"],
            ValueOptions = ["--from-dir", "--partition"],
        },
        new CliVerb(
            "template",
            [
                "sqlflow template capture --kind <kind> [--release <tag>] [--out <file.json>]",
                "                                   Save a kind's schema from the OSDU data definitions (newest release by",
                "                                   default), and with --out write the bundled schema beside the mapping that",
                "                                   pins it, so a repository can import it again without the network",
                "sqlflow template import <schema.json> --kind <kind> [--release <tag>] | import --from-dir <dir> --kind <kind>",
                "sqlflow template list | show --kind <kind> [--version <v>] | delete --kind <kind> --version <v>",
                "                                   The templates in the module's database: the OSDU schemas mappings pin (needs --db)",
            ],
            DeliveryVerbs.TemplateAsync)
        {
            Subcommands = ["capture", "import", "list", "show", "delete"],
            ValueOptions = ["--release", "--version", "--from-dir", "--out"],
        },
        new CliVerb(
            "assertions",
            [
                "sqlflow assertions list <flow.yaml> [--partition <id>] [--max <n>]",
                "                                   An assertion flow's runs in a partition, newest first: how many of their",
                "                                   tests passed, failed, warned, errored or were skipped (needs --db)",
                "sqlflow assertions status <flow.yaml> [--partition <id>]",
                "                                   Where each test stands: its latest result, or that it has not run, and",
                "                                   whether its definition changed since (needs --db)",
                "sqlflow assertions report <flow.yaml> [--partition <id>] [--run <n>] [--format json|md|html|junit] [--out <file>]",
                "                                   A run's full report (the latest, or the one --run names): every test and",
                "                                   assertion with what it expected, what it found and the records that failed",
                "                                   it. JUnit XML is what a CI server reads (needs --db). The tests themselves",
                "                                   run as any flow runs: sqlflow run <flow.yaml> --payload '{\"tests\":[\"name\"]}'",
            ],
            DeliveryAssertionVerbs.AssertionsAsync)
        {
            Subcommands = ["list", "status", "report"],
            ValueOptions = ["--partition", "--max", "--run", "--format", "--out"],
        },
        new CliVerb(
            "dimensions",
            [
                "sqlflow dimensions list <flow.yaml> [--partition <id>]",
                "                                   A dimension flow's dimensions in a partition: what each holds, when it",
                "                                   was built, its table, and a newer build that failed or is running",
                "                                   (needs --db)",
                "sqlflow dimensions table <flow.yaml> --dimension <name> [--search <text>] [--attr <attribute>=<value> ...] [--order value|key|records|id|<attribute>] [--desc] [--max <n>]",
                "                                   The dimension as one table, osdu.dim_<flow>_<dimension>: a row per key",
                "                                   and value it collects, a column per attribute, each row with the number",
                "                                   a table of facts joins on (needs --db)",
                "sqlflow dimensions values <flow.yaml> --dimension <name> [--search <text>] [--attr <attribute>=<value> ...] [--order value|records] [--removed] [--max <n>]",
                "                                   A dimension's values, each human-friendly value with its records, its",
                "                                   search filter and the keys most records hold (needs --db)",
                "sqlflow dimensions keys <flow.yaml> --dimension <name> [--value <value> | --left-out] [--search <text>] [--attr <attribute>=<value> ...] [--order arrival|count] [--max <n>]",
                "                                   The keys exactly as the index holds them (an id for a reference), each",
                "                                   with its label, its value or why it has none, and its search filter",
                "                                   (needs --db)",
                "sqlflow dimensions attributes <flow.yaml> --dimension <name> --attribute <name> [--attr <attribute>=<value> ...] [--value <value> ...] [--search <text>] [--max <n>]",
                "                                   The values an attribute of the dimension's keys holds, the most records",
                "                                   first, each with its keys; --attr and --value narrow it to the keys the",
                "                                   other picks of a cascade leave (needs --db)",
                "sqlflow dimensions filter <flow.yaml> --dimension <name> --value <value> [--value <value> ...]",
                "                                   The search that finds every record holding the values named: one query",
                "                                   a line on the console, ready to send, what it leaves out on the error",
                "                                   stream (needs --db)",
                "sqlflow dimensions search <flow.yaml> [--pick <dimension>=<value> ...] [--where <dimension>.<attribute>=<value> ...] [--kind <kind>] [--within <query>]",
                "                                   The search across dimensions: records holding a value picked in each",
                "                                   dimension (OR within one, AND across them), the query on the console,",
                "                                   the request to send with --json (needs --db)",
                "sqlflow dimensions history <flow.yaml> --dimension <name> [--max <n>]",
                "                                   The builds, newest first: what each found, read and changed (needs --db)",
                "sqlflow dimensions changes <flow.yaml> --dimension <name> [--build <n>] [--value <value>] [--change added|removed|moved|restored] [--max <n>]",
                "                                   The change log, newest first: keys that arrived, left, came back or",
                "                                   moved to another value (needs --db)",
                "sqlflow dimensions remove <flow.yaml> --dimension <name>",
                "                                   Removes a dimension the flow no longer declares, and everything kept of",
                "                                   it in the partition, for good: refused for one the flow declares or a",
                "                                   cache flow captures; recorded as an activity of the flow (needs --db)",
                "sqlflow dimensions export <flow.yaml> --dimension <name> [--set values|keys|table] [--format csv|jsonl] [--out <file>]",
                "                                   The whole of a dimension's values or keys, each with its search filter,",
                "                                   or its table: its rows and columns as the database holds them",
                "                                   (needs --db). Building is a run:",
                "                                   sqlflow run <flow.yaml> --payload '{\"dimensions\":[\"name\"]}'",
            ],
            DeliveryDimensionVerbs.DimensionsAsync)
        {
            Subcommands = ["list", "table", "values", "keys", "attributes", "filter", "search", "history", "changes", "export", "remove"],
            ValueOptions = ["--partition", "--dimension", "--search", "--order", "--value", "--pick", "--where", "--attr", "--attribute", "--kind", "--within", "--max", "--build", "--change", "--set", "--format", "--out"],
            Flags = ["--removed", "--left-out", "--desc"],
        },
    ];

    public void ConfigureServices(CliModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A node has no catalog connection, so its module database is always the module's own reference; a command uses
        // the catalog the command line names unless the environment gives the module a database of its own.
        services.AddDatabase(OsduModuleDatabase.Create(
            services.Scope == CliServiceScope.Worker
                ? OsduModuleDatabase.EnvironmentReference
                : OsduModuleDatabase.ResolveReference(null, Environment.GetEnvironmentVariable)));

        services.Services.AddDeliveryKind();
        services.Services.AddDeliveryLedger();
        services.Services.AddScoped(provider => provider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<OsduDbContext>>().CreateDbContext());

        // A node runs for as long as the pod does, so its metrics are worth exporting; a one-shot command ends before
        // the first export and exports nothing by design. The node takes these settings from its environment, as it
        // takes every other one, and the provider lives with the container and flushes when it stops.
        if (services.Scope == CliServiceScope.Worker)
        {
            services.Services.AddNodeMetricsExport(TelemetryOptions.FromEnvironment(Environment.GetEnvironmentVariable));
        }
    }
}
