using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Production DDMS historian's part of the units of work (docs/atomic-delivery-plan.md), against the fakes of its
/// ingestion and query services and of Storage: the ProductionValues record reported as the unit's once it is written and
/// before its first points request, each request's accepted series versions reported as points, the versions of a partly
/// refused request reported before the try fails, a resumed try reporting only the requests no try recorded; and the undo
/// after each failure point, which keeps every point (the historian has no delete), removes the record the unit created, gives
/// back the version an update replaced, minds a record OSDU held before the unit began, leaves the record to newer work, and
/// answers every artifact once, a failure for its own artifact alone.
/// </summary>
public sealed class AtomicTimeSeriesTests
{
    private const string ValuesId = "dev:work-product-component--ProductionValues:pv-1";
    private const string OtherId = "dev:work-product-component--ProductionValues:pv-2";
    private const string ValuesKind = "osdu:wks:work-product-component--ProductionValues:2.0.0";
    private const string Well = "dev:master-data--Well:w-1:";
    private const string Records = "/api/storage/v2/records";
    private const string Ingestion = "/production-values/";
    private const long Day0 = 1_704_067_200_000;
    private const long Day = 86_400_000;
    private const long FirstVersion = 1_781_770_405_994;

    private static readonly DeliveryUnit Unit = new(Guid.Parse("0192aa00-0000-7000-8000-0000000071e5"), new DateTime(2026, 9, 17, 11, 58, 0, DateTimeKind.Utc));

    private static TimeSeriesSettings Settings(int settle = 30, long maxRequestBytes = TimeSeriesSettings.DefaultMaxRequestBytes)
        => new() { QueryRoot = FakeOsduPlatform.TimeSeriesQueryRoot, SettleSeconds = settle, PollSeconds = 1, MaxRequestBytesPerRequest = maxRequestBytes };

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, TimeSeriesSettings? settings = null)
        {
            Platform = platform;
            Hook = new ShapeCallHook(platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Hook, allowLoopback: true);
            var client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var historian = new DdmsService("historian", FakeOsduPlatform.TimeSeriesRoot, DdmsShape.ProductionTimeSeriesV1, DdmsCatalog.TimeSeriesCollections)
            {
                TimeSeries = settings ?? Settings(),
            };
            var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [historian], ProtocolOptions = options }, "production");
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, 0, new ProductionTimeSeriesRouteTests.SteppingClock(), DdmsRouting.Of(flow));
            Ledger = new ShapeArtifactLedger(Unit, () => Hook.Count);
        }

        public FakeOsduPlatform Platform { get; }

        public ShapeCallHook Hook { get; }

        public HttpRuntime Runtime { get; }

        public OsduDdmsProtocol Protocol { get; }

        /// <summary>What the worker's ledger holds of the record's unit.</summary>
        public ShapeArtifactLedger Ledger { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Hook.Dispose();
        }
    }

    private static JsonObject Values(string id = ValuesId, string name = "Daily allocation") => ValuesOf(id, name, ("OIL", "Double"), ("WELLS", "Integer"));

    private static JsonObject ValuesOf(string id, string name, params (string Id, string Kind)[] series) => FakeOsduPlatform.Record(id, ValuesKind, new JsonObject
    {
        ["Name"] = name,
        ["ReportingEntityID"] = Well,
        ["ProductionMetricValues"] = new JsonArray(series.Select(s => (JsonNode?)new JsonObject
        {
            ["DDMSDatasetID"] = s.Id,
            ["ParameterKindID"] = $"dev:reference-data--ParameterKind:{s.Kind}:",
            ["UnitOfMeasureID"] = "dev:reference-data--UnitOfMeasure:bbl%2Fd:",
        }).ToArray()),
    });

    /// <summary>A points file in the ingestion service's own body: two days of oil and of the well count.</summary>
    private static RafsRouteTests.NamedFiles Points() => Points(
        ("OIL", [(Day0, JsonValue.Create(1.5)), (Day0 + Day, JsonValue.Create(2.5))]),
        ("WELLS", [(Day0, JsonValue.Create(3)), (Day0 + Day, JsonValue.Create(4))]));

    /// <summary>Six hundred days of oil, which a body limit of 10 000 bytes splits into three requests.</summary>
    private static RafsRouteTests.NamedFiles ThreeRequests()
        => Points(("OIL", Enumerable.Range(0, 600).Select(i => (Day0 + (i * Day), (JsonNode?)JsonValue.Create(1000.25 + i)))));

    private static RafsRouteTests.NamedFiles Points(params (string Id, IEnumerable<(long T, JsonNode? V)> Points)[] series)
        => new(("points.json", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["timeseries"] = new JsonArray(series.Select(s => (JsonNode?)new JsonObject
            {
                ["timeseriesId"] = s.Id,
                ["points"] = new JsonArray(s.Points.Select(p => (JsonNode?)new JsonObject { ["timestamp"] = p.T, ["value"] = p.V }).ToArray()),
            }).ToArray()),
        }.ToJsonString())));

    private static DeliveryKey KeyOf(string id) => DeliveryKey.Derive("production", [id]);

    /// <summary>One try of the record's pending work; a ledger makes it a try of that unit, resuming what earlier tries reported.</summary>
    private static DeliveryWork Work(JsonObject document, ShapeArtifactLedger? ledger, IPayloadSource? points = null, bool metadata = true, long? existing = null)
    {
        var id = document["id"]!.GetValue<string>();
        return new DeliveryWork
        {
            Key = KeyOf(id),
            TargetId = id,
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = points is not null,
            Payload = points,
            ExistingVersion = existing,
            CompletedSteps = ledger is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyDictionary<string, string>>(ledger.Completed, StringComparer.Ordinal),
            StepCompleted = ledger is null ? null : ledger.Listen,
            Unit = ledger?.Unit,
        };
    }

    private static UndoWork Undo(ShapeArtifactLedger ledger, string id = ValuesId, UndoReason reason = UndoReason.Failed, bool keepRecord = false)
        => ledger.Undo(KeyOf(id), id, reason, keepRecord);

    private static async Task<IReadOnlyList<UndoResult>> UndoAsync(Rig rig, UndoWork work)
    {
        var results = await rig.Protocol.UndoAsync([work]);
        UndoAnswers.EachOnce([work], results);
        return results;
    }

    private static long Stored(FakeOsduPlatform platform, string id = ValuesId) => platform.Records[id]["version"]!.GetValue<long>();

    private static string NameOf(FakeOsduPlatform platform, string id = ValuesId) => platform.Records[id]["data"]!["Name"]!.GetValue<string>();

    private static string V(long version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Makes the record Storage holds define OIL alone just before the points go, so the ingestion service refuses WELLS.</summary>
    private static void ForgetWells(FakeOsduPlatform platform)
    {
        var metrics = platform.Records[ValuesId]["data"]!["ProductionMetricValues"]!.AsArray();
        metrics.Remove(metrics.Single(m => m!["DDMSDatasetID"]!.GetValue<string>() == "WELLS"));
    }

    [Fact]
    public async Task A_created_record_whose_points_follow_is_reported_after_its_write_and_before_its_first_points_request()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points()));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("PUT " + Records, rig.Hook.Seen[0]);
        Assert.StartsWith("POST " + FakeOsduPlatform.TimeSeriesRoot + Ingestion, rig.Hook.Seen[1], StringComparison.Ordinal);

        // The record is the unit's from the call after its write, before any point can land; each request's versions follow it.
        Assert.Equal(["metadata", "points-1", "points-1"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal([1, 2], rig.Ledger.Reports.Take(2).Select(r => r.Calls));
        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal(
            (ArtifactRoles.Record, ValuesId, (long?)Stored(platform), (long?)null, ArtifactStatus.Pending),
            (record.Role, record.TargetId, record.Version, record.PriorVersion, record.State));
        var points = rig.Ledger["points:1"];
        Assert.Equal(
            (ArtifactRoles.Points, ValuesId, $"OIL={V(FirstVersion)}, WELLS={V(FirstVersion + 1)}", ArtifactStatus.Pending, (long?)null),
            (points.Role, points.TargetId, points.Locator, points.State, points.Version));

        // The read back marks the request settled and names nothing new.
        var settled = rig.Ledger.Reports[2].Report;
        Assert.Empty(settled.Artifacts);
        Assert.Equal("true", settled.Returned["settled"]);
        Assert.Equal(2, rig.Ledger.Rows.Count);
    }

    [Fact]
    public async Task A_record_written_without_points_reports_nothing_since_its_one_write_is_the_whole_delivery()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var (report, _) = Assert.Single(rig.Ledger.Reports);
        Assert.Equal("metadata", report.Step);
        Assert.Empty(report.Artifacts);
        Assert.Empty(rig.Ledger.Rows);
        Assert.Empty(rig.Ledger.Open());
    }

    [Fact]
    public async Task A_points_request_refused_whole_leaves_only_the_record_and_the_undo_removes_it_through_storage()
    {
        var platform = new FakeOsduPlatform { TimeSeriesPartition = "tenant2" };
        using var rig = new Rig(platform);

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.StartsWith($"the ingestion service did not take every series of request 1 of the points of {ValuesId}", held.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("It accepted", held.Message, StringComparison.Ordinal);
        Assert.Equal([TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Empty(platform.TimeSeries);

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        var removed = UndoAnswers.Of(results, TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactStatus.Removed, $"{ValuesId}: removed from OSDU (reversible)"), (removed.Outcome, removed.Note));
        Assert.Equal([$"GET {Records}/{ValuesId}", $"POST {Records}/{ValuesId}:delete"], rig.Hook.Since(calls));
        Assert.Contains(ValuesId, platform.Removed);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(500, false)]
    public async Task A_points_request_the_service_refuses_by_its_status_leaves_the_record_named_alone(int status, bool holds)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Post, Ingestion), (HttpStatusCode)status);

        var failed = await Assert.ThrowsAnyAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.Equal(holds, failed is RecordHeldException);
        Assert.Equal([TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Empty(platform.TimeSeries);

        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: holds ? UndoReason.Held : UndoReason.Failed));
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results).Outcome);
        Assert.Contains(ValuesId, platform.Removed);
    }

    [Fact]
    public async Task A_record_write_storage_refuses_reports_nothing_and_leaves_nothing_to_undo()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Put, Records), HttpStatusCode.BadRequest);

        await Assert.ThrowsAnyAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.Empty(rig.Ledger.Reports);
        Assert.DoesNotContain(rig.Hook.Seen, c => c.Contains(Ingestion, StringComparison.Ordinal));

        var calls = rig.Hook.Count;
        Assert.Empty(await rig.Protocol.UndoAsync([Undo(rig.Ledger)]));
        Assert.Equal(calls, rig.Hook.Count);
        Assert.False(platform.Records.ContainsKey(ValuesId));
    }

    [Fact]
    public async Task A_read_back_the_query_service_refuses_holds_the_record_with_its_points_named_and_the_undo_removes_the_record()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Get, "/versions/"), HttpStatusCode.Forbidden);

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.Contains("the query service refused to read OIL version", held.Message, StringComparison.Ordinal);
        Assert.Equal([TargetArtifact.RecordSlot, "points:1"], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.DoesNotContain(rig.Ledger.Reports, r => r.Report.Returned.ContainsKey(ProductionTimeSeriesShape.SettledValue));

        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        Assert.Equal(ArtifactStatus.Kept, UndoAnswers.Of(results, "points:1").Outcome);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
    }

    [Fact]
    public async Task A_partly_accepted_request_names_the_versions_it_took_before_the_try_fails_and_the_undo_keeps_them()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Before(ShapeCallHook.Call(HttpMethod.Post, Ingestion), () => ForgetWells(platform));

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.EndsWith($"It accepted OIL as version {V(FirstVersion)}, which a later delivery sends again as new versions", held.Message, StringComparison.Ordinal);

        // The accepted series are the unit's, named after the request that took them and before the try fails.
        var (partial, calls) = rig.Ledger.Last("points-1-partial");
        Assert.Equal(2, calls);
        Assert.Equal($"OIL={V(FirstVersion)}", partial.Returned["accepted"]);
        Assert.Equal(64, partial.Returned[ProductionTimeSeriesShape.HashValue].Length);
        var points = Assert.Single(partial.Artifacts);
        Assert.Matches("^points:1:partial:[0-9a-f]{32}$", points.Slot);
        Assert.Equal((ArtifactRoles.Points, ValuesId, $"OIL={V(FirstVersion)}", ArtifactStatus.Pending), (points.Role, points.TargetId, points.Locator, points.Status));
        Assert.Equal([TargetArtifact.RecordSlot, points.Slot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.DoesNotContain(rig.Ledger.Reports, r => r.Report.Step == "points-1");

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        var kept = UndoAnswers.Of(results, points.Slot);
        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.Equal($"the historian has no delete for points, so the series versions it accepted stay (OIL={V(FirstVersion)})", kept.Note);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);

        // What the historian accepted stays; only the record goes, and no call touches the points.
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
        Assert.Contains(ValuesId, platform.Removed);
        Assert.DoesNotContain(rig.Hook.Since(before), c => c.Contains(Ingestion, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_partial_refusal_of_points_alone_reports_only_the_points_and_the_undo_calls_nothing()
    {
        var platform = new FakeOsduPlatform();
        var existing = platform.Put(ValuesOf(ValuesId, "Daily allocation", ("OIL", "Double")));
        using var rig = new Rig(platform);

        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points(), metadata: false, existing: existing)));
        var row = Assert.Single(rig.Ledger.Rows);
        Assert.Equal((ArtifactRoles.Points, $"OIL={V(FirstVersion)}"), (row.Role, row.Locator));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(results).Outcome);
        Assert.Equal(calls, rig.Hook.Count);
        Assert.Equal(existing, Stored(platform));
        Assert.DoesNotContain(ValuesId, platform.Removed);
    }

    [Fact]
    public async Task Requests_accepted_before_a_failed_one_are_named_and_a_resumed_try_adds_only_the_requests_no_try_recorded()
    {
        var platform = new FakeOsduPlatform { TimeSeriesMappingDelay = 0 };
        platform.TimeSeriesFailing.Add(3);
        using var rig = new Rig(platform, Settings(settle: 3, maxRequestBytes: 10_000));

        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, ThreeRequests())));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal([TargetArtifact.RecordSlot, "points:1", "points:2"], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Equal([$"OIL={V(FirstVersion)}", $"OIL={V(FirstVersion + 1)}"], rig.Ledger.Rows.Skip(1).Select(r => r.Locator));

        // The next try of the same unit sends only the third request, and the query service does not serve the points in time.
        platform.TimeSeriesMappingDelay = 100;
        var reports = rig.Ledger.Reports.Count;
        var late = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, ThreeRequests())));
        Assert.Contains("the query service does not serve 3 of the 3 request(s) yet", late.Message, StringComparison.Ordinal);
        Assert.Equal(["points-3"], rig.Ledger.Reports.Skip(reports).Select(r => r.Report.Step));
        Assert.Equal([TargetArtifact.RecordSlot, "points:1", "points:2", "points:3"], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Equal(ArtifactRoles.Record, rig.Ledger[TargetArtifact.RecordSlot].Role);
        Assert.Equal(3, platform.TimeSeries[(ValuesId, "OIL")].Count);

        // The unit fails: every request's versions are kept, and the record it created goes.
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            [
                (ArtifactStatus.Kept, $"the historian has no delete for points, so the series versions it accepted stay (OIL={V(FirstVersion)})"),
                (ArtifactStatus.Kept, $"the historian has no delete for points, so the series versions it accepted stay (OIL={V(FirstVersion + 1)})"),
                (ArtifactStatus.Kept, $"the historian has no delete for points, so the series versions it accepted stay (OIL={V(FirstVersion + 2)})"),
            ],
            results.Where(r => r.Item.Artifact.Role == ArtifactRoles.Points).OrderBy(r => r.Item.Artifact.Slot, StringComparer.Ordinal).Select(r => (r.Outcome, r.Note)));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(3, platform.TimeSeries[(ValuesId, "OIL")].Count);
    }

    [Fact]
    public async Task A_version_the_historian_lost_after_accepting_it_is_still_named_and_kept_by_the_undo()
    {
        var platform = new FakeOsduPlatform();
        platform.TimeSeriesLost.Add("OIL");
        using var rig = new Rig(platform, Settings(settle: 2));

        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.Contains($"OIL version {V(FirstVersion)} answers 404: Failed to get a Stream Mapping", lost.Message, StringComparison.Ordinal);
        Assert.Equal($"OIL={V(FirstVersion)}, WELLS={V(FirstVersion + 1)}", rig.Ledger["points:1"].Locator);
        Assert.False(platform.TimeSeries.ContainsKey((ValuesId, "OIL")));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            (ArtifactStatus.Kept, $"the historian has no delete for points, so the series versions it accepted stay (OIL={V(FirstVersion)}, WELLS={V(FirstVersion + 1)})"),
            (UndoAnswers.Of(results, "points:1").Outcome, UndoAnswers.Of(results, "points:1").Note));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Single(platform.TimeSeries[(ValuesId, "WELLS")]);
    }

    [Fact]
    public async Task A_points_request_whose_answer_was_lost_after_it_landed_fails_the_try_and_the_undo_still_removes_the_record()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.LoseAnswer(ShapeCallHook.Call(HttpMethod.Post, Ingestion));

        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        Assert.StartsWith("HTTP transport failure calling POST", lost.Message, StringComparison.Ordinal);

        // The service stored the versions; the try never learned them, so only the record is the unit's.
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
        Assert.Equal([TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results).Outcome);
        Assert.Contains(ValuesId, platform.Removed);
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
    }

    [Fact]
    public async Task An_update_that_fails_after_its_write_names_the_version_it_replaced_and_gets_it_written_back()
    {
        var platform = new FakeOsduPlatform { TimeSeriesPartition = "tenant2" };
        var earlier = platform.Put(Values(name: "Earlier"));
        using var rig = new Rig(platform);

        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(name: "Later"), rig.Ledger, Points(), existing: earlier)));
        var written = Stored(platform);
        Assert.Equal("Later", NameOf(platform));
        var row = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Version, (long?)written, (long?)earlier), (row.Role, row.Version, row.PriorVersion));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        var restored = Assert.Single(results);
        Assert.Equal((ArtifactStatus.Restored, $"{ValuesId}: version {V(earlier)} written back as version {V(written + 1)}"), (restored.Outcome, restored.Note));
        Assert.Equal([$"GET {Records}/{ValuesId}/{V(earlier)}", "PUT " + Records], rig.Hook.Since(calls));
        Assert.Equal(("Earlier", written + 1), (NameOf(platform), Stored(platform)));
        Assert.DoesNotContain(ValuesId, platform.Removed);
    }

    [Theory]
    [InlineData(-5_000_000, false, ArtifactStatus.Restored)]
    [InlineData(-5_000_000, true, ArtifactStatus.Kept)]
    [InlineData(-6, false, ArtifactStatus.Restored)]
    [InlineData(-4, false, ArtifactStatus.Removed)]
    [InlineData(1, false, ArtifactStatus.Removed)]
    public async Task A_record_osdu_created_before_the_unit_began_is_not_the_units_to_remove(int minutesFromStart, bool earlierPurged, ArtifactStatus expected)
    {
        // Another system wrote the record under the id the flow claims; the unit's write was a create as far as the ledger knows.
        var platform = new FakeOsduPlatform { TimeSeriesPartition = "tenant2" };
        var theirs = platform.Put(Values(name: "Another system"));
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(name: "Ours"), rig.Ledger, Points())));
        var ours = Stored(platform);
        Assert.Equal((ArtifactRoles.Record, (long?)ours), (rig.Ledger[TargetArtifact.RecordSlot].Role, rig.Ledger[TargetArtifact.RecordSlot].Version));

        platform.Records[ValuesId]["createTime"] = Unit.StartedUtc.AddMinutes(minutesFromStart).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        if (earlierPurged)
        {
            platform.History[ValuesId].RemoveAt(0);
        }

        var result = Assert.Single(await UndoAsync(rig, Undo(rig.Ledger)));
        Assert.Equal(expected, result.Outcome);
        switch (expected)
        {
            case ArtifactStatus.Restored:
                // Older than the unit less the five minutes allowed for clocks: the version before the unit's write goes back.
                Assert.Equal($"{ValuesId}: version {V(theirs)} written back as version {V(ours + 1)}", result.Note);
                Assert.Equal("Another system", NameOf(platform));
                Assert.DoesNotContain(ValuesId, platform.Removed);
                break;
            case ArtifactStatus.Kept:
                Assert.StartsWith($"{ValuesId}: OSDU created the record at ", result.Note, StringComparison.Ordinal);
                Assert.Contains("before this delivery began", result.Note, StringComparison.Ordinal);
                Assert.EndsWith("which version it held before is not known; nothing was put back", result.Note, StringComparison.Ordinal);
                Assert.Equal(("Ours", ours), (NameOf(platform), Stored(platform)));
                Assert.DoesNotContain(ValuesId, platform.Removed);
                break;
            default:
                Assert.Contains(ValuesId, platform.Removed);
                break;
        }
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_and_the_undo_calls_nothing()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Before(ShapeCallHook.Call(HttpMethod.Post, Ingestion), () => ForgetWells(platform));
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Abandoned, keepRecord: true));
        var record = UndoAnswers.Of(results, TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactStatus.Superseded, "the record's newer work writes it again, so it is left as it is"), (record.Outcome, record.Note));
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Points).Outcome);
        Assert.Equal(calls, rig.Hook.Count);
        Assert.DoesNotContain(ValuesId, platform.Removed);
    }

    [Fact]
    public async Task A_second_undo_finds_the_record_gone_keeps_the_points_again_and_never_throws()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Before(ShapeCallHook.Call(HttpMethod.Post, Ingestion), () => ForgetWells(platform));
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        var work = Undo(rig.Ledger, reason: UndoReason.Held);

        var first = await UndoAsync(rig, work);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(first, TargetArtifact.RecordSlot).Outcome);

        var again = await UndoAsync(rig, work);
        var record = UndoAnswers.Of(again, TargetArtifact.RecordSlot);
        Assert.Equal((ArtifactStatus.Gone, $"{ValuesId}: OSDU no longer holds the record"), (record.Outcome, record.Note));
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(again, r => r.Item.Artifact.Role == ArtifactRoles.Points).Outcome);

        // Once settled, nothing of the unit is open, and an undo of nothing calls nothing.
        rig.Ledger.Settle(first);
        var calls = rig.Hook.Count;
        Assert.Empty(await rig.Protocol.UndoAsync([Undo(rig.Ledger)]));
        Assert.Equal(calls, rig.Hook.Count);
    }

    [Fact]
    public async Task A_storage_call_that_fails_answers_failed_for_the_record_alone_until_an_undo_gets_through()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Before(ShapeCallHook.Call(HttpMethod.Post, Ingestion), () => ForgetWells(platform));
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));

        // Storage refuses the soft delete: the record's undo fails, and the points are answered as ever.
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Post, ":delete"), HttpStatusCode.InternalServerError);
        var first = await UndoAsync(rig, Undo(rig.Ledger));
        var refused = UndoAnswers.Of(first, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Failed, refused.Outcome);
        Assert.Contains("HTTP 500", refused.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(first, r => r.Item.Artifact.Role == ArtifactRoles.Points).Outcome);
        Assert.DoesNotContain(ValuesId, platform.Removed);
        rig.Ledger.Settle(first);

        // The sweep's next undo takes the record alone; Storage cannot be read this time.
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Get, Records + "/" + ValuesId), HttpStatusCode.ServiceUnavailable);
        var second = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactStatus.Failed), (Assert.Single(second).Item.Artifact.Slot, second[0].Outcome));
        Assert.Contains("HTTP 503", second[0].Note, StringComparison.Ordinal);
        rig.Ledger.Settle(second);

        var third = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(third).Outcome);
        Assert.Contains(ValuesId, platform.Removed);
        rig.Ledger.Settle(third);
        Assert.Empty(rig.Ledger.Open());
    }

    [Fact]
    public async Task An_undo_of_several_records_answers_every_artifact_of_each_once()
    {
        var platform = new FakeOsduPlatform { TimeSeriesPartition = "tenant2" };
        using var rig = new Rig(platform);
        var other = new ShapeArtifactLedger(new DeliveryUnit(Guid.Parse("0192aa00-0000-7000-8000-0000000071e6"), Unit.StartedUtc), () => rig.Hook.Count);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(), rig.Ledger, Points())));
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(OtherId), other, Points())));

        var works = new[] { Undo(rig.Ledger), Undo(other, OtherId) };
        var results = await rig.Protocol.UndoAsync(works);
        UndoAnswers.EachOnce(works, results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(ValuesId, platform.Removed);
        Assert.Contains(OtherId, platform.Removed);
        Assert.Equal(
            [$"{ValuesId}: removed from OSDU (reversible)", $"{OtherId}: removed from OSDU (reversible)"],
            results.Select(r => r.Note).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_artifact_the_historian_never_makes_is_kept_and_a_record_the_flow_cannot_route_fails_every_artifact()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        UndoItem Item(long id, TargetArtifact artifact) => new(id, artifact, Unit.Id, Unit.StartedUtc);

        var strange = new UndoWork
        {
            Key = KeyOf(ValuesId),
            TargetId = ValuesId,
            Reason = UndoReason.Failed,
            Items = [Item(9001, TargetArtifact.Created("objects:0", ArtifactRoles.Objects, ValuesId)), Item(9002, TargetArtifact.Created("session", ArtifactRoles.Session, "s-1"))],
        };
        var unroutable = new UndoWork
        {
            Key = KeyOf("pv-orphan"),
            TargetId = "pv-orphan",
            Reason = UndoReason.Failed,
            Items = [Item(9003, TargetArtifact.RecordWritten("pv-orphan", 3, null)), Item(9004, TargetArtifact.Created("points:1", ArtifactRoles.Points, "pv-orphan", locator: "OIL=1"))],
        };

        var results = await rig.Protocol.UndoAsync([strange, unroutable]);
        UndoAnswers.EachOnce([strange, unroutable], results);
        Assert.All(
            results.Where(r => r.Item.ArtifactId is 9001 or 9002),
            r => Assert.Equal((ArtifactStatus.Kept, "the historian shape makes nothing of this kind beside a record"), (r.Outcome, r.Note)));
        Assert.All(results.Where(r => r.Item.ArtifactId is 9003 or 9004), r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.StartsWith("the record cannot be routed to its DDMS: ", r.Note, StringComparison.Ordinal);
        });
        Assert.Empty(rig.Hook.Seen);
    }
}
