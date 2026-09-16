using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The two versions of the WellLog schema the suites deliver the sample rows as: the sample flow's 1.4.0, and 1.5.0, the
/// next version the Open Group publishes, saved from the data definitions as a fixture (Fixtures/templates). A pipeline on
/// 1.5.0 is the sample flow with the sample mapping pinned to it; delivering to a partition of its own, it reads that
/// partition's cache, which holds the sample records under that partition's ids.
/// </summary>
internal static class WellLogVersions
{
    public const string CurrentKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    public const string NextKind = "osdu:wks:work-product-component--WellLog:1.5.0";

    public const string CurrentMapping = "WellLog@1.4.0";

    public const string NextMapping = "WellLog@1.5.0";

    /// <summary>The content version of the WellLog 1.5.0 fixture, as <c>sqlflow template capture</c> saved it.</summary>
    public const string NextTemplateVersion = "2f8a99cb38d32480";

    public const string NextOrigin = "OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/work-product-component/WellLog.1.5.0.json";

    private const string CurrentTemplateVersion = "26a3c3441882db4f";

    /// <summary>Saves WellLog 1.5.0 into <paramref name="store"/>; saving it again changes nothing.</summary>
    public static async Task SaveNextTemplateAsync(ITemplateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates", NextKind.Replace(':', '_') + ".json");
        var schema = TemplateSources.FromBundledJson(File.ReadAllText(file), NextKind, new DateTimeOffset(2026, 9, 16, 12, 21, 35, TimeSpan.Zero), file);
        var saved = await store.SaveAsync(schema, NextOrigin, "tests");
        Assert.Equal(NextTemplateVersion, saved.Template.Version);
    }

    /// <summary>
    /// <paramref name="flow"/> as a pipeline named <paramref name="name"/> that renders WellLog 1.5.0 and delivers to
    /// <paramref name="partition"/>, with its mapping written under <paramref name="root"/>.
    /// </summary>
    public static FlowDefinition OnNextVersion(FlowDefinition flow, string name, string partition, string root)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var mappings = Path.Combine(root, "next-mappings-" + partition);
        Directory.CreateDirectory(mappings);
        File.WriteAllText(Path.Combine(mappings, NextMapping + ".yaml"), NextMappingDocument(partition));
        return flow with
        {
            Name = name,
            Render = flow.Render with
            {
                Mapping = NextMapping,
                MappingsDirectory = mappings,
                Parameters = new Dictionary<string, string>(flow.Render.Parameters, StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = partition },
            },
            Target = flow.Target with
            {
                Headers = new Dictionary<string, string>(flow.Target.Headers, StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = partition },
            },
        };
    }

    /// <summary>
    /// Imports the sample cache records as the cache of <paramref name="partition"/>, under that partition's ids, as the
    /// capture of the cache flow <paramref name="flowName"/>. The files are written under <paramref name="root"/>.
    /// </summary>
    public static async Task ImportPartitionCacheAsync(ICacheStore caches, string partition, string flowName, string root)
    {
        ArgumentNullException.ThrowIfNull(caches);
        var directory = Path.Combine(root, "references-" + partition);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(Samples.References, "*.json"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains(Samples.SampleCacheScope + ":", text, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, Path.GetFileName(file)), text.Replace(Samples.SampleCacheScope + ":", partition + ":", StringComparison.Ordinal));
        }

        var cacheFlow = new DeliveryDocumentLoader().LoadCache(Samples.CacheFlow);
        var builder = new SnapshotBuilder(caches, partition, flowName, new TestClock(Samples.SampleCacheCaptured), Samples.Logger<SnapshotBuilder>());
        await builder.ImportDirectoryAsync(directory, cacheFlow.Types, new CacheCapture(null, "tests", "sample files for " + partition));
    }

    /// <summary>
    /// The sample WellLog 1.4.0 mapping pinned to WellLog 1.5.0, with its entries untouched. Its fixtures, the regression
    /// suite the preflight renders against the partition's cache, are written for <paramref name="partition"/>: a render
    /// there reads that partition's references. Every edit is checked to have applied.
    /// </summary>
    private static string NextMappingDocument(string partition)
    {
        var text = File.ReadAllText(Path.Combine(Samples.Mappings, CurrentMapping + ".yaml")).ReplaceLineEndings("\n");
        text = Replace(text, "# Mapping: Recall well logs into the WellLog 1.4.0 template", "# Mapping: Recall well logs into the WellLog 1.5.0 template", 1);
        text = Replace(text, "\nversion: 1.4.0\n", "\nversion: 1.5.0\n", 1);
        text = Replace(text, $"  kind: {CurrentKind}\n  version: {CurrentTemplateVersion}\n", $"  kind: {NextKind}\n  version: {NextTemplateVersion}\n", 1);
        text = Replace(text, $"\"kind\": \"{CurrentKind}\"", $"\"kind\": \"{NextKind}\"", 2);
        if (partition == Samples.SampleCacheScope)
        {
            return text;
        }

        var at = text.IndexOf("\nfixtures:\n", StringComparison.Ordinal);
        Assert.True(at > 0, "The sample WellLog mapping has no fixtures section: the derived 1.5.0 mapping no longer follows it.");
        var fixtures = Replace(text[at..], $"dataPartition: {Samples.SampleCacheScope} }}", $"dataPartition: {partition} }}", 2);
        fixtures = Replace(fixtures, $"\"{Samples.SampleCacheScope}:", $"\"{partition}:", expected: null);
        return text[..at] + fixtures;
    }

    /// <summary>Replaces <paramref name="from"/>, which must occur <paramref name="expected"/> times, or at least once when that is null.</summary>
    private static string Replace(string text, string from, string to, int? expected)
    {
        var found = text.Split(from).Length - 1;
        Assert.True(
            expected is { } count ? found == count : found > 0,
            $"The sample WellLog mapping holds '{from.ReplaceLineEndings(" ")}' {found} time(s), not {expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "at least once"}: the derived 1.5.0 mapping no longer follows it.");
        return text.Replace(from, to, StringComparison.Ordinal);
    }
}
