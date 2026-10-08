using System.Globalization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// One artifact as the ledger keeps it (docs/atomic-delivery-plan.md, Artifacts): what a unit of work created in OSDU, or set
/// out to create, with where it stands.
/// </summary>
public sealed record LedgerArtifact
{
    public required long ArtifactId { get; init; }

    public required DeliveryKey Key { get; init; }

    /// <summary>The unit of work that created it.</summary>
    public required Guid UnitId { get; init; }

    /// <summary>When that unit began.</summary>
    public required DateTime UnitStartedUtc { get; init; }

    public required string Slot { get; init; }

    public required string Role { get; init; }

    public string? TargetId { get; init; }

    public string? Locator { get; init; }

    public long? Version { get; init; }

    public long? PriorVersion { get; init; }

    public required ArtifactStatus Status { get; init; }

    public string? Note { get; init; }

    public int UndoAttempts { get; init; }

    public DateTime? NextUndoUtc { get; init; }

    public Guid? SubmissionId { get; init; }

    public Guid? CreatedRunId { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }

    public DateTime? SettledUtc { get; init; }

    public Guid? SettledRunId { get; init; }

    public string? SettledBy { get; init; }

    /// <summary>The artifact as an undo takes it.</summary>
    public UndoItem ToItem() => new(
        ArtifactId,
        new TargetArtifact
        {
            Slot = Slot,
            Role = Role,
            TargetId = TargetId,
            Locator = Locator,
            Version = Version,
            PriorVersion = PriorVersion,
            Status = Status == ArtifactStatus.Intent ? ArtifactStatus.Intent : ArtifactStatus.Pending,
            Note = Note,
        },
        UnitId,
        UnitStartedUtc);
}

/// <summary>What an undo settled one artifact as, which the ledger writes with the undo's attempt.</summary>
/// <param name="ArtifactId">The artifact.</param>
/// <param name="Status">Removed, restored, gone, kept, superseded or failed.</param>
/// <param name="Note">Why it was kept or failed, or what the target answered; redacted, and cut to the column's width.</param>
/// <param name="RetryAtUtc">For a failed undo, when the sweep tries it again; null when it is not tried again.</param>
public sealed record ArtifactSettlement(long ArtifactId, ArtifactStatus Status, string? Note, DateTime? RetryAtUtc);

/// <summary>
/// One undo of one record's aborted units, written in one transaction: its attempt (outcome <see cref="AttemptOutcome.Undone"/>,
/// phase <see cref="AttemptPhases.Undo"/>) and what it settled each artifact as. The record's own status is not changed: an
/// undo takes back what a delivery left, it does not decide what the record is.
/// </summary>
public sealed record RecordUndo(DeliveryKey DeliveryKey, AttemptRecord Attempt, IReadOnlyList<ArtifactSettlement> Settlements, Guid? RunId, string SettledBy)
{
    /// <summary>
    /// The record itself written back to the version the ledger holds (<see cref="RecordVersionMove.From"/>) as a new version
    /// OSDU gave the write (<see cref="RecordVersionMove.To"/>): the ledger moves the record's version with it, so a verify finds
    /// OSDU holding what the ledger says rather than drift. Null when the undo wrote no version of the record back.
    /// </summary>
    public RecordVersionMove? Moved { get; init; }
}

/// <summary>The record's version an undo moved: from the version it wrote back to the version OSDU gave that write.</summary>
public sealed record RecordVersionMove(long From, long To);

/// <summary>One page of the sweep: the artifacts of whole records, and the last record's key, after which the next page starts.</summary>
public sealed record ArtifactSweepPage(IReadOnlyList<LedgerArtifact> Artifacts, DeliveryKey? Last)
{
    public static ArtifactSweepPage Empty { get; } = new([], null);
}

/// <summary>How many artifacts of a flow are still open, by state: what the flow's overview and the record pages say is left to undo.</summary>
public sealed record ArtifactCounts(long Intent, long Pending, long Due, long Failed, long FailedExhausted)
{
    public static ArtifactCounts None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Artifacts an undo still has to take: due, and failed (whether or not the sweep tries them again).</summary>
    public long ToUndo => Due + Failed;
}

/// <summary>What bounds the undo of aborted deliveries (docs/atomic-delivery-plan.md, When the undo runs).</summary>
public static class ArtifactLimits
{
    /// <summary>How many times the sweep tries an undo that failed before it is left for an operator's undo run.</summary>
    public const int MaxUndoAttempts = 10;

    /// <summary>The most records one page of the sweep takes, and one write of their undos settles.</summary>
    public const int SweepPage = 200;

    /// <summary>The most records one sweep takes in a run before it leaves the rest for the next one.</summary>
    public const int SweepPerRun = 10_000;

    /// <summary>
    /// How much earlier than a unit's start OSDU may say it created a record, with clocks apart, for the record still to count
    /// as created by the unit: an undo removes a record only when OSDU created it after this.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>When a failed undo is tried again: after 1, 2, 4 ... minutes, at most six hours apart.</summary>
    public static DateTime RetryAt(DateTime nowUtc, int attempts)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(0, attempts - 1)), 360);
        return nowUtc + TimeSpan.FromMinutes(minutes);
    }

    /// <summary>A note cut to the width the ledger keeps.</summary>
    public static string? Note(string? note)
        => note is null ? null : note.Length <= Data.DeliveryModel.MaxArtifactNoteLength ? note : string.Concat(note.AsSpan(0, Data.DeliveryModel.MaxArtifactNoteLength - 3), "...");

    /// <summary>A locator cut to the width the ledger keeps, marked when it was cut.</summary>
    public static string? Locator(string? locator)
        => locator is null ? null : locator.Length <= Data.DeliveryModel.MaxArtifactLocatorLength ? locator : string.Concat(locator.AsSpan(0, Data.DeliveryModel.MaxArtifactLocatorLength - 3), "...");

    /// <summary>The line an undo's outcome counts read as: "2 removed, 1 kept".</summary>
    public static string Describe(IEnumerable<ArtifactStatus> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var counts = outcomes.GroupBy(o => o).OrderBy(g => g.Key).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()} {ArtifactStatuses.Name(g.Key)}")).ToList();
        return counts.Count == 0 ? "nothing to undo" : string.Join(", ", counts);
    }
}

/// <summary>
/// One record of a flow with artifacts an undo may still take (docs/atomic-delivery-plan.md, When the undo runs), its open
/// artifacts counted by state: what the flow's open undos list, and what an operator opens the record's page from.
/// </summary>
/// <param name="Key">The record.</param>
/// <param name="Intent">Calls about to go whose answer the ledger has not had.</param>
/// <param name="Pending">Artifacts of a unit that has not ended, or that newer work abandoned and the sweep has not reached.</param>
/// <param name="Due">Artifacts of an aborted unit the undo has not run for.</param>
/// <param name="Failed">Artifacts whose undo was refused or could not reach OSDU, whether or not the sweep tries them again.</param>
/// <param name="FailedExhausted">Of <paramref name="Failed"/>, those tried <see cref="ArtifactLimits.MaxUndoAttempts"/> times, left for an operator's undo run.</param>
/// <param name="OldestUtc">When the oldest of them was written.</param>
/// <param name="NextUndoUtc">The earliest time the sweep tries a failed one again; null when none is due a retry.</param>
public sealed record OpenArtifactRecord(
    DeliveryKey Key, long Intent, long Pending, long Due, long Failed, long FailedExhausted, DateTime OldestUtc, DateTime? NextUndoUtc)
{
    /// <summary>Every open artifact of the record.</summary>
    public long Open => Intent + Pending + Due + Failed;

    /// <summary>The artifacts an undo still has to take: due, and failed.</summary>
    public long ToUndo => Due + Failed;
}

/// <summary>One page of a flow's records with open artifacts, and how many records of the flow have any.</summary>
public sealed record OpenArtifactRecordPage(IReadOnlyList<OpenArtifactRecord> Records, long Total)
{
    public static OpenArtifactRecordPage Empty { get; } = new([], 0);
}
