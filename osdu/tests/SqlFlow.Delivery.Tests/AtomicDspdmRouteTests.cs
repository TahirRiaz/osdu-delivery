using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dspdm route as a unit of work (docs/atomic-delivery-plan.md, The routes) against the fake DSPDM: an insert declared at
/// <c>save-begin</c>, before the save whose answer can be lost, with the business object's kind and the key that finds the row
/// again, and reported with the primary key DSPDM drew once the save settles or a later try adopts the row; updates and held
/// rows report nothing; and the undo after every failure point: the row deleted for good by its primary key or by the one
/// row its key finds, a key that finds none gone, one that finds several kept, a key that cannot be told kept, a refused or
/// unreachable call failed for exactly its items, never thrown, and a second undo that finds the row gone.
/// </summary>
public sealed class AtomicDspdmRouteTests
{
    private const string Kind = "acme:dspdm:well:1.0.0";
    private const string DailyKind = "acme:dspdm:well_vol_daily:1.0.0";
    private const string NoteKind = "acme:dspdm:site_note:1.0.0";
    private const string Save = "/api/dspdm/v1/save";
    private const string Common = "/api/dspdm/v1/common";

    private static readonly string[] Owned = ["OPERATOR", "UWI", "WELL_NAME"];

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, DspdmTarget? target = null)
        {
            Network = new FaultyNetwork(platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Network, allowLoopback: true);
            var client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Protocol = new OsduDspdmProtocol(client, new ProtocolOptions(), target ?? new DspdmTarget { Root = FakeOsduPlatform.DspdmRoot }, NullLogger<OsduDspdmProtocol>.Instance);
        }

        public FaultyNetwork Network { get; }

        public HttpRuntime Runtime { get; }

        public OsduDspdmProtocol Protocol { get; }

        public IDeliveryProtocol Delivery => Protocol;

        public void Dispose()
        {
            Runtime.Dispose();
            Network.Dispose();
        }
    }

    /// <summary>
    /// The network between the route and the fake platform, refusing the calls a test names before they reach the platform;
    /// every other call goes through as it is. A save whose answer is lost is the fake's own (<c>DspdmLostSaves</c>).
    /// </summary>
    private sealed class FaultyNetwork(FakeOsduPlatform platform) : DelegatingHandler(platform)
    {
        private readonly object _gate = new();
        private readonly List<(HttpMethod Method, string Path, HttpStatusCode Status)> _refused = [];

        /// <summary>The calls refused, as "METHOD path".</summary>
        public List<string> Refused { get; } = [];

        public void Refuse(HttpMethod method, string path, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
        {
            lock (_gate)
            {
                _refused.Add((method, path, status));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            lock (_gate)
            {
                foreach (var (method, refused, status) in _refused)
                {
                    if (method == request.Method && string.Equals(path, refused, StringComparison.Ordinal))
                    {
                        Refused.Add($"{request.Method} {path}");
                        return Task.FromResult(new HttpResponseMessage(status)
                        {
                            Content = new StringContent("<html><body>upstream unavailable; credential sig=leaked-secret</body></html>", Encoding.UTF8, "text/html"),
                        });
                    }
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// What the ledger keeps of one record's unit of work, emulated in memory: each step a route reports, by name, as a later
    /// try of the unit reads it back, and each artifact by its slot, a slot reported again updating its one row as the
    /// ledger's upsert does (SqlServerLedgerBulk.Artifacts.cs); a slot the route settled itself reopens when it reports it again.
    /// A unit that delivers leaves its intents due (<see cref="LeftDueByCommit"/>), which the sweep then undoes.
    /// </summary>
    private sealed class Ledger(Func<int> calls)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _steps = new(StringComparer.Ordinal);
        private long _ids;

        /// <summary>The ledger's artifact numbers are its own; a second record's ledger starts where a test says.</summary>
        public long FirstId { get; init; }

        public DeliveryUnit Unit { get; } = new(Guid.NewGuid(), DateTime.UtcNow);

        public List<(StepReport Report, int Calls)> Reports { get; } = [];

        public List<ArtifactRow> Rows { get; } = [];

        public DeliveryWork Track(DeliveryWork work)
        {
            lock (_gate)
            {
                return work with
                {
                    Unit = Unit,
                    CompletedSteps = new Dictionary<string, IReadOnlyDictionary<string, string>>(_steps, StringComparer.Ordinal),
                    StepCompleted = ReportAsync,
                };
            }
        }

        public ArtifactRow Row => Assert.Single(Rows);

        /// <summary>What a delivered unit leaves due: every intent whose call the delivery never learned the answer of.</summary>
        public IReadOnlyList<ArtifactRow> LeftDueByCommit => Rows.Where(r => r.Artifact.Status == ArtifactStatus.Intent).ToList();

        public UndoWork Undo(DeliveryWork work, UndoReason reason = UndoReason.Held, IEnumerable<ArtifactRow>? rows = null) => new()
        {
            Key = work.Key,
            TargetId = work.TargetId,
            TargetState = work.TargetState,
            CommittedVersion = work.ExistingVersion,
            Reason = reason,
            Items = (rows ?? Rows.Where(r => ArtifactStatuses.IsOpen(r.Artifact.Status))).Select(r => new UndoItem(r.Id, r.Artifact, Unit.Id, Unit.StartedUtc)).ToList(),
        };

        private Task ReportAsync(StepReport report, CancellationToken ct)
        {
            lock (_gate)
            {
                Reports.Add((report, calls()));
                _steps[report.Step] = report.Returned;
                foreach (var artifact in report.Artifacts)
                {
                    Upsert(artifact);
                }
            }

            return Task.CompletedTask;
        }

        private void Upsert(TargetArtifact artifact)
        {
            var row = Rows.FirstOrDefault(r => string.Equals(r.Artifact.Slot, artifact.Slot, StringComparison.Ordinal));
            if (row is null)
            {
                Rows.Add(new ArtifactRow(FirstId + ++_ids, artifact) { SettledByRoute = SettledByRoute(artifact) });
                return;
            }

            // A slot an undo settled never reopens; one the route settled itself (removed, kept, gone) does when it reports it again.
            if (row.Artifact.Status is not (ArtifactStatus.Intent or ArtifactStatus.Pending) && !row.SettledByRoute)
            {
                return;
            }

            var old = row.Artifact;
            var created = old.Role == ArtifactRoles.Record && artifact.Role == ArtifactRoles.Version;
            row.Artifact = new TargetArtifact
            {
                Slot = old.Slot,
                Role = created ? old.Role : artifact.Role,
                TargetId = artifact.TargetId ?? old.TargetId,
                Locator = artifact.Locator ?? old.Locator,
                Version = artifact.Version ?? old.Version,
                PriorVersion = created ? old.PriorVersion : old.PriorVersion ?? artifact.PriorVersion,
                Status = artifact.Status,
                Note = artifact.Note ?? old.Note,
            };
            row.SettledByRoute = SettledByRoute(artifact);
        }

        /// <summary>Whether the route reported <paramref name="artifact"/> settled itself, which a later report of its slot reopens.</summary>
        private static bool SettledByRoute(TargetArtifact artifact)
            => artifact.Status is ArtifactStatus.Removed or ArtifactStatus.Kept or ArtifactStatus.Gone;
    }

    /// <summary>One artifact row of the emulated ledger.</summary>
    private sealed class ArtifactRow(long id, TargetArtifact artifact)
    {
        public long Id { get; } = id;

        public TargetArtifact Artifact { get; set; } = artifact;

        /// <summary>Whether the route settled the artifact itself (the ledger's SettledBy 'route'), rather than an undo.</summary>
        public bool SettledByRoute { get; set; }
    }

    private static string TargetId(string key, string entity = "well") => $"dev:{entity}:{DeliveryKey.Derive("acme", [key]).Value:N}";

    private static JsonObject Well(string uwi, string name = "Alpha 1", string? key = null) => new()
    {
        ["id"] = TargetId(key ?? uwi.Trim()),
        ["kind"] = Kind,
        [DspdmKinds.OwnedProperty] = new JsonArray(Owned.Select(o => (JsonNode?)o).ToArray()),
        ["data"] = new JsonObject { ["UWI"] = uwi, ["WELL_NAME"] = name, ["OPERATOR"] = "Acme" },
    };

    private static DeliveryWork Work(JsonObject document, IReadOnlyDictionary<string, string>? state = null, long? existing = null) => new()
    {
        Key = DeliveryKey.Derive("acme", [document["id"]!.GetValue<string>()]),
        TargetId = document["id"]!.GetValue<string>(),
        Document = document,
        DeliverMetadata = true,
        DeliverPayload = false,
        ExistingVersion = existing,
        TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

    private static int SaveIndex(FakeOsduPlatform platform, int nth = 1)
    {
        var saves = platform.Calls.Select((c, i) => (Call: c, Index: i)).Where(c => c.Call.Method == HttpMethod.Post && c.Call.Uri.AbsolutePath == Save).ToList();
        Assert.True(saves.Count >= nth, $"the platform took {saves.Count} save(s), not {nth}");
        return saves[nth - 1].Index;
    }

    /// <summary>The locator an insert's intent carries, as the kind and the key's values.</summary>
    private static (string Kind, JsonObject Key) Locator(TargetArtifact artifact)
    {
        var locator = JsonNode.Parse(artifact.Locator!)!.AsObject();
        return (locator["kind"]!.GetValue<string>(), locator["key"]!.AsObject());
    }

    private static UndoItem Item(long id, TargetArtifact artifact) => new(id, artifact, Guid.NewGuid(), DateTime.UtcNow);

    private static UndoWork Undo(params UndoItem[] items) => new()
    {
        Key = DeliveryKey.Derive("acme", ["hand-made"]),
        TargetId = TargetId("hand-made"),
        Reason = UndoReason.Failed,
        Items = items,
    };

    private static void AssertEachOnce(IReadOnlyList<UndoWork> works, IReadOnlyList<UndoResult> results)
        => Assert.Equal(works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(), results.Select(r => r.Item.ArtifactId).Order());

    [Fact]
    public async Task An_insert_is_declared_at_save_begin_before_the_save_and_reported_with_its_primary_key_after()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);

        var outcome = await rig.Delivery.DeliverAsync(ledger.Track(Work(Well(" A-1 "))));

        Assert.Equal("1001", outcome.Returned[OsduDspdmProtocol.IdValue]);

        // The intent goes with the save-begin mark, before the save: the kind and the key as the find sent it, trimmed.
        var begun = Assert.Single(ledger.Reports, r => r.Report.Step == OsduDspdmProtocol.SaveBeginStep);
        var intent = Assert.Single(begun.Report.Artifacts);
        Assert.Equal((OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, ArtifactStatus.Intent), (intent.Slot, intent.Role, intent.Status));
        Assert.Null(intent.TargetId);
        var (kind, key) = Locator(intent);
        Assert.Equal(Kind, kind);
        Assert.Equal("""{"UWI":"A-1"}""", key.ToJsonString());
        Assert.True(begun.Calls <= SaveIndex(platform), "the insert's intent was reported after its save was sent");
        Assert.Equal("insert", begun.Report.Returned[OsduDspdmProtocol.OperationValue]);

        // Once the save settles, the row is reported under the same slot with the primary key DSPDM drew.
        var saved = Assert.Single(ledger.Reports, r => r.Report.Step == OsduDspdmProtocol.SaveStep);
        var row = Assert.Single(saved.Report.Artifacts);
        Assert.True(saved.Calls > SaveIndex(platform));
        Assert.Equal((OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, ArtifactStatus.Pending, "1001", intent.Locator), (row.Slot, row.Role, row.Status, row.TargetId, row.Locator));
        Assert.Equal((ArtifactStatus.Pending, "1001", intent.Locator), (ledger.Row.Artifact.Status, ledger.Row.Artifact.TargetId, ledger.Row.Artifact.Locator));
        Assert.Empty(ledger.LeftDueByCommit);
    }

    [Fact]
    public async Task An_update_and_an_unchanged_save_report_no_artifact()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var state = new Dictionary<string, string>(first.Returned, StringComparer.Ordinal);

        var updates = new Ledger(() => platform.Calls.Count);
        var updated = await rig.Delivery.DeliverAsync(updates.Track(Work(Well("A-1", name: "Alpha One"), state, first.TargetVersion)));
        var unchanged = new Ledger(() => platform.Calls.Count);
        var same = await rig.Delivery.DeliverAsync(unchanged.Track(Work(Well("A-1", name: "Alpha One"), state, updated.TargetVersion)));

        Assert.Equal(OsduDspdmProtocol.Updated, updated.Returned[OsduDspdmProtocol.OperationValue]);
        Assert.Equal(OsduDspdmProtocol.Unchanged, same.Returned[OsduDspdmProtocol.OperationValue]);

        // DSPDM keeps no versions, so an update leaves nothing to undo and declares nothing.
        foreach (var ledger in new[] { updates, unchanged })
        {
            Assert.Equal("update", Assert.Single(ledger.Reports, r => r.Report.Step == OsduDspdmProtocol.SaveBeginStep).Report.Returned[OsduDspdmProtocol.OperationValue]);
            Assert.All(ledger.Reports, r => Assert.Empty(r.Report.Artifacts));
            Assert.Empty(ledger.Rows);
        }
    }

    [Fact]
    public async Task A_held_row_reports_nothing()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        well.Rows[500] = new JsonObject { ["WELL_ID"] = 500L, ["UWI"] = "A-1", ["OPERATOR"] = "Legacy", ["ROW_CREATED_DATE"] = "2020-01-01T00:00:00.000" };
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);

        var held = (await rig.Delivery.DeliverBatchAsync([ledger.Track(Work(Well("A-1")))]))[0];

        Assert.IsType<RecordHeldException>(held.Failure);
        Assert.Empty(ledger.Reports);
        Assert.Empty(ledger.Rows);
        Assert.Equal(0, platform.DspdmSaves);
    }

    [Fact]
    public async Task An_insert_whose_answer_was_lost_leaves_its_intent_and_the_undo_finds_the_row_by_its_key_and_deletes_it_for_good()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var work = ledger.Track(Work(Well("A-1")));

        var lost = (await rig.Delivery.DeliverBatchAsync([work]))[0];

        Assert.Equal(502, Assert.IsType<OsduStatusException>(lost.Failure).StatusCode);
        Assert.Equal([1001L], platform.DspdmRows("WELL").Keys);
        Assert.Equal(ArtifactStatus.Intent, ledger.Row.Artifact.Status);
        Assert.Null(ledger.Row.Artifact.TargetId);

        // The undo runs in a later process (the sweep), which reads the business object's metadata afresh.
        using var sweep = new Rig(platform);
        var undo = ledger.Undo(work);
        var from = platform.Calls.Count;
        var results = await sweep.Protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var removed = Assert.Single(results);
        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Equal("row 1001 of WELL deleted from DSPDM, for good (DSPDM keeps no deleted rows)", removed.Note);
        Assert.Empty(platform.DspdmRows("WELL"));
        Assert.Equal("DELETE /api/dspdm/v1/delete/WELL/1001", Sent(platform, from)[^1]);
        var lookup = JsonNode.Parse(platform.Calls[^2].Body!)!;
        Assert.Equal("EQUALS", lookup["criteriaFilters"]![0]!["operator"]!.GetValue<string>());
        Assert.Equal("A-1", lookup["criteriaFilters"]![0]!["values"]![0]!.GetValue<string>());

        // A second undo of the same intent finds no row with its key: gone, never a second delete.
        var again = Assert.Single(await sweep.Protocol.UndoAsync([undo]));
        Assert.Equal(ArtifactStatus.Gone, again.Outcome);
        Assert.Equal("no row of WELL has the key the insert was sent with: the insert did not land", again.Note);
        Assert.Single(platform.Calls.Skip(from), c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_resumed_try_adopts_the_row_a_lost_insert_made_under_the_same_slot_and_the_undo_deletes_it_by_its_primary_key()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        Assert.False((await rig.Delivery.DeliverBatchAsync([ledger.Track(Work(Well("A-1")))]))[0].Succeeded);
        var locator = ledger.Row.Artifact.Locator;

        var work = ledger.Track(Work(Well("A-1")));
        var resumed = await rig.Delivery.DeliverAsync(work);

        Assert.Equal("1001", resumed.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Single(platform.DspdmRows("WELL"));

        // The resumed try takes the row over as the unit's insert: its save-begin says so and names the row by its key again,
        // and the settled save names it by the primary key DSPDM gave it.
        var resumedBegin = ledger.Reports.Last(r => r.Report.Step == OsduDspdmProtocol.SaveBeginStep).Report;
        Assert.Equal("insert", resumedBegin.Returned[OsduDspdmProtocol.OperationValue]);
        var redeclared = Assert.Single(resumedBegin.Artifacts);
        Assert.Equal((OsduDspdmProtocol.RowSlot, ArtifactStatus.Intent, locator), (redeclared.Slot, redeclared.Status, redeclared.Locator));
        Assert.Null(redeclared.TargetId);
        Assert.Single(ledger.Rows);
        var adopted = Assert.Single(ledger.Reports.Last(r => r.Report.Step == OsduDspdmProtocol.SaveStep).Report.Artifacts);
        Assert.Equal((OsduDspdmProtocol.RowSlot, "1001", ArtifactStatus.Pending), (adopted.Slot, adopted.TargetId, adopted.Status));
        Assert.Equal((ArtifactStatus.Pending, "1001", locator), (ledger.Row.Artifact.Status, ledger.Row.Artifact.TargetId, ledger.Row.Artifact.Locator));

        // Held afterwards (a later step of the unit), the row is deleted by the key DSPDM gave it, without a lookup by key.
        var from = platform.Calls.Count;
        var results = await rig.Protocol.UndoAsync([ledger.Undo(work)]);

        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results).Outcome);
        Assert.Equal(["DELETE /api/dspdm/v1/delete/WELL/1001"], Sent(platform, from));
        Assert.Empty(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task A_row_adopted_after_two_lost_answers_is_still_named_by_the_unit_when_its_delivery_completes()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        platform.DspdmLostSaves.Add(2);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);

        // The insert lands and its answer is lost; the next try adopts the row, and the answer to its save is lost too.
        Assert.False((await rig.Delivery.DeliverBatchAsync([ledger.Track(Work(Well("A-1")))]))[0].Succeeded);
        Assert.False((await rig.Delivery.DeliverBatchAsync([ledger.Track(Work(Well("A-1")))]))[0].Succeeded);
        var work = ledger.Track(Work(Well("A-1")));
        var delivered = await rig.Delivery.DeliverAsync(work);

        Assert.True(delivered.Succeeded, delivered.Failure?.Message);
        Assert.Equal("1001", delivered.Returned[OsduDspdmProtocol.IdValue]);

        // The row is the unit's: the ledger names it by its primary key, so the delivered unit leaves no intent due behind.
        Assert.Equal((ArtifactStatus.Pending, "1001"), (ledger.Row.Artifact.Status, ledger.Row.Artifact.TargetId));
        Assert.Empty(ledger.LeftDueByCommit);

        // Whatever the ledger left due, the sweep that undoes it must not delete the row the record was delivered as.
        if (ledger.LeftDueByCommit.Count > 0)
        {
            await rig.Protocol.UndoAsync([ledger.Undo(work, UndoReason.Abandoned, ledger.LeftDueByCommit)]);
        }

        Assert.Equal([1001L], platform.DspdmRows("WELL").Keys);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("broken")]
    public async Task An_insert_that_committed_nothing_is_gone_on_undo(string failure)
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        if (failure == "refused")
        {
            platform.DspdmRefuses = (attribute, value) => attribute == "WELL_NAME" && value?.GetValue<string>() == "Bad" ? "Invalid value for WELL_NAME" : null;
        }
        else
        {
            platform.DspdmBrokenSaves.Add(1);
        }

        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var work = ledger.Track(Work(Well("A-1", name: failure == "refused" ? "Bad" : "Alpha 1")));

        var outcome = (await rig.Delivery.DeliverBatchAsync([work]))[0];

        Assert.False(outcome.Succeeded);
        Assert.Empty(platform.DspdmRows("WELL"));
        Assert.Equal(ArtifactStatus.Intent, ledger.Row.Artifact.Status);

        var gone = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
        Assert.Contains("the insert did not land", gone.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Two_rows_with_the_recorded_key_are_kept_and_neither_is_deleted()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var work = ledger.Track(Work(Well("A-1")));
        Assert.False((await rig.Delivery.DeliverBatchAsync([work]))[0].Succeeded);

        // Another system wrote a row with the same key since (DSPDM's unique constraint was dropped meanwhile).
        well.Rows[2000] = new JsonObject { ["WELL_ID"] = 2000L, ["UWI"] = "A-1", ["OPERATOR"] = "Other", ["ROW_CREATED_DATE"] = "2026-03-02T00:00:00.000" };

        var kept = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));

        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.Equal("2 rows of WELL have the key the insert was sent with, so which one it made cannot be told; none was deleted", kept.Note);
        Assert.Equal([1001L, 2000L], well.Rows.Keys);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_recorded_key_that_cannot_find_the_row_is_kept_saying_why()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmDailyVolumes();
        using var rig = new Rig(platform);
        var work = Undo(
            Item(1, TargetArtifact.Intent(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, """{"kind":"acme:dspdm:well_vol_daily:1.0.0","key":{"UWI":"A-1"}}""")),
            Item(2, new TargetArtifact { Slot = OsduDspdmProtocol.RowSlot, Role = ArtifactRoles.Rows, Status = ArtifactStatus.Intent }),
            Item(3, TargetArtifact.Intent(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, "not json")),
            Item(4, TargetArtifact.Intent(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, """{"kind":"","key":{"UWI":"A-1"}}""")));

        var results = await rig.Protocol.UndoAsync([work]);

        AssertEachOnce([work], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Kept, r.Outcome));
        Assert.Equal(
            "the key the insert recorded has no VOLUME_DATE, which the rows of WELL VOL DAILY are found by now, so its row cannot be told",
            Assert.Single(results, r => r.Item.ArtifactId == 1).Note);
        Assert.All(results.Where(r => r.Item.ArtifactId != 1), r => Assert.Contains("nor the business object and key to find it by", r.Note, StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_row_whose_key_is_too_long_to_record_is_still_delivered_and_reported_by_its_primary_key()
    {
        var platform = new FakeOsduPlatform();
        var notes = new FakeDspdmObject("SITE NOTE", "site_note")
        {
            Attributes =
            {
                new("SITE_NOTE_ID", "integer", PrimaryKey: true, Mandatory: true),
                new("TITLE", "character varying(1500)", Mandatory: true),
                new("ROW_CREATED_DATE", "timestamp without time zone"),
                new("ROW_CHANGED_DATE", "timestamp without time zone"),
            },
        };
        notes.Constraints["UK_SITE_NOTE_TITLE"] = ["TITLE"];
        platform.DspdmObjects[notes.Name] = notes;
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var title = new string('t', 1100);
        var document = new JsonObject
        {
            ["id"] = TargetId("long-note", "site_note"),
            ["kind"] = NoteKind,
            [DspdmKinds.OwnedProperty] = new JsonArray("TITLE"),
            ["data"] = new JsonObject { ["TITLE"] = title },
        };
        var work = ledger.Track(Work(document));

        // The key does not fit what the ledger keeps of a locator, so the insert cannot be found by it if its answer is lost;
        // the row is still saved, and named by its primary key once the save answers. Another record of the same delivery, a
        // row of another business object, keeps its own outcome.
        var outcomes = await rig.Delivery.DeliverBatchAsync([work, Work(Well("A-1"))]);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        var outcome = outcomes[0];
        Assert.Equal("1001", outcome.Returned[OsduDspdmProtocol.IdValue]);
        Assert.Empty(Assert.Single(ledger.Reports, r => r.Report.Step == OsduDspdmProtocol.SaveBeginStep).Report.Artifacts);
        Assert.Equal([1001L], notes.Rows.Keys);
        Assert.Equal([1001L], platform.DspdmRows("WELL").Keys);
        var row = ledger.Row.Artifact;
        Assert.Equal((ArtifactRoles.Rows, ArtifactStatus.Pending, "1001"), (row.Role, row.Status, row.TargetId));
        Assert.Empty(ledger.LeftDueByCommit);

        // Known by its primary key alone, the row is deleted from the business object the record's id names.
        var removed = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Equal("row 1001 of SITE NOTE deleted from DSPDM, for good (DSPDM keeps no deleted rows)", removed.Note);
        Assert.Empty(notes.Rows);
        Assert.Equal([1001L], platform.DspdmRows("WELL").Keys);
    }

    [Fact]
    public async Task A_batch_whose_save_was_lost_names_each_row_by_its_own_key_and_one_undo_deletes_them_all()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var a = new Ledger(() => platform.Calls.Count);
        var b = new Ledger(() => platform.Calls.Count) { FirstId = 100 };
        var works = new[] { a.Track(Work(Well("A-1"))), b.Track(Work(Well("B-2"))) };

        var lost = await rig.Delivery.DeliverBatchAsync(works);

        Assert.All(lost, o => Assert.IsType<OsduStatusException>(o.Failure));
        Assert.Equal(1, platform.DspdmSaves);
        Assert.Equal("A-1", Locator(a.Row.Artifact).Key["UWI"]!.GetValue<string>());
        Assert.Equal("B-2", Locator(b.Row.Artifact).Key["UWI"]!.GetValue<string>());
        Assert.All(new[] { a, b }, l => Assert.True(Assert.Single(l.Reports, r => r.Report.Step == OsduDspdmProtocol.SaveBeginStep).Calls <= SaveIndex(platform)));

        UndoWork[] undos = [a.Undo(works[0]), b.Undo(works[1])];
        var results = await rig.Protocol.UndoAsync(undos);

        AssertEachOnce(undos, results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Equal("row 1001 of WELL deleted from DSPDM, for good (DSPDM keeps no deleted rows)", Assert.Single(results, r => r.Item.UnitId == a.Unit.Id).Note);
        Assert.Equal("row 1002 of WELL deleted from DSPDM, for good (DSPDM keeps no deleted rows)", Assert.Single(results, r => r.Item.UnitId == b.Unit.Id).Note);
        Assert.Empty(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task A_batch_whose_save_is_refused_for_one_row_leaves_only_that_rows_intent_open()
    {
        var platform = new FakeOsduPlatform { DspdmRefuses = (attribute, value) => attribute == "WELL_NAME" && value?.GetValue<string>() == "Bad" ? "Invalid value for WELL_NAME" : null };
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var good = new Ledger(() => platform.Calls.Count);
        var bad = new Ledger(() => platform.Calls.Count) { FirstId = 100 };
        var works = new[] { good.Track(Work(Well("A-1"))), bad.Track(Work(Well("B-2", name: "Bad"))) };

        var outcomes = await rig.Delivery.DeliverBatchAsync(works);

        // The refused call is sent again one row at a time: the good row is inserted and named, the bad one held with its intent.
        Assert.True(outcomes[0].Succeeded, outcomes[0].Failure?.Message);
        Assert.IsType<RecordHeldException>(outcomes[1].Failure);
        var id = outcomes[0].Returned[OsduDspdmProtocol.IdValue];
        Assert.Equal((ArtifactStatus.Pending, id), (good.Row.Artifact.Status, good.Row.Artifact.TargetId));
        Assert.Equal(ArtifactStatus.Intent, bad.Row.Artifact.Status);
        Assert.Empty(good.LeftDueByCommit);

        var gone = Assert.Single(await rig.Protocol.UndoAsync([bad.Undo(works[1])]));
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
        Assert.Equal([long.Parse(id, CultureInfo.InvariantCulture)], platform.DspdmRows("WELL").Keys);
    }

    [Fact]
    public async Task A_delete_DSPDM_refuses_fails_that_row_alone_and_the_undo_never_throws()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var first = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var second = await rig.Delivery.DeliverAsync(Work(Well("B-2")));
        rig.Network.Refuse(HttpMethod.Delete, "/api/dspdm/v1/delete/WELL/1001");
        var work = Undo(
            Item(1, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, first.Returned[OsduDspdmProtocol.IdValue], locator: """{"kind":"acme:dspdm:well:1.0.0","key":{"UWI":"A-1"}}""")),
            Item(2, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, second.Returned[OsduDspdmProtocol.IdValue], locator: """{"kind":"acme:dspdm:well:1.0.0","key":{"UWI":"B-2"}}""")));

        var results = await rig.Protocol.UndoAsync([work]);

        AssertEachOnce([work], results);
        var failed = Assert.Single(results, r => r.Item.ArtifactId == 1);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains("503", failed.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-secret", failed.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.ArtifactId == 2).Outcome);
        Assert.Equal([1001L], platform.DspdmRows("WELL").Keys);
    }

    [Fact]
    public async Task A_lookup_that_fails_fails_only_the_intents_that_needed_it()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(2);
        using var rig = new Rig(platform);
        var known = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var ledger = new Ledger(() => platform.Calls.Count) { FirstId = 10 };
        var lostWork = ledger.Track(Work(Well("B-2")));
        Assert.False((await rig.Delivery.DeliverBatchAsync([lostWork]))[0].Succeeded);

        // The metadata is read already; the reads of rows by key are refused.
        rig.Network.Refuse(HttpMethod.Post, Common);
        UndoWork[] works =
        [
            Undo(Item(1, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, known.Returned[OsduDspdmProtocol.IdValue], locator: """{"kind":"acme:dspdm:well:1.0.0","key":{"UWI":"A-1"}}"""))),
            ledger.Undo(lostWork),
        ];
        var results = await rig.Protocol.UndoAsync(works);

        AssertEachOnce(works, results);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.ArtifactId == 1).Outcome);
        var failed = Assert.Single(results, r => r.Item.ArtifactId == ledger.Row.Id);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains("503", failed.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-secret", failed.Note, StringComparison.Ordinal);
        Assert.Equal([1002L], platform.DspdmRows("WELL").Keys);
    }

    [Fact]
    public async Task A_row_deleted_since_is_gone_and_artifacts_of_other_roles_are_kept()
    {
        var platform = new FakeOsduPlatform();
        var well = platform.DspdmWell();
        using var rig = new Rig(platform);
        var delivered = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        well.Rows.Clear();
        var work = Undo(
            Item(1, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, delivered.Returned[OsduDspdmProtocol.IdValue], locator: """{"kind":"acme:dspdm:well:1.0.0","key":{"UWI":"A-1"}}""")),
            Item(2, TargetArtifact.Created("dataset:0", ArtifactRoles.Dataset, "dev:dataset--File.Generic:x")),
            Item(3, TargetArtifact.RecordWritten(TargetId("A-1"), 5, null)));

        var results = await rig.Protocol.UndoAsync([work]);

        AssertEachOnce([work], results);
        var gone = Assert.Single(results, r => r.Item.ArtifactId == 1);
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
        Assert.Equal("row 1001 of WELL not found in DSPDM", gone.Note);
        Assert.All(results.Where(r => r.Item.ArtifactId != 1), r => Assert.Equal((ArtifactStatus.Kept, "the dspdm route makes nothing of this kind"), (r.Outcome, r.Note)));
    }

    [Fact]
    public async Task A_row_known_only_by_its_primary_key_is_deleted_from_the_business_object_its_record_id_names()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        using var rig = new Rig(platform);
        var delivered = await rig.Delivery.DeliverAsync(Work(Well("A-1")));
        var id = delivered.Returned[OsduDspdmProtocol.IdValue];

        // A record id that names no entity cannot tell the business object: the row is left, and nothing is deleted.
        var nameless = new UndoWork
        {
            Key = DeliveryKey.Derive("acme", ["nameless"]),
            TargetId = "nameless",
            Reason = UndoReason.Failed,
            Items = [Item(1, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, id))],
        };
        var kept = Assert.Single(await rig.Protocol.UndoAsync([nameless]));
        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Delete);

        var known = Undo(Item(2, TargetArtifact.Created(OsduDspdmProtocol.RowSlot, ArtifactRoles.Rows, id)));
        var removed = Assert.Single(await rig.Protocol.UndoAsync([known]));

        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"row {id} of WELL deleted from DSPDM, for good (DSPDM keeps no deleted rows)"), removed.Note);
        Assert.Empty(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task An_undo_whose_business_object_cannot_be_read_fails_every_row_item_and_a_later_undo_takes_them()
    {
        var platform = new FakeOsduPlatform();
        platform.DspdmWell();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var work = ledger.Track(Work(Well("A-1")));
        Assert.False((await rig.Delivery.DeliverBatchAsync([work]))[0].Succeeded);

        // A sweep in another process cannot read DSPDM's metadata.
        using var sweep = new Rig(platform);
        sweep.Network.Refuse(HttpMethod.Post, Common, HttpStatusCode.BadGateway);
        var undo = ledger.Undo(work);
        var failed = Assert.Single(await sweep.Protocol.UndoAsync([undo]));

        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains("502", failed.Note, StringComparison.Ordinal);
        Assert.Single(platform.DspdmRows("WELL"));

        using var later = new Rig(platform);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(await later.Protocol.UndoAsync([undo])).Outcome);
        Assert.Empty(platform.DspdmRows("WELL"));
    }

    [Fact]
    public async Task A_key_of_two_attributes_finds_the_row_its_lost_insert_made()
    {
        var platform = new FakeOsduPlatform();
        var daily = platform.DspdmDailyVolumes();
        platform.DspdmLostSaves.Add(1);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var document = new JsonObject
        {
            ["id"] = TargetId("A-1 2024-05-01", "well_vol_daily"),
            ["kind"] = DailyKind,
            [DspdmKinds.OwnedProperty] = new JsonArray("OIL_VOLUME", "UWI", "VOLUME_DATE"),
            ["data"] = new JsonObject { ["UWI"] = "A-1", ["VOLUME_DATE"] = "2024-05-01", ["OIL_VOLUME"] = 10.5 },
        };
        var work = ledger.Track(Work(document));

        Assert.False((await rig.Delivery.DeliverBatchAsync([work]))[0].Succeeded);
        var (kind, key) = Locator(ledger.Row.Artifact);
        Assert.Equal(DailyKind, kind);
        Assert.Equal("A-1", key["UWI"]!.GetValue<string>());
        Assert.Equal("2024-05-01", key["VOLUME_DATE"]!.GetValue<string>());

        // A row of another day of the same well is no match for the key.
        daily.Rows[7] = new JsonObject { ["WELL_VOL_DAILY_ID"] = 7L, ["UWI"] = "A-1", ["VOLUME_DATE"] = "2024-05-02", ["ROW_CREATED_DATE"] = "2024-05-03T00:00:00.000" };
        var removed = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));

        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"row 1001 of WELL VOL DAILY deleted from DSPDM, for good (DSPDM keeps no deleted rows)"), removed.Note);
        Assert.Equal([7L], daily.Rows.Keys);
    }
}
