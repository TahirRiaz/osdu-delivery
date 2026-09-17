using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dspdm route (osdu/specs/production-dspdm/INTEGRATION.md) against a fake of the Production DDMS core service built
/// from the brief and DSPDM's code: the business object read from DSPDM's metadata, rows found by their unique key before
/// they are saved, inserted and updated in one save, a save whose answer was lost found again instead of inserted twice,
/// rows another system wrote left alone unless the flow takes them over, a refused save sent again one row at a time,
/// versions from the rows' change dates, and a delete that is for good.
/// </summary>
public sealed class DspdmRouteTests
{
    private const string Kind = "acme:dspdm:well:1.0.0";
    private const string DailyKind = "acme:dspdm:well_vol_daily:1.0.0";
    private const string Common = "POST " + FakeOsduPlatform.DspdmRoot + "/common";
    private const string Save = "POST " + FakeOsduPlatform.DspdmRoot + "/save";

    private static readonly string[] Owned = ["DEPTH", "IS_ACTIVE", "OPERATOR", "REMARK", "SPUD_DATE", "UWI", "WELL_NAME"];

    private static readonly long Start = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, DspdmTarget? target = null, ProtocolOptions? options = null, bool partition = true)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (partition)
            {
                headers["data-partition-id"] = "opendes";
            }

            var client = new OsduHttpClient(Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, headers);
            Target = target ?? new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot };
            Protocol = new OsduDspdmProtocol(client, options ?? new ProtocolOptions(), Target, NullLogger<OsduDspdmProtocol>.Instance);
        }

        public HttpRuntime Runtime { get; }

        public DspdmTarget Target { get; }

        public OsduDspdmProtocol Protocol { get; }

        public IDeliveryProtocol Delivery => Protocol;

        public void Dispose() => Runtime.Dispose();
    }

    private static string TargetId(string key, string entity = "well") => $"opendes:{entity}:{DeliveryKey.Derive("acme", [key]).Value:N}";

    /// <summary>A rendered WELL row: its attributes, and the attributes its mapping fills.</summary>
    private static JsonObject Well(string uwi, string name = "Alpha 1", string op = "Acme", JsonObject? more = null, string? key = null, string[]? owned = null)
    {
        var data = new JsonObject { ["UWI"] = uwi, ["WELL_NAME"] = name, ["OPERATOR"] = op };
        foreach (var (attribute, value) in more ?? [])
        {
            data[attribute] = value?.DeepClone();
        }

        return new JsonObject
        {
            ["id"] = TargetId(key ?? uwi),
            ["kind"] = Kind,
            [DspdmKinds.OwnedProperty] = new JsonArray((owned ?? Owned).Select(o => (JsonNode?)o).ToArray()),
            ["data"] = data,
        };
    }

    private sealed class Reports
    {
        public Dictionary<string, IReadOnlyDictionary<string, string>> Steps { get; } = new(StringComparer.Ordinal);

        public Task Add(string step, IReadOnlyDictionary<string, string> values)
        {
            lock (Steps)
            {
                Steps[step] = values;
            }

            return Task.CompletedTask;
        }
    }

    private static DeliveryWork Work(
        JsonObject document, IReadOnlyDictionary<string, string>? state = null, long? existing = null,
        Reports? reports = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null, bool metadata = true) => new()
        {
            Key = DeliveryKey.Derive("acme", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            SourceKey = document["data"]!["UWI"]?.ToJsonString(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = false,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            StepCompleted = reports is null ? null : (step, values, _) => reports.Add(step, values),
        };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

    private static JsonObject Body(FakeOsduPlatform platform, int index) => JsonNode.Parse(platform.Calls[index].Body!)!.AsObject();

    private static List<JsonObject> SavedRows(FakeOsduPlatform platform)
        => platform.Calls.Where(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/save", StringComparison.Ordinal))
            .SelectMany(c => JsonNode.Parse(c.Body!)!.AsObject().Single().Value!["data"]!.AsArray().Select(r => r!.AsObject()))
            .ToList();

    private static void Conform(FakeOsduPlatform platform)
        => OsduContracts.AssertConform(platform.Calls, null, FilterValueTyping, OsduContracts.ProductionDspdm);

    /// <summary>
    /// The difference the brief records between DSPDM's contract and its code (section 8): the contract types each value of a
    /// query condition as an object, and the service reads any JSON value (<c>CriteriaFilter.values</c> is <c>Object[]</c>)
    /// and converts it to the attribute's type.
    /// </summary>
    private static bool FilterValueTyping(string violation)
        => violation.Contains("POST /common body.criteriaFilters[", StringComparison.Ordinal)
           && violation.Contains("].values[", StringComparison.Ordinal)
           && violation.EndsWith("is not of type object", StringComparison.Ordinal);

    private static Dictionary<string, string> State(DeliveryOutcome outcome) => new(outcome.Returned, StringComparer.Ordinal);

    [Fact]
    public async Task New_rows_are_found_then_inserted_in_one_save_and_their_keys_kept()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var reports = new Reports();

        var outcomes = await rig.Delivery.DeliverBatchAsync(
        [
            Work(Well(" A-1 ", more: new JsonObject { ["DEPTH"] = 1234.567, ["SPUD_DATE"] = "2024-05-01T10:00:00Z", ["IS_ACTIVE"] = "Y" }), reports: reports),
            Work(Well("B-2", name: "Bravo 2")),
        ]);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(
            [
                Common, Common, Common,
                Common,
                Save,
            ],
            Sent(platform));
        Assert.Equal("BUSINESS OBJECT", Body(platform, 0)["boName"]!.GetValue<string>());
        var find = Body(platform, 3);
        Assert.Equal("WELL", find["boName"]!.GetValue<string>());
        Assert.Equal("IN", find["criteriaFilters"]![0]!["operator"]!.GetValue<string>());
        Assert.Equal(["A-1", "B-2"], find["criteriaFilters"]![0]!["values"]!.AsArray().Select(v => v!.GetValue<string>()));
        var save = Body(platform, 4)["WELL"]!;
        Assert.Equal("en", save["language"]!.GetValue<string>());
        Assert.Equal("GMT+00:00", save["timezone"]!.GetValue<string>());
        Assert.True(save["readBack"]!.GetValue<bool>());
        Assert.All(save["data"]!.AsArray(), row => Assert.Null(row!["WELL_ID"]));
        Assert.DoesNotContain(save["data"]!.AsArray(), row => row!.AsObject().ContainsKey("REMARK"));

        var rows = platform.DspdmRows("WELL");
        Assert.Equal([1001L, 1002L], rows.Keys);
        Assert.Equal("A-1", rows[1001]["UWI"]!.GetValue<string>());
        Assert.Equal(1234.57m, rows[1001]["DEPTH"]!.GetValue<decimal>());
        Assert.Equal("2024-05-01T10:00:00.000", rows[1001]["SPUD_DATE"]!.GetValue<string>());
        Assert.True(rows[1001]["IS_ACTIVE"]!.GetValue<bool>());

        var first = outcomes[0];
        Assert.Equal(Start, first.TargetVersion);
        Assert.Equal("WELL", first.Returned[OsduDspdmProtocol.BusinessObjectValue]);
        Assert.Equal("1001", first.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Equal(OsduDspdmProtocol.Inserted, first.Returned[OsduDspdmProtocol.OperationValue]);
        Assert.Equal("inserted as row 1001 of WELL", first.Detail);
        Assert.Equal([OsduDspdmProtocol.FindStep, OsduDspdmProtocol.SaveBeginStep, OsduDspdmProtocol.SaveStep], first.Steps.Select(s => s.Name));
        Assert.Equal("none", first.Steps[0].Returned["found"]);
        Assert.Equal("insert", reports.Steps[OsduDspdmProtocol.SaveBeginStep][OsduDspdmProtocol.OperationValue]);
        Assert.Equal("1001", reports.Steps[OsduDspdmProtocol.SaveStep][OsduDspdmProtocol.IdValue]);
        Assert.Equal("1002", outcomes[1].Returned[OsduDspdmProtocol.IdValue]);
        Conform(platform);
    }

    [Fact]
    public async Task A_changed_row_is_updated_by_its_key_and_an_attribute_the_source_cleared_is_cleared()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1", more: new JsonObject { ["REMARK"] = "old", ["DEPTH"] = 10 })));
        platform.DspdmAdvance(TimeSpan.FromMinutes(5));

        var from = platform.Calls.Count;
        var second = await rig.Delivery.DeliverAsync(Work(Well("A-1", name: "Alpha One", more: new JsonObject { ["DEPTH"] = 10 }), State(first), first.TargetVersion));

        Assert.Equal([Common, Save], Sent(platform, from));
        var sent = SavedRows(platform).Last();
        Assert.Equal(1001L, sent["WELL_ID"]!.GetValue<long>());
        Assert.True(sent.ContainsKey("REMARK"));
        Assert.Null(sent["REMARK"]);
        Assert.False(sent.ContainsKey("SPUD_DATE") && sent["SPUD_DATE"] is not null);

        var row = platform.DspdmRows("WELL")[1001];
        Assert.Equal("Alpha One", row["WELL_NAME"]!.GetValue<string>());
        Assert.Null(row["REMARK"]);
        Assert.Single(platform.DspdmRows("WELL"));
        Assert.Equal(OsduDspdmProtocol.Updated, second.Returned[OsduDspdmProtocol.OperationValue]);
        Assert.Equal(Start + (long)TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)).TotalMilliseconds, second.TargetVersion);
        Assert.Equal("updated row 1001 of WELL", second.Detail);
        Conform(platform);
    }

    [Fact]
    public async Task An_unchanged_row_keeps_the_version_the_find_read()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        platform.DspdmAdvance(TimeSpan.FromHours(1));

        var second = await rig.Delivery.DeliverAsync(Work(Well("A-1"), State(first), first.TargetVersion));

        Assert.Equal(OsduDspdmProtocol.Unchanged, second.Returned[OsduDspdmProtocol.OperationValue]);
        Assert.Equal(first.TargetVersion, second.TargetVersion);
        Assert.Null(platform.DspdmRows("WELL")[1001]["ROW_CHANGED_DATE"]);
        Assert.Equal("row 1001 of WELL unchanged in DSPDM", second.Detail);
    }

    [Fact]
    public async Task A_save_whose_answer_was_lost_is_found_again_and_not_inserted_twice()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var reports = new Reports();

        var lost = (await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"), reports: reports)]))[0];
        var failure = Assert.IsType<OsduStatusException>(lost.Failure);
        Assert.Equal(502, failure.StatusCode);
        Assert.Single(platform.DspdmRows("WELL"));
        Assert.True(reports.Steps.ContainsKey(OsduDspdmProtocol.SaveBeginStep));
        Assert.False(reports.Steps.ContainsKey(OsduDspdmProtocol.SaveStep));

        // The worker keeps the completed steps of a try that failed and may be retried.
        var retry = await rig.Delivery.DeliverAsync(Work(Well("A-1"), completed: reports.Steps));

        Assert.Single(platform.DspdmRows("WELL"));
        Assert.Equal("1001", retry.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Equal(OsduDspdmProtocol.Unchanged, retry.Returned[OsduDspdmProtocol.OperationValue]);
        Assert.Equal(Start, retry.TargetVersion);
        Assert.Equal("row 1001 of WELL unchanged in DSPDM (the row an earlier try saved, whose answer was lost)", retry.Detail);
        Assert.Equal(1001L, SavedRows(platform).Last()["WELL_ID"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_lost_answer_to_a_save_of_several_rows_fails_them_all_and_the_retry_finds_them_all()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var reports = new[] { new Reports(), new Reports() };

        var lost = await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"), reports: reports[0]), Work(Well("B-2"), reports: reports[1])]);
        Assert.All(lost, o => Assert.IsType<OsduStatusException>(o.Failure));
        Assert.Equal(1, platform.DspdmSaves);

        var retried = await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"), completed: reports[0].Steps), Work(Well("B-2"), completed: reports[1].Steps)]);
        Assert.All(retried, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(["1001", "1002"], retried.Select(o => o.Returned[OsduDspdmProtocol.IdValue]));
        Assert.Equal(2, platform.DspdmRows("WELL").Count);
    }

    [Fact]
    public async Task A_row_the_record_did_not_write_holds_the_record_unless_the_flow_takes_such_rows_over()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        well.Rows[500] = new JsonObject { ["WELL_ID"] = 500L, ["UWI"] = "A-1", ["OPERATOR"] = "Legacy", ["ROW_CREATED_DATE"] = "2020-01-01T00:00:00.000" };
        using (var rig = new Rig(platform))
        {
            var held = await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"))]);
            var reason = Assert.IsType<RecordHeldException>(held[0].Failure);
            Assert.Equal("WELL already holds row 500 with UWI 'A-1', which this record did not write. Set target.dspdm.existingRows to update to take such rows over, or remove the row.", reason.Message);
            Assert.Equal(0, platform.DspdmSaves);
            Assert.Equal("Legacy", well.Rows[500]["OPERATOR"]!.GetValue<string>());
        }

        using var takeover = new Rig(platform, new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot, ExistingRows = DspdmExistingRows.Update });
        var taken = await takeover.Delivery.DeliverAsync(Work(Well("A-1")));
        Assert.Equal("500", taken.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Equal("updated row 500 of WELL (an existing row taken over (target.dspdm.existingRows: update))", taken.Detail);
        Assert.Equal("Acme", well.Rows[500]["OPERATOR"]!.GetValue<string>());
        Assert.Single(well.Rows);
    }

    [Fact]
    public async Task Two_records_of_one_delivery_that_are_one_row_hold_the_later_one()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);

        var outcomes = await rig.Delivery.DeliverBatchAsync([Work(Well("A-1", key: "source-1")), Work(Well(" A-1", key: "source-2"))]);

        Assert.True(outcomes[0].Succeeded);
        var held = Assert.IsType<RecordHeldException>(outcomes[1].Failure);
        Assert.Equal("another record of this delivery (\"A-1\") is the same row of WELL (UWI 'A-1'), and a row is one record's", held.Message);
        Assert.Single(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task A_row_that_is_no_longer_there_is_inserted_again()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var state = new Dictionary<string, string>(StringComparer.Ordinal) { [OsduDspdmProtocol.BusinessObjectValue] = "WELL", [OsduDspdmProtocol.IdValue] = "77" };

        var outcome = await rig.Delivery.DeliverAsync(Work(Well("A-1"), state, existing: 5));

        Assert.Equal("1001", outcome.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Equal("inserted as row 1001 of WELL (row 77 it was delivered as is no longer in DSPDM)", outcome.Detail);
    }

    [Fact]
    public async Task A_changed_key_updates_the_records_row_and_a_key_another_row_holds_holds_the_record()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1", key: "source-1")));
        await rig.Delivery.DeliverAsync(Work(Well("B-2", key: "source-2")));

        var renamed = await rig.Delivery.DeliverAsync(Work(Well("A-9", key: "source-1"), State(first), first.TargetVersion));
        Assert.Equal("1001", renamed.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Equal("updated row 1001 of WELL (its key changed)", renamed.Detail);
        Assert.Equal("A-9", platform.DspdmRows("WELL")[1001]["UWI"]!.GetValue<string>());

        var clash = (await rig.Delivery.DeliverBatchAsync([Work(Well("B-2", key: "source-1"), State(first), first.TargetVersion)]))[0];
        var held = Assert.IsType<RecordHeldException>(clash.Failure);
        Assert.Equal("UWI 'B-2' is the key of row 1002 of WELL, and the record's row is 1001; two rows cannot share a key", held.Message);
    }

    [Theory]
    [InlineData("SURFACE_X", "1.5", "SURFACE_X, which is not an active attribute of WELL")]
    [InlineData("WELL_ID", "7", "WELL_ID, the primary key of WELL, which DSPDM gives a row when it inserts it")]
    [InlineData("ROW_CHANGED_DATE", "\"2026-01-01T00:00:00Z\"", "ROW_CHANGED_DATE, which DSPDM fills on every save")]
    [InlineData("STATUS_CODE", "\"X\"", "STATUS_CODE, which is read-only in WELL")]
    [InlineData("DEPTH", "\"deep\"", "DEPTH takes a number")]
    [InlineData("WELL_NAME", "\"A name much longer than thirty characters\"", "WELL_NAME takes at most 30 characters")]
    [InlineData("SPUD_DATE", "\"next week\"", "SPUD_DATE takes a date in one of the forms DSPDM reads")]
    [InlineData("UWI", "\"  \"", "UWI is empty, and the rows of WELL are found again by UWI")]
    [InlineData("uwi", "\"A-1\"", "two properties name the attribute UWI")]
    public async Task A_row_DSPDM_would_refuse_is_held_before_anything_is_saved(string attribute, string json, string reason)
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var document = Well("A-1");
        document["data"]![attribute] = JsonNode.Parse(json);

        var outcome = (await rig.Delivery.DeliverBatchAsync([Work(document)]))[0];

        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.StartsWith("the row cannot be saved in WELL: ", held.Message, StringComparison.Ordinal);
        Assert.Contains(reason, held.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.DspdmSaves);
    }

    [Fact]
    public async Task A_mapping_that_fills_an_attribute_DSPDM_keeps_is_held()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);

        var outcome = (await rig.Delivery.DeliverBatchAsync([Work(Well("A-1", owned: [.. Owned, "STATUS_CODE", "ROW_CHANGED_BY"]))]))[0];

        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Equal(
            "the row cannot be saved in WELL: its mapping fills STATUS_CODE, which is read-only in WELL; its mapping fills ROW_CHANGED_BY, which DSPDM fills on every save",
            held.Message);
    }

    [Fact]
    public async Task Mandatory_attributes_are_required_to_insert_and_cannot_be_cleared()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var noOperator = Well("A-1");
        noOperator["data"]!.AsObject().Remove("OPERATOR");

        var insert = (await rig.Delivery.DeliverBatchAsync([Work(noOperator)]))[0];
        Assert.Equal("DSPDM requires OPERATOR to insert a row of WELL, and the row renders it empty", Assert.IsType<RecordHeldException>(insert.Failure).Message);
        Assert.Equal(0, platform.DspdmSaves);

        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var update = (await rig.Delivery.DeliverBatchAsync([Work(noOperator, State(first), first.TargetVersion)]))[0];
        Assert.Equal("OPERATOR is mandatory in WELL, and the row renders it empty, which would clear it", Assert.IsType<RecordHeldException>(update.Failure).Message);
        Assert.Equal(1, platform.DspdmSaves);
    }

    [Fact]
    public async Task A_refused_save_is_sent_again_one_row_at_a_time_so_one_bad_row_holds_only_itself()
    {
        var platform = new FakeOsduPlatform { DspdmRefuses = (attribute, value) => attribute == "REMARK" && value?.GetValue<string>() == "bad" ? "Invalid value for REMARK" : null };
        platform.DspdmWell();
        using var rig = new Rig(platform);

        var outcomes = await rig.Delivery.DeliverBatchAsync(
        [
            Work(Well("A-1")),
            Work(Well("B-2", more: new JsonObject { ["REMARK"] = "bad" })),
            Work(Well("C-3")),
        ]);

        Assert.True(outcomes[0].Succeeded);
        Assert.True(outcomes[2].Succeeded);
        var held = Assert.IsType<RecordHeldException>(outcomes[1].Failure);
        Assert.Equal("DSPDM refused the row: /api/dspdm/v1/save: DSPDM refused the call (HTTP 500, status WARNING): Invalid value for REMARK", held.Message);
        Assert.DoesNotContain("com.lgc", held.Message, StringComparison.Ordinal);
        Assert.Equal(4, platform.DspdmSaves);
        Assert.Equal(["A-1", "C-3"], platform.DspdmRows("WELL").Values.Select(r => r["UWI"]!.GetValue<string>()));
        Assert.Equal(OsduDspdmProtocol.SaveStep, outcomes[1].Steps[^1].Name);
        Assert.Equal(500, outcomes[1].Steps[^1].Status);
    }

    [Fact]
    public async Task A_database_failure_is_retried_and_a_constraint_the_database_names_holds_the_row()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmBrokenSaves.Add(1);
        using var rig = new Rig(platform);

        var broken = (await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"))]))[0];
        var failure = Assert.IsType<DspdmRefusalProbe>(DspdmRefusalProbe.Of(broken.Failure));
        Assert.False(failure.AboutTheRows);
        Assert.Contains("status ERROR): Unable to acquire a database connection", broken.Failure!.Message, StringComparison.Ordinal);
        Assert.Empty(platform.DspdmRows("WELL"));

        platform.DspdmViolates = (attribute, value) => attribute == "WELL_NAME" && value?.GetValue<string>() == "Nope";
        var violated = (await rig.Delivery.DeliverBatchAsync([Work(Well("B-2", name: "Nope"))]))[0];
        var held = Assert.IsType<RecordHeldException>(violated.Failure);
        Assert.Contains("due to check constraint violation", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PSQLException", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_whose_save_fails_on_the_database_is_saved_row_by_row()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmBrokenSaves.Add(1);
        using var rig = new Rig(platform);

        var outcomes = await rig.Delivery.DeliverBatchAsync([Work(Well("A-1")), Work(Well("B-2"))]);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(3, platform.DspdmSaves);
    }

    [Fact]
    public async Task A_key_DSPDM_keeps_differently_is_found_by_DSPDMs_own_comparison()
    {
        var platform = new FakeOsduPlatform { DspdmStoredText = (attribute, text) => attribute == "UWI" ? text.ToUpperInvariant() : text };
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("abc-1")));
        Assert.Equal("ABC-1", platform.DspdmRows("WELL")[1001]["UWI"]!.GetValue<string>());

        var from = platform.Calls.Count;
        var second = await rig.Delivery.DeliverAsync(Work(Well("abc-1", name: "Renamed"), State(first), first.TargetVersion));

        Assert.Equal([Common, Common, Save], Sent(platform, from));
        Assert.Equal("EQUALS", Body(platform, from + 1)["criteriaFilters"]![0]!["operator"]!.GetValue<string>());
        Assert.Equal("1001", second.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Single(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task Rows_are_found_by_a_key_of_two_attributes()
    {
        var platform = new FakeOsduPlatform();
        var daily = platform.DspdmDailyVolumes();
        daily.Rows[9] = new JsonObject { ["WELL_VOL_DAILY_ID"] = 9L, ["UWI"] = "A-1", ["VOLUME_DATE"] = "2024-05-02", ["ROW_CREATED_DATE"] = "2024-05-03T00:00:00.000" };
        using var rig = new Rig(platform, new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot, ExistingRows = DspdmExistingRows.Update });

        static JsonObject Day(string uwi, string day, double oil) => new()
        {
            ["id"] = TargetId(uwi + day, "well_vol_daily"),
            ["kind"] = DailyKind,
            [DspdmKinds.OwnedProperty] = new JsonArray("OIL_VOLUME", "UWI", "VOLUME_DATE"),
            ["data"] = new JsonObject { ["UWI"] = uwi, ["VOLUME_DATE"] = day, ["OIL_VOLUME"] = oil },
        };

        var outcomes = await rig.Delivery.DeliverBatchAsync(new[] { Day("A-1", "2024-05-01", 10), Day("A-1", "2024-05-02", 11), Day("B-2", "2024-05-01", 12) }.Select(d => Work(d)).ToList());

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(["1001", "9", "1002"], outcomes.Select(o => o.Returned[OsduDspdmProtocol.IdValue]));
        Assert.Equal(11m, daily.Rows[9]["OIL_VOLUME"]!.GetValue<decimal>());
        Assert.Equal(3, daily.Rows.Count);
        Conform(platform);
    }

    [Fact]
    public async Task A_row_is_verified_by_its_primary_key_and_its_change_date()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        using var rig = new Rig(platform);
        Assert.True(rig.Delivery.VerifiesWithTargetState);
        Assert.Equal(256, rig.Delivery.MaxVerifyBatch);
        var a = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var b = await rig.Delivery.DeliverAsync(Work(Well("B-2")));
        var c = await rig.Delivery.DeliverAsync(Work(Well("C-3")));
        well.Rows[1002]["ROW_CHANGED_DATE"] = "2026-04-01T00:00:00.000";
        well.Rows.Remove(1003);

        var from = platform.Calls.Count;
        var results = await rig.Delivery.VerifyBatchAsync(
        [
            new VerifyRequest(TargetId("A-1"), a.TargetVersion, State(a)),
            new VerifyRequest(TargetId("B-2"), b.TargetVersion, State(b)),
            new VerifyRequest(TargetId("C-3"), c.TargetVersion, State(c)),
            new VerifyRequest(TargetId("D-4"), 7, null),
            new VerifyRequest(TargetId("A-1"), null, State(a)),
        ]);

        Assert.Equal([Common], Sent(platform, from));
        Assert.Equal([VerifyOutcome.Match, VerifyOutcome.Drifted, VerifyOutcome.Missing, VerifyOutcome.Error, VerifyOutcome.Match], results.Select(r => r.Outcome));
        Assert.Equal("row 1002 of WELL changed at 2026-04-01T00:00:00.000Z; the ledger holds the save of 2026-03-01T08:00:01.000Z", results[1].Detail);
        Assert.Equal("row 1003 of WELL not found", results[2].Detail);
        Assert.Equal("the record's target state names no DSPDM row, so there is no row to read back", results[3].Detail);
        Assert.Equal("no expected version recorded; observed version adopted", results[4].Detail);
        Assert.Equal(VerifyOutcome.Error, (await rig.Delivery.VerifyAsync(TargetId("A-1"), a.TargetVersion)).Outcome);

        using var moved = new Rig(platform, new DspdmTarget
        {
            Root = FakeOsduPlatform.DspdmRoot,
            BusinessObjects = new Dictionary<string, DspdmBusinessObject>(StringComparer.Ordinal) { ["well"] = new() { Name = "WELL HEADER" } },
        });
        var elsewhere = (await moved.Delivery.VerifyBatchAsync([new VerifyRequest(TargetId("A-1"), a.TargetVersion, State(a))]))[0];
        Assert.Equal(VerifyOutcome.Error, elsewhere.Outcome);
        Assert.Contains("DSPDM has no business object 'WELL HEADER'", elsewhere.Detail, StringComparison.Ordinal);
        Conform(platform);
    }

    [Fact]
    public async Task A_row_is_read_back_as_a_record_and_deleted_for_good()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var delivered = await rig.Delivery.DeliverAsync(Work(Well("A-1", more: new JsonObject { ["SPUD_DATE"] = "2024-05-01T10:00:00Z" })));
        var id = TargetId("A-1");

        var read = await rig.Delivery.ReadAsync(id, State(delivered), CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(id, read["id"]!.GetValue<string>());
        Assert.Equal(delivered.TargetVersion, read["version"]!.GetValue<long>());
        Assert.Equal("WELL", read["dspdm"]!["businessObject"]!.GetValue<string>());
        Assert.Equal(1001L, read["dspdm"]!["id"]!.GetValue<long>());
        Assert.Equal("2024-05-01T10:00:00.000Z", read["data"]!["SPUD_DATE"]!.GetValue<string>());
        Assert.Null(read["data"]!["type"]);
        Assert.Null(read["data"]!["id"]);
        var stateless = await Assert.ThrowsAsync<DeliveryException>(() => rig.Delivery.ReadAsync(id));
        Assert.Contains("the record's target state names no DSPDM row", stateless.Message, StringComparison.Ordinal);

        var record = await Assert.ThrowsAsync<DeliveryException>(() => rig.Delivery.DeleteAsync(id, RemovalScope.Record, State(delivered)));
        Assert.Equal($"{id}: {OsduDspdmProtocol.RecordScopeRefused}.", record.Message);
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Delivery.DeleteAsync(id, RemovalScope.History, State(delivered)));
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Delivery.DeleteAsync(id, RemovalScope.Everything, null));
        Assert.Single(platform.DspdmRows("WELL"));

        var deleted = await rig.Delivery.DeleteAsync(id, RemovalScope.Everything, State(delivered));
        Assert.True(deleted.Deleted);
        Assert.Equal("row 1001 of WELL deleted from DSPDM, for good", deleted.Detail);
        Assert.Empty(platform.DspdmRows("WELL"));
        Assert.Contains("DELETE /api/dspdm/v1/delete/WELL/1001", Sent(platform));

        var gone = await rig.Delivery.DeleteAsync(id, RemovalScope.Everything, State(delivered));
        Assert.True(gone.AlreadyGone);
        Assert.Null(await rig.Delivery.ReadAsync(id, State(delivered), CancellationToken.None));
        Conform(platform);
    }

    [Fact]
    public async Task The_probe_asks_DSPDMs_health_and_a_read_of_its_metadata()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using (var rig = new Rig(platform))
        {
            var probe = await rig.Delivery.ProbeAsync();
            Assert.True(probe.Reachable, probe.Detail);
            Assert.Equal("/api/dspdm/v1/common", probe.Path);
            Assert.Equal(["GET /api/dspdm/v1/health", Common], Sent(platform));
        }

        using var anonymous = new Rig(platform, partition: false);
        var refused = await anonymous.Delivery.ProbeAsync();
        Assert.False(refused.Reachable);
        Assert.Equal(401, refused.Status);
    }

    [Theory]
    [InlineData("missing", "DSPDM has no business object 'WELL' for the rows of well. Name the business object under target.dspdm.businessObjects.well.name")]
    [InlineData("entity", "DSPDM's business object 'WELL' is the entity 'wellbore', and the kind names 'well'.")]
    [InlineData("inactive", "DSPDM's business object 'WELL' is not active.")]
    [InlineData("metadata", "'WELL' is one of DSPDM's metadata tables, which its save refuses to write")]
    [InlineData("catalog", "'WELL' is an equipment catalog table, which DSPDM's save hands to its equipment save")]
    [InlineData("no-key", "DSPDM's metadata gives the business object 'WELL' no primary key")]
    [InlineData("two-keys", "DSPDM's business object 'WELL' has a primary key of 2 attributes (UWI, WELL_ID)")]
    [InlineData("text-key", "DSPDM's business object 'WELL' has the primary key WELL_ID of type 'character varying(20)'")]
    [InlineData("no-constraint", "DSPDM's business object 'WELL' has no unique constraint, so a row could not be found again after a save whose answer was lost")]
    [InlineData("two-constraints", "DSPDM's business object 'WELL' has 2 unique constraints (UK_WELL_NAME: WELL_NAME; UK_WELL_UWI: UWI); name the one its rows are found by under target.dspdm.businessObjects.well.key.")]
    [InlineData("declared-other", "target.dspdm.businessObjects.well.key names REMARK, which is not a unique constraint of DSPDM's business object 'WELL' (UK_WELL_UWI: UWI).")]
    [InlineData("declared-key", "The key the rows of 'WELL' are found by names its primary key WELL_ID")]
    [InlineData("inactive-key", "DSPDM's business object 'WELL' has no active attribute UWI, which the key its rows are found by names.")]
    public async Task A_business_object_the_route_cannot_write_is_refused_before_the_run(string problem, string message)
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        IReadOnlyList<string> key = [];
        switch (problem)
        {
            case "missing":
                platform.DspdmObjects.Clear();
                break;
            case "entity":
                platform.DspdmObjects["WELL"] = Copy(well, "wellbore");
                break;
            case "inactive":
                well.Active = false;
                break;
            case "metadata":
                well.MetadataTable = true;
                break;
            case "catalog":
                well.CatalogTable = true;
                break;
            case "no-key":
                Replace(well, "WELL_ID", a => a with { PrimaryKey = false });
                break;
            case "two-keys":
                Replace(well, "UWI", a => a with { PrimaryKey = true });
                break;
            case "text-key":
                Replace(well, "WELL_ID", a => a with { DataType = "character varying(20)" });
                break;
            case "no-constraint":
                well.Constraints.Clear();
                break;
            case "two-constraints":
                well.Constraints["UK_WELL_NAME"] = ["WELL_NAME"];
                break;
            case "declared-other":
                key = ["REMARK"];
                break;
            case "declared-key":
                well.Constraints["UK_WELL_ID"] = ["WELL_ID"];
                key = ["WELL_ID"];
                break;
            case "inactive-key":
                Replace(well, "UWI", a => a with { Active = false });
                break;
        }

        using var rig = new Rig(platform, new DspdmTarget
        {
            Root = FakeOsduPlatform.DspdmRoot,
            BusinessObjects = new Dictionary<string, DspdmBusinessObject>(StringComparer.Ordinal) { ["well"] = new() { Key = key } },
        });

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.CheckKindAsync(Kind));
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);

        // A delivery refuses the business object the same way, and saves nothing.
        var outcome = (await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"))]))[0];
        Assert.Contains(message, outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(0, platform.DspdmSaves);

        static FakeDspdmObject Copy(FakeDspdmObject source, string entity)
        {
            var copy = new FakeDspdmObject(source.Name, entity);
            copy.Attributes.AddRange(source.Attributes);
            foreach (var (name, members) in source.Constraints)
            {
                copy.Constraints[name] = members;
            }

            return copy;
        }

        static void Replace(FakeDspdmObject business, string name, Func<FakeDspdmColumn, FakeDspdmColumn> change)
        {
            var index = business.Attributes.FindIndex(a => a.Name == name);
            business.Attributes[index] = change(business.Attributes[index]);
        }
    }

    [Fact]
    public async Task A_declared_key_chooses_among_the_unique_constraints_and_the_metadata_is_read_once()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        well.Constraints["UK_WELL_NAME"] = ["WELL_NAME"];
        using var rig = new Rig(platform, new DspdmTarget
        {
            Root = FakeOsduPlatform.DspdmRoot,
            BusinessObjects = new Dictionary<string, DspdmBusinessObject>(StringComparer.Ordinal) { ["well"] = new() { Name = "WELL", Key = ["WELL_NAME"] } },
        });

        await rig.Protocol.CheckKindAsync(Kind);
        await rig.Protocol.CheckKindAsync(Kind);
        var outcome = await rig.Delivery.DeliverAsync(Work(Well("A-1", name: "Alpha 1")));

        Assert.Equal(3, platform.Calls.Count(c => JsonNode.Parse(c.Body ?? "{}")!["boName"]?.GetValue<string>() is "BUSINESS OBJECT" or "BUSINESS OBJECT ATTR" or "BUS OBJ ATTR UNIQ CONSTRAINTS"));
        var find = platform.Calls.Last(c => c.Uri.AbsolutePath.EndsWith("/common", StringComparison.Ordinal));
        Assert.Equal("WELL_NAME", JsonNode.Parse(find.Body!)!["criteriaFilters"]![0]!["boAttrName"]!.GetValue<string>());
        Assert.Equal("1001", outcome.Returned[OsduDspdmProtocol.IdValue]);
    }

    [Fact]
    public async Task Metadata_that_could_not_be_read_is_read_again_and_a_timezone_goes_with_every_request()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot, Timezone = "GMT-05:00" });

        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.CheckKindAsync(Kind));
        platform.DspdmWell();
        await rig.Protocol.CheckKindAsync(Kind);

        var outcome = await rig.Delivery.DeliverAsync(Work(Well("A-1", more: new JsonObject { ["SPUD_DATE"] = "2024-05-01T10:00:00Z" })));
        Assert.True(outcome.Succeeded);
        Assert.Equal("2024-05-01T05:00:00.000", platform.DspdmRows("WELL")[1001]["SPUD_DATE"]!.GetValue<string>());
        Assert.All(
            platform.Calls.Where(c => c.Method == HttpMethod.Post),
            c => Assert.Contains("\"timezone\":\"GMT-05:00\"", c.Body!, StringComparison.Ordinal));

        // The version is the save's UTC time as DSPDM writes it, whatever zone labels it.
        Assert.Equal(Start, outcome.TargetVersion);
    }

    [Fact]
    public async Task Records_whose_document_is_not_due_are_left_alone()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);

        var outcome = (await rig.Delivery.DeliverBatchAsync([Work(Well("A-1"), existing: 42, metadata: false)]))[0];

        Assert.False(outcome.MetadataDelivered);
        Assert.Equal(42, outcome.TargetVersion);
        Assert.Empty(platform.Calls);
    }

    /// <summary>What a failure's type says, for the assertions that look into a DSPDM refusal.</summary>
    private sealed record DspdmRefusalProbe(bool AboutTheRows)
    {
        public static DspdmRefusalProbe? Of(Exception? failure)
            => failure is Engine.Protocols.Dspdm.DspdmRefusal refusal ? new DspdmRefusalProbe(refusal.AboutTheRows) : null;
    }
}
