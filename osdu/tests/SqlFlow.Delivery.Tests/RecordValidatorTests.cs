using System.Diagnostics;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.ValidationFixtures;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A whole record checked against its template (<see cref="RecordValidator"/>): every rule JSON Schema states that the checks
/// hold, at the exact place it is broken; what could not be checked counted as such and never as met; the ids the record
/// names at its relationships collected for the reference lookup; and every bound a check of a record of any size keeps.
/// </summary>
public sealed class RecordValidatorTests
{
    private static RecordFindings Check(JsonNode? record, IReadOnlySet<string>? routeFilled = null, ValidationLimits? limits = null)
        => RecordValidator.Check(record, Rules, routeFilled, limits);

    [Fact]
    public void A_record_meeting_every_rule_has_no_problem_and_nothing_unchecked_and_names_the_records_it_refers_to()
    {
        var found = Check(ValidRecord());

        Assert.Empty(found.Problems);
        Assert.Equal(0, found.ProblemCount);
        Assert.Empty(found.Unverified);
        Assert.Equal(0, found.UnverifiedCount);
        Assert.True(found.Rules > 20, $"only {found.Rules} rules were applied");
        Assert.True(found.Values > 20);
        Assert.Equal(
            ["dev:master-data--Well:W-1:", "dev:reference-data--MeasurementType:KB:", "dev:master-data--Field:F-1:"],
            found.References.Select(r => r.Id));
        var measurement = Assert.Single(found.References, r => r.EntityType == "reference-data--MeasurementType");
        Assert.Equal("data.Measurements[].TypeID", measurement.At);
        Assert.Equal("data.Measurements[0].TypeID", measurement.Path);
    }

    [Theory]
    [MemberData(nameof(Breaks))]
    public void A_record_breaking_one_rule_has_one_problem_naming_the_rule_and_where(string change, string rule, string at, string path)
    {
        var found = Check(Record(r => Apply(r, change)));

        var problem = Assert.Single(found.Problems);
        Assert.Equal(1, found.ProblemCount);
        Assert.Equal(rule, problem.Rule);
        Assert.Equal(at, problem.At);
        Assert.Equal(path, problem.Path);
        Assert.False(string.IsNullOrWhiteSpace(problem.Message));
        Assert.DoesNotContain("at ", problem.Message[..Math.Min(3, problem.Message.Length)], StringComparison.Ordinal);
    }

    /// <summary>One change each, as <c>path=json</c> (a value set) or <c>-path</c> (a property removed), and what it breaks.</summary>
    public static TheoryData<string, string, string, string> Breaks() => new()
    {
        { "-kind", "required", "kind", "kind" },
        { "kind=\"thing\"", "pattern", "kind", "kind" },
        { "id=\"dev:master-data--Other:T-1\"", "pattern", "id", "id" },
        { "version=1.5", "type", "version", "version" },
        { "-acl.owners", "required", "acl.owners", "acl.owners" },
        { "acl.owners=[\"not an address\"]", "pattern", "acl.owners", "acl.owners[0]" },
        { "acl.groups=[]", "additionalProperties", "acl.groups", "acl.groups" },
        { "legal.otherRelevantDataCountries=[\"NOR\"]", "pattern", "legal.otherRelevantDataCountries", "legal.otherRelevantDataCountries[0]" },
        { "legal.status=\"pending\"", "pattern", "legal.status", "legal.status" },
        { "tags.Count=3", "type", "tags.Count", "tags.Count" },
        { "createdBy=\"someone\"", "additionalProperties", "createdBy", "createdBy" },
        { "-data.Name", "required", "data.Name", "data.Name" },
        { "data.Name=\"Al pha\"", "pattern", "data.Name", "data.Name" },
        { "data.Name=\"AlphaBetaGamma\"", "maxLength", "data.Name", "data.Name" },
        { "data.Name=null", "type", "data.Name", "data.Name" },
        { "data.Name=7", "type", "data.Name", "data.Name" },
        { "data.Code=\"ab-12\"", "pattern", "data.Code", "data.Code" },
        { "data.Status=\"Planned\"", "enum", "data.Status", "data.Status" },
        { "data.Depth=-1", "minimum", "data.Depth", "data.Depth" },
        { "data.Depth=20000", "exclusiveMaximum", "data.Depth", "data.Depth" },
        { "data.Depth=1.05", "multipleOf", "data.Depth", "data.Depth" },
        { "data.Depth=\"deep\"", "type", "data.Depth", "data.Depth" },
        { "data.Count=2147483648", "format", "data.Count", "data.Count" },
        { "data.Count=1.5", "type", "data.Count", "data.Count" },
        { "data.SpudDate=\"2026-09-01\"", "format", "data.SpudDate", "data.SpudDate" },
        { "data.SpudDate=\"2026-02-30T10:00:00Z\"", "format", "data.SpudDate", "data.SpudDate" },
        { "data.WellID=\"W-1\"", "relationship", "data.WellID", "data.WellID" },
        { "data.WellID=\"dev:master-data--Wellbore:W-1:\"", "relationship", "data.WellID", "data.WellID" },
        { "data.WellID=\"dev:master-data--Well:W 1:\"", "relationship", "data.WellID", "data.WellID" },
        { "data.Measurements=[{\"Value\":1}]", "required", "data.Measurements[].TypeID", "data.Measurements[0].TypeID" },
        { "data.Measurements=[{\"TypeID\":\"dev:reference-data--MeasurementType:KB:\",\"Value\":\"high\"}]", "type", "data.Measurements[].Value", "data.Measurements[0].Value" },
        { "data.Measurements={}", "type", "data.Measurements", "data.Measurements" },
        { "data.Aliases=[\"a\",\"a\"]", "uniqueItems", "data.Aliases", "data.Aliases" },
        { "data.Aliases=[\"a\",\"b\",\"c\",\"d\"]", "maxItems", "data.Aliases", "data.Aliases" },
        { "data.Context={\"OtherID\":\"x\"}", "anyOf", "data.Context", "data.Context" },
        { "data.Strict={\"B\":\"x\"}", "additionalProperties", "data.Strict.B", "data.Strict.B" },
        { "data.Datasets=[\"dev:master-data--Well:W-1:\"]", "relationship", "data.Datasets", "data.Datasets[0]" },
    };

    [Theory]
    [InlineData("data.Remark=null")]
    [InlineData("data.Remark=\"noted\"")]
    [InlineData("data.Extra=null")]
    [InlineData("data.Extra=[1,{\"a\":null}]")]
    [InlineData("data.Context={\"BasinID\":\"dev:reference-data--Basin:North:\"}")]
    [InlineData("data.Datasets=[\"dev:dataset--File.Generic:F-1:\"]")]
    [InlineData("data.Depth=0")]
    [InlineData("data.Depth=19999.9")]
    [InlineData("data.Depth=0.3")]
    [InlineData("data.Count=-2147483648")]
    [InlineData("data.Name=\"Ωmega😀\"")]
    [InlineData("tags.Anything=\"text\"")]
    [InlineData("-data.Code")]
    [InlineData("-id")]
    public void A_value_every_rule_allows_is_no_problem(string change)
    {
        var found = Check(Record(r => Apply(r, change)));
        Assert.Empty(found.Problems);
        Assert.Equal(0, found.UnverifiedCount);
    }

    [Fact]
    public void A_record_missing_a_property_its_root_requires_is_said_to_be_the_record_and_not_a_value()
    {
        var problem = Assert.Single(Check(Record(r => r.Remove("legal"))).Problems);
        Assert.Equal("the record has no legal, which the schema requires", problem.Message);
        Assert.Equal("legal", ValidationVerdict.Where(problem));
    }

    [Fact]
    public void A_record_that_is_not_an_object_is_one_problem_at_the_record_itself()
    {
        var problem = Assert.Single(Check(new JsonArray(1, 2)).Problems);
        Assert.Equal("type", problem.Rule);
        Assert.Equal("the record", ValidationVerdict.Where(problem));
        Assert.Equal("a list of 2 where the schema takes an object", problem.Message);
    }

    [Fact]
    public void Every_rule_of_every_allOf_branch_holds_so_a_property_two_branches_describe_is_held_to_both()
    {
        // Common gives data.Name a pattern, the record's own branch its lengths: a value breaking each is one problem each.
        var both = Check(Record(r => DataOf(r)["Name"] = "Too long now")).Problems;
        Assert.Equal(["pattern", "maxLength"], both.Select(p => p.Rule).Order(StringComparer.Ordinal).Reverse());

        // An empty name is shorter than one branch allows and does not match the other's pattern.
        var empty = Check(Record(r => DataOf(r)["Name"] = string.Empty)).Problems;
        Assert.Equal(["minLength", "pattern"], empty.Select(p => p.Rule).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Text_length_is_counted_in_characters_as_JSON_Schema_counts_them_not_in_UTF16_units()
    {
        // Ten characters, of which five take two UTF-16 units each: within the ten the schema allows.
        Assert.Empty(Check(Record(r => DataOf(r)["Name"] = "😀😀😀😀😀abcde")).Problems);

        var problem = Assert.Single(Check(Record(r => DataOf(r)["Name"] = "😀😀😀😀😀😀😀😀😀😀😀")).Problems);
        Assert.Equal("maxLength", problem.Rule);
        Assert.Contains("11 characters", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_matching_no_form_names_what_the_first_form_found_and_a_value_matching_the_second_is_fine()
    {
        var problem = Assert.Single(Check(Record(r => DataOf(r)["Context"] = new JsonObject { ["Unknown"] = "x" })).Problems);
        Assert.Equal("anyOf", problem.Rule);
        Assert.Contains("matches none of the 2 forms", problem.Message, StringComparison.Ordinal);
        Assert.Contains("has no FieldID", problem.Message, StringComparison.Ordinal);

        // The second form matches: what it refers to is collected, and nothing the failed trial of the first form saw is.
        var basin = Check(Record(r => DataOf(r)["Context"] = new JsonObject { ["BasinID"] = "dev:reference-data--Basin:North:" }));
        Assert.Empty(basin.Problems);
        Assert.Contains(basin.References, r => r.Id == "dev:reference-data--Basin:North:" && r.At == "data.Context.BasinID");
        Assert.DoesNotContain(basin.References, r => r.EntityType == "master-data--Field");
    }

    [Fact]
    public void Problems_past_the_listing_bound_are_counted_exactly_and_the_listing_keeps_the_first()
    {
        var items = new JsonArray(Enumerable.Range(0, 120).Select(i => (JsonNode?)new JsonObject { ["Value"] = i }).ToArray());
        var found = Check(Record(r => DataOf(r)["Measurements"] = items), limits: new ValidationLimits { MaxProblemsListed = 50 });

        Assert.Equal(120, found.ProblemCount);
        Assert.Equal(50, found.Problems.Count);
        Assert.Equal("data.Measurements[0].TypeID", found.Problems[0].Path);
        Assert.Equal("data.Measurements[49].TypeID", found.Problems[^1].Path);
    }

    [Fact]
    public void A_long_value_is_quoted_clipped()
    {
        var problem = Assert.Single(Check(Record(r => DataOf(r)["Code"] = new string('x', 5000))).Problems);
        Assert.Equal(TemplateValueRules.MaxQuoted + 3, problem.Value.Length);
        Assert.EndsWith("...", problem.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_the_route_fills_when_it_sends_the_record_is_not_judged_as_rendered_nor_required()
    {
        var filled = new HashSet<string>(["data.Datasets", "data.Name"], StringComparer.Ordinal);
        var record = Record(r =>
        {
            DataOf(r)["Datasets"] = new JsonArray("<dataset id the File service returns>");
            DataOf(r).Remove("Name");
        });

        Assert.Equal(2, Check(record).ProblemCount);
        var found = Check(record, filled);
        Assert.Empty(found.Problems);
        Assert.Equal(0, found.UnverifiedCount);
    }

    [Fact]
    public void Each_distinct_id_is_collected_once_with_the_first_place_it_was_found_and_a_bound_cuts_the_rest()
    {
        var record = Record(r => DataOf(r)["Measurements"] = new JsonArray(
            new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:KB:" },
            new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:DF:" },
            new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:KB:" }));

        var all = Check(record);
        Assert.Equal(4, all.References.Count);
        Assert.Equal("data.Measurements[0].TypeID", all.References.Single(r => r.Id.EndsWith(":KB:", StringComparison.Ordinal)).Path);
        Assert.False(all.ReferencesCut);

        var cut = Check(record, limits: new ValidationLimits { MaxReferences = 2 });
        Assert.Equal(2, cut.References.Count);
        Assert.True(cut.ReferencesCut);
    }

    [Fact]
    public void A_value_nested_deeper_than_the_bound_is_counted_as_not_checked_never_as_met()
    {
        var item = new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:KB:", ["Value"] = "not a number" };
        var found = Check(Record(r => DataOf(r)["Measurements"] = new JsonArray(item)), limits: new ValidationLimits { MaxDepth = 3 });

        Assert.Empty(found.Problems);
        Assert.True(found.UnverifiedCount > 0);
        Assert.Contains(found.Unverified, u => u.Rule == "depth" && u.Message.Contains("more than 3 levels", StringComparison.Ordinal));
    }

    [Fact]
    public void Items_past_the_bound_of_a_list_are_counted_as_not_checked()
    {
        var items = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode?)new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:KB:" }).ToArray());
        items.Add(new JsonObject { ["Value"] = 1 });
        var found = Check(Record(r => DataOf(r)["Measurements"] = items), limits: new ValidationLimits { MaxItems = 2 });

        // The item that breaks a rule is past the bound, so it is not reached; the list says so.
        Assert.Empty(found.Problems);
        var cut = Assert.Single(found.Unverified, u => u.Rule == "items");
        Assert.Equal("data.Measurements", cut.At);
        Assert.Contains("6 items", cut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_reaches_its_value_bound_stops_says_so_and_checks_no_more()
    {
        var items = new JsonArray(Enumerable.Range(0, 500).Select(_ => (JsonNode?)new JsonObject { ["Value"] = "not a number" }).ToArray());
        var found = Check(Record(r => DataOf(r)["Measurements"] = items), limits: new ValidationLimits { MaxValues = 300 });

        var stop = Assert.Single(found.Unverified, u => u.Rule == "budget");
        Assert.Contains("300 values", stop.Message, StringComparison.Ordinal);
        Assert.True(found.ProblemCount is > 0 and < 1000, $"{found.ProblemCount} problems were found after the bound");
        Assert.True(found.Values <= 301);
    }

    [Fact]
    public void A_check_that_runs_out_of_time_stops_and_counts_the_rest_as_not_checked()
    {
        var items = new JsonArray(Enumerable.Range(0, 200_000).Select(i => (JsonNode?)new JsonObject { ["TypeID"] = "dev:reference-data--MeasurementType:KB:", ["Value"] = i }).ToArray());
        var clock = Stopwatch.StartNew();
        var found = Check(Record(r => DataOf(r)["Measurements"] = items), limits: new ValidationLimits { Budget = TimeSpan.FromMilliseconds(1), MaxValues = long.MaxValue });

        Assert.Contains(found.Unverified, u => u.Rule == "budget" && u.Message.Contains("second", StringComparison.Ordinal));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the check ran {clock.Elapsed} past a 1 ms budget");
    }

    [Fact]
    public void A_pattern_neither_dialect_reads_is_counted_as_not_checked_at_every_value_it_applies_to()
    {
        var schema = SchemaOf("""{ "type": "object", "properties": { "Code": { "type": "string", "pattern": "(?<open" } } }""");
        var rules = SchemaRules.Of(schema);
        var found = RecordValidator.Check(new JsonObject { ["Code"] = "anything" }, rules);

        Assert.Empty(found.Problems);
        var unread = Assert.Single(found.Unverified);
        Assert.Equal("pattern", unread.Rule);
        Assert.Contains("read by neither ECMAScript nor .NET", unread.Message, StringComparison.Ordinal);
        Assert.Contains(rules.Notes, n => n.Contains("(?<open", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pattern_that_runs_out_of_time_on_a_value_is_not_checked_rather_than_broken()
    {
        // Nested quantifiers over a long run that fails at its end: the classic catastrophic backtrack.
        var schema = SchemaOf("""{ "type": "object", "properties": { "Words": { "type": "string", "pattern": "^(\\w+\\s?)*$" } } }""");
        var value = string.Concat(Enumerable.Repeat("word ", 30)) + "!";
        var clock = Stopwatch.StartNew();
        var found = RecordValidator.Check(new JsonObject { ["Words"] = value }, SchemaRules.Of(schema));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"a pattern ran {clock.Elapsed}");
        Assert.Empty(found.Problems);
        var timedOut = Assert.Single(found.Unverified);
        Assert.Equal("pattern", timedOut.Rule);
        Assert.Contains("within the time a check allows", timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyword_the_checks_do_not_apply_is_counted_as_not_checked_where_a_value_meets_it()
    {
        var schema = SchemaOf("""{ "type": "object", "properties": { "Code": { "type": "string", "not": { "const": "x" } }, "Other": { "type": "string" } } }""");
        var found = RecordValidator.Check(new JsonObject { ["Code"] = "x", ["Other"] = "y" }, SchemaRules.Of(schema));

        var skipped = Assert.Single(found.Unverified);
        Assert.Equal("not", skipped.Rule);
        Assert.Equal("Code", skipped.At);
    }

    [Fact]
    public void A_reference_the_bundle_does_not_hold_leaves_its_part_not_checked_and_the_rest_checked()
    {
        var schema = SchemaOf("""
            {
              "type": "object",
              "properties": {
                "Lost": { "$ref": "#/definitions/Missing.1.0.0" },
                "Remote": { "$ref": "osdu:wks:AbstractCommonResources:1.0.0" },
                "Kept": { "type": "integer" }
              }
            }
            """);
        var found = RecordValidator.Check(new JsonObject { ["Lost"] = "x", ["Remote"] = 1, ["Kept"] = "not a number" }, SchemaRules.Of(schema));

        Assert.Equal("Kept", Assert.Single(found.Problems).At);
        Assert.Equal(["Lost", "Remote"], found.Unverified.Select(u => u.At).Order(StringComparer.Ordinal));
        Assert.All(found.Unverified, u => Assert.Equal("schema", u.Rule));
    }

    [Fact]
    public void A_list_whose_items_are_given_by_position_is_counted_as_not_checked()
    {
        var schema = SchemaOf("""{ "type": "object", "properties": { "Pair": { "type": "array", "items": [ { "type": "string" }, { "type": "number" } ] } } }""");
        var found = RecordValidator.Check(new JsonObject { ["Pair"] = new JsonArray("a", 1) }, SchemaRules.Of(schema));

        Assert.Equal("items", Assert.Single(found.Unverified).Rule);
    }

    [Fact]
    public void A_value_the_schema_names_no_type_for_takes_null_and_one_naming_null_takes_it_too()
    {
        var schema = SchemaOf("""{ "type": "object", "properties": { "Any": { }, "Maybe": { "type": ["integer", "null"] }, "Must": { "type": "integer" } } }""");
        var rules = SchemaRules.Of(schema);

        Assert.Empty(RecordValidator.Check(new JsonObject { ["Any"] = null, ["Maybe"] = null }, rules).Problems);
        var problem = Assert.Single(RecordValidator.Check(new JsonObject { ["Must"] = null }, rules).Problems);
        Assert.Equal("null where the schema takes an integer", problem.Message);
    }

    [Fact]
    public void A_stored_first_version_reads_with_null_modify_stamps_which_are_absent_not_wrong()
    {
        var rules = SchemaRules.Of(SchemaOf("""{ "type": "object", "properties": { "modifyUser": { "type": "string" }, "modifyTime": { "type": "string" } } }"""));
        var record = new JsonObject { ["modifyUser"] = null, ["modifyTime"] = null };

        Assert.Empty(RecordValidator.Check(record, rules, form: RecordForm.Stored).Problems);
        Assert.Equal(["modifyUser", "modifyTime"], RecordValidator.Check(record, rules).Problems.Select(p => p.Path));
    }

    [Fact]
    public void Checking_the_same_record_twice_finds_the_same_and_checks_from_many_threads_at_once_agree()
    {
        var broken = Record(r =>
        {
            DataOf(r)["Name"] = "AlphaBetaGamma";
            DataOf(r)["Status"] = "Planned";
        });
        var expected = Check(broken);

        var results = new RecordFindings[64];
        Parallel.For(0, results.Length, i => results[i] = Check(broken));

        Assert.All(results, r =>
        {
            Assert.Equal(expected.ProblemCount, r.ProblemCount);
            Assert.Equal(expected.Problems.Select(p => (p.Path, p.Rule)), r.Problems.Select(p => (p.Path, p.Rule)));
            Assert.Equal(expected.Rules, r.Rules);
        });
    }

    /// <summary>Applies one change: <c>path=json</c> sets the value, <c>-path</c> removes the property.</summary>
    internal static void Apply(JsonObject record, string change)
    {
        var remove = change.StartsWith('-');
        var (path, json) = remove ? (change[1..], null) : (change[..change.IndexOf('=', StringComparison.Ordinal)], change[(change.IndexOf('=', StringComparison.Ordinal) + 1)..]);
        var segments = path.Split('.');
        var holder = record;
        foreach (var segment in segments[..^1])
        {
            if (holder[segment] is not JsonObject next)
            {
                next = new JsonObject();
                holder[segment] = next;
            }

            holder = next;
        }

        if (remove)
        {
            holder.Remove(segments[^1]);
        }
        else
        {
            holder[segments[^1]] = JsonNode.Parse(json!);
        }
    }
}
