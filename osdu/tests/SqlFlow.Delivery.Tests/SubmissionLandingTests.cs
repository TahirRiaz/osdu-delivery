using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Submissions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The checks a flow's landing has to pass before records sent through the API are accepted (docs/stage4-design.md
/// section 4.2 step 1). A submission whose files land where no pre flow reads, or under a name none of them selects,
/// would be accepted, land, and then deliver nothing, with every run reporting success, so the door refuses it instead.
/// What a pre flow reads is read out of the catalog's copy of its document through the platform's shared file
/// selection, which is the same decision the engine makes when it picks files up.
/// </summary>
public class SubmissionLandingTests
{
    private const string BaseFlow = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_SAMPLE_DB}
          record:
            object: OsduSample.ing.WellLog
            key: [source_project, log_id]
          datasets:
            curves:
              object: OsduSample.ing.WellLogCurve
              join: { source_project: source_project, log_id: log_id }
              orderBy: [curve_ordinal]
          lastModified: update_date
          work: work/demo
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/petrodb
          headers: { data-partition-id: opendes }
          protocol: osduWellLog
        """;

    /// <summary>Where this flow's rows land, for the pre flows that sit beside it in the same folder.</summary>
    private static readonly string Submissions = """
          submissions:
            record:
              preFlow: demo-pre
              landing: ../data/record
            datasets:
              curves:
                preFlow: demo-curves-pre
                landing: ../data/curves
        """.ReplaceLineEndings("\n");

    /// <summary>The flow as the catalog holds it, landing its records and its curve rows for their pre flows.</summary>
    private static string Flow => BaseFlow.ReplaceLineEndings("\n")
        .Replace("  lastModified: update_date", Submissions + "\n  lastModified: update_date", StringComparison.Ordinal);

    /// <summary>The flow as the catalog holds it: parsed from a document sitting beside its pre flows.</summary>
    private static FlowDefinition Parse(string? yaml = null) => new DeliveryDocumentLoader().ParseFlow(yaml ?? Flow, "flows/demo.yaml");

    /// <summary>A pre flow's stored definition, in the shape the catalog's parsed copy of a file flow takes.</summary>
    private static string Definition(string location, string? srcFile = "*.csv", string type = "csv")
    {
        var options = srcFile is null ? string.Empty : ",\"options\":{\"srcFile\":\"" + srcFile + "\"}";
        return "{\"flow\":{\"source\":{\"type\":\"" + type + "\",\"location\":\"" + location + "\"" + options + "}}}";
    }

    /// <summary>The repository's flows, with each pre flow reading the folder its dataset lands in.</summary>
    private static List<SubmissionPreFlow> Repository(string? recordLocation = "../data/record", string? recordGlob = "*.csv") =>
    [
        new("demo-pre", "flows/demo-pre.yaml", recordLocation is null ? null : Definition(recordLocation, recordGlob)),
        new("demo-curves-pre", "flows/demo-curves-pre.yaml", Definition("../data/curves")),
    ];

    [Fact]
    public void A_flow_whose_pre_flows_read_what_it_lands_takes_records()
        => Assert.Null(SubmissionLanding.Refusal(Parse(), Repository()));

    [Fact]
    public void A_flow_that_declares_no_landing_takes_no_records()
    {
        // Opt-in: without source.submissions there is nowhere for records sent through the API to land.
        var refusal = SubmissionLanding.Refusal(Parse(BaseFlow), Repository());

        Assert.NotNull(refusal);
        Assert.Contains("takes no records through the API", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pre_flow_the_repository_does_not_hold_is_refused()
    {
        var refusal = SubmissionLanding.Refusal(Parse(), [new SubmissionPreFlow("demo-curves-pre", "flows/demo-curves-pre.yaml", Definition("../data/curves"))]);

        Assert.NotNull(refusal);
        Assert.Contains("'demo-pre'", refusal, StringComparison.Ordinal);
        Assert.Contains("this repository does not hold", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_landing_file_the_pre_flow_would_not_select_is_refused()
    {
        // The pre flow reads the right folder, but only parquet files; the record rows land as csv, so a run of it would
        // select nothing and the submission would sit undelivered with nothing reporting a fault.
        var refusal = SubmissionLanding.Refusal(Parse(), Repository(recordGlob: "*.parquet"));

        Assert.NotNull(refusal);
        Assert.Contains("demo-pre", refusal, StringComparison.Ordinal);
        Assert.Contains("*.parquet", refusal, StringComparison.Ordinal);
        Assert.Contains("_record.csv", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_landing_folder_the_pre_flow_does_not_read_from_is_refused()
    {
        var refusal = SubmissionLanding.Refusal(Parse(), Repository(recordLocation: "../data/elsewhere"));

        Assert.NotNull(refusal);
        Assert.Contains("demo-pre", refusal, StringComparison.Ordinal);
        Assert.Contains("does not read from", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_landing_inside_the_folder_the_pre_flow_reads_is_taken()
    {
        // A pre flow watching a folder reads what lands beneath it, so a landing in a subfolder of its location is read.
        var flow = Parse(Flow.Replace("landing: ../data/record", "landing: ../data/record/inbox", StringComparison.Ordinal));

        Assert.Null(SubmissionLanding.Refusal(flow, Repository()));
    }

    [Fact]
    public void A_pre_flow_whose_folder_cannot_be_compared_is_left_to_the_run()
    {
        // A location naming a secret resolves only where the run resolves it, so the folder is not second-guessed here.
        // The file name and the format are still decided, because neither depends on the location.
        Assert.Null(SubmissionLanding.Refusal(Parse(), Repository(recordLocation: "${env:LANDING_ROOT}/record")));

        var refusal = SubmissionLanding.Refusal(Parse(), [
            new("demo-pre", "flows/demo-pre.yaml", Definition("${env:LANDING_ROOT}/record", srcFile: "*.parquet")),
            new("demo-curves-pre", "flows/demo-curves-pre.yaml", Definition("../data/curves")),
        ]);
        Assert.NotNull(refusal);
        Assert.Contains("*.parquet", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pre_flow_whose_document_the_catalog_does_not_hold_is_not_refused_on_an_absence()
    {
        // Nothing is known about what it reads, which is not evidence that it reads nothing.
        Assert.Null(SubmissionLanding.Refusal(Parse(), Repository(recordLocation: null)));

        Assert.Null(SubmissionLanding.Refusal(Parse(), [
            new("demo-pre", "flows/demo-pre.yaml", """{"name":"demo-pre","flowKind":"file"}"""),
            new("demo-curves-pre", "flows/demo-curves-pre.yaml", Definition("../data/curves")),
        ]));
    }

    [Fact]
    public void A_landing_taking_a_form_no_pre_flow_reads_is_refused()
    {
        // The loader refuses an unknown format when it reads the document, so this is the catalog holding a row whose
        // format stopped being one the product lands; the door still says why rather than writing a file nothing parses.
        var parsed = Parse();
        var submissions = parsed.Source.Submissions! with { Record = parsed.Source.Submissions!.Record with { Format = "avro" } };
        var flow = parsed with { Source = parsed.Source with { Submissions = submissions } };

        var refusal = SubmissionLanding.Refusal(flow, Repository());

        Assert.NotNull(refusal);
        Assert.Contains("'avro'", refusal, StringComparison.Ordinal);
        Assert.Contains("is not a landing format", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_the_check_decides_on_is_the_name_the_landing_will_carry()
    {
        // The check tests the generated file name, not a pattern standing in for it, so it is the same decision the
        // engine makes when the pre flow runs. Its dataset and extension are what the glob has to accept.
        var fileName = SubmissionLanding.FileName(Guid.Parse("0195c0de-0000-7000-8000-00000000abcd"), SqlFlow.Delivery.Source.SourceDatasets.Record, LandingFormats.Csv);

        Assert.Equal("0195c0de00007000800000000000abcd_record.csv", fileName);
    }
}
