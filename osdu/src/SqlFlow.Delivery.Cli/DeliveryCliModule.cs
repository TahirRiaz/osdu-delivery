using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.Telemetry;
using SqlFlow.Cli.Hosting;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The OSDU module as the <c>sqlflow</c> CLI composes it: the delivery, retrieval and cache flow kinds (so SQLFlow's own
/// <c>validate</c>, <c>run</c> and <c>worker</c> verbs read and execute them), the ledger, templates and caches over the
/// module's database, and the module's own verbs: <c>check</c>, <c>preview</c>, <c>fixtures</c>, <c>records</c>, <c>config</c>,
/// <c>cache</c> and <c>template</c>.
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
                "sqlflow check    <flow.yaml> [--interface <name>] [--set k=v] [--connect]",
                "                                   The delivery preflight: the flow's documents, its mapping against the",
                "                                   pinned template, and the version of the cache it reads (needs --db:",
                "                                   templates and caches live in the module's database). With --connect it",
                "                                   also opens the flow's ingestion tables and reports what it would read.",
                "                                   A flow that declares interfaces is checked one interface at a time, each",
                "                                   with its route, its ledger and what it waits for; --interface checks one.",
            ],
            DeliveryVerbs.CheckAsync)
        {
            Flags = ["--connect"],
            ValueOptions = ["--interface"],
        },
        new CliVerb(
            "preview",
            [
                "sqlflow preview  <flow.yaml> [--interface <name>] [--key <key>] [--set k=v] [--out <file.json>]",
                "                                   Render one record as a delivery would, and send nothing: the scope's",
                "                                   first record, or the one --key names (a delivery key, an OSDU id the",
                "                                   ledger holds, a source key, or a JSON array of the key's parts). Says what",
                "                                   the next run would do with it, and shows the document, the files it would",
                "                                   upload and the requests the route would make (needs --db). --out writes",
                "                                   the whole preview as JSON.",
            ],
            DeliveryPreviewVerbs.PreviewAsync)
        {
            ValueOptions = ["--interface", "--key", "--out"],
        },
        new CliVerb(
            "fixtures",
            [
                "sqlflow fixtures update <flow.yaml> [--interface <name>] [--dry-run]",
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
            ValueOptions = ["--interface"],
        },
        new CliVerb(
            "records",
            [
                "sqlflow records list <flow.yaml> [--interface <name>] [--search <term>] [--contains]",
                "                     [--status <status>] [--max <n>]",
                "                                   An interface's records from the ledger: the key, the status, the OSDU id",
                "                                   and the last error of each (needs --db)",
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
            ValueOptions = ["--interface", "--search", "--status", "--max", "--key", "--attempts"],
        },
        new CliVerb(
            "config",
            [
                "sqlflow config list [--repo <id>]",
                "                                   The central configuration: the values the control plane supplies to the",
                "                                   runs it queues, so a flow's ${env:NAME} resolves from one place rather",
                "                                   than from every node (needs --db)",
                "sqlflow config effective --repo <id>",
                "                                   What a run of that repository's flows is given: the control plane's",
                "                                   properties with the repository's own over them (needs --db)",
                "sqlflow config set <name> --value <value> [--repo <id>] [--description <text>]",
                "                                   Set a property, for every repository or for one. The value is a",
                "                                   non-secret value or a ${env:...} or ${keyvault:...} reference a node",
                "                                   resolves, never a secret (needs --db)",
                "sqlflow config remove <name> [--repo <id>]",
            ],
            DeliveryConfigVerbs.ConfigAsync)
        {
            Subcommands = ["list", "effective", "set", "remove"],
            ValueOptions = ["--repo", "--value", "--description"],
        },
        new CliVerb(
            "cache",
            [
                "sqlflow cache list <partition | cache.yaml>",
                "                                   The versions of a partition's cache: when each was captured, by which",
                "                                   run, and what it holds (needs --db)",
                "sqlflow cache import <cache.yaml> --from-dir <dir>",
                "                                   Write type files as a version of the cache, for offline work (needs --db).",
                "                                   A cache is captured from OSDU by running its cache flow: sqlflow run <cache.yaml>",
            ],
            DeliveryVerbs.CacheAsync)
        {
            Subcommands = ["list", "import"],
            ValueOptions = ["--from-dir"],
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
