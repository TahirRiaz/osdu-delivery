using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Submissions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The records a source sends inline (design.md section 3.4): their shape, the type each column takes across the rows of
/// its dataset, the canonical form the ledger stores and hashes, and every refusal, which names the record, the child
/// dataset and the column it is about and never a value.
/// </summary>
public class InlineRecordsTests
{
    [Fact]
    public void Columns_take_one_type_across_rows_and_keep_the_order_they_were_first_named_in()
    {
        var records = InlineRecords.Parse("""
            [
              { "record": { "facility_name": "WB-1", "depth": 12, "active": true, "note": null },
                "datasets": { "aliases": [ { "alias_name": "A", "ordinal": 1 }, { "alias_name": "B", "ordinal": 1.5 } ] } },
              { "record": { "depth": 12.5, "facility_name": "WB-2" } }
            ]
            """);

        Assert.Equal(2, records.Records.Count);
        Assert.Equal(2, records.ChildRowCount);
        Assert.Equal(
            new[]
            {
                new InlineColumn("facility_name", InlineColumnTypes.Text),
                new InlineColumn("depth", InlineColumnTypes.Real),
                new InlineColumn("active", InlineColumnTypes.Flag),
                new InlineColumn("note", InlineColumnTypes.Text),
            },
            records.RootColumns);
        var aliases = Assert.Single(records.DatasetColumns);
        Assert.Equal("aliases", aliases.Key);
        Assert.Equal(
            new[] { new InlineColumn("alias_name", InlineColumnTypes.Text), new InlineColumn("ordinal", InlineColumnTypes.Real) },
            aliases.Value);

        // A whole number stays a long where nothing widens it, and the column of a row that also holds a decimal is a double.
        Assert.Equal(12L, records.Records[0].Row["depth"]);
        Assert.Equal(12.5, records.Records[1].Row["depth"]);
        Assert.True((bool)records.Records[0].Row["active"]!);
        Assert.Null(records.Records[0].Row["note"]);
    }

    [Fact]
    public void The_canonical_form_ignores_key_order_and_whitespace_and_is_what_the_hash_is_over()
    {
        var records = InlineRecords.Parse("""
            [ { "record": { "facility_name": "WB-1", "facility_description": "first" },
                "datasets": { "aliases": [ { "alias_name": "A" } ] } } ]
            """);
        var reordered = InlineRecords.Parse(
            """[{"datasets":{"aliases":[{"alias_name":"A"}]},"record":{"facility_description":"first","facility_name":"WB-1"}}]""");

        Assert.Equal(records.Json, reordered.Json);
        Assert.Equal(records.ContentHash, reordered.ContentHash);
        Assert.Equal(records.ContentBytes, reordered.ContentBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(records.Json), records.ContentBytes);

        // Record order is not key order: two submissions that send the same records in another order are not the same.
        var swapped = InlineRecords.Parse("""[{"record":{"facility_name":"B"}},{"record":{"facility_name":"A"}}]""");
        var straight = InlineRecords.Parse("""[{"record":{"facility_name":"A"}},{"record":{"facility_name":"B"}}]""");
        Assert.NotEqual(swapped.ContentHash, straight.ContentHash);
    }

    [Fact]
    public void The_stored_form_reads_back_as_the_same_records()
    {
        var records = InlineRecords.Parse("""
            [ { "record": { "facility_name": "WB-1", "depth": 1000.0, "count": 1000, "ok": false, "note": null },
                "datasets": { "aliases": [ { "alias_name": "A" } ], "events": [] } } ]
            """);

        var stored = InlineRecords.Parse(records.Json);
        Assert.Equal(records.Json, stored.Json);
        Assert.Equal(records.ContentHash, stored.ContentHash);
        Assert.Equal(records.ChildRowCount, stored.ChildRowCount);
        // A real number keeps its fraction in the stored form, so the column does not turn into a whole number.
        Assert.Contains("\"depth\":1000.0", records.Json, StringComparison.Ordinal);
        Assert.Equal(
            new[] { new InlineColumn("count", InlineColumnTypes.Whole), new InlineColumn("depth", InlineColumnTypes.Real), new InlineColumn("facility_name", InlineColumnTypes.Text), new InlineColumn("note", InlineColumnTypes.Text), new InlineColumn("ok", InlineColumnTypes.Flag) },
            stored.RootColumns);
        // A child dataset named with no rows is still one the submission carried.
        Assert.Equal(["aliases", "events"], stored.DatasetColumns.Select(s => s.Key));
    }

    [Fact]
    public void Text_survives_unicode_control_free_content_and_long_values()
    {
        var long_ = new string('x', 50_000);
        var records = InlineRecords.Parse(JsonSerializer.Serialize(new[]
        {
            new { record = new Dictionary<string, object?> { ["facility_name"] = "Brønnøysund Å / 15⁄9", ["note"] = long_, ["emoji"] = "well \U0001F6E2" } },
        }));

        var row = InlineRecords.Parse(records.Json).Records[0].Row;
        Assert.Equal("Brønnøysund Å / 15⁄9", row["facility_name"]);
        Assert.Equal(long_, row["note"]);
        Assert.Equal("well \U0001F6E2", row["emoji"]);
    }

    [Theory]
    [InlineData("{}", "records must be a JSON array")]
    [InlineData("[]", "records is empty")]
    [InlineData("[1]", "records[0] must be an object")]
    [InlineData("""[{"datasets":{}}]""", "records[0] has no \"record\"")]
    [InlineData("""[{"record":{}}]""", "records[0].record has no columns")]
    [InlineData("""[{"record":[]}]""", "records[0].record must be an object")]
    [InlineData("""[{"record":{"a":1},"extra":1}]""", "records[0] has a key other than")]
    [InlineData("""[{"record":{"a":1},"record":{"b":2}}]""", "records[0] names \"record\" twice")]
    [InlineData("""[{"record":{"a":[1]}}]""", "records[0].record.a is a nested value")]
    [InlineData("""[{"record":{"a":{"b":1},"c":1}}]""", "records[0].record.a is a nested value")]
    [InlineData("""[{"record":{"a":1}},{"record":{"a":"x"}}]""", "records[1].record.a is a string, but records[0].record.a is a number")]
    [InlineData("""[{"record":{"a":true}},{"record":{"a":1}}]""", "records[1].record.a is a number, but records[0].record.a is a boolean")]
    [InlineData("""[{"record":{"a":1,"A":2}}]""", "records[0].record.A is named twice")]
    [InlineData("""[{"record":{"a":123456789012345678901234}}]""", "records[0].record.a is an integer outside the 64-bit range")]
    [InlineData("""[{"record":{"a":1e400}}]""", "records[0].record.a is a number outside the range of a double")]
    [InlineData("""[{"record":{"deliveryKey":"not-a-key"}}]""", "records[0].record.deliveryKey must be the record's delivery key as a UUID")]
    [InlineData("""[{"record":{"deliveryKey":7}}]""", "records[0].record.deliveryKey must be the record's delivery key as a UUID")]
    [InlineData("""[{"record":{"a":1},"datasets":{"aliases":[{"deliveryKey":"6c773479-64d0-57ca-8877-9ea97d18b766"}]}}]""", "records[0].datasets.aliases[0].deliveryKey is reserved in a child row")]
    [InlineData("""[{"record":{"a":1},"datasets":{"record":[]}}]""", "which names the dataset row itself")]
    [InlineData("""[{"record":{"a":1},"datasets":{"1aliases":[]}}]""", "not an identifier")]
    [InlineData("""[{"record":{"a":1},"datasets":{"aliases":{}}}]""", "records[0].datasets.aliases must be an array of rows")]
    [InlineData("""[{"record":{"a":1},"datasets":[]}]""", "records[0].datasets must be an object")]
    [InlineData("""[{"record":{"a":1},"datasets":{"aliases":[1]}}]""", "records[0].datasets.aliases[0] must be an object")]
    [InlineData("""[{"record":{" a":1}}]""", "padded with spaces")]
    [InlineData("""[{"record":{"":1}}]""", "empty")]
    [InlineData("""[{"record":{"a":1},"files":[]}]""", "records[0].files must be an object of payload names")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":1}}]""", "records[0].files.curves must be where the payload's files are")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":""}}]""", "records[0].files.curves must name where the payload's files are")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":"  "}}]""", "records[0].files.curves must name where the payload's files are")]
    [InlineData("""[{"record":{"a":1},"files":{"1curves":"x"}}]""", "records[0].files names a payload that is not an identifier")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":{"location":"x","nope":1}}}]""", "has a key other than \"location\" and \"hash\"")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":{"location":7}}}]""", "records[0].files.curves.location must be the folder or glob")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":{"location":"x","hash":7}}}]""", "records[0].files.curves.hash must be the payload's content hash")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":{"location":"x","hash":""}}}]""", "records[0].files.curves.hash must be 1 to")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":"x","CURVES":"y"}}]""", "records[0].files.CURVES is named twice")]
    [InlineData("""[{"record":{"a":1},"files":{"curves":"x"},"files":{"curves":"y"}}]""", "records[0] names \"files\" twice")]
    public void Malformed_records_are_refused_naming_where(string json, string message)
    {
        var ex = Assert.Throws<FlowValidationException>(() => InlineRecords.Parse(json));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_json_is_refused_as_such()
    {
        var ex = Assert.Throws<FlowValidationException>(() => InlineRecords.Parse("{not json"));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ceilings_of_one_submission_are_enforced()
    {
        var tooMany = "[" + string.Join(",", Enumerable.Range(0, InlineRecords.MaxRecords + 1).Select(i => $"{{\"record\":{{\"n\":{i}}}}}")) + "]";
        Assert.Contains($"at most {InlineRecords.MaxRecords}", Assert.Throws<FlowValidationException>(() => InlineRecords.Parse(tooMany)).Message, StringComparison.Ordinal);

        var columns = string.Join(",", Enumerable.Range(0, InlineRecords.MaxColumns + 1).Select(i => $"\"c{i}\":1"));
        Assert.Contains($"at most {InlineRecords.MaxColumns}", Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{{columns}}}}}]")).Message, StringComparison.Ordinal);

        var datasets = string.Join(",", Enumerable.Range(0, InlineRecords.MaxDatasets + 1).Select(i => $"\"s{i}\":[]"));
        Assert.Contains($"at most {InlineRecords.MaxDatasets}", Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{\"a\":1}},\"datasets\":{{{datasets}}}}}]")).Message, StringComparison.Ordinal);

        var name = new string('c', InlineRecords.MaxNameLength + 1);
        Assert.Contains("longer than", Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{\"{name}\":1}}}}]")).Message, StringComparison.Ordinal);

        // The content ceiling counts the canonical form's bytes, whatever whitespace the request carried.
        var big = JsonSerializer.Serialize(Enumerable.Range(0, 20).Select(i => new { record = new Dictionary<string, object?> { ["n"] = i, ["blob"] = new string('y', 500_000) } }));
        Assert.Contains("at most", Assert.Throws<FlowValidationException>(() => InlineRecords.Parse(big)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_points_at_where_its_payload_files_already_are()
    {
        var records = InlineRecords.Parse("""
            [ { "record": { "log_id": "L-1" }, "files": { "curves": "abfss://lake@acct.dfs.core.windows.net/recall/L-1/*.parquet" } },
              { "record": { "log_id": "L-2" }, "files": { "curves": { "location": "abfss://lake@acct.dfs.core.windows.net/recall/L-2", "hash": "sha256:abc" } } },
              { "record": { "log_id": "L-3" } },
              { "record": { "log_id": "L-4" }, "files": null } ]
            """);

        Assert.Equal(["curves"], records.FileSets);
        Assert.Equal("abfss://lake@acct.dfs.core.windows.net/recall/L-1/*.parquet", records.Records[0].Files["curves"].Location);
        Assert.Null(records.Records[0].Files["curves"].Hash);
        Assert.Equal("sha256:abc", records.Records[1].Files["curves"].Hash);
        // A record that points at nothing carries no files at all, which is what lets a flow hold it instead of guessing.
        Assert.Empty(records.Records[2].Files);
        Assert.Empty(records.Records[3].Files);

        // The stored form writes both spellings the same way, and reads back as the same records.
        Assert.Contains("""{"location":"abfss://lake@acct.dfs.core.windows.net/recall/L-1/*.parquet"}""", records.Json, StringComparison.Ordinal);
        var stored = InlineRecords.Parse(records.Json);
        Assert.Equal(records.Json, stored.Json);
        Assert.Equal(records.ContentHash, stored.ContentHash);
        Assert.Equal("sha256:abc", stored.Records[1].Files["curves"].Hash);
    }

    [Fact]
    public void Where_the_files_are_is_part_of_what_the_content_hash_covers()
    {
        var at = InlineRecords.Parse("""[{"record":{"log_id":"L-1"},"files":{"curves":"lake/one"}}]""");
        var elsewhere = InlineRecords.Parse("""[{"record":{"log_id":"L-1"},"files":{"curves":"lake/two"}}]""");
        var hashed = InlineRecords.Parse("""[{"record":{"log_id":"L-1"},"files":{"curves":{"location":"lake/one","hash":"sha256:abc"}}}]""");
        var none = InlineRecords.Parse("""[{"record":{"log_id":"L-1"}}]""");

        Assert.Equal(at.ContentHash, InlineRecords.Parse("""[{"record":{"log_id":"L-1"},"files":{"curves":{"location":"lake/one"}}}]""").ContentHash);
        Assert.NotEqual(at.ContentHash, elsewhere.ContentHash);
        Assert.NotEqual(at.ContentHash, hashed.ContentHash);
        Assert.NotEqual(at.ContentHash, none.ContentHash);
    }

    [Fact]
    public void A_submission_points_at_a_bounded_number_of_payloads_with_bounded_locations()
    {
        var sets = string.Join(",", Enumerable.Range(0, InlineRecords.MaxFileSets + 1).Select(i => $"\"s{i}\":\"lake/{i}\""));
        Assert.Contains(
            $"at most {InlineRecords.MaxFileSets}",
            Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{\"a\":1}},\"files\":{{{sets}}}}}]")).Message,
            StringComparison.Ordinal);

        var long_ = new string('x', InlineRecords.MaxLocationLength + 1);
        Assert.Contains(
            $"1 to {InlineRecords.MaxLocationLength} characters",
            Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{\"a\":1}},\"files\":{{\"curves\":\"{long_}\"}}}}]")).Message,
            StringComparison.Ordinal);

        var hash = new string('h', InlineRecords.MaxHashLength + 1);
        Assert.Contains(
            $"1 to {InlineRecords.MaxHashLength} characters",
            Assert.Throws<FlowValidationException>(() => InlineRecords.Parse($"[{{\"record\":{{\"a\":1}},\"files\":{{\"curves\":{{\"location\":\"lake/one\",\"hash\":\"{hash}\"}}}}}}]")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_may_carry_the_delivery_key_it_derived_itself()
    {
        var records = InlineRecords.Parse("""[{"record":{"deliveryKey":"6C773479-64D0-57CA-8877-9EA97D18B766","facility_name":"WB-INLINE-1"}}]""");
        Assert.Equal("6C773479-64D0-57CA-8877-9EA97D18B766", records.Records[0].Row["deliveryKey"]);
        Assert.Contains(records.RootColumns, c => c.Name.Equals("deliveryKey", StringComparison.Ordinal));
    }
}

/// <summary>The accepted form of a submission request: what a repeat has to match, and what a changed one is refused for.</summary>
public class InlineSubmissionStateTests
{
    private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string> { ["logSource"] = "STAT_COMP" };

    private static Model.FlowDefinition Fixture() => new DeliveryDocumentLoader().LoadFlow(Samples.Flow);

    [Fact]
    public void A_repeat_is_the_same_request_however_it_was_written()
    {
        var flow = Fixture();
        var id = Guid.NewGuid();
        var accepted = InlineSubmissionState.Accept(id, flow, "deliver", false, Values, InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, "api:source");
        var repeat = InlineSubmissionState.Accept(id, flow, " Deliver ", false, Values, InlineRecords.Parse("""[ { "record" : { "x" : 1 } } ]"""), DateTime.UtcNow.AddMinutes(5), "api:another");

        Assert.Empty(accepted.Differences(repeat));
        Assert.Equal(accepted.RequestHash, repeat.RequestHash);
        Assert.Equal(accepted.ContentHash, repeat.ContentHash);
        Assert.Equal("deliver", repeat.Operation);
    }

    [Fact]
    public void Every_part_of_a_request_is_part_of_what_a_repeat_must_match()
    {
        var flow = Fixture();
        var id = Guid.NewGuid();
        var records = InlineRecords.Parse("""[{"record":{"x":1}}]""");
        var accepted = InlineSubmissionState.Accept(id, flow, "deliver", false, Values, records, DateTime.UtcNow, "api:source");

        var other = InlineSubmissionState.Accept(id, flow, "plan", true, new Dictionary<string, string> { ["logSource"] = "STAT_CPI" }, InlineRecords.Parse("""[{"record":{"x":2}}]"""), DateTime.UtcNow, "api:source");
        var differences = accepted.Differences(other);
        Assert.Equal(4, differences.Count);
        Assert.Contains(differences, d => d.StartsWith("the operation", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("force", StringComparison.Ordinal));
        Assert.Contains("the flow parameter values", differences);
        Assert.Contains("the records", differences);
        Assert.NotEqual(accepted.RequestHash, other.RequestHash);

        // One part at a time, so a difference is never hidden by another.
        Assert.Equal(["the records"], accepted.Differences(InlineSubmissionState.Accept(id, flow, "deliver", false, Values, InlineRecords.Parse("""[{"record":{"x":2}}]"""), DateTime.UtcNow, "api:source")));
        Assert.Equal(["force (false, not true)"], accepted.Differences(InlineSubmissionState.Accept(id, flow, "deliver", true, Values, records, DateTime.UtcNow, "api:source")));
    }

    [Fact]
    public void The_parameter_values_are_stored_sorted_and_read_back()
    {
        var flow = Fixture();
        var accepted = InlineSubmissionState.Accept(
            Guid.NewGuid(), flow, "deliver", false, new Dictionary<string, string> { ["zebra"] = "z", ["alpha"] = "a" },
            InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, "api:source");

        Assert.Equal("""{"alpha":"a","zebra":"z"}""", accepted.ParametersJson);
        Assert.Equal(new Dictionary<string, string> { ["alpha"] = "a", ["zebra"] = "z" }, accepted.Parameters());
    }

    [Fact]
    public void Only_the_operations_a_submission_can_ask_for_are_accepted()
    {
        var flow = Fixture();
        Assert.Equal([DeliveryOperations.Deliver, DeliveryOperations.Plan], InlineSubmissionState.Operations);
        foreach (var operation in new[] { "verify", "drain", "intake", "known-state", "" })
        {
            Assert.ThrowsAny<ArgumentException>(() => InlineSubmissionState.Accept(
                Guid.NewGuid(), flow, operation, false, Values, InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, "api:source"));
        }

        Assert.Throws<ArgumentException>(() => InlineSubmissionState.Accept(
            Guid.Empty, flow, "deliver", false, Values, InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, "api:source"));
    }

    [Fact]
    public void A_long_actor_label_is_cut_to_what_the_ledger_holds()
    {
        var flow = Fixture();
        var accepted = InlineSubmissionState.Accept(
            Guid.NewGuid(), flow, "deliver", false, Values, InlineRecords.Parse("""[{"record":{"x":1}}]"""), DateTime.UtcNow, new string('a', 500));

        Assert.Equal(InlineSubmissionState.MaxActorLength, accepted.ReceivedBy.Length);
    }
}

/// <summary>The columns a mapping reads, per dataset, with the template variables each fills: the column half of a flow's source contract.</summary>
public class MappingColumnsTests
{
    private static Model.MappingDefinition Load(string reference)
    {
        var path = Path.Combine(Samples.Mappings, reference + ".yaml");
        return new DeliveryDocumentLoader().ParseMapping(File.ReadAllText(path), path);
    }

    [Fact]
    public void The_wellbore_mapping_reads_its_key_its_values_and_the_alias_dataset_that_fills_the_name_aliases()
    {
        var columns = MappingColumns.Read(Load("Wellbore@1.0.0"));

        Assert.Equal(["facility_name"], columns.Key);
        Assert.Equal(["facility_name", "facility_description", "facility_id"], columns.RecordNames);
        var name = columns.Record[0];
        Assert.True(name.Key);
        Assert.True(name.Label);
        var fills = Assert.Single(name.Uses);
        Assert.Equal("osdu.data.FacilityName", fills.Entry.Target.Text);
        Assert.Equal(ColumnRole.Value, fills.Role);
        Assert.True(fills.Entry.Required);
        var description = columns.Record[1];
        Assert.False(description.Key);
        Assert.False(Assert.Single(description.Uses).Entry.Required);

        var aliases = Assert.Single(columns.Datasets);
        Assert.Equal("aliases", aliases.Name);
        Assert.Equal("osdu.data.NameAliases", Assert.Single(aliases.Repeaters).Target.Text);
        var alias = Assert.Single(aliases.Columns);
        Assert.Equal("alias_name", alias.Name);
        Assert.Equal("osdu.data.NameAliases[].AliasName", Assert.Single(alias.Uses).Entry.Target.Text);
        Assert.Equal(["alias_name"], aliases.ColumnNames);
    }

    [Fact]
    public void The_well_log_mapping_says_which_columns_find_cached_records_and_which_only_the_label_reads()
    {
        var columns = MappingColumns.Read(Load("WellLog@1.4.0"));

        Assert.Equal(["source_project", "log_id"], columns.Key);
        Assert.Equal(["source_project", "log_id"], columns.RecordNames.Take(2));
        // The wellbore's name fills no variable itself: it finds the cached wellbore whose id the record points at.
        var uwi = columns.Record.Single(c => c.Name == "wellbore_uwi");
        var find = Assert.Single(uwi.Uses);
        Assert.Equal(ColumnRole.FindBy, find.Role);
        Assert.Equal("osdu.data.WellboreID", find.Entry.Target.Text);
        Assert.Equal("cache.Wellbore.FacilityName = dataset.wellbore_uwi", find.Find?.ToString());
        Assert.True(uwi.Label);
        // One column can fill several variables.
        Assert.Equal(["osdu.data.SamplingStart", "osdu.data.TopMeasuredDepth"], columns.Record.Single(c => c.Name == "index_min").Uses.Select(u => u.Entry.Target.Text));
        // The vertical reference gives the measurement's value, and finds its unit through three findBy lines.
        var elevation = columns.Record.Single(c => c.Name == "elev_meas_ref");
        Assert.Single(elevation.Uses, u => u.Role == ColumnRole.Value);
        Assert.Equal(3, elevation.Uses.Count(u => u.Role == ColumnRole.FindBy));
        // The label reads log_run, which no entry reads.
        var run = columns.Record.Single(c => c.Name == "log_run");
        Assert.True(run.Label);
        Assert.Empty(run.Uses);
        // The flow's version column is not the mapping's business.
        Assert.DoesNotContain("update_date", columns.RecordNames);

        var curves = Assert.Single(columns.Datasets);
        Assert.Equal("curves", curves.Name);
        Assert.Equal("osdu.data.Curves", Assert.Single(curves.Repeaters).Target.Text);
        Assert.Equal(
            ["curve_id", "curve_unit", "index_unit", "index_min", "index_max", "curve_description", "curve_version", "business_value"],
            curves.ColumnNames);
        // A curve without a business value is not a reason to hold its log.
        Assert.All(curves.Columns.Single(c => c.Name == "business_value").Uses, u => Assert.False(u.Entry.Required));
    }
}
