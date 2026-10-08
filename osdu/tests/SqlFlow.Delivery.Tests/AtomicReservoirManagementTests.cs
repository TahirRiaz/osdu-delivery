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
/// The Reservoir Management DDMS's part of the units of work (docs/atomic-delivery-plan.md), against the fake of the service
/// and of Storage: the header record reported as the unit's once it is written and before its first row, each block of rows
/// reported with the keys the service gave them (split where its runs would not fit one locator), the rows posted since the
/// last block reported before a failed try ends with an intent for a post whose answer never came, and the replacement of the
/// earlier delivery's rows marked before it begins; then the undo after each failure point (a row refused, a post unanswered
/// that landed or not, an answer without the row's key, blocks partly posted, a resumed try, a replacement that had begun
/// deleting), which first finds and deletes the one row an unanswered post made (posted after every row a block names, it can
/// hang from one of them), then the unit's rows the last posted first, keeps what it cannot tell or put back, removes or writes
/// back the record, minds a record OSDU held before the unit began, leaves the record to newer work, does no harm a second
/// time, and answers every artifact once, a failure for its own artifacts alone, the record waiting with rows not yet undone.
/// </summary>
public sealed class AtomicReservoirManagementTests
{
    private const string PhiKId = "dev:work-product-component--PersistedCollection:phik-1";
    private const string SecondId = "dev:work-product-component--PersistedCollection:phik-2";
    private const string PhiKKind = "osdu:wks:work-product-component--PersistedCollection:1.2.0";
    private const string Reservoir = "dev:master-data--Reservoir:r1:";
    private const string Root = FakeOsduPlatform.ReservoirManagementRoot + "/ddms/";
    private const string Records = "/api/storage/v2/records";
    private const string Rt = "phi-k-synthesis-rt";
    private const string PhiK = "phi-k-synthesis-phi-k";

    private static readonly DeliveryUnit Unit = new(Guid.Parse("0192aa00-0000-7000-8000-0000000052a1"), new DateTime(2026, 9, 17, 11, 58, 0, DateTimeKind.Utc));

    /// <summary>Two rock types, the first with two Phi-K rows and the second with one: keys 101 to 105 on an empty service.</summary>
    private const string TwoRockTypes = """
        {
          "phi-k-synthesis-rt": [
            { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ { "phie": 0.21, "kgas": 12.5 }, { "phie": 0.18, "kgas": 8.1 } ] },
            { "rt_tab_name": "RT2", "rt_phi_k_tab": 2, "phi-k-synthesis-phi-k": [ { "phie": 0.25 } ] }
          ]
        }
        """;

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform)
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
            var rm = new DdmsService("rm", FakeOsduPlatform.ReservoirManagementRoot, DdmsShape.ReservoirManagement, DdmsCatalog.ReservoirManagementCollections)
            {
                ReservoirManagement = new ReservoirManagementSettings { SettleSeconds = 30, PollSeconds = 5 },
            };
            var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [rm], ProtocolOptions = options }, "reservoir");
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, time: new ProductionTimeSeriesRouteTests.SteppingClock(), routing: DdmsRouting.Of(flow));
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

    private static JsonObject Header(string name = "Phi-K 1", string id = PhiKId)
        => FakeOsduPlatform.Record(id, PhiKKind, new JsonObject { ["name"] = name, ["ParentObjectID"] = Reservoir });

    private static RafsRouteTests.NamedFiles Rows(string json) => new(("rows.json", Encoding.UTF8.GetBytes(json)));

    /// <summary>One rock type with <paramref name="samples"/> Phi-K rows below it.</summary>
    private static RafsRouteTests.NamedFiles OneRockType(int samples)
    {
        var rows = string.Join(", ", Enumerable.Range(0, samples).Select(i => string.Create(CultureInfo.InvariantCulture, $"{{ \"phie\": {i / 1000.0} }}")));
        return Rows($$"""{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT1", "rt_phi_k_tab": 1, "phi-k-synthesis-phi-k": [ {{rows}} ] } ] }""");
    }

    /// <summary><paramref name="count"/> rock types, each with <paramref name="samplesEach"/> Phi-K rows below it.</summary>
    private static RafsRouteTests.NamedFiles RockTypes(int count, int samplesEach)
    {
        var types = string.Join(", ", Enumerable.Range(0, count).Select(i =>
        {
            var samples = string.Join(", ", Enumerable.Repeat("{ \"phie\": 0.1 }", samplesEach));
            return string.Create(CultureInfo.InvariantCulture, $$"""{ "rt_tab_name": "RT{{i}}", "rt_phi_k_tab": {{i}}, "phi-k-synthesis-phi-k": [ {{samples}} ] }""");
        }));
        return Rows($$"""{ "phi-k-synthesis-rt": [ {{types}} ] }""");
    }

    private static DeliveryKey KeyOf(string id) => DeliveryKey.Derive("reservoir", [id]);

    /// <summary>One try of the record's pending work; a ledger makes it a try of that unit, resuming what earlier tries reported.</summary>
    private static DeliveryWork Work(
        JsonObject document, ShapeArtifactLedger? ledger, IPayloadSource? rows = null, bool metadata = true, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
    {
        var id = document["id"]!.GetValue<string>();
        return new DeliveryWork
        {
            Key = KeyOf(id),
            TargetId = id,
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = rows is not null,
            Payload = rows,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = ledger is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyDictionary<string, string>>(ledger.Completed, StringComparer.Ordinal),
            StepCompleted = ledger is null ? null : ledger.Listen,
            Unit = ledger?.Unit,
        };
    }

    private static UndoWork Undo(
        ShapeArtifactLedger ledger, string id = PhiKId, UndoReason reason = UndoReason.Failed, bool keepRecord = false, IReadOnlyDictionary<string, string>? state = null, long? committed = null)
        => ledger.Undo(KeyOf(id), id, reason, keepRecord, state, committed);

    private static async Task<IReadOnlyList<UndoResult>> UndoAsync(Rig rig, UndoWork work)
    {
        var results = await rig.Protocol.UndoAsync([work]);
        UndoAnswers.EachOnce([work], results);
        return results;
    }

    private static Func<HttpRequestMessage, bool> RowPost => ShapeCallHook.Call(HttpMethod.Post, Root);

    /// <summary>The <paramref name="n"/>th row posted through the hook, counted from 1.</summary>
    private static Func<HttpRequestMessage, bool> Post(int n) => ShapeCallHook.Nth(RowPost, n);

    private static string Delete(string table, long key) => string.Create(CultureInfo.InvariantCulture, $"DELETE {Root}{table}/{key}?catalog_entity_id={key}");

    private static string HeldUnder(string table, string parent) => $"GET {Root}{table}/header-entity/{parent}?header_entity_id={parent}";

    private static long[] Keys(FakeOsduPlatform platform, string table) => [.. platform.Rows(table).Keys];

    private static string NameOf(FakeOsduPlatform platform) => platform.Records[PhiKId]["data"]!["name"]!.GetValue<string>();

    private static long Stored(FakeOsduPlatform platform) => platform.Records[PhiKId]["version"]!.GetValue<long>();

    private static string V(long version) => version.ToString(CultureInfo.InvariantCulture);

    private const string Refused = """{ "detail": { "One or several attributes mandatory are NULL": {} } }""";

    [Fact]
    public async Task The_record_is_reported_after_its_write_and_before_its_first_row_and_each_block_names_its_rows()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var outcome = await rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("PUT " + Records, rig.Hook.Seen[0]);

        Assert.Equal(["metadata", "sync", "rows-begin", "rows-0", "rows-done"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal(1, rig.Ledger.Last("metadata").Calls);
        Assert.Equal(rig.Hook.Count, rig.Ledger.Last("rows-0").Calls);
        Assert.All(rig.Ledger.Reports.Where(r => r.Report.Step is "sync" or "rows-begin" or "rows-done"), r => Assert.Empty(r.Report.Artifacts));

        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Record, PhiKId, (long?)Stored(platform), (long?)null), (record.Role, record.TargetId, record.Version, record.PriorVersion));
        var rows = rig.Ledger["rows:0"];
        Assert.Equal(
            (ArtifactRoles.Rows, PhiKId, $"{Rt}=101;{PhiK}=102-103;{Rt}=104;{PhiK}=105", ArtifactStatus.Pending),
            (rows.Role, rows.TargetId, rows.Locator, rows.State));
        Assert.Equal(2, rig.Ledger.Rows.Count);
    }

    [Fact]
    public async Task A_record_without_rows_reports_nothing_since_its_one_write_is_the_whole_delivery()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var outcome = await rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Empty(Assert.Single(rig.Ledger.Reports).Report.Artifacts);
        Assert.Empty(rig.Ledger.Rows);

        // A rows file that holds no row is one write too.
        var empty = new ShapeArtifactLedger(Unit, () => rig.Hook.Count);
        var bare = await rig.Protocol.DeliverAsync(Work(Header("renamed"), empty, Rows("{}"), existing: outcome.TargetVersion));
        Assert.True(bare.Succeeded, bare.Failure?.Message);
        Assert.Equal(["metadata", "rows-done"], empty.Reports.Select(r => r.Report.Step));
        Assert.All(empty.Reports, r => Assert.Empty(r.Report.Artifacts));
        Assert.Empty(empty.Rows);
    }

    [Fact]
    public async Task A_record_whose_rows_wait_for_the_service_to_take_it_in_is_named_alone_and_its_undo_removes_it()
    {
        var platform = new FakeOsduPlatform { RmSearchServes = _ => false };
        using var rig = new Rig(platform);

        var waiting = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.StartsWith($"Search does not serve {PhiKId} yet", waiting.Message, StringComparison.Ordinal);
        Assert.Equal([TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Empty(platform.Rows(Rt));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results).Outcome);
        Assert.Contains(PhiKId, platform.Removed);
    }

    [Fact]
    public async Task A_refused_row_names_the_rows_posted_before_it_and_the_undo_deletes_them_last_first_then_the_record()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(4), (HttpStatusCode)422, Refused);

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Contains("refused rows.json: phi-k-synthesis-rt[1]", held.Message, StringComparison.Ordinal);

        // The rows posted since the last block are the unit's before the try fails; a refused row made nothing, so no intent.
        var (partial, calls) = rig.Ledger.Last(ReservoirManagementShape.RowsPartialStep);
        Assert.Equal(rig.Hook.Count, calls);
        Assert.Equal(("3", Reservoir), (partial.Returned["failedAt"], partial.Returned["parent"]));
        var block = Assert.Single(partial.Artifacts);
        Assert.Equal(("rows:0", ArtifactRoles.Rows, $"{Rt}=101;{PhiK}=102-103", ArtifactStatus.Pending), (block.Slot, block.Role, block.Locator, block.Status));
        Assert.Equal([TargetArtifact.RecordSlot, "rows:0"], rig.Ledger.Rows.Select(r => r.Slot));

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        Assert.Equal(
            (ArtifactStatus.Removed, $"rows {Rt}=101;{PhiK}=102-103 deleted (the service has no reversible delete)"),
            (UndoAnswers.Of(results, "rows:0").Outcome, UndoAnswers.Of(results, "rows:0").Note));
        Assert.Equal(
            (ArtifactStatus.Removed, $"{PhiKId}: removed from OSDU (reversible)"),
            (UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Note));
        Assert.Equal(
            [Delete(PhiK, 103), Delete(PhiK, 102), Delete(Rt, 101), $"GET {Records}/{PhiKId}", $"POST {Records}/{PhiKId}:delete"],
            rig.Hook.Since(before));
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
        Assert.Contains(PhiKId, platform.Removed);
    }

    [Fact]
    public async Task An_unanswered_post_records_an_intent_for_the_row_it_may_have_made_and_the_undo_finds_and_deletes_that_row()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(4);
        using var rig = new Rig(platform);

        var lost = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Equal(502, lost.StatusCode);
        Assert.Equal([101L, 104L], Keys(platform, Rt));

        // Reported right after the post that lost its answer, before anything else is asked: the rows it knows, and the intent.
        var (partial, calls) = rig.Ledger.Last(ReservoirManagementShape.RowsPartialStep);
        Assert.Equal(rig.Hook.Count, calls);
        Assert.StartsWith($"POST {Root}{Rt}", rig.Hook.Seen[calls - 1], StringComparison.Ordinal);
        Assert.Equal(["rows:0", "rows-pending:3"], partial.Artifacts.Select(a => a.Slot));
        var intent = rig.Ledger["rows-pending:3"];
        Assert.Equal((ArtifactRoles.Rows, ArtifactStatus.Intent, $"{Rt}|{PhiKId}", (string?)null), (intent.Role, intent.State, intent.Locator, intent.TargetId));

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Equal(
            (ArtifactStatus.Removed, $"row 104 of {Rt} under {PhiKId}, which the unanswered post made, deleted"),
            (UndoAnswers.Of(results, "rows-pending:3").Outcome, UndoAnswers.Of(results, "rows-pending:3").Note));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        // The row the unanswered post made was posted after every row of the block, so it goes first.
        Assert.Equal(
            [HeldUnder(Rt, PhiKId), Delete(Rt, 104), Delete(PhiK, 103), Delete(PhiK, 102), Delete(Rt, 101), $"GET {Records}/{PhiKId}", $"POST {Records}/{PhiKId}:delete"],
            rig.Hook.Since(before));
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task An_unanswered_post_of_a_row_below_a_posted_row_is_undone_before_that_row_so_every_row_goes_at_once()
    {
        // The second Phi-K row lands under the first rock type, and its answer is lost.
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(3);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Equal($"{Rt}=101;{PhiK}=102", rig.Ledger["rows:0"].Locator);
        Assert.Equal($"{PhiK}|101", rig.Ledger["rows-pending:2"].Locator);
        Assert.Equal([102L, 103L], Keys(platform, PhiK));

        var results = await UndoAsync(rig, Undo(rig.Ledger));

        // The row the unanswered post made was posted last, so it goes before the rows it belongs under.
        Assert.Empty(platform.Rows(PhiK));
        Assert.Empty(platform.Rows(Rt));
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
    }

    [Fact]
    public async Task An_unanswered_post_that_never_landed_is_gone_on_undo()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(4), HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Equal(ArtifactStatus.Intent, rig.Ledger["rows-pending:3"].State);
        Assert.Equal([101L], Keys(platform, Rt));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            (ArtifactStatus.Gone, $"no row of {Rt} under {PhiKId} is outside what the deliveries name: the post never landed"),
            (UndoAnswers.Of(results, "rows-pending:3").Outcome, UndoAnswers.Of(results, "rows-pending:3").Note));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Empty(platform.Rows(Rt));
    }

    [Fact]
    public async Task An_unanswered_post_among_rows_no_delivery_names_is_kept_and_nothing_is_deleted_for_it()
    {
        var platform = new FakeOsduPlatform();
        platform.Rows(Rt)[50] = new JsonObject
        {
            ["id_phi_k_synthesis_rt"] = 50,
            ["id_phi_k_synthesis"] = PhiKId,
            ["parent_object_id"] = Reservoir,
            ["rt_tab_name"] = "written elsewhere",
            ["rt_phi_k_tab"] = 50,
        };
        platform.RmLostPosts.Add(4);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            (ArtifactStatus.Kept, $"2 rows of {Rt} under {PhiKId} are named by no delivery (50, 104); which one the unanswered post made cannot be told, so none is deleted"),
            (UndoAnswers.Of(results, "rows-pending:3").Outcome, UndoAnswers.Of(results, "rows-pending:3").Note));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Equal([50L, 104L], Keys(platform, Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task An_answer_without_the_rows_key_counts_as_unanswered_and_the_row_it_made_is_found_and_deleted()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Rewrite(Post(4), HttpStatusCode.OK, "{}");

        var failed = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Contains("without the row's id_phi_k_synthesis_rt", failed.Message, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Intent, rig.Ledger["rows-pending:3"].State);

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows-pending:3").Outcome);
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task Blocks_posted_across_several_steps_are_all_deleted_in_the_reverse_of_their_posting()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(111), (HttpStatusCode)422, Refused);

        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, OneRockType(120))));
        Assert.Equal(["rows-0", "rows-1", ReservoirManagementShape.RowsPartialStep], rig.Ledger.Reports.Where(r => r.Report.Artifacts.Count > 0 && r.Report.Step != "metadata").Select(r => r.Report.Step));
        Assert.Equal(
            [$"{Rt}=101;{PhiK}=102-150", $"{PhiK}=151-200", $"{PhiK}=201-210"],
            new[] { "rows:0", "rows:1", "rows:2" }.Select(s => rig.Ledger[s].Locator));

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var deletes = rig.Hook.Since(before).Where(c => c.StartsWith("DELETE ", StringComparison.Ordinal)).ToList();
        Assert.Equal(Enumerable.Range(102, 109).Reverse().Select(k => Delete(PhiK, k)).Append(Delete(Rt, 101)), deletes);
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task A_block_whose_runs_do_not_fit_one_locator_is_split_into_parts_and_every_part_is_undone()
    {
        // Rock types and their Phi-K rows alternate, so no run of keys is longer than one.
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(51), (HttpStatusCode)422, Refused);

        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, RockTypes(26, 1))));
        var parts = rig.Ledger.Rows.Where(r => r.Slot.StartsWith("rows:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["rows:0", "rows:0:1"], parts.Select(p => p.Slot));
        Assert.All(parts, p => Assert.True(p.Locator!.Length <= 900, $"{p.Slot} names {p.Locator.Length} characters"));
        Assert.Equal(
            Enumerable.Range(101, 50).Select(k => (long)k),
            parts.SelectMany(p => p.Locator!.Split(';')).Select(run => long.Parse(run[(run.IndexOf('=', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture)));

        // The refused row is the first of the next block: nothing was posted since the last block, so nothing more is reported.
        Assert.DoesNotContain(rig.Ledger.Reports, r => r.Report.Step == ReservoirManagementShape.RowsPartialStep);

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal([ArtifactStatus.Removed, ArtifactStatus.Removed], results.Where(r => r.Item.Artifact.Role == ArtifactRoles.Rows).Select(r => r.Outcome));
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task An_unanswered_post_at_the_first_row_of_a_block_reports_its_intent_alone()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(51);
        using var rig = new Rig(platform);

        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, RockTypes(51, 0))));
        var (partial, _) = rig.Ledger.Last(ReservoirManagementShape.RowsPartialStep);
        Assert.Equal(["rows-pending:50"], partial.Artifacts.Select(a => a.Slot));
        Assert.Equal($"{Rt}=101-150", rig.Ledger["rows:0"].Locator);

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            (ArtifactStatus.Removed, $"row 151 of {Rt} under {PhiKId}, which the unanswered post made, deleted"),
            (UndoAnswers.Of(results, "rows-pending:50").Outcome, UndoAnswers.Of(results, "rows-pending:50").Note));
        Assert.Empty(platform.Rows(Rt));
    }

    [Fact]
    public async Task A_resumed_try_finds_the_row_an_unanswered_post_made_and_the_ledger_keeps_one_row_per_block()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(4);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));

        // The next try of the same unit finds the four rows by their values, and its first post of its own is refused.
        var calls = rig.Hook.Count;
        rig.Hook.Answer(Post(1), (HttpStatusCode)422, Refused);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        Assert.Equal(
            [HeldUnder(Rt, PhiKId), HeldUnder(PhiK, "101"), HeldUnder(PhiK, "104"), $"POST {Root}{PhiK}"],
            rig.Hook.Since(calls).Where(c => !c.Contains("/phi-k-synthesis/", StringComparison.Ordinal)));
        Assert.Equal([TargetArtifact.RecordSlot, "rows:0", "rows-pending:3"], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Equal($"{Rt}=101;{PhiK}=102-103;{Rt}=104", rig.Ledger["rows:0"].Locator);
        Assert.Equal(ArtifactStatus.Intent, rig.Ledger["rows-pending:3"].State);
        Assert.Equal(ArtifactRoles.Record, rig.Ledger[TargetArtifact.RecordSlot].Role);

        // The row the unanswered post made is the block's now: the intent finds nothing left outside what the unit names.
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Equal(ArtifactStatus.Gone, UndoAnswers.Of(results, "rows-pending:3").Outcome);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
    }

    [Fact]
    public async Task A_replacement_that_began_deleting_the_earlier_rows_is_kept_and_the_rest_of_the_unit_is_undone()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(Header(), null, Rows(TwoRockTypes)));
        Assert.True(first.Succeeded, first.Failure?.Message);

        // The new rows are posted and the earlier delivery's give way, the last first; the second delete fails.
        rig.Hook.Answer(ShapeCallHook.Nth(ShapeCallHook.Call(HttpMethod.Delete, Root), 2), HttpStatusCode.InternalServerError);
        var update = Work(Header("Phi-K 2"), rig.Ledger, Rows("""{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT9", "rt_phi_k_tab": 9 } ] }"""), existing: first.TargetVersion, state: first.Returned);
        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(update));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal(["metadata", "sync", "rows-begin", "rows-0", ReservoirManagementShape.RowsReplaceStep], rig.Ledger.Reports.Select(r => r.Report.Step));
        var (replace, at) = rig.Ledger.Last(ReservoirManagementShape.RowsReplaceStep);
        Assert.Equal(Delete(PhiK, 105), rig.Hook.Seen[at]);
        Assert.Equal(("5", "rows-replace", "5 earlier row(s) to delete"), (replace.Returned["earlier"], replace.Artifacts.Single().Slot, replace.Artifacts.Single().Locator));
        var written = Stored(platform);
        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Version, (long?)written, first.TargetVersion), (record.Role, record.Version, record.PriorVersion));
        Assert.Equal([101L, 104L, 106L], Keys(platform, Rt));
        Assert.Equal([102L, 103L], Keys(platform, PhiK));

        var results = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned, committed: first.TargetVersion));
        Assert.Equal(
            (ArtifactStatus.Removed, $"rows {Rt}=106 deleted (the service has no reversible delete)"),
            (UndoAnswers.Of(results, "rows:0").Outcome, UndoAnswers.Of(results, "rows:0").Note));
        Assert.Equal(
            (ArtifactStatus.Kept, "the delivery had posted every row and began deleting the earlier delivery's (5 earlier row(s) to delete); the ones it deleted cannot be put back, and the record's next delivery posts its rows again"),
            (UndoAnswers.Of(results, "rows-replace").Outcome, UndoAnswers.Of(results, "rows-replace").Note));
        Assert.Equal(
            (ArtifactStatus.Restored, $"{PhiKId}: version {V(first.TargetVersion!.Value)} written back as version {V(written + 1)}"),
            (UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Note));

        // The unit's row goes; the earlier delivery's rows the replacement had not reached stay, and nothing puts back the one it deleted.
        Assert.Equal([101L, 104L], Keys(platform, Rt));
        Assert.Equal([102L, 103L], Keys(platform, PhiK));
        Assert.Equal("Phi-K 1", NameOf(platform));
    }

    [Fact]
    public async Task Rows_posted_beside_a_record_the_unit_did_not_write_are_undone_and_the_earlier_rows_and_the_record_are_left()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await rig.Protocol.DeliverAsync(Work(Header(), null, Rows(TwoRockTypes)));
        Assert.True(first.Succeeded, first.Failure?.Message);
        var version = Stored(platform);

        // Two new rock types, the second's answer lost after it landed, under the parent the earlier rows share.
        platform.RmLostPosts.Add(7);
        var rows = Rows("""{ "phi-k-synthesis-rt": [ { "rt_tab_name": "RT8", "rt_phi_k_tab": 8 }, { "rt_tab_name": "RT9", "rt_phi_k_tab": 9 } ] }""");
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, rows, metadata: false, existing: first.TargetVersion, state: first.Returned)));
        Assert.Equal(["rows:0", "rows-pending:1"], rig.Ledger.Rows.Select(r => r.Slot));

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned, committed: first.TargetVersion));
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Equal(
            $"row 107 of {Rt} under {PhiKId}, which the unanswered post made, deleted",
            UndoAnswers.Of(results, "rows-pending:1").Note);
        Assert.Equal([HeldUnder(Rt, PhiKId), Delete(Rt, 107), Delete(Rt, 106)], rig.Hook.Since(before));
        Assert.Equal([101L, 104L], Keys(platform, Rt));
        Assert.Equal([102L, 103L, 105L], Keys(platform, PhiK));
        Assert.Equal(version, Stored(platform));
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_but_the_rows_the_unit_posted_are_still_deleted()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(4), (HttpStatusCode)422, Refused);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));

        var before = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Abandoned, keepRecord: true));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Equal(ArtifactStatus.Superseded, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.All(rig.Hook.Since(before), c => Assert.StartsWith("DELETE " + Root, c, StringComparison.Ordinal));
        Assert.Empty(platform.Rows(Rt));
        Assert.DoesNotContain(PhiKId, platform.Removed);
    }

    [Fact]
    public async Task A_second_undo_finds_everything_gone_and_never_throws()
    {
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(4);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        var work = Undo(rig.Ledger);
        await UndoAsync(rig, work);

        var again = await UndoAsync(rig, work);
        Assert.Equal(
            (ArtifactStatus.Gone, $"the service no longer holds the rows {Rt}=101;{PhiK}=102-103"),
            (UndoAnswers.Of(again, "rows:0").Outcome, UndoAnswers.Of(again, "rows:0").Note));
        Assert.Equal(ArtifactStatus.Gone, UndoAnswers.Of(again, "rows-pending:3").Outcome);
        Assert.Equal((ArtifactStatus.Gone, $"{PhiKId}: OSDU no longer holds the record"), (UndoAnswers.Of(again, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(again, TargetArtifact.RecordSlot).Note));
        Assert.Empty(platform.Rows(Rt));
    }

    [Fact]
    public async Task A_call_that_fails_answers_failed_for_its_own_artifacts_alone_and_the_next_undo_finishes()
    {
        // The service refuses a delete of a block's row: every block fails together, the unanswered post's row goes all the same,
        // and the record waits with the rows that hang from it.
        var platform = new FakeOsduPlatform();
        platform.RmLostPosts.Add(4);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Delete, PhiK + "/103"), HttpStatusCode.InternalServerError);

        var first = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(first, "rows:0").Outcome);
        Assert.Contains("HTTP 500", UndoAnswers.Of(first, "rows:0").Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(first, "rows-pending:3").Outcome);
        var waiting = UndoAnswers.Of(first, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Failed, waiting.Outcome);
        Assert.StartsWith("1 item(s) the delivery made beside the record could not be undone yet (", waiting.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(PhiKId, platform.Removed);
        Assert.Equal([101L], Keys(platform, Rt));
        rig.Ledger.Settle(first);

        var second = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            [("rows:0", ArtifactStatus.Removed), (TargetArtifact.RecordSlot, ArtifactStatus.Removed)],
            second.Select(r => (r.Item.Artifact.Slot, r.Outcome)));
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
        Assert.Contains(PhiKId, platform.Removed);

        // The rows under the unanswered post's parent cannot be read: that intent fails alone, the blocks go, and the record waits.
        var other = new FakeOsduPlatform();
        other.RmLostPosts.Add(4);
        using var reading = new Rig(other);
        await Assert.ThrowsAsync<OsduStatusException>(() => reading.Protocol.DeliverAsync(Work(Header(), reading.Ledger, Rows(TwoRockTypes))));
        reading.Hook.Answer(ShapeCallHook.Call(HttpMethod.Get, "/header-entity/"), HttpStatusCode.InternalServerError);
        var unread = await UndoAsync(reading, Undo(reading.Ledger));
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(unread, "rows-pending:3").Outcome);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(unread, "rows:0").Outcome);
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(unread, TargetArtifact.RecordSlot).Outcome);
        reading.Ledger.Settle(unread);
        var found = await UndoAsync(reading, Undo(reading.Ledger));
        Assert.Equal(
            [("rows-pending:3", ArtifactStatus.Removed), (TargetArtifact.RecordSlot, ArtifactStatus.Removed)],
            found.Select(r => (r.Item.Artifact.Slot, r.Outcome)));
        Assert.Empty(other.Rows(Rt));
        Assert.Contains(PhiKId, other.Removed);
    }

    [Fact]
    public async Task A_record_osdu_created_before_the_unit_began_is_given_back_its_earlier_version()
    {
        var platform = new FakeOsduPlatform();
        var theirs = platform.Put(Header("Another system"));
        using var rig = new Rig(platform);
        rig.Hook.Answer(Post(4), (HttpStatusCode)422, Refused);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header("Ours"), rig.Ledger, Rows(TwoRockTypes))));
        var ours = Stored(platform);
        Assert.Equal(ArtifactRoles.Record, rig.Ledger[TargetArtifact.RecordSlot].Role);
        platform.Records[PhiKId]["createTime"] = "2020-01-01T00:00:00.000Z";

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "rows:0").Outcome);
        Assert.Equal(
            (ArtifactStatus.Restored, $"{PhiKId}: version {V(theirs)} written back as version {V(ours + 1)}"),
            (UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Note));
        Assert.Equal("Another system", NameOf(platform));
        Assert.DoesNotContain(PhiKId, platform.Removed);
    }

    [Fact]
    public async Task An_undo_of_several_records_answers_every_artifact_once_and_keeps_what_the_shape_never_makes()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var other = new ShapeArtifactLedger(new DeliveryUnit(Guid.Parse("0192aa00-0000-7000-8000-0000000052a2"), Unit.StartedUtc), () => rig.Hook.Count);

        // Each record's second rock type is refused.
        var posts = 0;
        rig.Hook.Answer(r => RowPost(r) && Interlocked.Increment(ref posts) % 4 == 0, (HttpStatusCode)422, Refused, times: 2);
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header(), rig.Ledger, Rows(TwoRockTypes))));
        await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Header("Phi-K 2", SecondId), other, Rows(TwoRockTypes))));

        UndoItem Item(long id, TargetArtifact artifact) => new(id, artifact, Unit.Id, Unit.StartedUtc);
        var first = Undo(rig.Ledger);
        var works = new[]
        {
            first with
            {
                Items =
                [
                    .. first.Items,
                    Item(9201, TargetArtifact.Created("points:1", ArtifactRoles.Points, PhiKId, locator: "OIL=1")),
                    Item(9202, TargetArtifact.Intent("rows-pending:7", ArtifactRoles.Rows, "no separator")),
                    Item(9203, TargetArtifact.Intent("rows-pending:8", ArtifactRoles.Rows, "nope|101")),
                ],
            },
            Undo(other, SecondId),
        };
        var results = await rig.Protocol.UndoAsync(works);
        UndoAnswers.EachOnce(works, results);
        Assert.Equal(
            [
                (9201L, ArtifactStatus.Kept, "the Reservoir Management shape makes nothing of this kind beside a record"),
                (9202L, ArtifactStatus.Kept, "the Reservoir Management shape makes nothing of this kind beside a record"),
                (9203L, ArtifactStatus.Kept, "nope is not a table of the Reservoir Management DDMS this route knows, so its rows cannot be read"),
            ],
            results.Where(r => r.Item.ArtifactId is 9201 or 9202 or 9203).OrderBy(r => r.Item.ArtifactId).Select(r => (r.Item.ArtifactId, r.Outcome, r.Note)));
        Assert.All(results.Where(r => r.Item.ArtifactId is not (9201 or 9202 or 9203)), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Empty(platform.Rows(Rt));
        Assert.Empty(platform.Rows(PhiK));
        Assert.Contains(PhiKId, platform.Removed);
        Assert.Contains(SecondId, platform.Removed);
    }
}
