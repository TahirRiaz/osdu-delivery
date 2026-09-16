using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class YamlDocumentLoaderTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        parameters:
          logSource: { required: true }
        source:
          connection: ${env:OSDU_SAMPLE_DB}
          record:
            object: OsduSample.ing.WellLog
            key: [source_project, log_id]
            scope: { log_name: logSource }
          datasets:
            curves:
              object: OsduSample.ing.WellLogCurve
              join: { source_project: source_project, log_id: log_id }
              orderBy: [curve_ordinal]
          payloads:
            curves:
              root: curves/{logSource}
              locationColumn: curve_folder
              pattern: "chunk_*.parquet"
              hashColumn: payload_hash
          lastModified: update_date
          work: work/{logSource}
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/petrodb
          headers: { data-partition-id: opendes }
          protocol: osduWellLog
          protocolOptions: { payload: curves, recordMethod: POST }
        reliability:
          concurrency: 2
          retry: { attempts: 5, backoff: fixed }
        """;

    /// <summary>Where a submission's rows land for the pre flows that read them, and where its files may sit. Newlines are
    /// normalized because the cases below replace passages that span lines, which a raw literal writes with this file's
    /// endings.</summary>
    private static readonly string Submissions = """
          submissions:
            record:
              preFlow: demo-pre
              landing: landing/record
            datasets:
              curves:
                preFlow: demo-curves-pre
                landing: landing/curves
            fileRoots:
              - abfss://lake@acct.dfs.core.windows.net/recall
              - archive/curves
        """.ReplaceLineEndings("\n");

    private static string WithSubmissions(string replacing) =>
        Flow.ReplaceLineEndings("\n").Replace("  lastModified: update_date", replacing + "\n  lastModified: update_date", StringComparison.Ordinal);

    [Fact]
    public void Records_sent_through_the_api_land_for_the_pre_flows_and_their_files_are_bounded()
    {
        var loader = new DeliveryDocumentLoader();

        // Opt-in: a flow that says nothing takes no records sent through the API, because they would have nowhere to land.
        Assert.Null(loader.ParseFlow(Flow, "inline.yaml").Source.Submissions);

        var submissions = loader.ParseFlow(WithSubmissions(Submissions), "inline.yaml").Source.Submissions!;
        Assert.Equal("demo-pre", submissions.Record.PreFlow);
        Assert.Equal("landing/record", submissions.Record.Landing);
        Assert.Equal(LandingFormats.Csv, submissions.Record.Format);
        Assert.Equal("demo-curves-pre", submissions.Datasets["curves"].PreFlow);
        Assert.Equal(["abfss://lake@acct.dfs.core.windows.net/recall", "archive/curves"], submissions.FileRoots);

        // A landing may take another form, but only one a pre flow reads.
        var ndjson = loader.ParseFlow(WithSubmissions(Submissions.Replace("landing: landing/record", "landing: landing/record\n      format: ndjson", StringComparison.Ordinal)), "inline.yaml");
        Assert.Equal(LandingFormats.Ndjson, ndjson.Source.Submissions!.Record.Format);
        var badFormat = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            WithSubmissions(Submissions.Replace("landing: landing/record", "landing: landing/record\n      format: avro", StringComparison.Ordinal)), "inline.yaml"));
        Assert.Contains("is not a landing format", badFormat.Message, StringComparison.Ordinal);

        // A dataset the flow does not read cannot be landed: there would be no table for its rows to reach.
        var unknownDataset = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            WithSubmissions(Submissions.Replace("curves:\n        preFlow: demo-curves-pre", "tops:\n        preFlow: demo-tops-pre", StringComparison.Ordinal)), "inline.yaml"));
        Assert.Contains("source.datasets does not declare", unknownDataset.Message, StringComparison.Ordinal);

        // A root is a prefix, never a pattern: it is resolved against the flow file exactly as the payload roots and the
        // landing folders are, so a repository-relative root is written the same way they are.
        var relative = loader.ParseFlow(WithSubmissions(Submissions.Replace("- archive/curves", "- ../shared/curves", StringComparison.Ordinal)), "inline.yaml");
        Assert.Equal("../shared/curves", relative.Source.Submissions!.FileRoots[1]);

        var refused = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            WithSubmissions(Submissions.Replace("- archive/curves", "- archive/*", StringComparison.Ordinal)), "inline.yaml"));
        Assert.Contains("source.submissions.fileRoots", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no wildcard", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_without_the_partition_header_is_refused_when_read()
    {
        var yaml = Flow.Replace("headers: { data-partition-id: opendes }", "headers: { }", StringComparison.Ordinal);
        Assert.DoesNotContain("data-partition-id", yaml, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "inline.yaml"));
        Assert.Contains("data-partition-id", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("datasetKind: osdu:wks:dataset--File.Generic:1.0")]
    [InlineData("manifestKind: osdu:wks:Manifest")]
    public void A_dataset_or_manifest_kind_the_target_would_refuse_is_rejected_when_read(string option)
    {
        var yaml = Flow.Replace("protocolOptions: { payload: curves, recordMethod: POST }", "protocolOptions: { payload: curves, recordMethod: POST, " + option + " }", StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(yaml, "inline.yaml"));
        Assert.Contains("major.minor.patch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    public void Only_a_session_threshold_that_cannot_overwrite_chunks_is_accepted(int threshold, bool accepted)
    {
        var yaml = Flow.Replace("protocolOptions: { payload: curves, recordMethod: POST }", "protocolOptions: { payload: curves, recordMethod: POST, sessionThresholdChunks: " + threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }", StringComparison.Ordinal);
        var loader = new DeliveryDocumentLoader();
        if (accepted)
        {
            Assert.Equal(threshold, loader.ParseFlow(yaml, "inline.yaml").Target.ProtocolOptions.SessionThresholdChunks);
        }
        else
        {
            var ex = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(yaml, "inline.yaml"));
            Assert.Contains("overwrite each other", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Loads_the_sample_flow_and_mapping()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.LoadFlow(Samples.Flow);
        Assert.Equal("recall-welllog", flow.Name);
        Assert.Equal(DeliveryProtocol.OsduWellLog, flow.Target.Protocol);
        Assert.Equal("WellLog", flow.Render.MappingName);
        Assert.Equal("1.4.0", flow.Render.MappingVersion);
        Assert.Equal(TargetAuthType.OAuth2ClientCredentials, flow.Target.Auth.Type);
        Assert.Equal(["Datasets", "DDMSDatasets", "ExtensionProperties"], flow.Target.ProtocolOptions.PreserveDataKeys);

        // The sample delivers what its own pre and ing flows load into the ingestion tables.
        Assert.Equal("OsduSample.ing.WellLog", flow.Source.Record.Object);
        Assert.Equal(["source_project", "log_id"], flow.Source.Record.Key);
        Assert.Equal("logSource", flow.Source.Record.Scope["log_name"]);
        Assert.Equal("OsduSample.ing.WellLogCurve", flow.Source.Datasets["curves"].Object);
        Assert.Equal("source_project", flow.Source.Datasets["curves"].Join["source_project"]);
        Assert.Equal(["curve_ordinal"], flow.Source.Datasets["curves"].OrderBy);
        Assert.Equal("curve_folder", flow.Source.Payloads["curves"].LocationColumn);
        Assert.Equal("chunk_count", flow.Source.Payloads["curves"].ChunkCountColumn);
        Assert.Equal("recall-welllog-pre", flow.Source.Submissions!.Record.PreFlow);
        Assert.Equal("recall-welllog-curves-pre", flow.Source.Submissions.Datasets["curves"].PreFlow);

        var mapping = new MappingCatalog(Samples.Mappings, loader).Load("WellLog@1.4.0");
        Assert.Equal(new TemplateReference("osdu:wks:work-product-component--WellLog:1.4.0", "26a3c3441882db4f"), mapping.Template);
        Assert.Contains(mapping.Entries, e => e.Target.Text == "osdu.data.Curves" && e.IsRepeater && e.Source!.Child == "curves");
        Assert.Equal(["curves"], mapping.ChildDatasets);
        Assert.Equal(2, mapping.Fixtures.Count);
        Assert.Equal(["opendes-reference-data-default"], mapping.Envelope.LegalTags);
        Assert.Equal(["NO"], mapping.Envelope.OtherRelevantDataCountries);
    }

    [Fact]
    public void Parses_inline_flow_with_defaults()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "inline.yaml");
        Assert.Equal(2, flow.Reliability.Concurrency);
        Assert.Equal(BackoffKind.Fixed, flow.Reliability.Retry.Backoff);
        Assert.Equal(5, flow.Reliability.Retry.Attempts);
        Assert.Equal(FlowSystemColumns.DefaultUpdated, flow.Source.SystemColumns.Updated);
        Assert.Equal(FlowSystemColumns.DefaultFileName, flow.Source.SystemColumns.FileName);
        Assert.Equal(FlowIncremental.DefaultOverlapSeconds, flow.Source.Incremental.OverlapSeconds);
        Assert.Equal(SourceIsolation.Snapshot, flow.Source.Incremental.Isolation);
        Assert.Equal("POST", flow.Target.ProtocolOptions.RecordMethod);
        Assert.Equal(ChangeDetection.RenderedHash, flow.Change.Detect);
    }

    [Fact]
    public void Unknown_keys_are_a_parse_error_with_the_path()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Flow.Replace("concurrency: 2", "concurency: 2", StringComparison.Ordinal), "flow.yaml"));
        Assert.StartsWith("flow.yaml:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("concurency", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Floating_mappings_and_unknown_protocols_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        var floating = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("WellLog@1.4.0", "WellLog", StringComparison.Ordinal), "f"));
        Assert.Contains("pinned", floating.Message, StringComparison.Ordinal);
        Assert.Equal(DeliveryProtocol.OsduManifest, loader.ParseFlow(Flow.Replace("osduWellLog", "osduManifest", StringComparison.Ordinal), "f").Target.Protocol);
        Assert.Equal(DeliveryProtocol.OsduFile, loader.ParseFlow(Flow.Replace("osduWellLog", "osduFile", StringComparison.Ordinal), "f").Target.Protocol);
        var unknownProtocol = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("osduWellLog", "ftp", StringComparison.Ordinal), "f"));
        Assert.Contains("target.protocol", unknownProtocol.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Undeclared_location_tokens_and_a_payload_the_protocol_does_not_stream_are_rejected()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("work: work/{logSource}", "work: work/{other}", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("root: curves/{logSource}", "root: curves/{other}", StringComparison.Ordinal), "f"));
        Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("payload: curves", "payload: grids", StringComparison.Ordinal), "f"));

        // A pattern matches file names inside the record's own payload folder, so it carries no path of its own.
        var path = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow.Replace("pattern: \"chunk_*.parquet\"", "pattern: \"{deliveryKey}/chunk_*.parquet\"", StringComparison.Ordinal), "f"));
        Assert.Contains("pattern", path.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_modified_column_orders_the_versions_of_a_record()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Equal("update_date", loader.ParseFlow(Flow, "f").Source.LastModified);
        Assert.Equal("update_date", loader.LoadFlow(Samples.Flow).Source.LastModified);

        // A flow may decide changes by what it renders alone, and then no business version orders its rows.
        var unordered = Flow.ReplaceLineEndings("\n").Replace("  lastModified: update_date\n", string.Empty, StringComparison.Ordinal);
        Assert.Null(loader.ParseFlow(unordered, "f").Source.LastModified);

        Assert.Equal(ChangeDetection.LastModified, loader.ParseFlow(Flow + "\nchange: { payloadDetect: lastModified }", "f").Change.PayloadDetect);
        var detect = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(Flow + "\nchange: { detect: lastModified }", "f"));
        Assert.Contains("source.lastModified", detect.Message, StringComparison.Ordinal);

        // A metadata-only flow streams no files, so there is no payload watermark to take.
        var noPayload = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            (Flow.ReplaceLineEndings("\n") + "\nchange: { payloadDetect: lastModified }")
                .Replace("osduWellLog", "osduRecord", StringComparison.Ordinal)
                .Replace("  protocolOptions: { payload: curves, recordMethod: POST }\n", string.Empty, StringComparison.Ordinal),
            "f"));
        Assert.Contains("no payload files", noPayload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapping_parse_reads_the_number_modifier_and_its_separators()
    {
        var loader = new DeliveryDocumentLoader();
        Modifier Only(string modifiers) => Assert.Single(loader
            .ParseMapping(TestSchema.MappingDocument($"  - {{ target: osdu.data.Weight, source: dataset.a, modifiers: {modifiers} }}"), "m.yaml")
            .Entries.Single(e => e.Target.Text == "osdu.data.Weight").Modifiers);

        var plain = Only("[number]");
        Assert.Equal(ModifierKind.Number, plain.Kind);
        Assert.Equal(".", plain.DecimalSeparator);
        Assert.Null(plain.GroupSeparator);
        Assert.Equal("number", plain.ToString());

        var european = Only("[{ number: { decimal: \",\", group: \" \" } }]");
        Assert.Equal(",", european.DecimalSeparator);
        Assert.Equal(" ", european.GroupSeparator);
        Assert.Equal("number(decimal ',', group ' ')", european.ToString());
    }

    [Fact]
    public void Mapping_parse_reads_sources_and_refuses_what_is_malformed()
    {
        var loader = new DeliveryDocumentLoader();
        var mapping = loader.ParseMapping(TestSchema.MappingYaml, "m.yaml");
        Assert.Equal(TestSchema.Template, mapping.Template);
        var unit = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Unit");
        Assert.Equal(MappingSourceKind.Cache, unit.Source!.Kind);
        Assert.True(unit.Source.ReadsRecordId);
        Assert.Equal("Code", Assert.Single(unit.FindBy).Field);
        Assert.Equal(new DatasetColumn(null, "unit"), unit.FindBy[0].Column);
        Assert.True(mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves").IsRepeater);
        Assert.Equal(new DatasetColumn("curves", "curve_id"), mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].CurveID").Source!.Column);

        string Refused(string entries) => Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingDocument(entries), "m.yaml")).Message;
        Assert.Contains("needs 'findBy'", Refused("  - { target: osdu.data.Unit, source: cache.UnitOfMeasure.id }"), StringComparison.Ordinal);
        Assert.Contains("both 'source' and 'static'", Refused("  - { target: osdu.data.Symbol, source: dataset.a, static: b }"), StringComparison.Ordinal);
        Assert.Contains("neither 'source' nor 'static'", Refused("  - { target: osdu.data.Symbol }"), StringComparison.Ordinal);
        Assert.Contains("must start with 'dataset.'", Refused("  - { target: osdu.data.Symbol, source: column_a }"), StringComparison.Ordinal);
        Assert.Contains("must start with 'osdu.'", Refused("  - { target: data.Symbol, source: dataset.a }"), StringComparison.Ordinal);
        Assert.Contains("fills the same variable", Refused("  - { target: osdu.data.Name, source: dataset.other }"), StringComparison.Ordinal);
        Assert.Contains("no entry repeats osdu.data.Curves", Refused("  - { target: \"osdu.data.Curves[].CurveID\", source: dataset.curves.curve_id }"), StringComparison.Ordinal);
        Assert.Contains("only an entry inside a repeater", Refused("  - { target: osdu.data.Symbol, source: dataset.curves.curve_id }"), StringComparison.Ordinal);
        Assert.Contains("is not a modifier", Refused("  - { target: osdu.data.Symbol, source: dataset.a, modifiers: [shout] }"), StringComparison.Ordinal);
        Assert.Contains("the date format 'MM.yyyy' has no day of the month", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: MM.yyyy }] }"), StringComparison.Ordinal);
        Assert.Contains("reads a two-digit year", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: dd.MM.yy }] }"), StringComparison.Ordinal);
        Assert.Contains("has no year", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: dd.MM }] }"), StringComparison.Ordinal);
        Assert.Contains("is one letter", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: d }] }"), StringComparison.Ordinal);
        Assert.Contains("never closed", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: \"dd.MM.yyyy 'at\" }] }"), StringComparison.Ordinal);
        Assert.Contains("date takes the input format as text", Refused("  - { target: osdu.data.When, source: dataset.a, modifiers: [{ date: [dd.MM.yyyy] }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes '.' or ',' as its decimal separator, not ';'", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: { decimal: \";\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("cannot use ',' both between digit groups and before the decimals", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: { decimal: \",\", group: \",\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("cannot use '.' both between digit groups", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: { group: \".\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("as its group separator, not '-'", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: { group: \"-\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes 'decimal' and 'group', not 'thousands'", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: { thousands: \",\" } }] }"), StringComparison.Ordinal);
        Assert.Contains("number takes its separators", Refused("  - { target: osdu.data.Weight, source: dataset.a, modifiers: [{ number: \",\" }] }"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("  - { target: osdu.data.Weight, static: .inf }"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("  - { target: osdu.data.Aliases, static: [a, .nan] }"), StringComparison.Ordinal);
        Assert.Contains("is NaN or Infinity, which a JSON record cannot carry", Refused("  - { target: osdu.data.Nested, static: { Inner: -.inf } }"), StringComparison.Ordinal);
        Assert.Contains("split needs the part", Refused("  - { target: osdu.data.Symbol, source: dataset.a, modifiers: [{ split: { separator: x } }] }"), StringComparison.Ordinal);
        Assert.Contains("appliesWhen 'dataset.a equals b'", Refused("  - { target: osdu.data.Symbol, source: dataset.a, appliesWhen: dataset.a equals b }"), StringComparison.Ordinal);
        Assert.Contains("compares cache.Wellbore", Refused("  - { target: osdu.data.Unit, source: cache.UnitOfMeasure.id, findBy: cache.Wellbore.Code = dataset.a }"), StringComparison.Ordinal);
        Assert.Contains("uses {param.missing}", Refused("  - { target: osdu.data.Symbol, static: \"{param.missing}\" }"), StringComparison.Ordinal);
        Assert.Contains("a static entry takes only", Refused("  - { target: osdu.data.Symbol, static: b, required: false }"), StringComparison.Ordinal);
        Assert.Contains("steps into more than one array", Refused("  - { target: \"osdu.data.Curves[].Points[].X\", source: dataset.a }"), StringComparison.Ordinal);

        Assert.Contains("template.version", Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace(TestSchema.Build().Version, "latest", StringComparison.Ordinal), "m")).Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("dataPartition: { required: true }", "other: { required: true }", StringComparison.Ordinal), "m"));
        var wrongKind = Assert.Throws<FlowValidationException>(() => loader.ParseMapping(TestSchema.MappingYaml.Replace("documentType: mapping", "flowType: delivery", StringComparison.Ordinal), "m"));
        Assert.Contains("expected 'documentType: mapping'", wrongKind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_requires_file_name_and_document_to_agree()
    {
        var dir = Samples.NewTempDirectory();
        File.WriteAllText(Path.Combine(dir, "Thing@2.0.0.yaml"), TestSchema.MappingYaml);
        var catalog = new MappingCatalog(dir, new DeliveryDocumentLoader());
        var ex = Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@2.0.0"));
        Assert.Contains("filed as", ex.Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => catalog.Load("Thing@9.9.9"));
        Assert.Contains("Thing@2.0.0", catalog.List());
    }

    [Fact]
    public void Flow_parameters_resolve_defaults_and_reject_unknowns()
    {
        var flow = new DeliveryDocumentLoader().ParseFlow(Flow, "f");
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, null));
        var values = FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" });

        // A declared location resolves against the flow file, with its tokens filled in.
        var work = FlowParameters.WorkLocation(flow, values);
        Assert.True(Path.IsPathRooted(work));
        Assert.EndsWith(Path.Combine("work", "STAT_COMP"), work, StringComparison.Ordinal);

        // The scope predicate: the column the run filters on, carrying the value of the parameter bound to it.
        Assert.Equal("STAT_COMP", FlowParameters.ScopeValues(flow, values)["log_name"]);

        // A parameter value that would climb out of a declared location is refused, because those locations bound
        // what a run may read and write.
        var climbing = FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "../etc" });
        Assert.Contains("must not contain", Assert.Throws<FlowValidationException>(() => FlowParameters.WorkLocation(flow, climbing)).Message, StringComparison.Ordinal);
        Assert.Throws<FlowValidationException>(() => FlowParameters.Resolve(flow, new Dictionary<string, string> { ["logSource"] = "x", ["nope"] = "y" }));
    }
}

/// <summary>
/// The identifiers the storage service polices on every record it accepts: the kind, and the partition the record
/// id is minted in. A value that fails either would fail every record of a run, so it is refused where it is read.
/// </summary>
public class OsduIdentifierValidationTests
{
    [Theory]
    [InlineData("test:wks:work-product-component--Thing:1")]
    [InlineData("test:wks:work-product-component--Thing:1.0")]
    [InlineData("test:wks:work product:1.0.0")]
    [InlineData("test:wks:work-product-component--Thing:1.0.x")]
    public void A_mapping_kind_the_storage_service_would_refuse_is_rejected_when_read(string kind)
    {
        var yaml = TestSchema.MappingYaml.Replace("kind: test:wks:work-product-component--Thing:1.0.0", "kind: " + kind, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(yaml, "m.yaml"));
        Assert.Contains("major.minor.patch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("static: [tag] }", "static: [tag, tag] }", "osdu.legal.legaltags")]
    [InlineData("static: [NO] }", "static: [NO, NO] }", "osdu.legal.otherRelevantDataCountries")]
    public void A_repeated_legal_entry_is_rejected_because_the_legal_lists_are_sets(string from, string to, string key)
    {
        var yaml = TestSchema.MappingYaml.Replace(from, to, StringComparison.Ordinal);
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(yaml, "m.yaml"));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_well_formed_mapping_kind_is_accepted()
    {
        var mapping = new DeliveryDocumentLoader().ParseMapping(TestSchema.MappingYaml, "m.yaml");
        Assert.Equal("test:wks:work-product-component--Thing:1.0.0", mapping.Kind);
    }

    [Theory]
    [InlineData("opendes")]
    [InlineData("my-partition.eu_1")]
    public void A_data_partition_that_is_a_valid_id_segment_is_used(string partition)
    {
        var context = Context(partition);
        Assert.Equal(partition, context.DataPartition);
    }

    [Theory]
    [InlineData("open des")]
    [InlineData("opendes/eu")]
    [InlineData("opendes:eu")]
    public void A_data_partition_that_would_mint_invalid_record_ids_is_refused(string partition)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Context(partition).DataPartition);
        Assert.Contains("not a valid OSDU id segment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_flow_without_the_partition_header_is_refused_when_read()
    {
        const string Retrieval = """
            flowType: retrieval
            name: wellbores-out
            source:
              endpoint: https://osdu.example.com
              headers: { }
              kind: "osdu:wks:master-data--Wellbore:1.0.0"
            target:
              location: ./out
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval(Retrieval, "r.yaml"));
        Assert.Contains("data-partition-id", ex.Message, StringComparison.Ordinal);

        var withHeader = Retrieval.Replace("headers: { }", "headers: { data-partition-id: opendes }", StringComparison.Ordinal);
        Assert.Equal("wellbores-out", new DeliveryDocumentLoader().ParseRetrieval(withHeader, "r.yaml").Name);
    }

    private static SqlFlow.Delivery.Snapshots.RenderContext Context(string partition) => new()
    {
        MappingReference = "Thing@1.0.0",
        CacheVersion = "r1",
        SchemaSnapshotVersion = "s1",
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [SqlFlow.Delivery.Snapshots.RenderContext.DataPartitionParameter] = partition },
    };
}
