using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The one path every undo takes (docs/atomic-delivery-plan.md, When the undo runs): the route is held to answering each
/// artifact it was given once with an outcome an undo can have, anything else leaves every artifact failed and tried again;
/// what it answered becomes each record's attempt and settlements, with the backoff of a failed undo, the end of its tries,
/// and every note redacted.
/// </summary>
public sealed class UndoRunnerTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A route whose undo answers what the test says, recording what it was given.</summary>
    private sealed class Answering(Func<IReadOnlyList<UndoWork>, IReadOnlyList<UndoResult>> answer) : IDeliveryProtocol
    {
        public List<UndoWork> Given { get; } = [];

        public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

        public bool Undoes => true;

        public Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
        {
            Given.AddRange(works);
            return Task.FromResult(answer(works));
        }

        public Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default) => throw new InvalidOperationException("an undo never delivers");

        public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default) => throw new InvalidOperationException("an undo never verifies");

        public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
            => throw new InvalidOperationException("an undo removes through the route's undo");

        public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default) => throw new InvalidOperationException("an undo never reads");

        public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default) => throw new InvalidOperationException("an undo never probes");
    }

    private static readonly DeliveryKey Key = DeliveryKey.Derive("undo-runner", ["log-1"]);

    private static readonly Guid Unit = Guid.Parse("0191e1f0-0000-7000-8000-0000000000aa");

    private static RecordState Record(long? version = 7) => new()
    {
        DeliveryKey = Key,
        FlowId = FlowId.Of("undo-runner"),
        SourceKey = "log-1",
        MappingName = "WellLog",
        TargetId = "dev:work-product-component--WellLog:log-1",
        TargetVersion = version,
        TargetStateJson = """{"datasetIds":"dev:dataset--File.Generic:live"}""",
    };

    private static LedgerArtifact Artifact(long id, string role = ArtifactRoles.Dataset, ArtifactStatus status = ArtifactStatus.Due, int attempts = 0, string? targetId = null) => new()
    {
        ArtifactId = id,
        Key = Key,
        UnitId = Unit,
        UnitStartedUtc = Now.AddMinutes(-3),
        Slot = $"{role}:{id}",
        Role = role,
        TargetId = targetId ?? $"dev:dataset--File.Generic:{id}",
        Status = status,
        UndoAttempts = attempts,
        CreatedUtc = Now.AddMinutes(-3),
        UpdatedUtc = Now.AddMinutes(-2),
    };

    private static UndoRunner Runner(IDeliveryProtocol route) => new(route, new TestClock(new DateTimeOffset(Now)), "undo-tests", Guid.Parse("0191e1f0-0000-7000-8000-0000000000bb"));

    private static async Task<RecordUndo> RunOneAsync(IDeliveryProtocol route, params LedgerArtifact[] artifacts)
        => Assert.Single(await Runner(route).RunAsync([new UndoRequest(Record(), artifacts, UndoReason.Held, KeepRecord: false)], "corr-1", CancellationToken.None));

    [Fact]
    public async Task What_the_route_answered_becomes_the_records_attempt_and_settlements()
    {
        var route = new Answering(works => works.SelectMany(w => w.Items).Select(i => i.ArtifactId == 1 ? UndoResult.Removed(i, "soft-deleted") : UndoResult.Kept(i, "no call removes historian points")).ToList());

        var undo = await RunOneAsync(route, Artifact(1), Artifact(2, ArtifactRoles.Points, ArtifactStatus.Pending));

        Assert.Equal([(1L, ArtifactStatus.Removed, (DateTime?)null), (2L, ArtifactStatus.Kept, null)], undo.Settlements.Select(s => (s.ArtifactId, s.Status, s.RetryAtUtc)));
        var attempt = undo.Attempt;
        Assert.Equal((AttemptOutcome.Undone, AttemptPhases.Undo, (string?)null, 7L), (attempt.Outcome, attempt.Phase, attempt.Error, attempt.TargetVersion!.Value));
        Assert.Equal("undo-tests", undo.SettledBy);
        var result = JsonNode.Parse(attempt.ResultJson!)!;
        Assert.Equal("corr-1", result["correlationId"]!.GetValue<string>());
        Assert.Equal("held", result["undo"]!["reason"]!.GetValue<string>());
        Assert.Equal([Unit.ToString("D")], result["undo"]!["units"]!.AsArray().Select(u => u!.GetValue<string>()));
        Assert.Equal(["removed", "kept"], result["undo"]!["artifacts"]!.AsArray().Select(a => a!["outcome"]!.GetValue<string>()));
        Assert.Equal("1 removed, 1 kept", result["undo"]!["summary"]!.GetValue<string>());

        // The route was given the record as the ledger holds it, and each artifact as it stands, an open one as pending.
        var given = Assert.Single(route.Given);
        Assert.Equal((7L, "dev:dataset--File.Generic:live"), (given.CommittedVersion!.Value, given.TargetState["datasetIds"]));
        Assert.All(given.Items, i => Assert.Equal(ArtifactStatus.Pending, i.Artifact.Status));
    }

    [Fact]
    public async Task A_failed_undo_is_tried_again_with_backoff_until_it_has_used_its_tries()
    {
        var route = new Answering(works => works.SelectMany(w => w.Items).Select(i => UndoResult.Failed(i, "HTTP 503 Service Unavailable")).ToList());

        var first = await RunOneAsync(route, Artifact(1));
        var later = await RunOneAsync(route, Artifact(1, attempts: 4));
        var last = await RunOneAsync(route, Artifact(1, attempts: ArtifactLimits.MaxUndoAttempts - 1));

        Assert.Equal(ArtifactLimits.RetryAt(Now, 1), Assert.Single(first.Settlements).RetryAtUtc);
        Assert.Equal(ArtifactLimits.RetryAt(Now, 5), Assert.Single(later.Settlements).RetryAtUtc);
        Assert.Null(Assert.Single(last.Settlements).RetryAtUtc);
        Assert.Contains("1 of 1 artifact(s) could not be undone yet", first.Attempt.Error, StringComparison.Ordinal);
        Assert.True(ArtifactLimits.RetryAt(Now, 20) - Now <= TimeSpan.FromHours(6));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("twice")]
    [InlineData("not-an-outcome")]
    [InlineData("throws")]
    public async Task A_route_that_answers_what_it_was_not_given_or_fails_as_a_whole_leaves_every_artifact_failed(string misbehaviour)
    {
        var route = new Answering(works =>
        {
            var items = works.SelectMany(w => w.Items).ToList();
            return misbehaviour switch
            {
                "foreign" => [.. items.Select(i => UndoResult.Removed(i)), UndoResult.Removed(items[0] with { ArtifactId = 99 })],
                "twice" => [.. items.Select(i => UndoResult.Removed(i)), UndoResult.Gone(items[1])],
                "not-an-outcome" => [UndoResult.Removed(items[0]), new UndoResult(items[1], ArtifactStatus.Live, null)],
                _ => throw new HttpRequestException("the DDMS closed the connection"),
            };
        });

        var undo = await RunOneAsync(route, Artifact(1), Artifact(2));

        Assert.All(undo.Settlements, s => Assert.Equal(ArtifactStatus.Failed, s.Status));
        Assert.All(undo.Settlements, s => Assert.Equal(ArtifactLimits.RetryAt(Now, 1), s.RetryAtUtc));
        Assert.All(undo.Settlements, s => Assert.StartsWith("the undo could not be completed: ", s.Note, StringComparison.Ordinal));
        Assert.NotNull(undo.Attempt.Error);
    }

    [Fact]
    public async Task An_artifact_the_route_did_not_answer_is_failed_while_the_others_settle()
    {
        var route = new Answering(works => [UndoResult.Removed(works[0].Items[0])]);

        var undo = await RunOneAsync(route, Artifact(1), Artifact(2));

        Assert.Equal([ArtifactStatus.Removed, ArtifactStatus.Failed], undo.Settlements.Select(s => s.Status));
        Assert.Equal("the route gave no answer for it", undo.Settlements[1].Note);
    }

    [Fact]
    public async Task Notes_are_redacted_before_they_are_kept()
    {
        var route = new Answering(works => works.SelectMany(w => w.Items).Select(i => UndoResult.Failed(i, "HTTP 401 from GET /records: Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.secret-token-value")).ToList());

        var undo = await RunOneAsync(route, Artifact(1));

        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", Assert.Single(undo.Settlements).Note, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", undo.Attempt.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", undo.Attempt.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_without_artifacts_are_left_out_and_each_record_gets_its_own_undo_in_order()
    {
        var route = new Answering(works => works.SelectMany(w => w.Items).Select(i => UndoResult.Gone(i)).ToList());
        var other = Record() with { DeliveryKey = DeliveryKey.Derive("undo-runner", ["log-2"]), TargetId = "dev:work-product-component--WellLog:log-2" };
        var otherArtifact = Artifact(3) with { Key = other.DeliveryKey };

        var undos = await Runner(route).RunAsync(
            [
                new UndoRequest(Record(), [], UndoReason.Abandoned, KeepRecord: true),
                new UndoRequest(other, [otherArtifact], UndoReason.Abandoned, KeepRecord: true),
                new UndoRequest(Record(), [Artifact(1)], UndoReason.Failed, KeepRecord: false),
            ],
            null,
            CancellationToken.None);

        Assert.Equal([other.DeliveryKey, Key], undos.Select(u => u.DeliveryKey));
        Assert.Equal([true, false], route.Given.Select(w => w.KeepRecord));
        Assert.Null(JsonNode.Parse(undos[0].Attempt.ResultJson!)!["correlationId"]);
        Assert.Empty(await Runner(route).RunAsync([], null, CancellationToken.None));
    }

    [Fact]
    public async Task A_stopped_run_stops_the_undo_rather_than_failing_its_artifacts()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var route = new Answering(_ => throw new OperationCanceledException(stopping.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(route).RunAsync(
            [new UndoRequest(Record(), [Artifact(1)], UndoReason.Held, KeepRecord: false)], null, stopping.Token));
    }
}
