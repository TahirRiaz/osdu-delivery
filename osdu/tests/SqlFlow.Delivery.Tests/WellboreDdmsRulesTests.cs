using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The record and column rules the Wellbore DDMS applies (<see cref="WellboreDdmsRules"/>), as its consistency modules
/// state them at the pinned commit (osdu/specs/wellbore-ddms/INTEGRATION.md sections 3.1 and 4.3), and the bulk link a
/// metadata update carries (<see cref="WellboreDdmsBulkLink"/>, section 5.3).
/// </summary>
public sealed class WellboreDdmsRulesTests
{
    private static JsonObject Data(string data) => TestSchema.Doc("{\"data\":" + data + "}");

    private static string? Record(string entityType, string data, bool withBulk = false)
        => WellboreDdmsRules.RecordProblem(entityType, Data(data), withBulk);

    [Fact]
    public void A_well_log_has_unique_curve_ids_a_reference_among_them_and_with_bulk_an_id_on_every_curve()
    {
        const string log = WellboreDdmsRules.WellLog;
        Assert.Null(Record(log, """{"ReferenceCurveID":"MD","Curves":[{"CurveID":"MD"},{"CurveID":"GR"}]}"""));
        Assert.Contains("data.Curves has the CurveID 'GR' more than once", Record(log, """{"Curves":[{"CurveID":"GR"},{"CurveID":"GR"}]}"""), StringComparison.Ordinal);

        // Curves without an id are left out of the uniqueness check, as the service leaves them out.
        Assert.Null(Record(log, """{"Curves":[{"CurveID":""},{"CurveID":""},{"Mnemonic":"X"},{"Mnemonic":"Y"}]}"""));
        Assert.Contains("data.ReferenceCurveID is 'MD' but data.Curves describes only GR", Record(log, """{"ReferenceCurveID":"MD","Curves":[{"CurveID":"GR"}]}"""), StringComparison.Ordinal);
        Assert.Null(Record(log, """{"ReferenceCurveID":"","Curves":[{"CurveID":"GR"}]}"""));

        // A curve without an id passes the record write and fails the bulk write, so it holds only a record that has bulk.
        Assert.Null(Record(log, """{"Curves":[{"CurveID":"GR"},{"Mnemonic":"X"}]}"""));
        Assert.Contains("data.Curves[1] has no CurveID", Record(log, """{"Curves":[{"CurveID":"GR"},{"Mnemonic":"X"}]}""", withBulk: true), StringComparison.Ordinal);

        // A record without data is not checked at all.
        Assert.Null(WellboreDdmsRules.RecordProblem(log, TestSchema.Doc("""{"kind":"k"}"""), withBulk: true));
        Assert.Null(Record(log, "{}", withBulk: true));
    }

    [Fact]
    public void Curve_ids_are_compared_as_the_service_compares_them()
    {
        // "1" and 1 are different values to Python, and a zero or false id is not given.
        Assert.Null(Record(WellboreDdmsRules.WellLog, """{"Curves":[{"CurveID":"1"},{"CurveID":1}]}"""));
        Assert.Null(Record(WellboreDdmsRules.WellLog, """{"Curves":[{"CurveID":0},{"CurveID":0},{"CurveID":false},{"CurveID":false}]}"""));
        Assert.NotNull(Record(WellboreDdmsRules.WellLog, """{"Curves":[{"CurveID":2},{"CurveID":2}]}"""));
        Assert.NotNull(Record("WORK-PRODUCT-COMPONENT--WELLLOG", """{"Curves":[{"CurveID":"A"},{"CurveID":"A"}]}"""));
    }

    [Fact]
    public void A_trajectory_has_unique_station_property_names()
    {
        const string trajectory = WellboreDdmsRules.WellboreTrajectory;
        Assert.Null(Record(trajectory, """{"AvailableTrajectoryStationProperties":[{"Name":"MD"},{"Name":"INC"}]}"""));
        Assert.Null(Record(trajectory, """{"Name":"no stations"}""", withBulk: true));
        Assert.Contains(
            "data.AvailableTrajectoryStationProperties has the Name 'MD' more than once; the Wellbore DDMS requires every Name of a WellboreTrajectory to be unique",
            Record(trajectory, """{"AvailableTrajectoryStationProperties":[{"Name":"MD"},{"Name":"MD"}]}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_ppfg_dataset_names_its_context_and_trajectory_and_keeps_its_curve_rules()
    {
        const string ppfg = WellboreDdmsRules.PpfgDataset;
        const string ok = "\"ContextTypeID\":\"ctx\",\"ReferenceWellTrajectoryID\":\"dev:work-product-component--WellboreTrajectory:t:\"";
        Assert.Null(Record(ppfg, "{" + ok + ",\"PrimaryReferenceCurveID\":\"TVD\",\"Curves\":[{\"CurveID\":\"TVD\"},{\"CurveID\":\"PP\"}]}", withBulk: true));
        Assert.Contains("data.ContextTypeID is not given", Record(ppfg, """{"ReferenceWellTrajectoryID":"t"}"""), StringComparison.Ordinal);
        Assert.Contains("data.ContextTypeID is not given", Record(ppfg, """{"ContextTypeID":"","ReferenceWellTrajectoryID":"t"}"""), StringComparison.Ordinal);
        Assert.Contains("data.ReferenceWellTrajectoryID is not given", Record(ppfg, """{"ContextTypeID":"ctx"}"""), StringComparison.Ordinal);
        Assert.Contains("more than once", Record(ppfg, "{" + ok + ",\"Curves\":[{\"CurveID\":\"PP\"},{\"CurveID\":\"PP\"}]}"), StringComparison.Ordinal);
        Assert.Contains("data.PrimaryReferenceCurveID is 'TVD' but data.Curves describes only PP", Record(ppfg, "{" + ok + ",\"PrimaryReferenceCurveID\":\"TVD\",\"Curves\":[{\"CurveID\":\"PP\"}]}"), StringComparison.Ordinal);
        Assert.Contains("data.Curves[0] has no CurveID", Record(ppfg, "{" + ok + ",\"Curves\":[{\"CurveUnit\":\"psi\"}]}", withBulk: true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pressure_test_has_unique_curve_ids_and_with_bulk_an_id_on_every_curve()
    {
        const string test = WellboreDdmsRules.PressureTest;
        Assert.Null(Record(test, """{"Curves":[{"CurveID":"P"},{"CurveID":"T"}]}""", withBulk: true));
        Assert.Contains("every CurveID of a WellPressureTestRawMeasurement to be unique", Record(test, """{"Curves":[{"CurveID":"P"},{"CurveID":"P"}]}"""), StringComparison.Ordinal);
        Assert.Contains("data.Curves[0, 2] has no CurveID", Record(test, """{"Curves":[{},{"CurveID":"P"},{"CurveID":""}]}""", withBulk: true), StringComparison.Ordinal);

        // Other kinds carry no rule here, whatever their data holds.
        Assert.Null(Record("master-data--Wellbore", """{"Curves":[{"CurveID":"P"},{"CurveID":"P"}]}""", withBulk: true));
    }

    [Fact]
    public void Array_columns_are_one_curve_counted_as_the_service_counts_them()
    {
        var curves = WellboreDdmsRules.CurveColumns(["MD", "ARR[0]", "ARR[1]", "ARR[2:3]", "A[B][1]", "X[]", "Y[:1]", string.Empty]);
        Assert.Equal(1, curves["MD"]);
        Assert.Equal(3, curves["ARR"]);
        Assert.Equal(1, curves["A[B]"]);
        Assert.Equal(1, curves["X[]"]);
        Assert.Equal(1, curves["Y[:1]"]);
        Assert.Equal(5, curves.Count);
    }

    [Fact]
    public void Bulk_columns_must_be_curves_of_the_record()
    {
        var record = Data("""{"Curves":[{"CurveID":"MD"},{"CurveID":"GR"},{"CurveID":"ARR","NumberOfColumns":2}]}""");
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIdsAndWidths, record, ["MD", "GR", "ARR[0]", "ARR[1]"]));

        // The record may describe curves the bulk does not carry.
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIdsAndWidths, record, ["MD"]));

        var unmatched = WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIds, record, ["MD", "RHOB", "NPHI"]);
        Assert.Contains("the bulk column(s) RHOB, NPHI match no data.Curves[].CurveID of the record (MD, GR, ARR)", unmatched, StringComparison.Ordinal);

        var widths = WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIdsAndWidths, record, ["MD", "GR[0]", "GR[1]", "ARR[0]"]);
        Assert.Contains("GR has 2 column(s) in the bulk data and NumberOfColumns 1 (not given)", widths, StringComparison.Ordinal);
        Assert.Contains("ARR has 1 column(s) in the bulk data and NumberOfColumns 2", widths, StringComparison.Ordinal);

        // Without the width rule (PPFGDataset), only the names count.
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIds, record, ["MD", "GR[0]", "GR[1]", "ARR[0]"]));

        Assert.Contains("data.Curves describes no curve", WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIds, Data("""{"Name":"x"}"""), ["MD"]), StringComparison.Ordinal);
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIds, Data("""{"Name":"x"}"""), []));
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.Unchecked, record, ["anything"]));
    }

    [Fact]
    public void A_session_has_to_carry_its_reference_curve_as_a_column()
    {
        // Seen live on ADME 0.29: every chunk of a session whose depth is the dataframe's index is accepted, and the
        // commit then answers "reference curve 'MD' do not cover the entire bulk" (INTEGRATION.md section 3.3).
        var log = Data("""{"Curves":[{"CurveID":"MD"},{"CurveID":"GR"}],"ReferenceCurveID":"MD"}""");
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIdsAndWidths, log, ["MD", "GR"]));

        var held = WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIdsAndWidths, log, ["GR"]);
        Assert.Contains("the session's chunks carry the column(s) GR and not the reference curve 'MD'", held, StringComparison.Ordinal);
        Assert.Contains("carries it as the row index of a dataframe does not count", held, StringComparison.Ordinal);

        // An array curve's columns are that curve, so a reference written as one counts.
        var array = Data("""{"Curves":[{"CurveID":"MD","NumberOfColumns":2},{"CurveID":"GR"}],"ReferenceCurveID":"MD"}""");
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIdsAndWidths, array, ["MD[0]", "MD[1]", "GR"]));

        // A PPFGDataset names its reference the other way round; a pressure test has none to check.
        var ppfg = Data("""{"Curves":[{"CurveID":"TVD"}],"PrimaryReferenceCurveID":"TVD"}""");
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIds, ppfg, ["TVD"]));
        Assert.Contains("not the reference curve 'TVD'", WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIds, ppfg, ["GR"]), StringComparison.Ordinal);
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIds, Data("""{"Curves":[{"CurveID":"GR"}]}"""), ["GR"]));

        // A trajectory's reference is the station whose type is measured depth.
        var trajectory = Data("""{"AvailableTrajectoryStationProperties":[{"Name":"MD","TrajectoryStationPropertyTypeID":"osdu:reference-data--TrajectoryStationPropertyType:MD:"},{"Name":"TVD","TrajectoryStationPropertyTypeID":"osdu:reference-data--TrajectoryStationPropertyType:TVD:"}]}""");
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.TrajectoryStations, trajectory, ["MD", "TVD"]));
        Assert.Contains("not the reference curve 'MD'", WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.TrajectoryStations, trajectory, ["TVD"]), StringComparison.Ordinal);

        // Nothing to check: no columns read, no rule in force, no data.
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIdsAndWidths, log, []));
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.Unchecked, log, ["GR"]));
        Assert.Null(WellboreDdmsRules.SessionReferenceProblem(DdmsBulkColumns.CurveIds, Data("{}"), ["GR"]));
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("2.0", true)]
    [InlineData("\"2\"", false)]
    [InlineData("3", false)]
    [InlineData("null", true)]
    public void A_curve_width_is_compared_as_a_number(string width, bool fits)
    {
        var declared = width == "null"
            ? Data("""{"Curves":[{"CurveID":"ARR","NumberOfColumns":null}]}""")
            : Data("{\"Curves\":[{\"CurveID\":\"ARR\",\"NumberOfColumns\":" + width + "}]}");
        var labels = width == "null" ? new[] { "ARR" } : ["ARR[0]", "ARR[1]"];
        Assert.Equal(fits, WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.CurveIdsAndWidths, declared, labels) is null);
    }

    [Fact]
    public void Trajectory_columns_must_be_station_properties_of_the_record()
    {
        var record = Data("""{"AvailableTrajectoryStationProperties":[{"Name":"MD"},{"Name":"INC"},{"Name":"AZI"}]}""");
        Assert.Null(WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.TrajectoryStations, record, ["MD", "INC", "AZI"]));
        Assert.Contains(
            "the bulk column(s) TVD match no data.AvailableTrajectoryStationProperties[].Name of the record (MD, INC, AZI)",
            WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.TrajectoryStations, record, ["MD", "TVD"]),
            StringComparison.Ordinal);
        Assert.Contains(
            "the record has no data.AvailableTrajectoryStationProperties",
            WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.TrajectoryStations, Data("""{"Name":"t"}"""), ["MD"]),
            StringComparison.Ordinal);

        // Declared as null, the property is there and names no station, so every column is unmatched.
        Assert.Contains(
            "the bulk column(s) MD match no",
            WellboreDdmsRules.ColumnsProblem(DdmsBulkColumns.TrajectoryStations, Data("""{"AvailableTrajectoryStationProperties":null}"""), ["MD"]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_metadata_update_carries_the_bulk_link_and_the_service_datasets_the_ddms_holds()
    {
        var stored = TestSchema.Doc("""
            {"id":"dev:work-product-component--WellLog:a","version":3,"data":{
              "ExtensionProperties":{"wdms":{"bulkURI":"urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1"}},
              "DDMSDatasets":["urn://wdms-1/uuid:38f0438e-71b8-4806-924b-9753796a77c1","urn://uuid:00000000-0000-0000-0000-000000000001","dev:dataset--File.Generic:f:"]}}
            """);
        var document = TestSchema.Doc("""{"data":{"Name":"GR","ExtensionProperties":{"source":"recall"},"DDMSDatasets":["dev:dataset--File.Generic:g:"]}}""");

        Assert.Equal("urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1", WellboreDdmsBulkLink.Of(stored));
        Assert.True(WellboreDdmsBulkLink.Carry(stored, document));
        Assert.Equal("urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1", WellboreDdmsBulkLink.Of(document));

        // What the mapping rendered under ExtensionProperties stays; only the DDMS's own dataset entries are added.
        Assert.Equal("recall", (string?)document["data"]!["ExtensionProperties"]!["source"]);
        Assert.Equal(
            ["dev:dataset--File.Generic:g:", "urn://wdms-1/uuid:38f0438e-71b8-4806-924b-9753796a77c1", "urn://uuid:00000000-0000-0000-0000-000000000001"],
            document["data"]!["DDMSDatasets"]!.AsArray().Select(n => (string?)n).ToList());

        // Carried twice, nothing is added twice.
        Assert.True(WellboreDdmsBulkLink.Carry(stored, document));
        Assert.Equal(3, document["data"]!["DDMSDatasets"]!.AsArray().Count);

        // A document without the blocks gets them.
        var bare = TestSchema.Doc("""{"data":{"Name":"GR"}}""");
        WellboreDdmsBulkLink.Carry(stored, bare);
        Assert.Equal("urn:wdms-1:uuid:38f0438e-71b8-4806-924b-9753796a77c1", WellboreDdmsBulkLink.Of(bare));
        Assert.Equal(2, bare["data"]!["DDMSDatasets"]!.AsArray().Count);
    }

    [Fact]
    public void A_bulk_link_the_ddms_does_not_hold_is_not_sent()
    {
        var rendered = TestSchema.Doc("""{"data":{"ExtensionProperties":{"wdms":{"bulkURI":"urn:wdms-1:uuid:11111111-1111-1111-1111-111111111111","other":1}}}}""");
        Assert.False(WellboreDdmsBulkLink.Carry(null, rendered));
        Assert.Null(WellboreDdmsBulkLink.Of(rendered));
        Assert.Equal(1, (int?)rendered["data"]!["ExtensionProperties"]!["wdms"]!["other"]);

        var stored = TestSchema.Doc("""{"data":{"ExtensionProperties":{"wdms":{"bulkURI":"urn:wdms-1:uuid:22222222-2222-2222-2222-222222222222"}}}}""");
        var differing = TestSchema.Doc("""{"data":{"ExtensionProperties":{"wdms":{"bulkURI":"urn:wdms-1:uuid:11111111-1111-1111-1111-111111111111"}}}}""");
        Assert.False(WellboreDdmsBulkLink.Carry(stored, differing));
        Assert.Equal("urn:wdms-1:uuid:22222222-2222-2222-2222-222222222222", WellboreDdmsBulkLink.Of(differing));

        // An empty link is none, and a stored version without one takes nothing away from a document that has none.
        Assert.Null(WellboreDdmsBulkLink.Of(TestSchema.Doc("""{"data":{"ExtensionProperties":{"wdms":{"bulkURI":""}}}}""")));
        var plain = TestSchema.Doc("""{"data":{"Name":"x"}}""");
        Assert.True(WellboreDdmsBulkLink.Carry(TestSchema.Doc("""{"data":{"Name":"x"}}"""), plain));
        Assert.Equal("""{"data":{"Name":"x"}}""", plain.ToJsonString());

        // A document whose ExtensionProperties is not an object is left for the service's schema check.
        var odd = TestSchema.Doc("""{"data":{"ExtensionProperties":"text"}}""");
        Assert.True(WellboreDdmsBulkLink.Carry(stored, odd));
        Assert.Equal("text", (string?)odd["data"]!["ExtensionProperties"]);
    }
}
