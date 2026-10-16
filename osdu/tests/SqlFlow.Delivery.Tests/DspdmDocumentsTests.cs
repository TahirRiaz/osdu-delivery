using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Dspdm;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the dspdm route reads before it sends anything (osdu/specs/production-dspdm/INTEGRATION.md): DSPDM's data types and
/// the form its values are compared in, the flow's <c>target.dspdm</c> block and the dspdm interface route, the mappings of
/// business object rows and the attribute list their renders carry, the route checks, the removal endpoints, and how an
/// error DSPDM answers is described.
/// </summary>
public sealed class DspdmDocumentsTests
{
    private const string Kind = "acme:dspdm:well:1.0.0";

    private readonly DeliveryDocumentLoader _loader = new();

    [Theory]
    [InlineData("character varying(20)", "Text", 20, null, null, false)]
    [InlineData("nvarchar(max)", "Text", null, null, null, false)]
    [InlineData("text", "Text", null, null, null, false)]
    [InlineData("numeric(10,2)", "Number", null, 10, 2, false)]
    [InlineData("decimal(12)", "Number", null, 12, 0, false)]
    [InlineData("numeric", "Number", null, null, null, false)]
    [InlineData("double precision", "Number", null, null, null, false)]
    [InlineData("integer", "Number", null, null, null, true)]
    [InlineData("tinyint", "Number", null, null, null, true)]
    [InlineData("timestamp without time zone", "Timestamp", null, null, null, false)]
    [InlineData("timestamp(6) with time zone", "Timestamp", null, null, null, false)]
    [InlineData("datetime2", "Timestamp", null, null, null, false)]
    [InlineData("datetimeoffset", "Timestamp", null, null, null, false)]
    [InlineData("date", "Date", null, null, null, false)]
    [InlineData("time without time zone", "Time", null, null, null, false)]
    [InlineData("bit", "Boolean", null, null, null, false)]
    [InlineData("jsonb", "Json", null, null, null, false)]
    [InlineData("bytea", "Binary", null, null, null, false)]
    [InlineData("geometry", "Other", null, null, null, false)]
    public void Data_types_are_read_as_DSPDM_reads_them(string dataType, string kind, int? length, int? precision, int? scale, bool whole)
    {
        var type = DspdmValues.TypeOf(dataType);
        Assert.Equal(kind, type.Kind.ToString());
        Assert.Equal(length, type.Length);
        Assert.Equal(precision, type.Precision);
        Assert.Equal(scale, type.Scale);
        Assert.Equal(whole, type.Whole);
    }

    [Theory]
    [InlineData("character varying(20)", "\" A-1 \"", "+00:00", "A-1")]
    [InlineData("numeric(10,2)", "12.345", "+00:00", "12.35")]
    [InlineData("numeric(10,2)", "12.344", "+00:00", "12.34")]
    [InlineData("numeric(10,2)", "\"12.50\"", "+00:00", "12.5")]
    [InlineData("numeric(10,0)", "-2.5", "+00:00", "-3")]
    [InlineData("double precision", "1.2500", "+00:00", "1.25")]
    [InlineData("integer", "7", "+00:00", "7")]
    [InlineData("integer", "7.5", "+00:00", null)]
    [InlineData("boolean", "\"Y\"", "+00:00", "true")]
    [InlineData("boolean", "\"0\"", "+00:00", "false")]
    [InlineData("bit", "true", "+00:00", "true")]
    [InlineData("boolean", "\"maybe\"", "+00:00", null)]
    [InlineData("timestamp without time zone", "\"2024-05-01T10:00:00Z\"", "+02:00", "2024-05-01T12:00:00.000")]
    [InlineData("timestamp without time zone", "\"2024-05-01T10:00:00.1234567-03:30\"", "+00:00", "2024-05-01T13:30:00.123")]
    [InlineData("timestamp without time zone", "\"2024-05-01T10:00:00+17:30\"", "+00:00", "2024-04-30T16:30:00.000")]
    [InlineData("timestamp without time zone", "\"2024-05-01 10:00:00+05:00\"", "+02:00", "2024-05-01T10:00:00.000")]
    [InlineData("timestamp without time zone", "\"2024-05-01T10:00:00\"", "+02:00", "2024-05-01T10:00:00.000")]
    [InlineData("timestamp without time zone", "\"2024/05/01\"", "+02:00", "2024-05-01T00:00:00.000")]
    [InlineData("timestamp without time zone", "\"2024-05-01 02:30 PM\"", "+00:00", "2024-05-01T14:30:00.000")]
    [InlineData("timestamp without time zone", "\"2024-02-30\"", "+00:00", null)]
    [InlineData("timestamp without time zone", "\"yesterday\"", "+00:00", null)]
    [InlineData("date", "\"2024-05-01T23:30:00Z\"", "+02:00", "2024-05-02")]
    [InlineData("date", "\"2024-05-01\"", "-05:00", "2024-05-01")]
    [InlineData("time", "\"10:15:30+05:00\"", "+02:00", "10:15:30.000")]
    [InlineData("time", "\"2024-05-01T10:15:30Z\"", "+02:00", "12:15:30.000")]
    [InlineData("time", "\"12:05 AM\"", "+00:00", "00:05:00.000")]
    [InlineData("jsonb", "{\"b\":1,\"a\":2}", "+00:00", "{\"a\":2,\"b\":1}")]
    [InlineData("character varying(20)", "null", "+00:00", null)]
    public void Values_are_compared_in_the_form_DSPDM_keeps_them(string dataType, string json, string zone, string? token)
    {
        var offset = TimeSpan.Parse(zone.TrimStart('+'), CultureInfo.InvariantCulture);
        Assert.Equal(token, DspdmValues.Token(DspdmValues.TypeOf(dataType), JsonNode.Parse(json), offset));
    }

    [Theory]
    [InlineData("character varying(5)", "\"abcdef\"", "takes at most 5 characters")]
    [InlineData("character varying(5)", "\" abc  \"", null)]
    [InlineData("integer", "3.5", "takes whole numbers from -2147483648 to 2147483647")]
    [InlineData("smallint", "40000", "takes whole numbers from -32768 to 32767")]
    [InlineData("numeric(4,2)", "123.4", "takes at most 2 digits before the decimal point")]
    [InlineData("numeric(4,2)", "0.125", null)]
    [InlineData("numeric(10,2)", "\"deep\"", "takes a number")]
    [InlineData("numeric(10,2)", "true", "takes a number")]
    [InlineData("boolean", "\"maybe\"", "takes true or false (or Y, N, 1, 0 as text)")]
    [InlineData("timestamp without time zone", "\"yesterday\"", "takes a date in one of the forms DSPDM reads, such as 2024-05-01T10:30:00Z")]
    [InlineData("timestamp without time zone", "1714557600", "takes a date or time written as text")]
    [InlineData("time", "\"25:00:00\"", "takes a time in one of the forms DSPDM reads, such as 10:30:00")]
    [InlineData("character varying(50)", "{\"a\":1}", "takes a single value, not a JSON object")]
    [InlineData("integer", "[1]", "takes a single value, not a JSON array")]
    [InlineData("jsonb", "{\"a\":1}", null)]
    [InlineData("integer", "null", null)]
    public void Values_an_attribute_does_not_take_are_refused_with_the_reason(string dataType, string json, string? refusal)
        => Assert.Equal(refusal, DspdmValues.Refusal(DspdmValues.TypeOf(dataType), JsonNode.Parse(json), TimeSpan.Zero));

    [Fact]
    public void A_rows_version_is_its_change_time_as_written_read_as_UTC()
    {
        var changed = new JsonObject { ["ROW_CREATED_DATE"] = "2026-03-01T08:00:00.000+02:00", ["ROW_CHANGED_DATE"] = "2026-03-01T09:30:15.250+02:00" };
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 30, 15, 250, TimeSpan.Zero).ToUnixTimeMilliseconds(), DspdmValues.VersionOf(changed));

        var created = new JsonObject { ["ROW_CREATED_DATE"] = "2026-03-01T08:00:00.000Z" };
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), DspdmValues.VersionOf(created));

        Assert.Null(DspdmValues.VersionOf(new JsonObject { ["UWI"] = "A-1" }));
        Assert.Equal("2026-03-01T09:30:15.250Z", DspdmValues.Moment(DspdmValues.VersionOf(changed)!.Value));
        Assert.Equal(1234L, DspdmValues.Key(JsonNode.Parse("1234")));
        Assert.Equal(1234L, DspdmValues.Key(JsonNode.Parse("\" 1234 \"")));
        Assert.Null(DspdmValues.Key(JsonNode.Parse("12.5")));
    }

    [Theory]
    [InlineData("GMT+00:00", true)]
    [InlineData("GMT+14:00", true)]
    [InlineData("GMT-12:00", true)]
    [InlineData("GMT+05:45", true)]
    [InlineData("GMT+14:30", false)]
    [InlineData("GMT-12:30", false)]
    [InlineData("GMT+5:00", false)]
    [InlineData("UTC", false)]
    [InlineData("GMT+02:60", false)]
    public void Time_zones_are_those_DSPDMs_save_takes(string timezone, bool valid)
        => Assert.Equal(valid, DspdmKinds.IsTimezone(timezone));

    [Fact]
    public void A_flow_names_DSPDM_its_time_zone_its_business_objects_and_what_happens_to_rows_it_did_not_write()
    {
        var flow = _loader.ParseFlow(Single("""
              dspdm:
                root: /api/dspdm/v1/
                timezone: GMT+02:00
                existingRows: update
                businessObjects:
                  well: { name: Well Header, key: [uwi] }
                  well_test: {}
            """), "wells.yaml");

        Assert.Equal(DeliveryProtocol.Dspdm, flow.Target.Protocol);
        var dspdm = flow.Target.Dspdm;
        Assert.Equal("/api/dspdm/v1", dspdm.Root);
        Assert.Equal("GMT+02:00", dspdm.Timezone);
        Assert.Equal(DspdmExistingRows.Update, dspdm.ExistingRows);
        Assert.Equal("WELL HEADER", dspdm.For("well").Name);
        Assert.Equal(["UWI"], dspdm.For("well").Key);
        Assert.Null(dspdm.For("well_test").Name);
        Assert.Empty(dspdm.For("well_test").Key);
        Assert.Empty(dspdm.For("wellbore").Key);
        Assert.Equal("WELL TEST", DspdmKinds.DefaultName("well_test"));

        var defaults = _loader.ParseFlow(Single(string.Empty), "wells.yaml");
        Assert.Null(defaults.Target.Dspdm.Root);
        Assert.Equal(DspdmTarget.DefaultTimeZone, defaults.Target.Dspdm.Timezone);
        Assert.Equal(DspdmExistingRows.Hold, defaults.Target.Dspdm.ExistingRows);
    }

    [Theory]
    [InlineData("  dspdm: { timezone: UTC }", "target.dspdm.timezone 'UTC' must be GMT+hh:mm or GMT-hh:mm")]
    [InlineData("  dspdm: { timezone: GMT+15:00 }", "target.dspdm.timezone 'GMT+15:00' must be GMT+hh:mm or GMT-hh:mm")]
    [InlineData("  dspdm: { root: api/dspdm }", "target.dspdm.root 'api/dspdm' must be a path under the endpoint starting with '/', such as /api/dspdm/v1.")]
    [InlineData("  dspdm: { existingRows: merge }", "target.dspdm.existingRows")]
    [InlineData("  dspdm:\n    businessObjects:\n      'well test': {}", "target.dspdm.businessObjects names 'well test', which is not an entity type")]
    [InlineData("  dspdm:\n    businessObjects:\n      well: { name: 1WELL }", "target.dspdm.businessObjects.well.name '1WELL' is not a name DSPDM gives a business object")]
    [InlineData("  dspdm:\n    businessObjects:\n      well: { name: WELL }\n      wells: { name: well }", "target.dspdm.businessObjects.wells and target.dspdm.businessObjects.well are both rows of the business object 'WELL'.")]
    [InlineData("  dspdm:\n    businessObjects:\n      well: { key: [] }", "target.dspdm.businessObjects.well.key lists no attribute.")]
    [InlineData("  dspdm:\n    businessObjects:\n      well: { key: [UWI, uwi] }", "target.dspdm.businessObjects.well.key names UWI more than once")]
    public void A_dspdm_block_that_cannot_be_used_is_refused_with_the_reason(string dspdm, string message)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(dspdm), "wells.yaml"));
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_dspdm_route_reads_a_dspdm_block()
    {
        var single = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single("  dspdm: { timezone: GMT+01:00 }", protocol: "storage"), "wells.yaml"));
        Assert.Contains("target.dspdm declares the Production DDMS core service the dspdm route writes rows to, and this flow's route is storage", single.Message, StringComparison.Ordinal);

        var unused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source("  dspdm: { timezone: GMT+01:00 }", "route: storage"), "estate.yaml"));
        Assert.Contains("no interface is delivered by it (route: dspdm)", unused.Message, StringComparison.Ordinal);

        var files = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source(string.Empty, "route: dspdm\n    files: { root: docs }"), "estate.yaml"));
        Assert.Contains("route is dspdm, which saves business object rows alone", files.Message, StringComparison.Ordinal);

        var source = _loader.ParseSource(Source("  dspdm: { timezone: GMT+01:00 }", "route: dspdm"), "estate.yaml");
        var wells = source.Interfaces.Single(i => i.Interface == "wells");
        var logs = source.Interfaces.Single(i => i.Interface == "logs");
        Assert.Equal(DeliveryProtocol.Dspdm, wells.Target.Protocol);
        Assert.Equal("GMT+01:00", wells.Target.Dspdm.Timezone);
        Assert.Equal(DeliveryProtocol.Storage, logs.Target.Protocol);
        Assert.Equal(DspdmTarget.DefaultTimeZone, logs.Target.Dspdm.Timezone);
    }

    [Fact]
    public void A_mapping_of_business_object_rows_has_no_envelope_and_fills_attributes_only()
    {
        var mapping = _loader.ParseMapping(WellMapping(), "well.yaml");
        Assert.Equal(Kind, mapping.Kind);
        Assert.Empty(mapping.Envelope.Owners);
        Assert.Empty(mapping.Envelope.LegalTags);

        var envelope = Assert.Throws<FlowValidationException>(() => _loader.ParseMapping(WellMapping("acl: { owners: [owners@x] }"), "well.yaml"));
        Assert.Contains("fills osdu.acl.owners, and acme:dspdm:well:1.0.0 is a row of a DSPDM business object, which has no access or legal block", envelope.Message, StringComparison.Ordinal);

        var outside = Assert.Throws<FlowValidationException>(() => _loader.ParseMapping(WellMapping("tags: { Source: acme }"), "well.yaml"));
        Assert.Contains("fills osdu.tags.Source, and acme:dspdm:well:1.0.0 is a row of a DSPDM business object, whose attributes are the properties of record.data", outside.Message, StringComparison.Ordinal);

        // An OSDU record still needs its envelope.
        var withoutEnvelope = TestSchema.MappingDocument().Replace(TestSchema.Indented(TestSchema.Envelope, 2), string.Empty, StringComparison.Ordinal);
        var record = Assert.Throws<FlowValidationException>(() => _loader.ParseMapping(withoutEnvelope, "thing.yaml"));
        Assert.Contains("every OSDU record carries owners", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rendered_row_lists_the_attributes_its_mapping_fills_even_when_they_render_empty()
    {
        var schema = WellSchema();
        var mapping = _loader.ParseMapping(WellMapping(), "well.yaml");
        var context = new RenderContext
        {
            MappingReference = "Well@1.0.0",
            CacheScope = "acme",
            CacheVersion = ReferenceSnapshot.Empty.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
        };
        var renderer = new MappingRenderer(mapping, schema, ReferenceSnapshot.Empty, context);
        var result = renderer.Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["uwi"] = " A-1 ", ["name"] = "Alpha 1", ["op"] = "Acme", ["remark"] = null }),
        });

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:well:" + result.Key!.Value.Value.ToString("N"), result.TargetId);
        Assert.Equal(["OPERATOR", "REMARK", "UWI", "WELL_NAME"], result.Document[DspdmKinds.OwnedProperty]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Null(result.Document["data"]!["REMARK"]);
        Assert.Null(result.Document["acl"]);

        var shape = MappingRenderer.Shape(mapping, schema, context.Parameters);
        Assert.Equal(["OPERATOR", "REMARK", "UWI", "WELL_NAME"], shape.Document[DspdmKinds.OwnedProperty]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("data", shape.Document.Last().Key);

        // A record's render lists nothing of the kind.
        var thing = new MappingRenderer(TestSchema.Mapping(), TestSchema.Build(), TestSchema.References(), TestSchema.Context());
        Assert.Null(thing.Render(new SourceRecord { Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "n", ["depth"] = "1" }) }).Document[DspdmKinds.OwnedProperty]);
    }

    [Fact]
    public void Business_object_rows_go_by_the_dspdm_route_alone()
    {
        Assert.Equal("dspdm", DeliveryProtocols.Name(DeliveryProtocol.Dspdm));
        var dspdm = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Dspdm });
        var storage = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Storage });

        RouteChecks.Check(dspdm, Kind);
        RouteChecks.Check(storage, "osdu:wks:master-data--Wellbore:1.1.0");

        var row = Assert.Throws<DeliveryException>(() => RouteChecks.Check(storage, Kind));
        Assert.Contains("a row of a DSPDM business object (its template's source is 'dspdm'), which only the dspdm route writes", row.Message, StringComparison.Ordinal);

        var record = Assert.Throws<DeliveryException>(() => RouteChecks.Check(dspdm, "osdu:wks:master-data--Wellbore:1.1.0"));
        Assert.Contains("an OSDU record, and the dspdm route writes rows of DSPDM business objects", record.Message, StringComparison.Ordinal);

        Assert.True(DspdmKinds.Is(Kind));
        Assert.False(DspdmKinds.Is("osdu:wks:master-data--Wellbore:1.1.0"));
        Assert.Equal("well", DspdmKinds.EntityOf(Kind));
        Assert.False(DeliveryProtocols.CarriesPayload(DeliveryProtocol.Dspdm));
        Assert.False(DeliveryProtocols.ReachesDdms(DeliveryProtocol.Dspdm));
    }

    [Fact]
    public void A_dspdm_flow_shows_a_permanent_delete_and_refuses_the_other_scopes()
    {
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = FakeOsduPlatform.Endpoint,
            Protocol = DeliveryProtocol.Dspdm,
            Dspdm = new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot },
        });
        var endpoints = RemovalEndpoints.Of(flow, Kind);
        Assert.StartsWith("(refused: DSPDM keeps no deleted rows", endpoints.Record, StringComparison.Ordinal);
        Assert.StartsWith("(refused: DSPDM keeps no earlier versions of a row", endpoints.History, StringComparison.Ordinal);
        Assert.Equal("/api/dspdm/v1/delete/{businessObject}/{row id}", endpoints.Everything);
        Assert.Equal("DELETE", endpoints.RecordMethod);
    }

    [Fact]
    public void An_error_DSPDM_answers_is_described_by_its_messages_and_never_its_stack_trace()
    {
        var described = OsduError.Describe("""
            {
              "status": { "statusCode": -2, "severity": "ERROR", "statusLabel": "ERROR" },
              "messages": [ { "message": "These record(s) cannot be inserted or updated due to unique constraint violation.", "status": { "statusCode": -2 } } ],
              "exception": { "message": "Value 'A-1' for attribute 'UWI' already exists", "stackTrace": "com.lgc.dspdm.core.common.exception.DSPDMException\n\tat com.lgc.Secret.run(Secret.java:1)" },
              "data": {}
            }
            """);
        Assert.Equal("These record(s) cannot be inserted or updated due to unique constraint violation.; Value 'A-1' for attribute 'UWI' already exists", described);
        Assert.DoesNotContain("com.lgc", described, StringComparison.Ordinal);

        Assert.Equal("DSPDM answered status FATAL without a message", OsduError.Describe("""{ "status": { "statusCode": -3, "statusLabel": "FATAL" }, "messages": [], "exception": { "stackTrace": "at x" } }"""));

        // A problem document's numeric status is not DSPDM's.
        Assert.Equal("Unsupported Media Type: Content-Type 'null' is not supported", OsduError.Describe("""{ "title": "Unsupported Media Type", "status": 415, "detail": "Content-Type 'null' is not supported" }"""));
    }

    /// <summary>A flow in the single form on the dspdm route; <paramref name="dspdm"/> is YAML indented two spaces, under target.</summary>
    private static string Single(string dspdm, string protocol = "dspdm") => ($$"""
        flowType: delivery
        name: wells
        source:
          connection: ${env:WELLS_DB}
          record: { object: Wells.ing.Well, key: [uwi] }
          work: work
        render:
          mapping: Well@1.0.0
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
          protocol: {{protocol}}
        {{dspdm}}
        """).ReplaceLineEndings("\n");

    /// <summary>A source with a wells interface routed by <paramref name="wells"/> and a storage interface.</summary>
    private static string Source(string dspdm, string wells) => ($$"""
        flowType: delivery
        name: estate
        source:
          connection: ${env:ESTATE_DB}
          work: work
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
        {{dspdm}}
        interfaces:
          wells:
            record: { object: Estate.ing.Well, key: [uwi] }
            {{wells}}
            mapping: Well@1.0.0
          logs:
            record: { object: Estate.ing.WellLog, key: [log_id] }
            mapping: WellLog@1.4.0
        """).ReplaceLineEndings("\n");

    internal static SchemaSnapshot WellSchema() => SchemaSnapshot.Parse(Kind, """
        {
          "$id": "https://example.org/dspdm/well.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "data": {
              "type": "object",
              "properties": {
                "UWI": { "type": "string" },
                "WELL_NAME": { "type": "string" },
                "OPERATOR": { "type": "string" },
                "REMARK": { "type": "string" },
                "DEPTH": { "type": "number" },
                "SPUD_DATE": { "type": "string", "format": "date-time" },
                "IS_ACTIVE": { "type": "boolean" }
              },
              "required": ["UWI"]
            }
          },
          "required": ["kind"]
        }
        """, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>The well's mapping, with <paramref name="record"/> laid beside its data at the record's own level.</summary>
    private static string WellMapping(string record = "") => $$"""
        documentType: mapping
        name: Well
        version: 1.0.0
        template:
          kind: {{Kind}}
          version: {{WellSchema().Version}}
        dataset:
          system: acme
          key: [uwi]
        parameters:
          dataPartition: { required: true }
        record:
          data:
            UWI: { $from: uwi, $modifiers: [trim] }
            WELL_NAME: { $from: name }
            OPERATOR: { $from: op }
            REMARK: { $from: remark, $required: false }

        """.ReplaceLineEndings("\n") + TestSchema.Indented(record, 2);
}
