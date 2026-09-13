using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The sample delivery estate (<c>samples/recall-welllog</c>) copied to a temp repository, for the platform tests
/// that need a run to genuinely execute. The one production flow kind needs its mapping and a drop to render from, which
/// the sample holds, and the template and the cache it reads, which live in the catalog; a test copies the estate and
/// saves those rather than inventing a second estate that would drift from the real one. This mirrors what the GUI e2e
/// fixture does, for the same reason.
/// </summary>
internal static class SampleEstate
{
    /// <summary>The log source the sample's drop was generated for; the flow's one required parameter.</summary>
    public const string LogSource = "STAT_COMP";

    /// <summary>
    /// The flow's name, which the copy keeps: the drop's manifest names the flow it was prepared for, and the
    /// intake refuses a drop prepared for a different one. A test scopes itself by repository instead, which is
    /// what makes the pipeline id unique anyway.
    /// </summary>
    public const string FlowName = "recall-welllog";

    /// <summary>The templates the sample mappings pin, by kind, and the bundled schema file each is saved from.</summary>
    private static readonly (string Kind, string File)[] Templates =
    [
        ("osdu:wks:work-product-component--WellLog:1.4.0", "osdu_wks_work-product-component--WellLog_1.4.0.json"),
        ("osdu:wks:master-data--Wellbore:1.3.0", "osdu_wks_master-data--Wellbore_1.3.0.json"),
    ];

    /// <summary>The parts of the estate a run needs: the documents, what they render with, and the drop itself.</summary>
    private static readonly string[] Parts = ["flows", "caches", "mappings", "references", "out"];

    /// <summary>
    /// Copies the estate into <paramref name="destination"/> and points the flow at the copied drop. The declared
    /// drop location is relative to the repository root in git, which is not where a temp copy sits; nothing else
    /// about the flow is touched, so what executes is the sample flow as shipped.
    /// </summary>
    public static string CopyTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var source = Locate();
        foreach (var part in Parts)
        {
            CopyDirectory(Path.Combine(source, part), Path.Combine(destination, part));
        }

        var flowFile = Path.Combine(destination, "flows", "recall-welllog.yaml");
        var drop = Path.Combine(destination, "out", "{logSource}").Replace('\\', '/');
        File.WriteAllText(flowFile, File.ReadAllText(flowFile).Replace("samples/recall-welllog/out/{logSource}", drop, StringComparison.Ordinal));
        return destination;
    }

    /// <summary>
    /// Saves the templates the sample mappings pin into the catalog at <paramref name="connectionString"/>, exactly as saving
    /// them on the Templates page does. Templates live in the catalog rather than the repository, so a run that renders
    /// the sample needs them there.
    /// </summary>
    public static async Task SaveTemplatesAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var store = new CatalogTemplateStore(() => CatalogDatabase.Create(connectionString));
        foreach (var (kind, file) in Templates)
        {
            var path = Path.Combine(Locate(), "templates", file);
            var schema = TemplateSources.FromBundledJson(await File.ReadAllTextAsync(path), kind, DateTimeOffset.UtcNow, path);
            await store.SaveAsync(schema, "file " + file, "tests");
        }
    }

    /// <summary>
    /// Writes the sample cache records into the catalog at <paramref name="connectionString"/> as a version of the sample
    /// cache, checked against the sample cache flow, exactly as 'sqlflow cache import' does. The sample flow reads the
    /// cache, and caches live in the catalog. A catalog that already holds the same content keeps its current version.
    /// </summary>
    public static async Task SaveCacheAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var root = Locate();
        var flow = new DeliveryDocumentLoader().LoadCache(Path.Combine(root, "caches", "osdu-reference-cache.yaml"));
        var store = new CatalogCacheStore(() => CatalogDatabase.Create(connectionString));
        var builder = new SnapshotBuilder(store, flow.Name, TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);
        await builder.ImportDirectoryAsync(Path.Combine(root, "references"), flow.Types, new CacheCapture(null, "tests", "sample files"), makeCurrent: true);
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
