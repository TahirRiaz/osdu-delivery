using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>One record whose aborted units an undo takes: the record as the ledger holds it, the artifacts to undo, why, and whether newer work writes the record again.</summary>
public sealed record UndoRequest(RecordState Record, IReadOnlyList<LedgerArtifact> Artifacts, UndoReason Reason, bool KeepRecord);

/// <summary>
/// Undoes what aborted deliveries left in OSDU (docs/atomic-delivery-plan.md, When the undo runs): the one path the worker (a
/// held or failed try, a unit abandoned at a claim), the sweep and a removal take. It hands the records' artifacts to the route
/// (<see cref="IDeliveryProtocol.UndoAsync"/>), holds the route to answering every artifact once, and turns what it answered
/// into each record's undo: an attempt (outcome undone, phase undo) naming every artifact and what became of it, and the
/// settlements the ledger writes with it. A route that cannot be reached, or that answers for an artifact it was not given,
/// leaves the artifacts failed, tried again by the sweep with backoff, never lost.
/// </summary>
public sealed class UndoRunner
{
    private readonly IDeliveryProtocol _protocol;
    private readonly TimeProvider _time;
    private readonly string _worker;
    private readonly Guid? _runId;

    public UndoRunner(IDeliveryProtocol protocol, TimeProvider time, string worker, Guid? runId)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        _protocol = protocol;
        _time = time;
        _worker = worker;
        _runId = runId;
    }

    /// <summary>
    /// Undoes each request's artifacts and returns each record's undo, in the order of <paramref name="requests"/>, leaving out
    /// a request with no artifacts. The correlation id names the undo's calls on its attempt.
    /// </summary>
    public async Task<IReadOnlyList<RecordUndo>> RunAsync(IReadOnlyList<UndoRequest> requests, string? correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var wanted = requests.Where(r => r.Artifacts.Count > 0).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var started = _time.GetUtcNow().UtcDateTime;
        var works = wanted.Select(ToWork).ToList();
        var byArtifact = new Dictionary<long, UndoResult>();
        string? failure = null;
        try
        {
            var answered = await _protocol.UndoAsync(works, ct).ConfigureAwait(false);
            var given = works.SelectMany(w => w.Items).Select(i => i.ArtifactId).ToHashSet();
            foreach (var result in answered)
            {
                if (!given.Contains(result.Item.ArtifactId))
                {
                    throw new DeliveryException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {DeliveryProtocols.Name(_protocol.Kind)} route answered an undo of artifact {result.Item.ArtifactId}, which it was not given."));
                }

                if (!ArtifactStatuses.IsUndoOutcome(result.Outcome))
                {
                    throw new DeliveryException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {DeliveryProtocols.Name(_protocol.Kind)} route answered artifact {result.Item.ArtifactId} as {ArtifactStatuses.Name(result.Outcome)}, which is not an outcome an undo can have."));
                }

                if (!byArtifact.TryAdd(result.Item.ArtifactId, result))
                {
                    throw new DeliveryException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {DeliveryProtocols.Name(_protocol.Kind)} route answered artifact {result.Item.ArtifactId} twice."));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nothing the route answered can be trusted once it failed as a whole, so every artifact is tried again later.
            failure = HeaderRedaction.RedactMessage(ex.Message);
            byArtifact.Clear();
        }

        var completed = _time.GetUtcNow().UtcDateTime;
        var undos = new List<RecordUndo>(wanted.Count);
        foreach (var (request, work) in wanted.Zip(works))
        {
            var results = work.Items
                .Select(item => byArtifact.TryGetValue(item.ArtifactId, out var result)
                    ? result
                    : UndoResult.Failed(item, failure is null ? "the route gave no answer for it" : $"the undo could not be completed: {failure}"))
                .ToList();
            undos.Add(ToUndo(request, work, results, started, completed, correlationId));
        }

        return undos;
    }

    private static UndoWork ToWork(UndoRequest request)
    {
        var record = request.Record;
        var items = request.Artifacts.OrderBy(a => a.ArtifactId).Select(a => a.ToItem()).ToList();
        var targetId = record.TargetId ?? record.ClaimedTargetId
            ?? request.Artifacts.FirstOrDefault(a => ArtifactRoles.IsTheRecord(a.Role))?.TargetId
            ?? string.Empty;
        return new UndoWork
        {
            Key = record.DeliveryKey,
            TargetId = targetId,
            TargetState = JsonMerge.ToValues(record.TargetStateJson),
            CommittedVersion = record.TargetVersion,
            Reason = request.Reason,
            KeepRecord = request.KeepRecord,
            Items = items,
        };
    }

    private RecordUndo ToUndo(UndoRequest request, UndoWork work, IReadOnlyList<UndoResult> results, DateTime started, DateTime completed, string? correlationId)
    {
        var attemptsOf = request.Artifacts.ToDictionary(a => a.ArtifactId, a => a.UndoAttempts);
        var settlements = results
            .Select(r =>
            {
                var note = r.Note is null ? null : HeaderRedaction.RedactMessage(r.Note);
                var tried = attemptsOf.GetValueOrDefault(r.Item.ArtifactId) + 1;
                var retry = r.Outcome == ArtifactStatus.Failed && tried < ArtifactLimits.MaxUndoAttempts
                    ? ArtifactLimits.RetryAt(completed, tried)
                    : (DateTime?)null;
                return new ArtifactSettlement(r.Item.ArtifactId, r.Outcome, note, retry);
            })
            .ToList();
        var failed = results.Where(r => r.Outcome == ArtifactStatus.Failed).ToList();
        var error = failed.Count == 0
            ? null
            : HeaderRedaction.RedactMessage(string.Create(
                CultureInfo.InvariantCulture,
                $"{failed.Count} of {results.Count} artifact(s) could not be undone yet: {string.Join("; ", failed.Take(3).Select(Describe))}{(failed.Count > 3 ? $"; and {failed.Count - 3} more" : string.Empty)}"));
        var record = request.Record;

        // The record itself given back the version the ledger holds: the ledger's version moves to the write that did it.
        var moved = results
            .Where(r => r.Outcome == ArtifactStatus.Restored && r.Rewrite is not null && ArtifactRoles.IsTheRecord(r.Item.Artifact.Role)
                && string.Equals(r.Item.Artifact.TargetId ?? work.TargetId, work.TargetId, StringComparison.Ordinal))
            .Select(r => r.Rewrite!.Value)
            .Where(w => w.WrittenBack == record.TargetVersion)
            .Select(w => new RecordVersionMove(w.WrittenBack, w.NewVersion))
            .LastOrDefault();
        var attempt = new AttemptRecord
        {
            DeliveryKey = record.DeliveryKey,
            SubmissionId = record.LastSubmissionId,
            RunId = _runId,
            Worker = _worker,
            StartedUtc = started,
            CompletedUtc = completed,
            Outcome = AttemptOutcome.Undone,
            Phase = AttemptPhases.Undo,
            TargetVersion = record.TargetVersion,
            Error = error,
            ResultJson = ResultJson(work, results, correlationId),
            WorkBatch = record.WorkBatch,
            SourceFileName = record.PendingSourceFileName,
            SourceRowNumber = record.PendingSourceRowNumber,
            SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
        };
        return new RecordUndo(record.DeliveryKey, attempt, settlements, _runId, _worker) { Moved = moved };
    }

    private static string Describe(UndoResult result)
        => $"{result.Item.Artifact.TargetId ?? result.Item.Artifact.Locator ?? result.Item.Artifact.Slot}: {result.Note ?? "failed"}";

    /// <summary>The undo's result: why, which units, and each artifact with what became of it.</summary>
    internal static string ResultJson(UndoWork work, IReadOnlyList<UndoResult> results, string? correlationId)
    {
        var artifacts = new JsonArray();
        foreach (var result in results)
        {
            var a = result.Item.Artifact;
            var node = new JsonObject
            {
                ["artifactId"] = result.Item.ArtifactId,
                ["slot"] = a.Slot,
                ["role"] = a.Role,
                ["outcome"] = ArtifactStatuses.Name(result.Outcome),
            };
            if (a.TargetId is not null)
            {
                node["targetId"] = a.TargetId;
            }

            if (a.Locator is not null)
            {
                node["locator"] = a.Locator;
            }

            if (a.Version is { } version)
            {
                node["version"] = version;
            }

            if (a.PriorVersion is { } prior)
            {
                node["priorVersion"] = prior;
            }

            if (result.Note is not null)
            {
                node["note"] = HeaderRedaction.RedactMessage(result.Note);
            }

            artifacts.Add(node);
        }

        var undo = new JsonObject
        {
            ["reason"] = UndoReasons.Name(work.Reason),
            ["keptRecord"] = work.KeepRecord,
            ["units"] = new JsonArray(results.Select(r => r.Item.UnitId.ToString("D")).Distinct(StringComparer.Ordinal).Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
            ["summary"] = ArtifactLimits.Describe(results.Select(r => r.Outcome)),
            ["artifacts"] = artifacts,
        };
        var root = new JsonObject();
        if (correlationId is not null)
        {
            root["correlationId"] = correlationId;
        }

        root["undo"] = undo;
        return root.ToJsonString();
    }
}
