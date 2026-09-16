using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The sample delivery estate (<c>osdu/samples/recall-welllog</c>) copied to a temp repository, for the API tests that
/// need a real flow: the two OSDU flows with the pre and ing chains that feed them, the mappings they render with, and
/// the data folders a submission lands into. What the flows render with lives in the module's database (the templates
/// and the partition cache), so a test saves those rather than inventing a second estate that would drift from the real
/// one. This mirrors what the engine suite and the GUI e2e fixture do, for the same reason.
/// </summary>
internal static class SampleEstate
{
    /// <summary>The log source the sample well log flow's schedule fires for; the flow's one required parameter.</summary>
    public const string LogSource = "STAT_COMP";

    /// <summary>The well log flow of the sample estate, which streams payload files beside its documents.</summary>
    public const string FlowName = "recall-welllog";

    /// <summary>The wellbore master data flow: the chain that takes records through the API, with no payload files.</summary>
    public const string WellboreFlowName = "recall-wellbore";

    /// <summary>The pre-ingestion flows the wellbore flow lands its submissions for.</summary>
    public const string WellborePreFlow = "recall-wellbore-pre";

    public const string WellboreAliasesPreFlow = "recall-wellbore-aliases-pre";

    /// <summary>The ingestion flows between those pre flows and the wellbore flow.</summary>
    public const string WellboreIngFlow = "recall-wellbore-ing";

    public const string WellboreAliasesIngFlow = "recall-wellbore-aliases-ing";

    /// <summary>The mapping the wellbore flow pins, and the template version it fills.</summary>
    public const string WellboreMapping = "Wellbore@1.0.0";

    public const string WellboreTemplateKind = "osdu:wks:master-data--Wellbore:1.3.0";

    public const string WellboreTemplateVersion = "58d6bdbd9d066a06";

    /// <summary>The templates the sample mappings pin, by kind, and the bundled schema file each is saved from.</summary>
    private static readonly (string Kind, string File)[] Templates =
    [
        ("osdu:wks:work-product-component--WellLog:1.4.0", "osdu_wks_work-product-component--WellLog_1.4.0.json"),
        (WellboreTemplateKind, "osdu_wks_master-data--Wellbore_1.3.0.json"),
    ];

    /// <summary>The parts of the estate a run needs: the documents, what they render with, and the data folders.</summary>
    private static readonly string[] Parts = ["flows", "caches", "mappings", "references", "templates", "data"];

    /// <summary>
    /// Copies the estate into <paramref name="destination"/> and returns it. Nothing about the flows is rewritten: what
    /// executes is the sample flow as shipped, reading the ingestion tables its document declares and landing its
    /// submissions in the copied data folders, which is exactly what makes the copy safe to write into.
    /// </summary>
    public static string CopyTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var source = Locate();
        foreach (var part in Parts)
        {
            CopyDirectory(Path.Combine(source, part), Path.Combine(destination, part));
        }

        return destination;
    }

    /// <summary>The flow document of the copied estate, as the catalog stores it.</summary>
    public static string FlowYaml(string root, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return File.ReadAllText(Path.Combine(root, "flows", flowName + ".yaml"));
    }

    /// <summary>The repo-relative path of a flow document, as the catalog records it.</summary>
    public static string FlowPath(string flowName) => "flows/" + flowName + ".yaml";

    /// <summary>
    /// Brings the module's own schema up to date in the catalog database, exactly as the control plane's bootstrap does
    /// before it serves a request. The database itself is never created here: the suite is given a disposable one.
    /// </summary>
    public static async Task MigrateModuleAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // One migrate at a time in this process: the suites run in parallel and every one of them brings the schema up to
        // date first, and two migrates of the same schema at once fail rather than wait for each other.
        await ModuleMigrate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ModuleDatabases.MigrateAsync(
                OsduModuleDatabase.Create(),
                connectionString,
                allowCreate: false,
                appliedBy: "control plane module tests",
                appliedUtc: DateTime.UtcNow,
                catalogAppliedMigrations: CatalogDatabase.KnownMigrations).ConfigureAwait(false);
        }
        finally
        {
            ModuleMigrate.Release();
        }
    }

    /// <summary>Serializes the module migrate across the suites of this process.</summary>
    private static readonly SemaphoreSlim ModuleMigrate = new(1, 1);

    /// <summary>
    /// Saves the templates the sample mappings pin into the module's database at <paramref name="connectionString"/>,
    /// exactly as saving them on the Templates page does. Templates live in that database rather than the repository, so
    /// a run that renders the sample needs them there.
    /// </summary>
    public static async Task SaveTemplatesAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await MigrateModuleAsync(connectionString);
        var store = new OsduTemplateStore(() => Context(connectionString));
        foreach (var (kind, file) in Templates)
        {
            var path = Path.Combine(Locate(), "templates", file);
            var schema = TemplateSources.FromBundledJson(await File.ReadAllTextAsync(path), kind, DateTimeOffset.UtcNow, path);
            await store.SaveAsync(schema, "file " + file, "tests");
        }
    }

    /// <summary>
    /// Merges the sample cache records into the cache of the sample partition in the module's database, checked against
    /// the sample cache flow, exactly as 'sqlflow cache import' does. The well log flow reads the cache of the partition
    /// it delivers to. A database that already holds the same content keeps its current version.
    /// </summary>
    public static async Task SaveCacheAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await MigrateModuleAsync(connectionString);
        var root = Locate();
        var flow = new DeliveryDocumentLoader().LoadCache(Path.Combine(root, "caches", "osdu-reference-cache.yaml"));
        var store = new OsduCacheStore(() => Context(connectionString));
        var builder = new SnapshotBuilder(store, flow.Scope, flow.Name, TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);
        await builder.ImportDirectoryAsync(Path.Combine(root, "references"), flow.Types, new CacheCapture(null, "tests", "sample files"));
    }

    /// <summary>A context over the module's schema in the catalog database the suite was given.</summary>
    public static OsduDbContext Context(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString));
    }

    /// <summary>
    /// The estate as the build copied it next to the test binaries. It is copied rather than read out of the
    /// repository so the suite works wherever the build output lands, which walking up from the binaries does not.
    /// </summary>
    private static string Locate()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "samples");
        if (!Directory.Exists(copied))
        {
            throw new DirectoryNotFoundException(
                $"The sample estate was not copied next to the test binaries ({copied}). It is a Content item of this test project; rebuild the suite.");
        }

        return copied;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }
}
