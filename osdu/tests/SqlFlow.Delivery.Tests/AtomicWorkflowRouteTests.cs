using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The workflow route as a unit of work (docs/atomic-delivery-plan.md, The routes) against the fake platform, with a network
/// between them that loses the answer of a call that landed or refuses a call before it lands: each input registration and
/// the anchor's write declared as an intent before the call whose answer can be lost, each run named before its trigger, the
/// records the results found reported as outputs, and the undo after every failure point: nothing taken back while a run is
/// going or cannot be asked about, runs kept with how they ended, outputs removed only when the route removes them and OSDU
/// created them after the unit began, the anchor and inputs removed or given back their earlier versions, every item
/// answered once, and a second undo that finds everything gone.
/// </summary>
public sealed class AtomicWorkflowRouteTests
{
    private const string Partition = "dev";
    private const string Anchor = "dev:master-data--ConnectedSourceRegistryEntry:csre-atomic";
    private const string AnchorKind = "osdu:wks:master-data--ConnectedSourceRegistryEntry:1.0.0";
    private const string DatasetAnchor = "dev:dataset--File.Generic:wells-atomic";
    private const string DatasetKind = "osdu:wks:dataset--File.Generic:1.0.0";
    private const string Workflow = "eds_scheduler";
    private const string WellKind = "osdu:wks:master-data--Well:1.0.0";
    private const string Output1 = "dev:master-data--Well:atomic-out-1";
    private const string Output2 = "dev:master-data--Well:atomic-out-2";
    private const string Output3 = "dev:master-data--Well:atomic-out-3";
    private const string Trigger = "/api/workflow/v1/workflow/" + Workflow + "/workflowRun";
    private const string RecordWrite = "/api/storage/v2/records";
    private const string Register = "/api/dataset/v1/registerDataset";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform)
        {
            Platform = platform;
            Network = new FaultyNetwork(platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Network, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
        }

        public FakeOsduPlatform Platform { get; }

        public FaultyNetwork Network { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public OsduWorkflowProtocol Protocol(WorkflowRoute route) => new(
            Client,
            new ProtocolOptions
            {
                WorkflowPollSeconds = 1,
                DatasetIndexWaitSeconds = 0,
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            },
            route,
            Samples.Logger<OsduWorkflowProtocol>(),
            Secrets);

        public void Dispose()
        {
            Runtime.Dispose();
            Network.Dispose();
        }
    }

    /// <summary>
    /// The network between the route and the fake platform, failing the calls a test names: a call whose answer is lost after
    /// it landed (forwarded to the platform, then answered 502 as a gateway answers when the service behind it went quiet),
    /// and a call refused before it reaches the platform. Every other call goes through as it is.
    /// </summary>
    private sealed class FaultyNetwork(FakeOsduPlatform platform) : DelegatingHandler(platform)
    {
        private readonly object _gate = new();
        private readonly List<Fault> _faults = [];

        /// <summary>The calls refused before they reached the platform, as "METHOD path".</summary>
        public List<string> Refused { get; } = [];

        /// <summary>Loses the answer of the next <paramref name="times"/> calls to <paramref name="path"/>; a path ending in * is a prefix.</summary>
        public void LoseAnswer(HttpMethod method, string path, int times = 1) => Add(new Fault(method, path, Lose: true, HttpStatusCode.BadGateway) { Remaining = times });

        /// <summary>Refuses calls to <paramref name="path"/> with <paramref name="status"/> before they reach the platform.</summary>
        public void Refuse(HttpMethod method, string path, HttpStatusCode status = HttpStatusCode.ServiceUnavailable, int times = int.MaxValue)
            => Add(new Fault(method, path, Lose: false, status) { Remaining = times });

        /// <summary>Lets every call through again.</summary>
        public void Heal()
        {
            lock (_gate)
            {
                _faults.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var fault = Take(request.Method, path);
            if (fault is { Lose: false })
            {
                lock (_gate)
                {
                    Refused.Add($"{request.Method} {path}");
                }

                return Answer(fault.Status, "the call was refused before it reached the service");
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (fault is null)
            {
                return response;
            }

            response.Dispose();
            return Answer(HttpStatusCode.BadGateway, "the service took the call and its answer was lost");
        }

        private void Add(Fault fault)
        {
            lock (_gate)
            {
                _faults.Add(fault);
            }
        }

        private Fault? Take(HttpMethod method, string path)
        {
            lock (_gate)
            {
                var fault = _faults.FirstOrDefault(f => f.Remaining > 0 && f.Method == method && f.Matches(path));
                if (fault is not null)
                {
                    fault.Remaining--;
                }

                return fault;
            }
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string message)
            => new(status) { Content = new StringContent(new JsonObject { ["code"] = (int)status, ["message"] = message }.ToJsonString(), Encoding.UTF8, "application/json") };

        private sealed record Fault(HttpMethod Method, string Path, bool Lose, HttpStatusCode Status)
        {
            public int Remaining { get; set; }

            public bool Matches(string path) => Path.EndsWith('*')
                ? path.StartsWith(Path[..^1], StringComparison.Ordinal)
                : string.Equals(path, Path, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What the ledger keeps of one record's unit of work, emulated in memory: each step a route reports, by name, as a later
    /// try of the unit reads it back, and each artifact by its slot, a slot reported again updating its one row as the
    /// ledger's upsert does (SqlServerLedgerBulk.Artifacts.cs): the id, locator and version of the later report where it
    /// gives them, the version a write replaced as first reported, and a slot first reported as the record the unit created
    /// kept so when a resumed try reports it as a version. A slot the route settled itself reopens when it reports it again;
    /// one an undo settled (<see cref="Settle"/>) never does, and stays open for the sweep only when its undo failed.
    /// </summary>
    private sealed class Ledger(Func<int> calls, DateTime? started = null)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _steps = new(StringComparer.Ordinal);
        private long _ids;

        /// <summary>Where the ledger's artifact numbers start, so the rows of several records stay apart.</summary>
        public long FirstId { get; init; }

        public DeliveryUnit Unit { get; } = new(Guid.NewGuid(), started ?? DateTime.UtcNow);

        /// <summary>Every report in the order it came, with how many calls the target had taken when it came.</summary>
        public List<(StepReport Report, int Calls)> Reports { get; } = [];

        /// <summary>The artifact rows, in the order their slots were first reported.</summary>
        public List<ArtifactRow> Rows { get; } = [];

        /// <summary>A try of the unit: the steps the ledger holds so far, and every report recorded.</summary>
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

        /// <summary>The one report of <paramref name="step"/>.</summary>
        public (StepReport Report, int Calls) Only(string step) => Assert.Single(Reports, r => r.Report.Step == step);

        /// <summary>The one row of <paramref name="slot"/>.</summary>
        public ArtifactRow Slot(string slot) => Assert.Single(Rows, r => r.Artifact.Slot == slot);

        /// <summary>The undo of what the unit left open, as the worker hands it to the route.</summary>
        public UndoWork Undo(DeliveryWork work, UndoReason reason = UndoReason.Failed, bool keepRecord = false) => new()
        {
            Key = work.Key,
            TargetId = work.TargetId,
            TargetState = work.TargetState,
            CommittedVersion = work.ExistingVersion,
            Reason = reason,
            KeepRecord = keepRecord,
            Items = Rows.Where(r => ArtifactStatuses.IsOpen(r.Artifact.Status)).Select(r => new UndoItem(r.Id, r.Artifact, Unit.Id, Unit.StartedUtc)).ToList(),
        };

        /// <summary>Writes what an undo answered, as the ledger settles an undo: a failed artifact stays open for the sweep, any other settles for good.</summary>
        public void Settle(IEnumerable<UndoResult> results)
        {
            lock (_gate)
            {
                foreach (var result in results)
                {
                    var row = Assert.Single(Rows, r => r.Id == result.Item.ArtifactId);
                    row.Artifact = row.Artifact with { Status = result.Outcome, Note = result.Note };
                    row.SettledByRoute = false;
                }
            }
        }

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

    /// <summary>One artifact row of the emulated ledger: the number the ledger gave it, and the artifact as the upserts left it.</summary>
    private sealed class ArtifactRow(long id, TargetArtifact artifact)
    {
        public long Id { get; } = id;

        public TargetArtifact Artifact { get; set; } = artifact;

        /// <summary>Whether the route settled the artifact itself (the ledger's SettledBy 'route'), rather than an undo.</summary>
        public bool SettledByRoute { get; set; }
    }

    private static string Input(int n, string anchor = Anchor)
        => WorkflowValues.DerivedDatasetId(anchor, "dataset--File.Generic", "h5-" + n.ToString(CultureInfo.InvariantCulture));

    private static JsonObject Document(string name = "registry entry") => FakeOsduPlatform.Record(Anchor, AnchorKind, new JsonObject { ["Name"] = name });

    private static WorkPayloadPart H5(string hash = "x1") => new(PayloadParts.Files, "h5", new MemoryFiles(("model-0.h5", "HDF-0"), ("model-1.h5", "HDF-1")), hash);

    private static DeliveryWork Work(JsonObject document, IReadOnlyList<WorkPayloadPart>? parts = null, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
        => new()
        {
            Key = DeliveryKey.Derive("atomic-workflow", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = true,
            Parts = parts ?? [],
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ForcedParts = new HashSet<string>(StringComparer.Ordinal) { PayloadParts.Files, PayloadParts.Workflow },
        };

    /// <summary>A storage anchor with one input set, one run, and the two records the run writes found by their ids.</summary>
    private static WorkflowRoute Route(int minimum = 2, bool remove = true, string? ids = null, int maxRecorded = WorkflowResults.DefaultMaxRecorded, WorkflowAnchor anchor = WorkflowAnchor.Storage)
        => new()
        {
            Anchor = anchor,
            Inputs = [new WorkflowInput("h5", DatasetKind, Optional: true)],
            Stages = [new WorkflowStage { Workflow = Workflow, Context = new JsonObject() }],
            Results = new WorkflowResults
            {
                Strategy = WorkflowResultStrategy.Ids,
                Template = ids ?? $"{Output1} {Output2}",
                Minimum = minimum,
                Remove = remove,
                MaxRecorded = maxRecorded,
            },
        };

    /// <summary>A platform whose workflow writes the records the results look for.</summary>
    private static FakeOsduPlatform Platform(string pending = "", int outputs = 2)
    {
        var platform = new FakeOsduPlatform();
        platform.Register(Workflow, new FakeOsduPlatform.Script
        {
            Pending = pending,
            Effect = (p, _) =>
            {
                foreach (var id in new[] { Output1, Output2, Output3 }.Take(outputs))
                {
                    p.Put(FakeOsduPlatform.Record(id, WellKind));
                }
            },
        });
        return platform;
    }

    private static long Version(FakeOsduPlatform platform, string id) => platform.Records[id]["version"]!.GetValue<long>();

    /// <summary>The position of the first call of <paramref name="method"/> to <paramref name="path"/> among the calls the platform took.</summary>
    private static int CallIndex(FakeOsduPlatform platform, HttpMethod method, string path)
    {
        var index = platform.Calls.FindIndex(c => c.Method == method && string.Equals(Uri.UnescapeDataString(c.Uri.AbsolutePath), path, StringComparison.Ordinal));
        Assert.True(index >= 0, $"the platform took no {method} {path}");
        return index;
    }

    private static int CountCalls(FakeOsduPlatform platform, HttpMethod method, string path)
        => platform.Calls.Count(c => c.Method == method && string.Equals(Uri.UnescapeDataString(c.Uri.AbsolutePath), path, StringComparison.Ordinal));

    private static UndoResult Outcome(IReadOnlyList<UndoResult> results, string slot)
        => Assert.Single(results, r => string.Equals(r.Item.Artifact.Slot, slot, StringComparison.Ordinal));

    /// <summary>Every item the undo was given is answered, and none twice.</summary>
    private static void AssertEachOnce(IReadOnlyList<UndoWork> works, IReadOnlyList<UndoResult> results)
        => Assert.Equal(works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(), results.Select(r => r.Item.ArtifactId).Order());

    /// <summary>Delivers a created record whose runs finish and whose results find fewer records than the route requires.</summary>
    private static async Task<(DeliveryWork Work, string RunId)> FailAtResultsAsync(Rig rig, Ledger ledger, OsduWorkflowProtocol protocol)
    {
        var work = ledger.Track(Work(Document(), [H5()]));
        var outcome = await protocol.DeliverAsync(work);
        Assert.Contains("2 of the 3 record(s) the route requires were found", Assert.IsType<DeliveryException>(outcome.Failure).Message, StringComparison.Ordinal);
        return (work, Assert.Single(rig.Platform.Runs).RunId);
    }

    [Fact]
    public async Task A_created_record_declares_its_inputs_and_anchor_before_writing_them_and_names_its_run_before_the_trigger()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);

        var outcome = await rig.Protocol(Route()).DeliverAsync(ledger.Track(Work(Document(), [H5()])));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var run = Assert.Single(platform.Runs);

        // Each input registration is declared before it is sent: storage holds neither id, so each creates its dataset.
        var declared = ledger.Only("register-h5-intent");
        Assert.Equal([OsduWorkflowProtocol.DatasetSlot("h5-0"), OsduWorkflowProtocol.DatasetSlot("h5-1")], declared.Report.Artifacts.Select(a => a.Slot));
        Assert.Equal([Input(0), Input(1)], declared.Report.Artifacts.Select(a => a.TargetId));
        Assert.All(declared.Report.Artifacts, a =>
        {
            Assert.Equal(ArtifactStatus.Intent, a.Status);
            Assert.Equal(ArtifactRoles.Record, a.Role);
            Assert.Null(a.Version);
            Assert.Null(a.PriorVersion);
        });
        var register = CallIndex(platform, HttpMethod.Put, Register);
        Assert.True(declared.Calls <= register, "the input intents were reported after their registration was sent");
        Assert.True(CallIndex(platform, HttpMethod.Post, "/api/storage/v2/query/records") < declared.Calls, "storage was not asked what the registrations replace before they were declared");
        var landed = ledger.Only("register-h5");
        Assert.True(landed.Calls > register);
        Assert.All(landed.Report.Artifacts, a => Assert.Equal(ArtifactStatus.Pending, a.Status));
        Assert.Equal([Version(platform, Input(0)), Version(platform, Input(1))], landed.Report.Artifacts.Select(a => a.Version));

        // The anchor: declared before the storage write, then reported as the record the unit created, at the version written.
        var anchorIntent = ledger.Only(OsduWorkflowProtocol.AnchorIntentStep);
        var intent = Assert.Single(anchorIntent.Report.Artifacts);
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactRoles.Record, Anchor, ArtifactStatus.Intent), (intent.Slot, intent.Role, intent.TargetId, intent.Status));
        Assert.Null(intent.Version);
        var write = CallIndex(platform, HttpMethod.Put, RecordWrite);
        Assert.True(anchorIntent.Calls <= write, "the anchor's intent was reported after its write was sent");
        var anchor = ledger.Only(OsduWorkflowProtocol.AnchorStep);
        var written = Assert.Single(anchor.Report.Artifacts);
        Assert.True(anchor.Calls > write);
        Assert.Equal((ArtifactRoles.Record, ArtifactStatus.Pending, Version(platform, Anchor)), (written.Role, written.Status, written.Version));
        Assert.Null(written.PriorVersion);

        // The run is named under the id the route chose, with the workflow an undo asks about it, before the trigger goes out.
        var marked = Assert.Single(ledger.Reports, r => r.Report.Step == OsduWorkflowProtocol.StageStep(1) && r.Report.Artifacts.Count > 0);
        Assert.Equal("triggering", marked.Report.Returned["state"]);
        var named = Assert.Single(marked.Report.Artifacts);
        Assert.Equal(
            (OsduWorkflowProtocol.RunSlot(run.RunId), ArtifactRoles.Run, run.RunId, Workflow, ArtifactStatus.Pending),
            (named.Slot, named.Role, named.TargetId, named.Locator, named.Status));
        Assert.True(marked.Calls <= CallIndex(platform, HttpMethod.Post, Trigger), "the run was named after its trigger was sent");

        // The records the run created that the results found, each under a digest of its id.
        var results = ledger.Only(OsduWorkflowProtocol.ResultsStep).Report;
        Assert.Equal([OsduWorkflowProtocol.OutputSlot(Output1), OsduWorkflowProtocol.OutputSlot(Output2)], results.Artifacts.Select(a => a.Slot));
        Assert.Equal([Output1, Output2], results.Artifacts.Select(a => a.TargetId));
        Assert.All(results.Artifacts, a => Assert.Equal((ArtifactRoles.Output, ArtifactStatus.Pending), (a.Role, a.Status)));
        Assert.Equal("2", results.Returned[OsduWorkflowProtocol.RecordsValue]);

        // However many reports there were, each slot is one row, and every row is pending once its call answered.
        Assert.Equal(
            [
                OsduWorkflowProtocol.DatasetSlot("h5-0"), OsduWorkflowProtocol.DatasetSlot("h5-1"), TargetArtifact.RecordSlot,
                OsduWorkflowProtocol.RunSlot(run.RunId), OsduWorkflowProtocol.OutputSlot(Output1), OsduWorkflowProtocol.OutputSlot(Output2),
            ],
            ledger.Rows.Select(r => r.Artifact.Slot));
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Pending, r.Artifact.Status));
    }

    [Fact]
    public async Task An_update_reports_versions_that_name_the_versions_its_writes_replaced()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var protocol = rig.Protocol(Route());
        var first = await protocol.DeliverAsync(Work(Document(), [H5()]));
        Assert.True(first.Succeeded, first.Failure?.Message);
        long[] inputs = [Version(platform, Input(0)), Version(platform, Input(1))];
        var anchorBefore = Version(platform, Anchor);
        Assert.Equal(anchorBefore, first.TargetVersion);

        var ledger = new Ledger(() => platform.Calls.Count);
        var outcome = await protocol.DeliverAsync(ledger.Track(Work(Document("renamed"), [H5("x2")], existing: first.TargetVersion, state: first.Returned)));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // Storage holds the inputs, so their registrations are declared as versions naming what they replace.
        var declared = ledger.Only("register-h5-intent").Report.Artifacts;
        Assert.All(declared, a => Assert.Equal((ArtifactRoles.Version, ArtifactStatus.Intent), (a.Role, a.Status)));
        Assert.Equal(inputs.Cast<long?>(), declared.Select(a => a.PriorVersion));
        var landed = ledger.Only("register-h5").Report.Artifacts;
        Assert.Equal(inputs.Cast<long?>(), landed.Select(a => a.PriorVersion));
        Assert.Equal([Version(platform, Input(0)), Version(platform, Input(1))], landed.Select(a => a.Version));
        Assert.All(landed, a => Assert.True(a.Version > a.PriorVersion));

        // The anchor's intent and its write name the version the ledger held, which an undo writes back.
        var intent = Assert.Single(ledger.Only(OsduWorkflowProtocol.AnchorIntentStep).Report.Artifacts);
        Assert.Equal((ArtifactRoles.Version, anchorBefore), (intent.Role, intent.PriorVersion));
        var written = Assert.Single(ledger.Only(OsduWorkflowProtocol.AnchorStep).Report.Artifacts);
        Assert.Equal((ArtifactRoles.Version, Version(platform, Anchor), anchorBefore), (written.Role, written.Version, written.PriorVersion));
        Assert.Equal((ArtifactRoles.Version, anchorBefore), (ledger.Slot(TargetArtifact.RecordSlot).Artifact.Role, ledger.Slot(TargetArtifact.RecordSlot).Artifact.PriorVersion));
    }

    [Fact]
    public async Task A_registration_whose_answer_was_lost_leaves_its_intents_and_the_undo_removes_the_datasets_that_landed()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Put, Register);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(502, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);

        // The datasets landed and the answer that named their versions did not: the intents alone name them.
        Assert.True(platform.Records.ContainsKey(Input(0)) && platform.Records.ContainsKey(Input(1)));
        Assert.DoesNotContain(ledger.Reports, r => r.Report.Step == "register-h5");
        Assert.Equal([Input(0), Input(1)], ledger.Rows.Select(r => r.Artifact.TargetId));
        Assert.All(ledger.Rows, r => Assert.Equal((ArtifactStatus.Intent, ArtifactRoles.Record), (r.Artifact.Status, r.Artifact.Role)));
        Assert.Equal(0, CountCalls(platform, HttpMethod.Put, RecordWrite));
        Assert.Empty(platform.Runs);

        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.All(results, r => Assert.Contains("removed from OSDU (reversible)", r.Note, StringComparison.Ordinal));
        Assert.Contains(Input(0), platform.Removed);
        Assert.Contains(Input(1), platform.Removed);

        // A second undo of the same artifacts (a sweep after a settlement that never reached the ledger) finds them gone.
        var again = await protocol.UndoAsync([undo]);
        AssertEachOnce([undo], again);
        Assert.All(again, r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.All(again, r => Assert.Contains("OSDU no longer holds the record", r.Note, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resumed_try_after_a_lost_registration_keeps_each_slot_the_record_the_unit_created()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Put, Register);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var lost = await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])));
        Assert.False(lost.Succeeded);
        long[] lostVersions = [Version(platform, Input(0)), Version(platform, Input(1))];

        var work = ledger.Track(Work(Document(), [H5()]));
        var resumed = await protocol.DeliverAsync(work);

        Assert.True(resumed.Succeeded, resumed.Failure?.Message);

        // The resumed try reads storage again and sees the unit's own write: it declares versions of what the lost try wrote.
        var redeclared = ledger.Reports.Last(r => r.Report.Step == "register-h5-intent").Report.Artifacts;
        Assert.All(redeclared, a => Assert.Equal(ArtifactRoles.Version, a.Role));
        Assert.Equal(lostVersions.Cast<long?>(), redeclared.Select(a => a.PriorVersion));

        // The ledger keeps each slot the record the unit created, with no version to write back, at the version that landed last.
        foreach (var (slot, id) in new[] { ("h5-0", Input(0)), ("h5-1", Input(1)) })
        {
            var row = ledger.Slot(OsduWorkflowProtocol.DatasetSlot(slot)).Artifact;
            Assert.Equal((ArtifactRoles.Record, ArtifactStatus.Pending, id), (row.Role, row.Status, row.TargetId));
            Assert.Null(row.PriorVersion);
            Assert.Equal(Version(platform, id), row.Version);
            Assert.Equal(2, platform.History[id].Count);
        }

        // An undo of the unit (its record held afterwards) removes the datasets it created, never writing back what it replaced.
        var results = await protocol.UndoAsync([ledger.Undo(work, UndoReason.Held)]);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-1")).Outcome);
        Assert.Contains(Input(0), platform.Removed);
        Assert.Equal(2, platform.History[Input(0)].Count);
    }

    [Fact]
    public async Task An_anchor_write_whose_answer_was_lost_is_undone_by_removing_the_record_it_created()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Put, RecordWrite);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(502, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);
        Assert.True(platform.Records.ContainsKey(Anchor));
        var anchor = ledger.Slot(TargetArtifact.RecordSlot).Artifact;
        Assert.Equal((ArtifactRoles.Record, ArtifactStatus.Intent, Anchor), (anchor.Role, anchor.Status, anchor.TargetId));
        Assert.Null(anchor.Version);
        Assert.DoesNotContain(ledger.Reports, r => r.Report.Step == OsduWorkflowProtocol.AnchorStep);
        Assert.Empty(platform.Runs);

        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-1")).Outcome);
        Assert.Contains(Anchor, platform.Removed);
        Assert.Contains(platform.Calls, c => c.Method == HttpMethod.Post && Uri.UnescapeDataString(c.Uri.AbsolutePath) == $"/api/storage/v2/records/{Anchor}:delete");
    }

    [Fact]
    public async Task An_anchor_update_whose_answer_was_lost_is_given_back_the_version_it_replaced()
    {
        var platform = Platform();
        var prior = platform.Put(Document("before"));
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Put, RecordWrite);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document("after"), existing: prior));

        var outcome = await protocol.DeliverAsync(work);

        Assert.False(outcome.Succeeded);
        var lostWrite = Version(platform, Anchor);
        Assert.Equal("after", platform.Records[Anchor]["data"]!["Name"]!.GetValue<string>());
        var row = ledger.Slot(TargetArtifact.RecordSlot).Artifact;
        Assert.Equal((ArtifactRoles.Version, ArtifactStatus.Intent, prior), (row.Role, row.Status, row.PriorVersion));
        Assert.Null(row.Version);

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        var restored = Outcome(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, restored.Outcome);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"version {prior} written back"), restored.Note, StringComparison.Ordinal);
        Assert.Equal("before", platform.Records[Anchor]["data"]!["Name"]!.GetValue<string>());
        Assert.True(Version(platform, Anchor) > lostWrite);
        Assert.DoesNotContain(Anchor, platform.Removed);
    }

    [Fact]
    public async Task An_anchor_update_refused_before_it_landed_is_gone_and_nothing_is_written_back()
    {
        var platform = Platform();
        var prior = platform.Put(Document("before"));
        using var rig = new Rig(platform);
        rig.Network.Refuse(HttpMethod.Put, RecordWrite, times: 1);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document("after"), existing: prior));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(503, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);
        Assert.Equal(prior, Version(platform, Anchor));

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        var gone = Outcome(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"latest version is still {prior}"), gone.Note, StringComparison.Ordinal);
        Assert.Equal(prior, Version(platform, Anchor));
        Assert.Equal(0, CountCalls(platform, HttpMethod.Put, RecordWrite));
    }

    [Fact]
    public async Task A_run_still_going_makes_every_item_wait_and_once_it_ended_everything_the_unit_made_is_taken_back()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, runId) = await FailAtResultsAsync(rig, ledger, protocol);
        Assert.Equal(6, ledger.Rows.Count);

        // The Workflow service says the run is still going: a run reads the inputs and can write records until it ends.
        platform.Workflows[Workflow] = new FakeOsduPlatform.Script { Terminal = "running" };
        var undo = ledger.Undo(work);
        var waiting = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], waiting);
        Assert.All(waiting, r => Assert.Equal(ArtifactStatus.Failed, r.Outcome));
        Assert.All(waiting, r => Assert.Contains($"workflow run {runId} of {Workflow} is still RUNNING", r.Note, StringComparison.Ordinal));
        Assert.Empty(platform.Removed);

        platform.Workflows[Workflow] = new FakeOsduPlatform.Script();
        var done = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], done);
        var run = Outcome(done, OsduWorkflowProtocol.RunSlot(runId));
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Contains("ended FINISHED", run.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, OsduWorkflowProtocol.OutputSlot(Output1)).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, OsduWorkflowProtocol.OutputSlot(Output2)).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, OsduWorkflowProtocol.DatasetSlot("h5-1")).Outcome);
        Assert.Equal(new[] { Output1, Output2, Anchor, Input(0), Input(1) }.Order(StringComparer.Ordinal), platform.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_run_whose_status_cannot_be_asked_makes_every_item_wait()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, runId) = await FailAtResultsAsync(rig, ledger, protocol);
        rig.Network.Refuse(HttpMethod.Get, $"{Trigger}/{runId}");

        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Failed, r.Outcome));
        Assert.All(results, r => Assert.Contains($"whether workflow run {runId} of {Workflow} has ended could not be asked", r.Note, StringComparison.Ordinal));
        Assert.All(results, r => Assert.Contains("503", r.Note, StringComparison.Ordinal));
        Assert.Empty(platform.Removed);
        Assert.Contains($"GET {Trigger}/{runId}", rig.Network.Refused);
    }

    [Fact]
    public async Task A_run_whose_status_is_not_one_the_route_knows_is_waited_for_rather_than_taken_as_ended()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, runId) = await FailAtResultsAsync(rig, ledger, protocol);

        // A status outside both of the Workflow service's vocabularies says nothing about whether the run has ended.
        platform.Workflows[Workflow] = new FakeOsduPlatform.Script { Terminal = "up_for_retry" };
        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Failed, r.Outcome));
        Assert.All(results, r => Assert.Equal($"workflow run {runId} of {Workflow} reports UP_FOR_RETRY, which is not a state the route knows as ended (a retry Airflow schedules is one), so nothing is taken back until it ends", r.Note));
        Assert.Empty(platform.Removed);
    }

    [Fact]
    public async Task A_trigger_that_never_landed_keeps_its_run_saying_so_and_takes_back_the_anchor_and_inputs()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.Refuse(HttpMethod.Post, Trigger, times: 1);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(503, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);
        Assert.Empty(platform.Runs);
        var runRow = Assert.Single(ledger.Rows, r => r.Artifact.Role == ArtifactRoles.Run).Artifact;
        Assert.Equal(ArtifactStatus.Pending, runRow.Status);

        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var run = Outcome(results, runRow.Slot);
        Assert.Equal(ArtifactStatus.Kept, run.Outcome);
        Assert.Equal($"the Workflow service has no run {runRow.TargetId} of {Workflow}: its trigger did not land", run.Note);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-1")).Outcome);
    }

    [Fact]
    public async Task A_trigger_whose_answer_was_lost_is_waited_for_and_the_records_its_run_wrote_unnamed_stay()
    {
        var platform = Platform(pending: "running");
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Post, Trigger);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(502, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);
        var run = Assert.Single(platform.Runs);
        Assert.Equal(run.RunId, ledger.Slot(OsduWorkflowProtocol.RunSlot(run.RunId)).Artifact.TargetId);

        // The run the lost answer started is still going: nothing is taken back.
        var undo = ledger.Undo(work);
        var waiting = await protocol.UndoAsync([undo]);
        Assert.All(waiting, r => Assert.Equal(ArtifactStatus.Failed, r.Outcome));
        Assert.Empty(platform.Removed);

        // Once it ended, the anchor and inputs go; the records it wrote that the delivery did not name stay, for an inventory of their kind.
        var done = await protocol.UndoAsync([undo]);
        AssertEachOnce([undo], done);
        Assert.Equal(ArtifactStatus.Kept, Outcome(done, OsduWorkflowProtocol.RunSlot(run.RunId)).Outcome);
        Assert.Contains("records it wrote that the delivery did not name are left for an inventory", Outcome(done, OsduWorkflowProtocol.RunSlot(run.RunId)).Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Outcome(done, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(Anchor, platform.Removed);
        Assert.DoesNotContain(Output1, platform.Removed);
        Assert.DoesNotContain(Output2, platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_whose_trigger_was_marked_sends_the_same_run_id_under_the_same_slot()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.LoseAnswer(HttpMethod.Post, Trigger);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var lost = await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])));
        Assert.False(lost.Succeeded);
        var run = Assert.Single(platform.Runs);
        Assert.Equal("triggering", ledger.Reports.Last(r => r.Report.Step == OsduWorkflowProtocol.StageStep(1)).Report.Returned["state"]);

        var resumed = await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])));

        Assert.True(resumed.Succeeded, resumed.Failure?.Message);

        // The same run id went again and the service answered that it has the run: one run, one slot, two marks of it.
        Assert.Single(platform.Runs);
        Assert.Equal(2, platform.Triggers(Workflow).Count());
        Assert.Equal(run.RunId, Assert.Single(ledger.Rows, r => r.Artifact.Role == ArtifactRoles.Run).Artifact.TargetId);
        Assert.Equal(2, ledger.Reports.Count(r => r.Report.Artifacts.Any(a => a.Role == ArtifactRoles.Run)));

        // The inputs and the anchor the first try wrote are not written again.
        Assert.Equal(1, CountCalls(platform, HttpMethod.Put, Register));
        Assert.Equal(1, CountCalls(platform, HttpMethod.Put, RecordWrite));
    }

    [Fact]
    public async Task A_failed_run_is_kept_saying_so_and_the_run_of_the_next_try_is_a_slot_of_its_own()
    {
        var platform = Platform();
        platform.Workflows[Workflow] = new FakeOsduPlatform.Script { Terminal = "failed" };
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var failed = await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])));
        Assert.Contains("failed (stage 1)", failed.Failure!.Message, StringComparison.Ordinal);

        platform.Workflows[Workflow] = Platform().Workflows[Workflow];
        var work = ledger.Track(Work(Document(), [H5()]));
        var second = await protocol.DeliverAsync(work);
        Assert.False(second.Succeeded);

        Assert.Equal(2, platform.Runs.Count);
        var runs = ledger.Rows.Where(r => r.Artifact.Role == ArtifactRoles.Run).Select(r => r.Artifact.TargetId).ToList();
        Assert.Equal(platform.Runs.Select(r => r.RunId), runs);

        // The fake answers every run of a workflow with the workflow's script, so both runs report how the failing one ended.
        platform.Workflows[Workflow] = new FakeOsduPlatform.Script { Terminal = "failed" };
        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        foreach (var run in platform.Runs)
        {
            var kept = Outcome(results, OsduWorkflowProtocol.RunSlot(run.RunId));
            Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
            Assert.Equal($"workflow run {run.RunId} of {Workflow} ended FAILED; Airflow keeps its log, and records it wrote that the delivery did not name are left for an inventory of their kind", kept.Note);
        }

        Assert.Equal(ArtifactStatus.Removed, Outcome(results, TargetArtifact.RecordSlot).Outcome);
    }

    [Fact]
    public async Task Outputs_are_reported_up_to_the_number_kept_even_when_too_few_were_found_and_never_the_anchor()
    {
        var platform = Platform(outputs: 3);
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 5, maxRecorded: 2, ids: $"{Anchor} {Output1} {Output2} {Output3}"));
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Contains("4 of the 5 record(s) the route requires were found", outcome.Failure!.Message, StringComparison.Ordinal);
        var results = ledger.Only(OsduWorkflowProtocol.ResultsStep).Report;
        Assert.Equal("4", results.Returned[OsduWorkflowProtocol.RecordsValue]);
        Assert.Equal([Output1, Output2], results.Artifacts.Select(a => a.TargetId));
        Assert.DoesNotContain(ledger.Rows, r => r.Artifact.Role == ArtifactRoles.Output && r.Artifact.TargetId == Anchor);
        Assert.Equal(OsduWorkflowProtocol.ResultsStep, ledger.Reports[^1].Report.Step);
    }

    [Fact]
    public async Task An_output_OSDU_created_before_the_unit_began_is_kept_and_one_within_the_clock_skew_is_removed()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, _) = await FailAtResultsAsync(rig, ledger, protocol);
        var started = ledger.Unit.StartedUtc;
        platform.Records[Output1]["createTime"] = (started - TimeSpan.FromDays(1)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        platform.Records[Output2]["createTime"] = (started - TimeSpan.FromMinutes(4)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        var kept = Outcome(results, OsduWorkflowProtocol.OutputSlot(Output1));
        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.Contains("before this delivery began", kept.Note, StringComparison.Ordinal);
        Assert.Contains("a run updated it", kept.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.OutputSlot(Output2)).Outcome);
        Assert.DoesNotContain(Output1, platform.Removed);
        Assert.Contains(Output2, platform.Removed);
    }

    [Fact]
    public async Task With_results_remove_off_the_outputs_stay_and_the_anchor_and_inputs_are_still_taken_back()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3, remove: false));
        var (work, _) = await FailAtResultsAsync(rig, ledger, protocol);

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        foreach (var output in new[] { Output1, Output2 })
        {
            var kept = Outcome(results, OsduWorkflowProtocol.OutputSlot(output));
            Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
            Assert.Contains("results.remove is off", kept.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(output, platform.Removed);
        }

        Assert.Equal(ArtifactStatus.Removed, Outcome(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Contains(Anchor, platform.Removed);
        Assert.Contains(Input(0), platform.Removed);
    }

    [Fact]
    public async Task An_anchor_another_system_created_before_the_unit_is_given_back_its_earlier_version_not_removed()
    {
        var platform = Platform();
        var theirs = platform.Put(Document("theirs"));
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));

        // The ledger holds no version of the record, so the unit reports it as one it created.
        var (work, _) = await FailAtResultsAsync(rig, ledger, protocol);
        var anchor = ledger.Slot(TargetArtifact.RecordSlot).Artifact;
        Assert.Equal(ArtifactRoles.Record, anchor.Role);
        Assert.Equal(theirs + 1, anchor.Version);
        platform.Records[Anchor]["createTime"] = (ledger.Unit.StartedUtc - TimeSpan.FromDays(30)).ToString("O", CultureInfo.InvariantCulture);

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        var restored = Outcome(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Restored, restored.Outcome);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"version {theirs} written back"), restored.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Anchor, platform.Removed);
        Assert.Equal("theirs", platform.Records[Anchor]["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_dataset_anchor_is_taken_back_through_the_dataset_service_even_when_newer_work_follows()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3, anchor: WorkflowAnchor.Dataset));
        var files = new WorkPayloadPart(PayloadParts.Files, PayloadParts.Files, new MemoryFiles(("wells.csv", "uwi\n1\n")), "f1");
        var work = ledger.Track(Work(FakeOsduPlatform.Record(DatasetAnchor, DatasetKind, new JsonObject { ["Name"] = "wells.csv" }), [files, H5()]));

        var outcome = await protocol.DeliverAsync(work);
        Assert.False(outcome.Succeeded);

        // The registration of a dataset anchor is declared under the record's slot before it is sent.
        var intent = Assert.Single(ledger.Only(OsduWorkflowProtocol.AnchorIntentStep).Report.Artifacts);
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactRoles.Record, DatasetAnchor, ArtifactStatus.Intent), (intent.Slot, intent.Role, intent.TargetId, intent.Status));
        Assert.Equal((ArtifactStatus.Pending, Version(platform, DatasetAnchor)), (ledger.Slot(TargetArtifact.RecordSlot).Artifact.Status, ledger.Slot(TargetArtifact.RecordSlot).Artifact.Version));

        // Newer work reads what OSDU holds of a dataset anchor and of the inputs, so they go whatever the newer work writes.
        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var anchor = Outcome(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Removed, anchor.Outcome);
        Assert.Contains("through the dataset service", anchor.Note, StringComparison.Ordinal);
        Assert.Contains(platform.Calls, c => c.Method == HttpMethod.Post && Uri.UnescapeDataString(c.Uri.AbsolutePath) == $"/api/dataset/v1/metadataRecord/{DatasetAnchor}/softDelete");
        Assert.DoesNotContain(platform.Calls, c => Uri.UnescapeDataString(c.Uri.AbsolutePath) == $"/api/storage/v2/records/{DatasetAnchor}:delete");
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Contains(Input(0, DatasetAnchor), platform.Removed);
        Assert.Contains(Input(1, DatasetAnchor), platform.Removed);
    }

    [Fact]
    public async Task A_storage_anchor_is_left_to_newer_work_while_its_inputs_and_outputs_are_taken_back()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, _) = await FailAtResultsAsync(rig, ledger, protocol);

        var results = await protocol.UndoAsync([ledger.Undo(work, UndoReason.Abandoned, keepRecord: true)]);

        var anchor = Outcome(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Superseded, anchor.Outcome);
        Assert.Contains("newer work writes it again", anchor.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Anchor, platform.Removed);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.OutputSlot(Output1)).Outcome);
        Assert.Contains(Input(1), platform.Removed);
        Assert.Contains(Output2, platform.Removed);
    }

    [Fact]
    public async Task The_undo_answers_every_item_once_and_a_second_undo_finds_everything_gone()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, runId) = await FailAtResultsAsync(rig, ledger, protocol);
        var undo = ledger.Undo(work);

        var first = await protocol.UndoAsync([undo]);
        var second = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], first);
        AssertEachOnce([undo], second);
        Assert.All(first.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.All(second.Where(r => r.Item.Artifact.Role != ArtifactRoles.Run), r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.Equal(ArtifactStatus.Kept, Outcome(second, OsduWorkflowProtocol.RunSlot(runId)).Outcome);
    }

    [Fact]
    public async Task A_removal_storage_refuses_fails_exactly_the_items_it_was_for()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, runId) = await FailAtResultsAsync(rig, ledger, protocol);

        // One input's soft delete, and the bulk soft delete the outputs go through, are refused.
        rig.Network.Refuse(HttpMethod.Post, $"/api/storage/v2/records/{Input(0)}:delete");
        rig.Network.Refuse(HttpMethod.Post, "/api/storage/v2/records/delete");
        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var input = Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-0"));
        Assert.Equal(ArtifactStatus.Failed, input.Outcome);
        Assert.Contains("503", input.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Failed, Outcome(results, OsduWorkflowProtocol.OutputSlot(Output1)).Outcome);
        Assert.Equal(ArtifactStatus.Failed, Outcome(results, OsduWorkflowProtocol.OutputSlot(Output2)).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.DatasetSlot("h5-1")).Outcome);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Kept, Outcome(results, OsduWorkflowProtocol.RunSlot(runId)).Outcome);
        Assert.Equal(new[] { Anchor, Input(1) }.Order(StringComparer.Ordinal), platform.Removed.Order(StringComparer.Ordinal));

        // The ledger settles what the undo took back for good; the sweep tries again only what failed, once storage takes it.
        ledger.Settle(results);
        rig.Network.Heal();
        var sweep = ledger.Undo(work);
        Assert.Equal(
            new[] { OsduWorkflowProtocol.DatasetSlot("h5-0"), OsduWorkflowProtocol.OutputSlot(Output1), OsduWorkflowProtocol.OutputSlot(Output2) }.Order(StringComparer.Ordinal),
            sweep.Items.Select(i => i.Artifact.Slot).Order(StringComparer.Ordinal));
        var retried = await protocol.UndoAsync([sweep]);
        AssertEachOnce([sweep], retried);
        Assert.All(retried, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        ledger.Settle(retried);
        Assert.Empty(ledger.Undo(work).Items);
        Assert.Equal(new[] { Anchor, Input(0), Input(1), Output1, Output2 }.Order(StringComparer.Ordinal), platform.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_storage_read_that_fails_fails_only_the_output_it_was_for()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3));
        var (work, _) = await FailAtResultsAsync(rig, ledger, protocol);
        rig.Network.Refuse(HttpMethod.Get, $"/api/storage/v2/records/{Output1}", HttpStatusCode.InternalServerError);

        var results = await protocol.UndoAsync([ledger.Undo(work)]);

        var failed = Outcome(results, OsduWorkflowProtocol.OutputSlot(Output1));
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.StartsWith(Output1 + ": ", failed.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, Outcome(results, OsduWorkflowProtocol.OutputSlot(Output2)).Outcome);
        Assert.DoesNotContain(Output1, platform.Removed);
        Assert.Contains(Output2, platform.Removed);
    }

    [Fact]
    public async Task Registrations_storage_cannot_say_anything_about_are_not_sent_and_nothing_is_declared()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.Refuse(HttpMethod.Post, "/api/storage/v2/query/records");
        var ledger = new Ledger(() => platform.Calls.Count);

        var outcome = await rig.Protocol(Route()).DeliverAsync(ledger.Track(Work(Document(), [H5()])));

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, CountCalls(platform, HttpMethod.Put, Register));
        Assert.Empty(ledger.Rows);
        Assert.False(platform.Records.ContainsKey(Input(0)));
        Assert.False(platform.Records.ContainsKey(Anchor));
    }

    [Fact]
    public async Task A_run_still_going_holds_back_only_the_undo_of_its_own_record()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var protocol = rig.Protocol(Route(minimum: 3));
        var waitingLedger = new Ledger(() => platform.Calls.Count);
        var (waitingWork, runId) = await FailAtResultsAsync(rig, waitingLedger, protocol);

        // Another record of the flow whose anchor write lost its answer: no run of its own.
        rig.Network.LoseAnswer(HttpMethod.Put, RecordWrite);
        var otherLedger = new Ledger(() => platform.Calls.Count) { FirstId = 100 };
        const string other = "dev:master-data--ConnectedSourceRegistryEntry:csre-other";
        var otherWork = otherLedger.Track(Work(FakeOsduPlatform.Record(other, AnchorKind)));
        Assert.False((await protocol.DeliverAsync(otherWork)).Succeeded);

        platform.Workflows[Workflow] = new FakeOsduPlatform.Script { Terminal = "queued" };
        UndoWork[] works = [waitingLedger.Undo(waitingWork), otherLedger.Undo(otherWork)];
        var results = await protocol.UndoAsync(works);

        AssertEachOnce(works, results);
        Assert.Equal(works[0].Items.Count, results.Count(r => r.Item.UnitId == waitingLedger.Unit.Id));
        Assert.All(results.Where(r => r.Item.UnitId == waitingLedger.Unit.Id), r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.Contains($"workflow run {runId} of {Workflow} is still QUEUED", r.Note, StringComparison.Ordinal);
        });
        var otherAnchor = Assert.Single(results, r => r.Item.UnitId == otherLedger.Unit.Id);
        Assert.Equal(ArtifactStatus.Removed, otherAnchor.Outcome);
        Assert.Equal([other], platform.Removed);
    }

    [Fact]
    public async Task A_registration_refused_before_it_landed_is_gone_on_undo()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        rig.Network.Refuse(HttpMethod.Put, Register);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route());
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.Equal(503, Assert.IsType<OsduStatusException>(outcome.Failure).StatusCode);
        Assert.False(platform.Records.ContainsKey(Input(0)));
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Intent, r.Artifact.Status));

        var undo = ledger.Undo(work);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.Empty(platform.Removed);
        Assert.DoesNotContain(platform.Calls, c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith(":delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_context_its_contract_refuses_holds_the_record_before_any_run_is_named_and_the_undo_takes_back_the_anchor()
    {
        var platform = Platform();
        platform.Register("csv_ingestion");
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var route = Route() with
        {
            Stages = [new WorkflowStage { Workflow = "csv_ingestion", Context = (JsonObject)JsonNode.Parse("""{ "id": "", "dataPartitionId": "{partition}" }""")! }],
        };
        var protocol = rig.Protocol(route);
        var work = ledger.Track(Work(Document(), [H5()]));

        var outcome = await protocol.DeliverAsync(work);

        Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Empty(platform.Runs);
        Assert.DoesNotContain(ledger.Rows, r => r.Artifact.Role == ArtifactRoles.Run);

        var undo = ledger.Undo(work, UndoReason.Held);
        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Equal(new[] { Anchor, Input(0), Input(1) }.Order(StringComparer.Ordinal), platform.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_resumed_try_after_too_few_results_runs_nothing_again_and_reports_the_outputs_under_the_same_slots()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var ledger = new Ledger(() => platform.Calls.Count);
        var protocol = rig.Protocol(Route(minimum: 3, ids: $"{Output1} {Output2} {Output3}"));
        Assert.False((await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])))).Succeeded);
        var rows = ledger.Rows.Count;

        // The third record the run wrote is listed by the next try, which looks again without running the workflow again.
        platform.Put(FakeOsduPlatform.Record(Output3, WellKind));
        var resumed = await protocol.DeliverAsync(ledger.Track(Work(Document(), [H5()])));

        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Single(platform.Runs);
        Assert.Single(platform.Triggers(Workflow));
        Assert.Equal(rows + 1, ledger.Rows.Count);
        Assert.Equal(
            [OsduWorkflowProtocol.OutputSlot(Output1), OsduWorkflowProtocol.OutputSlot(Output2), OsduWorkflowProtocol.OutputSlot(Output3)],
            ledger.Rows.Where(r => r.Artifact.Role == ArtifactRoles.Output).Select(r => r.Artifact.Slot));
        Assert.Equal(1, ledger.Reports.Count(r => r.Report.Artifacts.Any(a => a.Role == ArtifactRoles.Run)));
    }

    [Fact]
    public async Task Artifacts_the_route_makes_nothing_of_and_runs_recorded_without_their_workflow_are_kept()
    {
        var platform = Platform();
        using var rig = new Rig(platform);
        var protocol = rig.Protocol(Route());
        var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow);
        var work = Work(Document());
        UndoItem[] items =
        [
            new(1, TargetArtifact.Created("session", ArtifactRoles.Session, "session-1"), unit.Id, unit.StartedUtc),
            new(2, new TargetArtifact { Slot = OsduWorkflowProtocol.RunSlot("r-1"), Role = ArtifactRoles.Run, TargetId = "r-1" }, unit.Id, unit.StartedUtc),
            new(3, new TargetArtifact { Slot = OsduWorkflowProtocol.OutputSlot("x"), Role = ArtifactRoles.Output, Status = ArtifactStatus.Intent, Locator = "somewhere" }, unit.Id, unit.StartedUtc),
        ];
        var undo = new UndoWork { Key = work.Key, TargetId = work.TargetId, Reason = UndoReason.Held, Items = items };

        var results = await protocol.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Kept, r.Outcome));
        Assert.Equal("the workflow route makes nothing of this kind", Assert.Single(results, r => r.Item.ArtifactId == 1).Note);
        Assert.Contains("nothing can ask about it", Assert.Single(results, r => r.Item.ArtifactId == 2).Note, StringComparison.Ordinal);
        Assert.Equal("the output names no record", Assert.Single(results, r => r.Item.ArtifactId == 3).Note);
        Assert.Empty(platform.Calls);
    }
}
