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
[Collection(SqlServerSuite.Name)]
public sealed class LineageTests : IDisposable
{
    private const string Platform = "${env:OSDU_URL}";

    /// <summary>The sample estate names its partition as the reference a node holds, and lineage compares it as written.</summary>
    private const string Partition = "${env:OSDU_DATA_PARTITION}";

    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The folder the sample source occupies in the repository: a repository is laid out one folder per source.</summary>
    private const string Source = "recall";

    /// <summary>The checkout the flows are scanned in. Everything the source holds is under <see cref="Source"/> in it.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osdu-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The recall estate as the repository holds it, with the fixture documents (a wellbore chain, a source of several
    /// interfaces, a retrieval) beside it, so one scan orders flows of every kind the module adds.
    /// </summary>
    public LineageTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var folder in new[] { "flows", "cache", "mappings", "data" })
        {
            Copy(Path.Combine(Samples.Source, folder), Path.Combine(_root, Source, folder));
        }

        File.Copy(Path.Combine(Samples.Source, "schedules.yaml"), Path.Combine(_root, Source, "schedules.yaml"));
        foreach (var folder in new[] { "flows", "mappings" })
        {
            Copy(Path.Combine(Samples.FixtureDocuments, folder), Path.Combine(_root, Source, folder), overwrite: true);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Copy(string from, string to, bool overwrite = false)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    /// <summary>A path inside the source's folder, named the way the source holds it (<c>flows/x.yaml</c>).</summary>
    private string PathOf(string relative) => Path.Combine(_root, Source, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The same path as the checkout records it, which is what lineage names a node and keys a warning by.</summary>
    private static string InRepo(string relative) => Source + "/" + relative;

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
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.TypeSystem, Platform), Partition, OsduKind.Group(kind), kind);

    private static string CacheKey(string name)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.CacheSystem, null), Partition, OsduLineage.CacheGroup, name);

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
    public void A_source_declares_every_interface_read_and_every_interface_write()
    {
        // One document, two interfaces: the graph has to carry each interface's own table and its own OSDU type, so a
        // source is ordered after everything any of its interfaces reads, and shows as writing everything they write
        // (docs/interfaces-design.md section 9).
        var lineage = Describe("flows/wells-source-03-interfaces-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            [
                "OsduData.arc.Wellbore", "OsduData.arc.WellboreAlias", "OsduData.arc.WellLog",
                "OsduData.arc.WellLogCurve", "OsduData.arc.WellboreTrajectory", "OsduData.arc.WellboreTrajectoryStation",
                "OsduData.arc.Document",
            ],
            lineage.Objects.Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(lineage.Objects, o => Assert.Equal(LineageRelation.Reads, o.Relation));
        var writes = Datasets(lineage, LineageRelation.Writes);
        Assert.Contains(Samples.WellboreKind, writes, StringComparison.Ordinal);
        Assert.Contains(Samples.WellLogKind, writes, StringComparison.Ordinal);
        Assert.Contains("work-product-component--Document", writes, StringComparison.Ordinal);

        Assert.Contains("work-product-component--WellboreTrajectory", writes, StringComparison.Ordinal);

        // The documents interface streams its files and the surveys their stations, so the source reads both folders.
        Assert.Contains(lineage.Files, f => f.Location == "../data/document-files" && f.Relation == LineageRelation.Reads);
        Assert.Contains(lineage.Files, f => f.Location == "../data/stations" && f.Relation == LineageRelation.Reads);

        // And the estate orders it after the ingestion flows that fill both of those tables.
        var (_, report) = Scan();
        Assert.True(WaveOf(report, "wells-wellbore-02-header-ing") < WaveOf(report, "wells-source-03-interfaces-delivery"));
        Assert.True(WaveOf(report, "recall-welllog-02-header-ing") < WaveOf(report, "wells-source-03-interfaces-delivery"));
    }

    [Fact]
    public void A_delivery_flow_reads_its_tables_payload_files_cache_types_and_searched_kinds_and_writes_its_mappings_type()
    {
        var lineage = Describe("flows/recall-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            ["OsduData.arc.WellLog", "OsduData.arc.WellLogCurve"],
            lineage.Objects.Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(lineage.Objects, o => Assert.Equal(LineageRelation.Reads, o.Relation));
        var payload = Assert.Single(lineage.Files);
        Assert.Equal((LineageRelation.Reads, "../data/curves", "chunk_*.parquet"), (payload.Relation, payload.Location, payload.FilePattern));

        Assert.Equal(
            "osdu-type/${env:OSDU_DATA_PARTITION}/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0",
            Datasets(lineage, LineageRelation.Writes));
        // The units and the curve dictionary come out of the partition's cache; the wellbores are searched for on the
        // platform, so the flow reads the wellbore kind itself and is ordered after whatever delivers wellbores there.
        Assert.Equal(
            "osdu-cache/${env:OSDU_DATA_PARTITION}/cache/CurveDictionary, osdu-cache/${env:OSDU_DATA_PARTITION}/cache/RecallDepthUnits, "
            + "osdu-cache/${env:OSDU_DATA_PARTITION}/cache/RecallUnits, "
            + "osdu-type/${env:OSDU_DATA_PARTITION}/master-data/osdu:wks:master-data--Wellbore:*",
            Datasets(lineage, LineageRelation.Reads));
        var written = lineage.Datasets.Single(d => d.Relation == LineageRelation.Writes);
        Assert.Equal((Platform, (char?)':'), (written.Instance, written.Separator));
        Assert.All(lineage.Datasets.Where(d => d.Relation == LineageRelation.Reads && d.System == "osdu-cache"), d => Assert.Null(d.Instance));
        Assert.Equal(Platform, lineage.Datasets.Single(d => d.Relation == LineageRelation.Reads && d.System == "osdu-type").Instance);
    }

    [Fact]
    public void A_record_only_delivery_flow_reads_no_payload_files()
    {
        var lineage = Describe("flows/wells-wellbore-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Files);
        Assert.Equal("osdu-type/${env:OSDU_DATA_PARTITION}/master-data/osdu:wks:master-data--Wellbore:1.3.0", Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_file_protocol_flow_also_writes_the_dataset_kind_it_registers_files_as()
    {
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "protocol: ddms", "protocol: file");
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "    sessionThresholdChunks: 1\n", "    datasetKind: osdu:wks:dataset--File.Generic:1.0.0\n");

        // The file route reaches its services under the endpoint, so the DDMS's own root goes with the DDMS route.
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "    ddmsRoot: /api/os-wellbore-ddms\n", string.Empty);

        var lineage = Describe("flows/recall-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            "osdu-type/${env:OSDU_DATA_PARTITION}/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0, osdu-type/${env:OSDU_DATA_PARTITION}/dataset/osdu:wks:dataset--File.Generic:1.0.0",
            Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_cache_flow_reads_its_kinds_and_writes_its_partitions_cache_types()
    {
        Directory.CreateDirectory(Path.Combine(_root, Source, "cache"));
        File.WriteAllText(PathOf("cache/multi-type-cache.yaml"), """
            flowType: cache
            name: multi-type-cache
            source:
              endpoint: ${env:OSDU_URL}
              headers:
                data-partition-id: ${env:OSDU_DATA_PARTITION}
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                name: UnitOfMeasure
                fields: [data.Code, data.Name]
              - kind: "osdu:wks:reference-data--LogCurveBusinessValue:*"
                name: LogCurveBusinessValue
                fields: [data.Code, data.Name]
            """);

        var lineage = Describe("cache/multi-type-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Objects);
        Assert.Empty(lineage.Files);
        Assert.Equal(
            "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, "
            + "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Equal(
            "osdu-cache/${env:OSDU_DATA_PARTITION}/cache/UnitOfMeasure, osdu-cache/${env:OSDU_DATA_PARTITION}/cache/LogCurveBusinessValue",
            Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_retrieval_flow_reads_its_kinds_and_lands_record_files_and_a_manifest()
    {
        var lineage = Describe("flows/wells-osdu-04-metadata-retrieval.yaml");

        Assert.Equal(
            "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*, "
            + "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--VerticalMeasurementType:*, osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--TrajectoryStationPropertyType:*, "
            + "osdu-type/${env:OSDU_DATA_PARTITION}/master-data/osdu:wks:master-data--Wellbore:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Empty(Datasets(lineage, LineageRelation.Writes));
        Assert.Equal(
            ["samples/wells/out/metadata|part-*.jsonl", "samples/wells/out/metadata|manifest.json"],
            lineage.Files.Select(f => $"{f.Location}|{f.FilePattern}").ToList());
        Assert.All(lineage.Files, f => Assert.Equal(LineageRelation.Writes, f.Relation));

        // The runner resolves this relative location against its working directory, not the flow file: lineage says so.
        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("target.location 'samples/wells/out/metadata' is a relative path", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gzipped_retrieval_to_a_storage_uri_lands_gzipped_files_without_a_warning()
    {
        Rewrite("flows/wells-osdu-04-metadata-retrieval.yaml", "location: samples/wells/out/metadata",
            "location: https://lake.dfs.core.windows.net/raw/osdu/{date}\n  compression: gzip");

        var lineage = Describe("flows/wells-osdu-04-metadata-retrieval.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal("part-*.jsonl.gz", lineage.Files[0].FilePattern);
    }

    [Fact]
    public void The_sample_estate_orders_files_pre_ing_delivery_cache_and_retrieval_flows()
    {
        var (collected, report) = Scan();

        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "recall-welllog-01-header-pre") < WaveOf(report, "recall-welllog-02-header-ing"));
        Assert.True(WaveOf(report, "recall-welllog-02-header-ing") < WaveOf(report, "recall-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "recall-welllog-02-curves-ing") < WaveOf(report, "recall-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-02-header-ing") < WaveOf(report, "wells-wellbore-03-header-delivery"));

        // The well log flow searches for the wellbores the wellbore flow delivers, and renders its units against the cache.
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "recall-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "recall-lookups-00-cache") < WaveOf(report, "recall-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "wells-osdu-04-metadata-retrieval"));

        // Every file node is where the flows read and land it, relative to the checkout.
        var files = report.Objects.Where(o => o.Kind == LineageNodeKind.File).Select(o => o.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                InRepo("cache/data/curve-dictionary"), InRepo("cache/data/curve-units"), InRepo("cache/data/depth-units"),
                InRepo("data/curves"), InRepo("data/curves-meta"), InRepo("data/document-files"),
                InRepo("data/stations"), InRepo("data/wellbore"),
                InRepo("data/wellbore-aliases"), InRepo("data/welllog"), InRepo("flows/samples/wells/out/metadata"),
            ],
            files);

        // The lookups cache flow's tables are loaded from the files in cache/data by the estate's own flows, each file landed
        // and keyed by a pre and an ing flow of its own, so it runs after them and writes each table into the cache.
        foreach (var (pre, ing, table) in new[]
        {
            ("recall-cacheunits-01-curve-pre", "recall-cacheunits-02-curve-ing", "RecallUnits"),
            ("recall-cacheunits-01-depth-pre", "recall-cacheunits-02-depth-ing", "RecallDepthUnits"),
            ("recall-cachecurvedictionary-01-pre", "recall-cachecurvedictionary-02-ing", "CurveDictionary"),
        })
        {
            Assert.True(WaveOf(report, pre) < WaveOf(report, ing), $"{pre} runs before {ing}");
            Assert.True(WaveOf(report, ing) < WaveOf(report, "recall-lookups-00-cache"), $"{ing} runs before the lookups cache flow");
            Assert.Contains(report.Edges, e => e.Flow == "recall-lookups-00-cache" && e.Relation == LineageRelation.Writes && e.ObjectKey == CacheKey(table));
        }

        // One node per exact OSDU type written, one per pattern read, and one per cache type, each captioned by its system.
        var types = report.Objects.Where(o => o.Kind == LineageNodeKind.Dataset).ToDictionary(o => o.Key, StringComparer.Ordinal);
        Assert.Contains(TypeKey(Samples.WellLogKind), types.Keys);
        Assert.Contains(TypeKey(Samples.WellboreKind), types.Keys);
        Assert.Contains(TypeKey("osdu:wks:master-data--Wellbore:*"), types.Keys);

        // Wellbores are searched for, never captured, so the cache holds no type for them.
        Assert.DoesNotContain(CacheKey("Wellbore"), types.Keys);
        Assert.Equal("osdu-type", ServerIdentity.DatasetSystem(TypeKey(Samples.WellLogKind)));

        Assert.Contains(report.Edges, e => e.Flow == "recall-welllog-03-header-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == TypeKey(Samples.WellLogKind));
        Assert.Contains(report.Edges, e => e.Flow == "recall-welllog-03-header-delivery" && e.Relation == LineageRelation.Reads && e.ObjectKey == CacheKey("RecallUnits"));
        Assert.Contains(report.Edges, e => e.Flow == "recall-welllog-03-header-delivery" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));
        Assert.Contains(report.Edges, e => e.Flow == "wells-osdu-04-metadata-retrieval" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));

        // The only thing the scan has to say about the module's flows is where the retrieval's files may land.
        Assert.Single(collected.Warnings, w => w.Contains("flow '", StringComparison.Ordinal) && w.Contains("lineage", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_mapping_costs_the_flow_its_osdu_nodes_with_a_warning_and_nothing_else()
    {
        File.Delete(PathOf("mappings/WellLog@1.4.0.yaml"));

        var lineage = Describe("flows/recall-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.Equal(
            "delivery flow 'recall-welllog-03-header-delivery' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read (Mapping 'WellLog@1.4.0' was not found under '"
            + InRepo("mappings")
            + "'. Expected one of: WellLog@1.4.0.yaml, WellLog@1.4.0.yml, 1.4.0.yaml, 1.4.0.yml.).",
            warning);
        Assert.Empty(lineage.Datasets);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);

        var (collected, report) = Scan();
        Assert.Contains(collected.Warnings, w => w == InRepo("flows/recall-welllog-03-header-delivery.yaml") + ": " + warning);
        Assert.True(WaveOf(report, "recall-welllog-02-header-ing") < WaveOf(report, "recall-welllog-03-header-delivery"));
    }

    [Fact]
    public void An_invalid_mapping_is_a_warning_naming_the_file_relative_to_the_checkout()
    {
        File.WriteAllText(PathOf("mappings/WellLog@1.4.0.yaml"), "documentType: mapping\nname: WellLog\nversion: [\n");

        var lineage = Describe("flows/recall-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith(
            "delivery flow 'recall-welllog-03-header-delivery' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read ("
            + InRepo("mappings/WellLog@1.4.0.yaml"),
            warning,
            StringComparison.Ordinal);
        Assert.DoesNotContain(_root, warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_filed_under_another_name_is_a_warning()
    {
        Rewrite("mappings/WellLog@1.4.0.yaml", "version: 1.4.0", "version: 1.4.1");

        var warning = Assert.Single(Describe("flows/recall-welllog-03-header-delivery.yaml").Warnings);

        Assert.Contains(InRepo("mappings/WellLog@1.4.0.yaml") + ": declares 'WellLog@1.4.1' but is filed as 'WellLog@1.4.0'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mappings_directory_outside_the_checkout_is_never_read()
    {
        // Three steps up from the flow's folder (<checkout>/wells/flows) is past the checkout itself.
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "  mapping: WellLog@1.4.0\n", "  mapping: WellLog@1.4.0\n  mappings: ../../../outside/mappings\n");

        var warning = Assert.Single(Describe("flows/recall-welllog-03-header-delivery.yaml").Warnings);

        Assert.Equal(
            "delivery flow 'recall-welllog-03-header-delivery' shows no OSDU type in lineage: render.mappings '../../../outside/mappings' lies outside the repository, so mapping 'WellLog@1.4.0' is not read.",
            warning);
    }

    [Fact]
    public void The_search_for_a_mappings_directory_stops_at_the_checkout()
    {
        // The checkout is a folder of its own inside a folder holding a mappings directory the flow must not reach.
        var parent = Path.Combine(_root, "outer");
        var checkout = Path.Combine(parent, "repo");
        Copy(PathOf("flows"), Path.Combine(checkout, "flows"));
        Copy(PathOf("mappings"), Path.Combine(parent, "mappings"));
        var path = Path.Combine(checkout, "flows", "recall-welllog-03-header-delivery.yaml");
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(path));

        var lineage = document.DescribeLineage(new RegisteredLineageContext(path, checkout));

        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("was not found under 'flows/mappings'", warning, StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_reference_naming_a_path_is_refused_before_any_file_is_read()
    {
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "mapping: WellLog@1.4.0", "mapping: ../mappings/WellLog@1.4.0");

        var warning = Assert.Single(Describe("flows/recall-welllog-03-header-delivery.yaml").Warnings);
        Assert.Contains("Mapping reference '../mappings/WellLog@1.4.0' names a path", warning, StringComparison.Ordinal);

        var catalog = new MappingCatalog(PathOf("mappings"), new DeliveryDocumentLoader());
        Assert.Throws<FlowValidationException>(() => catalog.Load("..\\mappings\\WellLog@1.4.0"));
        Assert.Throws<FlowValidationException>(() => catalog.Load("WellLog@../1.4.0"));
        Assert.Equal("WellLog@1.4.0", catalog.Load("WellLog@1.4.0").Reference);
    }

    [Fact]
    public void A_flow_whose_partition_names_no_cache_keeps_its_tables_and_files_and_says_why_it_has_no_osdu_nodes()
    {
        Rewrite("flows/recall-welllog-03-header-delivery.yaml", "    data-partition-id: ${env:OSDU_DATA_PARTITION}\n", "    data-partition-id: \"open des\"\n");

        var lineage = Describe("flows/recall-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith(
            "delivery flow 'recall-welllog-03-header-delivery' shows no OSDU node in lineage: target.headers: data-partition-id 'open des' is neither a partition id",
            warning,
            StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);
    }

    [Fact]
    public void A_partition_reference_stays_its_text_and_a_literal_endpoint_is_identified_by_hash()
    {
        Directory.CreateDirectory(Path.Combine(_root, Source, "cache"));
        File.WriteAllText(PathOf("cache/searched-cache.yaml"), """
            flowType: cache
            name: searched-cache
            source:
              endpoint: https://osdu.example.com
              headers:
                data-partition-id: ${env:OSDU_PARTITION}
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                name: UnitOfMeasure
                fields: [data.Code, data.Name]
            """);

        var lineage = Describe("cache/searched-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.All(lineage.Datasets, d => Assert.Equal("${env:OSDU_PARTITION}", d.Namespace));
        var (collected, report) = Scan();
        Assert.Contains(collected.Facts, f => f.Flow == "searched-cache" && f.ServerRef.StartsWith("dataset:osdu-type:inline:", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Facts, f => f.ServerRef.Contains("example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Objects, o => o.Key.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void An_osdu_type_the_graph_cannot_hold_is_left_out_with_a_warning()
    {
        var warnings = new List<string>();

        Assert.Null(OsduLineage.Type(LineageRelation.Writes, Platform, "dev", "osdu:wks:master-data--Wellbore:*", "flow 'x'", "a write", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, Platform, "dev", "not a kind", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, new string('e', DeclaredDataset.MaxInstanceLength + 1), "dev", Samples.WellboreKind, "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(
            LineageRelation.Reads, Platform, "dev", "osdu:wks:master-data--" + new string('W', DeclaredDataset.MaxPartLength) + ":1.0.0", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "dev", "Well|bore", "flow 'x'", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "dev", " ", "flow 'x'", warnings));

        Assert.Equal(6, warnings.Count);
        Assert.DoesNotContain(warnings, w => w.Contains(new string('e', 100), StringComparison.Ordinal));
        Assert.Equal("osdu:wks:master-data--Wellbore:1.3.0",
            OsduLineage.Type(LineageRelation.Writes, Platform, "dev", Samples.WellboreKind, "flow 'x'", "a write", warnings)!.Name);
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
        using var catalog = new OsduTestDatabase();
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
        Rewrite("mappings/WellLog@1.4.0.yaml", "Recall well logs (one record per log)", "Recall well logs, one record per log");
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
