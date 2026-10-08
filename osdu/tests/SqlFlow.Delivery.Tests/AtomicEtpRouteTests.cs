using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Etp;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests.Etp;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The etp route as a unit of work (docs/atomic-delivery-plan.md, The routes) against the fake Reservoir DDMS, with a relay
/// that can drop the connection after a message reached the server and before its answer comes back: a dataspace the
/// delivery created reported on the first record that needed it, each new object declared before the commit whose answer can
/// be lost and reported pending after it, updates reporting nothing; and the undo after every failure point: objects the store
/// created after the unit began deleted in one transaction per dataspace, older ones kept, missing ones gone, a locked
/// dataspace unlocked and locked again, the dataspace itself kept, a refused commit, a refused read, an unreachable server and
/// a dropped connection each failing exactly their objects, every item answered once, and a second undo that finds them gone.
/// </summary>
public sealed class AtomicEtpRouteTests : IDisposable
{
    private const string Space = "demo/study";
    private const string OtherSpace = "demo/other";
    private const string Type = "obj_Grid2dRepresentation";
    private const string StoreType = "resqml20.obj_Grid2dRepresentation";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly HttpRuntime _http = new(new FlowReliability { TimeoutSeconds = 20 }, Secrets, TimeProvider.System, handler: null, allowLoopback: true);

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// A TCP relay between the route and the fake server that, once armed, drops both connections at the server's first answer
    /// after the condition holds: the message reached the server and was acted on, and its answer never comes back, as when the
    /// network between them fails. It drops once; every other byte goes through as it is.
    /// </summary>
    private sealed class DroppingRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _server;
        private readonly CancellationTokenSource _stopped = new();
        private readonly List<Task> _relays = [];
        private readonly Task _accepting;
        private Func<bool>? _drop;
        private int _dropped;

        public DroppingRelay(Uri server)
        {
            _server = server.Port;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Uri = new UriBuilder(server) { Port = ((IPEndPoint)_listener.LocalEndpoint).Port }.Uri;
            _accepting = Task.Run(() => AcceptAsync(_stopped.Token));
        }

        /// <summary>Where the route connects instead of the server.</summary>
        public Uri Uri { get; }

        /// <summary>How many connections the relay dropped.</summary>
        public int Dropped => Volatile.Read(ref _dropped);

        /// <summary>Drops the connection at the server's next answer once <paramref name="when"/> holds.</summary>
        public void DropWhen(Func<bool> when) => Volatile.Write(ref _drop, when);

        public async ValueTask DisposeAsync()
        {
            await _stopped.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            Task[] relays;
            lock (_relays)
            {
                relays = [.. _relays];
            }

            await _accepting.ConfigureAwait(false);
            await Task.WhenAll(relays).ConfigureAwait(false);
            _stopped.Dispose();
        }

        private async Task AcceptAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                lock (_relays)
                {
                    _relays.Add(Task.Run(() => RelayAsync(client, ct), CancellationToken.None));
                }
            }
        }

        private async Task RelayAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var server = new TcpClient())
            {
                try
                {
                    await server.ConnectAsync(IPAddress.Loopback, _server, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var up = PumpAsync(client.GetStream(), server.GetStream(), answers: false, linked.Token);
                var down = PumpAsync(server.GetStream(), client.GetStream(), answers: true, linked.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);

                // Either side ending ends the connection on both: closing the sockets is what the route sees as the drop.
                await linked.CancelAsync().ConfigureAwait(false);
                client.Close();
                server.Close();
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, bool answers, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    if (answers && Volatile.Read(ref _drop) is { } drop && drop() && Interlocked.CompareExchange(ref _drop, null, drop) == drop)
                    {
                        Interlocked.Increment(ref _dropped);
                        return;
                    }

                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection ended; the other pump ends with it.
            }
        }
    }

    /// <summary>
    /// What the ledger keeps of one record's unit of work, emulated in memory: each step a route reports, by name, as a later
    /// try of the unit reads it back, and each artifact by its slot, a slot reported again updating its one row as the
    /// ledger's upsert does (SqlServerLedgerBulk.Artifacts.cs); a slot the route settled itself reopens when it reports it again.
    /// </summary>
    private sealed class Ledger(Func<int> calls)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _steps = new(StringComparer.Ordinal);
        private long _ids;

        /// <summary>Where the ledger's artifact numbers start, so the rows of several records stay apart.</summary>
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

        public ArtifactRow Slot(string slot) => Assert.Single(Rows, r => r.Artifact.Slot == slot);

        public UndoWork Undo(DeliveryWork work, UndoReason reason = UndoReason.Failed) => new()
        {
            Key = work.Key,
            TargetId = work.TargetId,
            TargetState = work.TargetState,
            Reason = reason,
            Items = Rows.Where(r => ArtifactStatuses.IsOpen(r.Artifact.Status)).Select(r => new UndoItem(r.Id, r.Artifact, Unit.Id, Unit.StartedUtc)).ToList(),
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

    private OsduEtpProtocol Route(Uri endpoint, bool locked = false)
    {
        var connection = new EtpConnection(
            _http,
            endpoint,
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" },
            [],
            new EtpSessionOptions { MaxMessageBytes = 2_000_000, RequestTimeout = TimeSpan.FromSeconds(20), KeepAlive = TimeSpan.Zero },
            NullLogger.Instance);
        return new OsduEtpProtocol(connection, new EtpTarget { Dataspace = Space, Lock = locked }, "dev", NullLogger<OsduEtpProtocol>.Instance);
    }

    private static DeliveryWork Work(Guid uuid, string title, string? dataspace = null, JsonArray? arrays = null, string? arrayPath = null, IReadOnlyDictionary<string, string>? state = null)
    {
        var data = new JsonObject { ["Xml"] = EtpSamples.Object(uuid, title, Type, arrayPath) };
        if (dataspace is not null)
        {
            data["Dataspace"] = dataspace;
        }

        if (arrays is not null)
        {
            data["Arrays"] = arrays;
        }

        return new DeliveryWork
        {
            Key = DeliveryKey.Derive("atomic-etp", [uuid.ToString("N")]),
            TargetId = $"dev:etp:{uuid:N}",
            SourceKey = title,
            Document = new JsonObject
            {
                ["kind"] = $"energistics:etp:{Type}:2.0.1",
                ["acl"] = new JsonObject
                {
                    ["viewers"] = new JsonArray { "data.default.viewers@dev.example.com" },
                    ["owners"] = new JsonArray { "data.default.owners@dev.example.com" },
                },
                ["legal"] = new JsonObject
                {
                    ["legaltags"] = new JsonArray { "dev-public-usa-dataset-1" },
                    ["otherRelevantDataCountries"] = new JsonArray { "US" },
                },
                ["data"] = data,
            },
            DeliverMetadata = true,
            DeliverPayload = false,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private static string ObjectUri(Guid uuid, string dataspace = Space) => EtpObjectXml.ObjectUri(dataspace, StoreType, uuid);

    private static FakeEtpServer.Dataspace SpaceOf(FakeEtpServer server, string path = Space) => server.Spaces[EtpObjectXml.DataspaceUri(path)];

    /// <summary>The position of the first message of <typeparamref name="T"/> the server received at or after <paramref name="from"/>.</summary>
    private static int Received<T>(FakeEtpServer server, int from = 0)
        where T : class, IEtpMessage
    {
        var frames = server.Received;
        for (var i = from; i < frames.Count; i++)
        {
            if (frames[i].Body is T)
            {
                return i;
            }
        }

        Assert.Fail($"the server received no {typeof(T).Name} after message {from}");
        return -1;
    }

    /// <summary>Makes the store say it created <paramref name="uri"/> at <paramref name="createdUtc"/>.</summary>
    private static void Created(FakeEtpServer server, string uri, DateTime createdUtc)
    {
        foreach (var space in server.Spaces.Values)
        {
            foreach (var (key, stored) in space.Objects.ToList())
            {
                if (string.Equals(stored.Uri, uri, StringComparison.Ordinal))
                {
                    space.Objects[key] = stored with { LastWrite = EtpTime.Microseconds(new DateTimeOffset(createdUtc, TimeSpan.Zero)) };
                }
            }
        }
    }

    /// <summary>An object another system left in the dataspace that names an array nobody supplied, so the server refuses every commit.</summary>
    private static void Unsupplied(FakeEtpServer server)
    {
        var uuid = Guid.NewGuid();
        SpaceOf(server).Objects[$"{StoreType}({uuid:D})"] = new FakeEtpServer.StoredObject(
            ObjectUri(uuid), StoreType, uuid, "Unsupplied", Encoding.UTF8.GetBytes(EtpSamples.Object(uuid, "Unsupplied", Type, "RESQML/nobody")), EtpTime.Microseconds(DateTimeOffset.UtcNow));
    }

    private static void AssertEachOnce(IReadOnlyList<UndoWork> works, IReadOnlyList<UndoResult> results)
        => Assert.Equal(works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(), results.Select(r => r.Item.ArtifactId).Order());

    private static UndoResult ResultFor(IReadOnlyList<UndoResult> results, Guid uuid, string dataspace = Space)
        => Assert.Single(results, r => r.Item.Artifact.TargetId == ObjectUri(uuid, dataspace));

    [Fact]
    public async Task A_dataspace_the_delivery_created_is_reported_once_on_the_first_record_that_needed_it()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var first = new Ledger(() => server.Received.Count);
        var second = new Ledger(() => server.Received.Count) { FirstId = 100 };

        var outcomes = await route.DeliverBatchAsync([first.Track(Work(Guid.NewGuid(), "First")), second.Track(Work(Guid.NewGuid(), "Second"))]);

        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        var created = Assert.Single(first.Reports, r => r.Report.Step == OsduEtpProtocol.DataspaceStep);
        var dataspace = Assert.Single(created.Report.Artifacts);
        Assert.Equal(
            (OsduEtpProtocol.DataspaceSlot, ArtifactRoles.Dataspace, "dev:dataset--ETPDataspace:demo-study", Space, ArtifactStatus.Pending),
            (dataspace.Slot, dataspace.Role, dataspace.TargetId, dataspace.Locator, dataspace.Status));
        Assert.Equal(Assert.Single(server.StorageRecords), dataspace.TargetId);
        Assert.True(created.Calls > Received<PutDataspaces>(server), "the dataspace was reported before the server created it");
        Assert.Equal("yes", created.Report.Returned["created"]);
        Assert.DoesNotContain(second.Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace);

        // A later delivery into the dataspace finds it and reports no dataspace.
        var later = new Ledger(() => server.Received.Count);
        Assert.True((await route.DeliverAsync(later.Track(Work(Guid.NewGuid(), "Third")))).Succeeded);
        Assert.DoesNotContain(later.Reports, r => r.Report.Step == OsduEtpProtocol.DataspaceStep);
        Assert.DoesNotContain(later.Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace);
    }

    [Fact]
    public async Task A_new_object_is_declared_before_the_commit_and_reported_pending_once_the_commit_answered()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);

        var outcome = await route.DeliverAsync(ledger.Track(Work(uuid, "Declared")));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var commit = Received<CommitTransaction>(server);
        var declared = Assert.Single(ledger.Reports, r => r.Report.Step == OsduEtpProtocol.CommitIntentStep);
        var intent = Assert.Single(declared.Report.Artifacts);
        Assert.Equal(
            (OsduEtpProtocol.ObjectSlot, ArtifactRoles.Objects, ObjectUri(uuid), Space, ArtifactStatus.Intent),
            (intent.Slot, intent.Role, intent.TargetId, intent.Locator, intent.Status));
        Assert.True(declared.Calls <= commit, "the object's intent was reported after the commit was sent");
        Assert.True(declared.Calls > Received<PutDataObjects>(server), "the object's intent was reported before its put");

        var landed = Assert.Single(ledger.Reports, r => r.Report.Step == OsduEtpProtocol.CommitStep);
        Assert.True(landed.Calls > commit);
        var pending = Assert.Single(landed.Report.Artifacts);
        Assert.Equal((ObjectUri(uuid), Space, ArtifactStatus.Pending), (pending.TargetId, pending.Locator, pending.Status));
        Assert.Equal(declared.Report.Returned["transaction"], landed.Report.Returned["transaction"]);

        // Two reports, one row per slot: the dataspace, and the object.
        Assert.Equal([OsduEtpProtocol.DataspaceSlot, OsduEtpProtocol.ObjectSlot], ledger.Rows.Select(r => r.Artifact.Slot));
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Pending, r.Artifact.Status));
    }

    [Fact]
    public async Task An_object_the_ledger_already_knows_is_updated_without_declaring_anything()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var first = await route.DeliverAsync(Work(uuid, "Original"));
        var ledger = new Ledger(() => server.Received.Count);

        var update = await route.DeliverAsync(ledger.Track(Work(uuid, "Changed", state: first.Returned)));

        Assert.True(update.Succeeded, update.Failure?.Message);
        Assert.Equal("Changed", Assert.Single(SpaceOf(server).Objects.Values).Name);
        Assert.All(ledger.Reports, r => Assert.Empty(r.Report.Artifacts));
        Assert.Empty(ledger.Rows);
    }

    [Fact]
    public async Task A_commit_the_server_failed_leaves_the_intent_and_the_undo_finds_no_object_to_delete()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        server.FailNext["Transaction.CommitTransaction"] = new ErrorInfo { Code = EtpErrorCodes.InvalidState, Message = "Cannot COMMIT Transaction: disk full" };
        var work = ledger.Track(Work(uuid, "Refused"));

        var outcome = await route.DeliverAsync(work);

        Assert.IsType<EtpProtocolException>(outcome.Failure);
        Assert.Empty(SpaceOf(server).Objects);
        Assert.Equal(ArtifactStatus.Intent, ledger.Slot(OsduEtpProtocol.ObjectSlot).Artifact.Status);
        Assert.DoesNotContain(ledger.Reports, r => r.Report.Step == OsduEtpProtocol.CommitStep);

        var undo = ledger.Undo(work);
        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var gone = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
        Assert.Equal($"the dataspace {Space} holds no object at {ObjectUri(uuid)}: the commit that would have created it did not land", gone.Note);
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataspace).Outcome);
        Assert.DoesNotContain(server.Received, f => f.Body is DeleteDataObjects);
    }

    [Fact]
    public async Task A_commit_the_server_refused_holds_the_record_with_its_intent_and_the_undo_finds_no_object()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        Assert.True((await route.DeliverAsync(Work(Guid.NewGuid(), "Existing"))).Succeeded);
        Unsupplied(server);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Refused at commit"));

        var outcome = await route.DeliverAsync(work);

        var held = Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Contains("Missing array(s)", held.Message, StringComparison.Ordinal);
        Assert.Null(server.Object(ObjectUri(uuid)));
        Assert.Equal(ArtifactStatus.Intent, Assert.Single(ledger.Rows).Artifact.Status);

        var gone = Assert.Single(await route.UndoAsync([ledger.Undo(work, UndoReason.Held)]));
        Assert.Equal(ArtifactStatus.Gone, gone.Outcome);
    }

    [Fact]
    public async Task A_commit_whose_answer_was_lost_leaves_the_intent_and_the_undo_deletes_the_object_that_landed()
    {
        await using var server = new FakeEtpServer();
        await using var relay = new DroppingRelay(server.Uri);
        var route = Route(relay.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        relay.DropWhen(() => server.Received.Any(f => f.Body is CommitTransaction));
        var work = ledger.Track(Work(uuid, "Lost answer"));

        var outcome = await route.DeliverAsync(work);

        // The server committed, and the route never heard so: the ledger names the object by its intent alone.
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, relay.Dropped);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.False(server.InTransaction);
        var intent = ledger.Slot(OsduEtpProtocol.ObjectSlot).Artifact;
        Assert.Equal((ArtifactStatus.Intent, ObjectUri(uuid), Space), (intent.Status, intent.TargetId, intent.Locator));

        var undo = ledger.Undo(work);
        var from = server.Received.Count;
        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var removed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Equal($"{ObjectUri(uuid)} deleted from the dataspace {Space}, for good (the store keeps no deleted objects); its arrays stay in the dataspace, since the store does not delete an object's arrays with it", removed.Note);
        Assert.Null(server.Object(ObjectUri(uuid)));
        Assert.True(Received<GetDataObjects>(server, from) < Received<StartTransaction>(server, from));
        Assert.True(Received<DeleteDataObjects>(server, from) < Received<CommitTransaction>(server, from));
        Assert.False(server.InTransaction);

        // A second undo of the same artifacts finds the object gone and sends no delete.
        var deletes = server.Received.Count(f => f.Body is DeleteDataObjects);
        var again = await route.UndoAsync([undo]);
        Assert.Equal(ArtifactStatus.Gone, ResultFor(again, uuid).Outcome);
        Assert.Equal(deletes, server.Received.Count(f => f.Body is DeleteDataObjects));
    }

    [Fact]
    public async Task A_resumed_try_after_a_lost_commit_answer_declares_the_object_again_under_the_same_slot()
    {
        await using var server = new FakeEtpServer();
        await using var relay = new DroppingRelay(server.Uri);
        var route = Route(relay.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        relay.DropWhen(() => server.Received.Any(f => f.Body is CommitTransaction));
        Assert.False((await route.DeliverAsync(ledger.Track(Work(uuid, "Lost answer")))).Succeeded);

        var resumed = await route.DeliverAsync(ledger.Track(Work(uuid, "Lost answer")));

        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(ObjectUri(uuid), resumed.Returned[OsduEtpProtocol.UriValue]);
        Assert.Equal(2, ledger.Reports.Count(r => r.Report.Step == OsduEtpProtocol.CommitIntentStep));
        var row = ledger.Slot(OsduEtpProtocol.ObjectSlot).Artifact;
        Assert.Equal((ArtifactStatus.Pending, ObjectUri(uuid)), (row.Status, row.TargetId));
        Assert.Single(ledger.Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace);
        Assert.Single(SpaceOf(server).Objects);
    }

    [Fact]
    public async Task An_object_committed_whose_arrays_were_never_filled_is_reported_pending_and_its_undo_never_throws()
    {
        await using var server = new FakeEtpServer { MaxMessageBytes = 70_000 };
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var values = new JsonArray();
        for (var i = 0; i < 20_000; i++)
        {
            values.Add(i * 0.5);
        }

        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Unfilled", arrayPath: "RESQML/deep", arrays: new JsonArray
        {
            new JsonObject { ["Path"] = "RESQML/deep", ["Type"] = "arrayOfDouble", ["Dimensions"] = new JsonArray { 10_000, 2 }, ["Values"] = values },
        }));
        server.FailNext["DataArray.PutDataSubarrays"] = new ErrorInfo { Code = EtpErrorCodes.InvalidState, Message = "storage unavailable" };

        var outcome = await route.DeliverAsync(work);

        // The commit landed before the fill failed: the object is the unit's, pending, and its array declared and empty.
        Assert.IsType<EtpProtocolException>(outcome.Failure);
        Assert.Contains("storage unavailable", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.Empty(SpaceOf(server).Arrays["RESQML/deep"].Slices);
        Assert.Equal(ArtifactStatus.Pending, ledger.Slot(OsduEtpProtocol.ObjectSlot).Artifact.Status);
        Assert.True(Assert.Single(ledger.Reports, r => r.Report.Step == OsduEtpProtocol.CommitStep).Calls < Received<PutDataSubarrays>(server));

        var undo = ledger.Undo(work);
        var results = await route.UndoAsync([undo]);

        // The store keeps an object's arrays when the object is deleted (brief 7.7: no cascade), and the fake, as it reads the
        // server's commit check (4.7, step 7), refuses a commit that leaves an array no object names: the undo answers the
        // object failed with the server's reason, rolls its transaction back, and leaves the object for its next try.
        AssertEachOnce([undo], results);
        var failed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains("refused the commit of the delete", failed.Note, StringComparison.Ordinal);
        Assert.Contains("Orphan array(s)", failed.Note, StringComparison.Ordinal);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.False(server.InTransaction);
    }

    [Fact]
    public async Task A_lock_that_failed_after_the_commit_leaves_a_pending_object_the_undo_deletes()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri, locked: true);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        server.FailNext["DataspaceOSDU.LockDataspaces"] = new ErrorInfo { Code = EtpErrorCodes.RequestDenied, Message = "no permission to lock" };
        var work = ledger.Track(Work(uuid, "Unlocked"));

        var outcome = await route.DeliverAsync(work);

        Assert.Contains("no permission to lock", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.False(SpaceOf(server).Locked);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.Equal(ArtifactStatus.Pending, ledger.Slot(OsduEtpProtocol.ObjectSlot).Artifact.Status);

        var results = await route.UndoAsync([ledger.Undo(work)]);

        Assert.Equal(ArtifactStatus.Removed, ResultFor(results, uuid).Outcome);
        Assert.Null(server.Object(ObjectUri(uuid)));
        Assert.False(SpaceOf(server).Locked);
    }

    [Fact]
    public async Task A_locked_dataspace_is_unlocked_for_the_undo_and_locked_again()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri, locked: true);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Published"));
        Assert.True((await route.DeliverAsync(work)).Succeeded);
        Assert.True(SpaceOf(server).Locked);

        var from = server.Received.Count;
        var results = await route.UndoAsync([ledger.Undo(work, UndoReason.Abandoned)]);

        Assert.Equal(ArtifactStatus.Removed, ResultFor(results, uuid).Outcome);
        Assert.Null(server.Object(ObjectUri(uuid)));
        Assert.True(SpaceOf(server).Locked);
        var locks = server.Received.Skip(from).Select(f => f.Body).OfType<LockDataspaces>().ToList();
        Assert.Equal([false, true], locks.Select(l => l.Lock));
        var unlock = Received<LockDataspaces>(server, from);
        Assert.True(unlock < Received<DeleteDataObjects>(server, from));
        Assert.True(Received<CommitTransaction>(server, from) < Received<LockDataspaces>(server, unlock + 1));
    }

    [Fact]
    public async Task A_delete_whose_commit_the_server_refuses_fails_rolls_back_and_locks_the_dataspace_again()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri, locked: true);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Kept by a refusal"));
        Assert.True((await route.DeliverAsync(work)).Succeeded);
        Unsupplied(server);

        var from = server.Received.Count;
        var undo = ledger.Undo(work);
        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var failed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Equal($"{ObjectUri(uuid)}: the Reservoir DDMS refused the commit of the delete: 1 Missing array(s)", failed.Note);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.Contains(server.Received.Skip(from), f => f.Body is RollbackTransaction);
        Assert.False(server.InTransaction);
        Assert.True(SpaceOf(server).Locked);
    }

    [Fact]
    public async Task A_commit_the_server_fails_during_the_undo_fails_its_dataspace_and_rolls_back()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Commit failed"));
        Assert.True((await route.DeliverAsync(work)).Succeeded);
        server.FailNext["Transaction.CommitTransaction"] = new ErrorInfo { Code = EtpErrorCodes.InvalidState, Message = "Cannot COMMIT Transaction: lost the database" };

        var results = await route.UndoAsync([ledger.Undo(work)]);

        var failed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.StartsWith($"the dataspace {Space}: ", failed.Note, StringComparison.Ordinal);
        Assert.Contains("lost the database", failed.Note, StringComparison.Ordinal);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.False(server.InTransaction);

        var retried = await route.UndoAsync([ledger.Undo(work)]);
        Assert.Equal(ArtifactStatus.Removed, ResultFor(retried, uuid).Outcome);
    }

    [Fact]
    public async Task An_object_the_store_created_before_the_unit_began_is_kept_and_one_within_the_clock_skew_is_deleted()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var (older, recent) = (Guid.NewGuid(), Guid.NewGuid());
        var a = new Ledger(() => server.Received.Count);
        var b = new Ledger(() => server.Received.Count) { FirstId = 100 };
        var works = new[] { a.Track(Work(older, "Older")), b.Track(Work(recent, "Recent")) };
        Assert.All(await route.DeliverBatchAsync(works), o => Assert.True(o.Succeeded, o.Failure?.Message));
        Created(server, ObjectUri(older), a.Unit.StartedUtc - TimeSpan.FromDays(2));
        Created(server, ObjectUri(recent), b.Unit.StartedUtc - TimeSpan.FromMinutes(4));

        UndoWork[] undos = [a.Undo(works[0]), b.Undo(works[1])];
        var results = await route.UndoAsync(undos);

        AssertEachOnce(undos, results);
        var kept = ResultFor(results, older);
        Assert.Equal(ArtifactStatus.Kept, kept.Outcome);
        Assert.Contains("before this delivery began", kept.Note, StringComparison.Ordinal);
        Assert.Contains("the store keeps no earlier versions", kept.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, ResultFor(results, recent).Outcome);
        Assert.NotNull(server.Object(ObjectUri(older)));
        Assert.Null(server.Object(ObjectUri(recent)));

        // Only the object the unit made went in the delete.
        var delete = Assert.Single(server.Received.Select(f => f.Body).OfType<DeleteDataObjects>());
        Assert.Equal([ObjectUri(recent)], delete.Uris.Values);
    }

    [Fact]
    public async Task The_dataspace_is_kept_and_artifacts_the_route_cannot_find_or_does_not_make_are_kept()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var unit = new DeliveryUnit(Guid.NewGuid(), DateTime.UtcNow);
        UndoItem[] items =
        [
            new(1, TargetArtifact.Created(OsduEtpProtocol.DataspaceSlot, ArtifactRoles.Dataspace, "dev:dataset--ETPDataspace:demo-study", locator: Space), unit.Id, unit.StartedUtc),
            new(2, TargetArtifact.Created("session", ArtifactRoles.Session, "s-1"), unit.Id, unit.StartedUtc),
            new(3, TargetArtifact.Created(OsduEtpProtocol.ObjectSlot, ArtifactRoles.Objects, ObjectUri(Guid.NewGuid())), unit.Id, unit.StartedUtc),
        ];
        var undo = new UndoWork { Key = DeliveryKey.Derive("atomic-etp", ["hand-made"]), TargetId = "dev:etp:hand-made", Reason = UndoReason.Removed, Items = items };

        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Kept, r.Outcome));
        Assert.Contains("never deletes a dataspace", Assert.Single(results, r => r.Item.ArtifactId == 1).Note, StringComparison.Ordinal);
        Assert.Equal("the etp route makes nothing of this kind", Assert.Single(results, r => r.Item.ArtifactId == 2).Note);
        Assert.Equal("the object's URI or dataspace was not recorded, so nothing can find it", Assert.Single(results, r => r.Item.ArtifactId == 3).Note);

        // Nothing to delete, so no session was opened.
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task A_read_the_server_refuses_fails_only_the_objects_of_its_dataspace()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var (here, there) = (Guid.NewGuid(), Guid.NewGuid());
        var a = new Ledger(() => server.Received.Count);
        var b = new Ledger(() => server.Received.Count) { FirstId = 100 };
        var works = new[] { a.Track(Work(here, "Here")), b.Track(Work(there, "There", dataspace: OtherSpace)) };
        Assert.All(await route.DeliverBatchAsync(works), o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(ObjectUri(there, OtherSpace), b.Slot(OsduEtpProtocol.ObjectSlot).Artifact.TargetId);
        Assert.Equal(OtherSpace, b.Slot(OsduEtpProtocol.ObjectSlot).Artifact.Locator);
        server.FailNext["Store.GetDataObjects"] = new ErrorInfo { Code = EtpErrorCodes.RequestDenied, Message = "no read access" };

        UndoWork[] undos = [a.Undo(works[0]), b.Undo(works[1])];
        var results = await route.UndoAsync(undos);

        AssertEachOnce(undos, results);
        var failed = ResultFor(results, here);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.StartsWith($"the dataspace {Space}: ", failed.Note, StringComparison.Ordinal);
        Assert.Contains("no read access", failed.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, ResultFor(results, there, OtherSpace).Outcome);
        Assert.NotNull(server.Object(ObjectUri(here)));
        Assert.Null(server.Object(ObjectUri(there, OtherSpace)));
        Assert.All(results.Where(r => r.Item.Artifact.Role == ArtifactRoles.Dataspace), r => Assert.Equal(ArtifactStatus.Kept, r.Outcome));
    }

    [Fact]
    public async Task An_unreachable_server_fails_every_object_and_a_later_undo_deletes_them()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Unreachable"));
        Assert.True((await route.DeliverAsync(work)).Succeeded);
        server.RefuseUpgradeWith = 503;

        var undo = ledger.Undo(work);
        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var failed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains("HTTP 503", failed.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Kept, Assert.Single(results, r => r.Item.Artifact.Role == ArtifactRoles.Dataspace).Outcome);
        Assert.NotNull(server.Object(ObjectUri(uuid)));

        server.RefuseUpgradeWith = null;
        Assert.Equal(ArtifactStatus.Removed, ResultFor(await route.UndoAsync([undo]), uuid).Outcome);
    }

    [Fact]
    public async Task A_connection_dropped_during_the_undo_fails_its_objects_and_a_later_undo_deletes_them()
    {
        await using var server = new FakeEtpServer();
        await using var relay = new DroppingRelay(server.Uri);
        var route = Route(relay.Uri);
        var uuid = Guid.NewGuid();
        var ledger = new Ledger(() => server.Received.Count);
        var work = ledger.Track(Work(uuid, "Dropped"));
        Assert.True((await route.DeliverAsync(work)).Succeeded);
        relay.DropWhen(() => server.Received.Any(f => f.Body is GetDataObjects));

        var undo = ledger.Undo(work);
        var results = await route.UndoAsync([undo]);

        AssertEachOnce([undo], results);
        var failed = ResultFor(results, uuid);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.StartsWith($"the dataspace {Space}: ", failed.Note, StringComparison.Ordinal);
        Assert.Equal(1, relay.Dropped);
        Assert.NotNull(server.Object(ObjectUri(uuid)));
        Assert.DoesNotContain(server.Received, f => f.Body is DeleteDataObjects);

        var retried = await route.UndoAsync([undo]);
        Assert.Equal(ArtifactStatus.Removed, ResultFor(retried, uuid).Outcome);
        Assert.Null(server.Object(ObjectUri(uuid)));
    }

    [Fact]
    public async Task A_batch_into_two_dataspaces_is_undone_in_one_transaction_per_dataspace()
    {
        await using var server = new FakeEtpServer();
        var route = Route(server.Uri);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var ledgers = ids.Select((_, i) => new Ledger(() => server.Received.Count) { FirstId = i * 100 }).ToList();
        var works = new[]
        {
            ledgers[0].Track(Work(ids[0], "One")),
            ledgers[1].Track(Work(ids[1], "Two", dataspace: OtherSpace)),
            ledgers[2].Track(Work(ids[2], "Three")),
        };
        Assert.All(await route.DeliverBatchAsync(works), o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Equal(2, server.StorageRecords.Count);

        // Each dataspace created is reported once, on the first record of the batch that needed it.
        Assert.Single(ledgers[0].Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace);
        Assert.Equal("dev:dataset--ETPDataspace:demo-other", Assert.Single(ledgers[1].Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace).Artifact.TargetId);
        Assert.DoesNotContain(ledgers[2].Rows, r => r.Artifact.Role == ArtifactRoles.Dataspace);

        var from = server.Received.Count;
        var undos = works.Select((w, i) => ledgers[i].Undo(w)).ToList();
        var results = await route.UndoAsync(undos);

        AssertEachOnce(undos, results);
        Assert.All(results.Where(r => r.Item.Artifact.Role == ArtifactRoles.Objects), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.All(ids, id => Assert.Null(server.Object(ObjectUri(id)) ?? server.Object(ObjectUri(id, OtherSpace))));
        var deletes = server.Received.Skip(from).Select(f => f.Body).OfType<DeleteDataObjects>().ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Equal(new[] { ObjectUri(ids[0]), ObjectUri(ids[2]) }.Order(StringComparer.Ordinal), deletes[0].Uris.Values.Order(StringComparer.Ordinal));
        Assert.Equal([ObjectUri(ids[1], OtherSpace)], deletes[1].Uris.Values);
        Assert.Equal(2, server.Received.Skip(from).Count(f => f.Body is CommitTransaction));
        Assert.Equal(2, server.Spaces.Count);
    }
}
