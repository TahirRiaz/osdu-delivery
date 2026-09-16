using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the module's flows contribute to SQLFlow's lineage (docs/lineage-design.md): a delivery flow reads its ingestion
/// tables, its payload files and the cache types its mapping resolves against, and writes the OSDU type its mapping fills;
/// a cache flow reads OSDU types and writes partition cache types; a retrieval flow reads OSDU types and lands files. The
/// sample estate is copied to a folder of its own, scanned with the module's kinds registered, and its waves ordered from
/// the files the pre flows read to the OSDU types the delivery flows write and the flows that read them back. A mapping
/// that cannot be read, or that lies outside the checkout, costs a flow its OSDU nodes with a warning and nothing else.
/// </summary>
public sealed class LineageTests : IDisposable
{
    private const string Platform = "${env:PETRODB_URL}";

    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "osdu-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var folder in new[] { "flows", "caches", "mappings", "data" })
        {
            Copy(Path.Combine(Samples.Root, folder), Path.Combine(_root, folder));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static YamlDocumentLoader Loader()
    {
        var documents = new DeliveryDocumentLoader();
        return YamlDocumentLoader.CreateDefault([new DeliveryFlowKind(documents), new CacheFlowKind(documents), new RetrievalFlowKind(documents)]);
    }

    private RegisteredFlowLineage Describe(string relative)
    {
        var path = PathOf(relative);
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(path));
        return document.DescribeLineage(new RegisteredLineageContext(path, _root));
    }

    private (CollectionResult Collected, LineageReport Report) Scan()
    {
        var collected = new FlowSetCollector(Loader()).Collect(_root);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
        return (collected, LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc));
    }

    private static string TypeKey(string kind)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.TypeSystem, Platform), "opendes", OsduKind.Group(kind), kind);

    private static string CacheKey(string name)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.CacheSystem, null), "opendes", OsduLineage.CacheGroup, name);

    private static string Datasets(RegisteredFlowLineage lineage, LineageRelation relation)
        => string.Join(", ", lineage.Datasets.Where(d => d.Relation == relation).Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}"));

    private static int WaveOf(LineageReport report, string flow)
    {
        var wave = report.ExecutionPlan.Waves.FirstOrDefault(w => w.Flows.Contains(flow, StringComparer.OrdinalIgnoreCase));
        Assert.True(wave is not null, $"'{flow}' is in no wave.");
        return wave!.Wave;
    }

    /// <summary>Replaces text in a copied document, whose line endings are read as LF whatever the checkout wrote.</summary>
    private void Rewrite(string relative, string from, string to)
    {
        var path = PathOf(relative);
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    [Fact]
    public void A_delivery_flow_reads_its_tables_payload_files_and_cache_types_and_writes_its_mappings_type()
    {
        var lineage = Describe("flows/recall-welllog.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            ["OsduSample.ing.WellLog", "OsduSample.ing.WellLogCurve"],
            lineage.Objects.Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(lineage.Objects, o => Assert.Equal(LineageRelation.Reads, o.Relation));
        var payload = Assert.Single(lineage.Files);
        Assert.Equal((LineageRelation.Reads, "../data/curves", "chunk_*.parquet"), (payload.Relation, payload.Location, payload.FilePattern));

        Assert.Equal(
            "osdu-type/opendes/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0",
            Datasets(lineage, LineageRelation.Writes));
        Assert.Equal(
            "osdu-cache/opendes/cache/LogCurveBusinessValue, osdu-cache/opendes/cache/UnitOfMeasure, osdu-cache/opendes/cache/Wellbore",
            Datasets(lineage, LineageRelation.Reads));
        var written = lineage.Datasets.Single(d => d.Relation == LineageRelation.Writes);
        Assert.Equal((Platform, (char?)':'), (written.Instance, written.Separator));
        Assert.All(lineage.Datasets.Where(d => d.Relation == LineageRelation.Reads), d => Assert.Null(d.Instance));
    }

    [Fact]
    public void A_record_only_delivery_flow_reads_no_payload_files()
    {
        var lineage = Describe("flows/recall-wellbore.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Files);
        Assert.Equal("osdu-type/opendes/master-data/osdu:wks:master-data--Wellbore:1.3.0", Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_file_protocol_flow_also_writes_the_dataset_kind_it_registers_files_as()
    {
        Rewrite("flows/recall-welllog.yaml", "protocol: osduWellLog", "protocol: osduFile");
        Rewrite("flows/recall-welllog.yaml", "    sessionThresholdChunks: 1\n", "    datasetKind: osdu:wks:dataset--File.Generic:1.0.0\n");

        var lineage = Describe("flows/recall-welllog.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            "osdu-type/opendes/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0, osdu-type/opendes/dataset/osdu:wks:dataset--File.Generic:1.0.0",
            Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_cache_flow_reads_its_kinds_and_writes_its_partitions_cache_types()
    {
        var lineage = Describe("caches/osdu-reference-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Objects);
        Assert.Empty(lineage.Files);
        Assert.Equal(
            "osdu-type/opendes/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, osdu-type/opendes/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*, "
            + "osdu-type/opendes/reference-data/osdu:wks:reference-data--VerticalMeasurementType:*, osdu-type/opendes/master-data/osdu:wks:master-data--Wellbore:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Equal(
            "osdu-cache/opendes/cache/UnitOfMeasure, osdu-cache/opendes/cache/LogCurveBusinessValue, osdu-cache/opendes/cache/VerticalMeasurementType, osdu-cache/opendes/cache/Wellbore",
            Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_retrieval_flow_reads_its_kinds_and_lands_record_files_and_a_manifest()
    {
        var lineage = Describe("flows/osdu-cache-sync.yaml");

        Assert.Equal(
            "osdu-type/opendes/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, osdu-type/opendes/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*, "
            + "osdu-type/opendes/reference-data/osdu:wks:reference-data--VerticalMeasurementType:*, osdu-type/opendes/master-data/osdu:wks:master-data--Wellbore:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Empty(Datasets(lineage, LineageRelation.Writes));
        Assert.Equal(
            ["samples/recall-welllog/out/metadata|part-*.jsonl", "samples/recall-welllog/out/metadata|manifest.json"],
            lineage.Files.Select(f => $"{f.Location}|{f.FilePattern}").ToList());
        Assert.All(lineage.Files, f => Assert.Equal(LineageRelation.Writes, f.Relation));

        // The runner resolves this relative location against its working directory, not the flow file: lineage says so.
        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("target.location 'samples/recall-welllog/out/metadata' is a relative path", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gzipped_retrieval_to_a_storage_uri_lands_gzipped_files_without_a_warning()
    {
        Rewrite("flows/osdu-cache-sync.yaml", "location: samples/recall-welllog/out/metadata",
            "location: https://lake.dfs.core.windows.net/raw/osdu/{date}\n  compression: gzip");

        var lineage = Describe("flows/osdu-cache-sync.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal("part-*.jsonl.gz", lineage.Files[0].FilePattern);
    }

    [Fact]
    public void The_sample_estate_orders_files_pre_ing_delivery_cache_and_retrieval_flows()
    {
        var (collected, report) = Scan();

        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "recall-welllog-pre") < WaveOf(report, "recall-welllog-ing"));
        Assert.True(WaveOf(report, "recall-welllog-ing") < WaveOf(report, "recall-welllog"));
        Assert.True(WaveOf(report, "recall-welllog-curves-ing") < WaveOf(report, "recall-welllog"));
        Assert.True(WaveOf(report, "recall-wellbore-ing") < WaveOf(report, "recall-wellbore"));

        // The cache captures the wellbores the wellbore flow delivers, and the well log flow renders against that cache.
        Assert.True(WaveOf(report, "recall-wellbore") < WaveOf(report, "osdu-reference-cache"));
        Assert.True(WaveOf(report, "osdu-reference-cache") < WaveOf(report, "recall-welllog"));
        Assert.True(WaveOf(report, "recall-wellbore") < WaveOf(report, "osdu-cache-sync"));

        // Every file node is where the flows read and land it, relative to the checkout.
        var files = report.Objects.Where(o => o.Kind == LineageNodeKind.File).Select(o => o.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["data/curves", "data/curves-meta", "data/wellbore", "data/wellbore-aliases", "data/welllog", "flows/samples/recall-welllog/out/metadata"],
            files);

        // One node per exact OSDU type written, one per pattern read, and one per cache type, each captioned by its system.
        var types = report.Objects.Where(o => o.Kind == LineageNodeKind.Dataset).ToDictionary(o => o.Key, StringComparer.Ordinal);
        Assert.Contains(TypeKey(Samples.WellLogKind), types.Keys);
        Assert.Contains(TypeKey(Samples.WellboreKind), types.Keys);
        Assert.Contains(TypeKey("osdu:wks:master-data--Wellbore:*"), types.Keys);
        Assert.Contains(CacheKey("Wellbore"), types.Keys);
        Assert.Equal("osdu-type", ServerIdentity.DatasetSystem(TypeKey(Samples.WellLogKind)));

        Assert.Contains(report.Edges, e => e.Flow == "recall-welllog" && e.Relation == LineageRelation.Writes && e.ObjectKey == TypeKey(Samples.WellLogKind));
        Assert.Contains(report.Edges, e => e.Flow == "recall-welllog" && e.Relation == LineageRelation.Reads && e.ObjectKey == CacheKey("UnitOfMeasure"));
        Assert.Contains(report.Edges, e => e.Flow == "osdu-reference-cache" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));
        Assert.Contains(report.Edges, e => e.Flow == "osdu-cache-sync" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));
        Assert.DoesNotContain(report.Edges, e => e.Flow == "osdu-reference-cache" && e.ObjectKey == TypeKey(Samples.WellLogKind));

        // The only thing the scan has to say about the module's flows is where the retrieval's files may land.
        Assert.Single(collected.Warnings, w => w.Contains("flow '", StringComparison.Ordinal) && w.Contains("lineage", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_mapping_costs_the_flow_its_osdu_nodes_with_a_warning_and_nothing_else()
    {
        File.Delete(PathOf("mappings/WellLog@1.4.0.yaml"));

        var lineage = Describe("flows/recall-welllog.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.Equal(
            "delivery flow 'recall-welllog' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read (Mapping 'WellLog@1.4.0' was not found under 'mappings'. Expected one of: WellLog@1.4.0.yaml, WellLog@1.4.0.yml, 1.4.0.yaml, 1.4.0.yml.).",
            warning);
        Assert.Empty(lineage.Datasets);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);

        var (collected, report) = Scan();
        Assert.Contains(collected.Warnings, w => w == "flows/recall-welllog.yaml: " + warning);
        Assert.True(WaveOf(report, "recall-welllog-ing") < WaveOf(report, "recall-welllog"));
    }

    [Fact]
    public void An_invalid_mapping_is_a_warning_naming_the_file_relative_to_the_checkout()
    {
        File.WriteAllText(PathOf("mappings/WellLog@1.4.0.yaml"), "documentType: mapping\nname: WellLog\nversion: [\n");

        var lineage = Describe("flows/recall-welllog.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith("delivery flow 'recall-welllog' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read (mappings/WellLog@1.4.0.yaml", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_filed_under_another_name_is_a_warning()
    {
        Rewrite("mappings/WellLog@1.4.0.yaml", "version: 1.4.0", "version: 1.4.1");

        var warning = Assert.Single(Describe("flows/recall-welllog.yaml").Warnings);

        Assert.Contains("mappings/WellLog@1.4.0.yaml: declares 'WellLog@1.4.1' but is filed as 'WellLog@1.4.0'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mappings_directory_outside_the_checkout_is_never_read()
    {
        Rewrite("flows/recall-welllog.yaml", "  mapping: WellLog@1.4.0\n", "  mapping: WellLog@1.4.0\n  mappings: ../../outside/mappings\n");

        var warning = Assert.Single(Describe("flows/recall-welllog.yaml").Warnings);

        Assert.Equal(
            "delivery flow 'recall-welllog' shows no OSDU type in lineage: render.mappings '../../outside/mappings' lies outside the repository, so mapping 'WellLog@1.4.0' is not read.",
            warning);
    }

    [Fact]
    public void The_search_for_a_mappings_directory_stops_at_the_checkout()
    {
        // The checkout is a folder of its own inside a folder holding a mappings directory the flow must not reach.
        var parent = Path.Combine(_root, "outer");
        var checkout = Path.Combine(parent, "repo");
        Copy(Path.Combine(_root, "flows"), Path.Combine(checkout, "flows"));
        Copy(Path.Combine(_root, "mappings"), Path.Combine(parent, "mappings"));
        var path = Path.Combine(checkout, "flows", "recall-welllog.yaml");
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(path));

        var lineage = document.DescribeLineage(new RegisteredLineageContext(path, checkout));

        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("was not found under 'flows/mappings'", warning, StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_reference_naming_a_path_is_refused_before_any_file_is_read()
    {
        Rewrite("flows/recall-welllog.yaml", "mapping: WellLog@1.4.0", "mapping: ../mappings/WellLog@1.4.0");

        var warning = Assert.Single(Describe("flows/recall-welllog.yaml").Warnings);
        Assert.Contains("Mapping reference '../mappings/WellLog@1.4.0' names a path", warning, StringComparison.Ordinal);

        var catalog = new MappingCatalog(Path.Combine(_root, "mappings"), new DeliveryDocumentLoader());
        Assert.Throws<FlowValidationException>(() => catalog.Load("..\\mappings\\WellLog@1.4.0"));
        Assert.Throws<FlowValidationException>(() => catalog.Load("WellLog@../1.4.0"));
        Assert.Equal("WellLog@1.4.0", catalog.Load("WellLog@1.4.0").Reference);
    }

    [Fact]
    public void A_flow_whose_partition_names_no_cache_keeps_its_tables_and_files_and_says_why_it_has_no_osdu_nodes()
    {
        Rewrite("flows/recall-welllog.yaml", "    data-partition-id: opendes\n", "    data-partition-id: \"open des\"\n");

        var lineage = Describe("flows/recall-welllog.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith(
            "delivery flow 'recall-welllog' shows no OSDU node in lineage: target.headers: data-partition-id 'open des' is neither a partition id",
            warning,
            StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);
    }

    [Fact]
    public void A_partition_reference_stays_its_text_and_a_literal_endpoint_is_identified_by_hash()
    {
        Rewrite("caches/osdu-reference-cache.yaml", "  endpoint: ${env:PETRODB_URL}", "  endpoint: https://osdu.example.com");
        Rewrite("caches/osdu-reference-cache.yaml", "data-partition-id: opendes", "data-partition-id: ${env:OSDU_PARTITION}");

        var lineage = Describe("caches/osdu-reference-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.All(lineage.Datasets, d => Assert.Equal("${env:OSDU_PARTITION}", d.Namespace));
        var (collected, report) = Scan();
        Assert.Contains(collected.Facts, f => f.Flow == "osdu-reference-cache" && f.ServerRef.StartsWith("dataset:osdu-type:inline:", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Facts, f => f.ServerRef.Contains("example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Objects, o => o.Key.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void An_osdu_type_the_graph_cannot_hold_is_left_out_with_a_warning()
    {
        var warnings = new List<string>();

        Assert.Null(OsduLineage.Type(LineageRelation.Writes, Platform, "opendes", "osdu:wks:master-data--Wellbore:*", "flow 'x'", "a write", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, Platform, "opendes", "not a kind", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, new string('e', DeclaredDataset.MaxInstanceLength + 1), "opendes", Samples.WellboreKind, "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(
            LineageRelation.Reads, Platform, "opendes", "osdu:wks:master-data--" + new string('W', DeclaredDataset.MaxPartLength) + ":1.0.0", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "opendes", "Well|bore", "flow 'x'", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "opendes", " ", "flow 'x'", warnings));

        Assert.Equal(6, warnings.Count);
        Assert.DoesNotContain(warnings, w => w.Contains(new string('e', 100), StringComparison.Ordinal));
        Assert.Equal("osdu:wks:master-data--Wellbore:1.3.0",
            OsduLineage.Type(LineageRelation.Writes, Platform, "opendes", Samples.WellboreKind, "flow 'x'", "a write", warnings)!.Name);
    }

    [Theory]
    [InlineData("osdu:wks:master-data--Wellbore:1.3.0", "master-data")]
    [InlineData("osdu:wks:reference-data--UnitOfMeasure:*", "reference-data")]
    [InlineData("osdu:wks:work-product-component--WellLog:1.4.0", "work-product-component")]
    [InlineData("osdu:wks:Manifest:1.0.0", "Manifest")]
    [InlineData("osdu:wks:*:*", "*")]
    [InlineData("osdu:wks:*-data--Wellbore:*", "*")]
    [InlineData("osdu:wks:master-data--*:*", "master-data")]
    public void A_kind_is_grouped_by_its_entity_types_prefix(string kind, string group)
        => Assert.Equal(group, OsduKind.Group(kind));

    [Fact]
    public async Task A_mapping_change_is_a_lineage_input_change_and_a_second_declaration_is_not()
    {
        using var catalog = new SqliteOsdu();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        var repoId = Guid.NewGuid();

        async Task ReconcileAsync()
        {
            await using var db = catalog.CreateDbContext();
            await sync.ReconcileAsync(db, repoId, _root, Utc, new List<string>(), CancellationToken.None);
        }

        async Task<bool> ChangedAsync()
        {
            await using var db = catalog.CreateDbContext();
            return await sync.MappingsChangedAsync(db, repoId, _root, CancellationToken.None);
        }

        // Nothing reconciled yet: the mappings on disk are new.
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        // A second file declaring a reference already on record is left out by the reconciliation, so it changes nothing.
        Directory.CreateDirectory(PathOf("mappings/copies"));
        File.Copy(PathOf("mappings/WellLog@1.4.0.yaml"), PathOf("mappings/copies/WellLog.yaml"));
        Assert.False(await ChangedAsync());
        Directory.Delete(PathOf("mappings/copies"), recursive: true);

        // An edit, a new mapping and a removed one each are.
        Rewrite("mappings/WellLog@1.4.0.yaml", "Recall well logs (one record per logging run)", "Recall well logs, one record per run");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        File.Copy(PathOf("mappings/Wellbore@1.0.0.yaml"), PathOf("mappings/Wellbore@1.0.1.yaml"));
        Rewrite("mappings/Wellbore@1.0.1.yaml", "version: 1.0.0", "version: 1.0.1");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        File.Delete(PathOf("mappings/Wellbore@1.0.1.yaml"));
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        // A broken mapping the rows do not hold as it is, is a change; once reconciled as invalid, it is not.
        File.WriteAllText(PathOf("mappings/Broken@1.0.0.yaml"), "documentType: mapping\nname: [\n");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());
    }
}
