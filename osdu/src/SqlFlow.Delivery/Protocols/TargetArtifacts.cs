using System.Globalization;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Protocols;

/// <summary>
/// What an artifact is (docs/atomic-delivery-plan.md, Artifacts): the record itself, a version of it, or something a delivery
/// made beside it. A route names each artifact it reports with one of these.
/// </summary>
public static class ArtifactRoles
{
    /// <summary>The record itself, written by a unit that created it, before a later call of the unit.</summary>
    public const string Record = "record";

    /// <summary>A version of a record that existed (the record, or a dataset beside it), written before a later call of the unit.</summary>
    public const string Version = "version";

    /// <summary>A dataset the unit registered for the record's files, inputs or manifest.</summary>
    public const string Dataset = "dataset";

    /// <summary>A dataset a DDMS registered for the record (a RAFS content table).</summary>
    public const string Content = "content";

    /// <summary>A record a workflow run wrote.</summary>
    public const string Output = "output";

    /// <summary>A Reservoir DDMS dataspace and its OSDU record.</summary>
    public const string Dataspace = "dataspace";

    /// <summary>A Wellbore DDMS bulk session.</summary>
    public const string Session = "session";

    /// <summary>A Seismic Store write lock, or a read-only flag a delivery lifted.</summary>
    public const string Lock = "lock";

    /// <summary>Rows a DDMS keeps under the record (Reservoir Management, DSPDM).</summary>
    public const string Rows = "rows";

    /// <summary>Historian points accepted under series versions.</summary>
    public const string Points = "points";

    /// <summary>An object store's objects, or ETP objects.</summary>
    public const string Objects = "objects";

    /// <summary>A workflow run a unit triggered, which can write records until it ends: an undo waits for it.</summary>
    public const string Run = "run";

    /// <summary>Every role, in the order the documentation lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Record, Version, Dataset, Content, Output, Dataspace, Session, Lock, Rows, Points, Objects, Run];

    /// <summary>
    /// Whether an artifact of <paramref name="role"/> is an OSDU id kept on the ledger after its unit commits: what the
    /// inventory joins with what OSDU serves. The others stand only for a unit's progress and go when it commits, since the
    /// record's own state names them then.
    /// </summary>
    public static bool KeptAfterCommit(string role) => role is Dataset or Content or Output or Dataspace;

    /// <summary>Whether an artifact of <paramref name="role"/> is the record itself or a version of it.</summary>
    public static bool IsTheRecord(string role) => role is Record or Version;

    /// <summary>Whether <paramref name="role"/> is one of the roles a route may report.</summary>
    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}

/// <summary>Where an artifact stands (docs/atomic-delivery-plan.md, Artifacts).</summary>
public enum ArtifactStatus
{
    /// <summary>The call that creates it is about to go; its id is not known yet.</summary>
    Intent,

    /// <summary>Created by a unit that has not ended.</summary>
    Pending,

    /// <summary>Its unit committed.</summary>
    Live,

    /// <summary>A later delivery of the record replaced it; it stays live in OSDU, since earlier versions of the record name it.</summary>
    Superseded,

    /// <summary>Its unit aborted and the undo has not run.</summary>
    Due,

    /// <summary>The undo removed it, reversibly where the route can.</summary>
    Removed,

    /// <summary>The undo wrote back the version it replaced.</summary>
    Restored,

    /// <summary>OSDU no longer held it when the undo came.</summary>
    Gone,

    /// <summary>No call removes it: it is left behind, and the note says why.</summary>
    Kept,

    /// <summary>The undo was refused or could not reach OSDU; it is tried again with backoff.</summary>
    Failed,
}

/// <summary>How artifact states are written in the ledger, and what each means for an undo.</summary>
public static class ArtifactStatuses
{
    public static string Name(ArtifactStatus status) => status switch
    {
        ArtifactStatus.Intent => "intent",
        ArtifactStatus.Pending => "pending",
        ArtifactStatus.Live => "live",
        ArtifactStatus.Superseded => "superseded",
        ArtifactStatus.Due => "due",
        ArtifactStatus.Removed => "removed",
        ArtifactStatus.Restored => "restored",
        ArtifactStatus.Gone => "gone",
        ArtifactStatus.Kept => "kept",
        ArtifactStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown artifact state."),
    };

    public static ArtifactStatus Parse(string text) => text switch
    {
        "intent" => ArtifactStatus.Intent,
        "pending" => ArtifactStatus.Pending,
        "live" => ArtifactStatus.Live,
        "superseded" => ArtifactStatus.Superseded,
        "due" => ArtifactStatus.Due,
        "removed" => ArtifactStatus.Removed,
        "restored" => ArtifactStatus.Restored,
        "gone" => ArtifactStatus.Gone,
        "kept" => ArtifactStatus.Kept,
        "failed" => ArtifactStatus.Failed,
        _ => throw new DeliveryException($"Unknown artifact state '{text}' in the ledger."),
    };

    /// <summary>Whether an artifact in <paramref name="status"/> may still be undone: an intent, a pending one, one due or one whose undo failed.</summary>
    public static bool IsOpen(ArtifactStatus status) => status is ArtifactStatus.Intent or ArtifactStatus.Pending or ArtifactStatus.Due or ArtifactStatus.Failed;

    /// <summary>The states an undo settles an artifact in: what a route's undo answers for each artifact it was given.</summary>
    public static bool IsUndoOutcome(ArtifactStatus status)
        => status is ArtifactStatus.Removed or ArtifactStatus.Restored or ArtifactStatus.Gone or ArtifactStatus.Kept or ArtifactStatus.Failed or ArtifactStatus.Superseded;
}

/// <summary>
/// What a route reports it created in OSDU, or is about to create, with the step that created it (docs/atomic-delivery-plan.md):
/// the ledger writes it in the transaction that writes the step, so it is named before the delivery goes on. The slot is the
/// route's name for it within the unit; reported again under the same slot (an intent completed by its id, a step reported
/// again by a resumed try), it updates the one row. A resumed try that reads OSDU again sees the unit's own write, so the
/// version a write replaced stays the one first reported, and a slot first reported as the record the unit created
/// (<see cref="ArtifactRoles.Record"/>) is not made a <see cref="ArtifactRoles.Version"/> of it. A slot the route settled
/// itself (a lock its close released) opens again when a later try of the unit reports it again (opens the lock again); one an
/// undo settled never does.
/// </summary>
public sealed record TargetArtifact
{
    /// <summary>The route's name for the artifact within its unit, unique there: <c>record</c>, <c>dataset:0</c>, <c>content:Kr</c>.</summary>
    public required string Slot { get; init; }

    /// <summary>What it is: one of <see cref="ArtifactRoles"/>.</summary>
    public required string Role { get; init; }

    /// <summary>The OSDU id, or the key the target gave it; null for an intent whose call has not answered.</summary>
    public string? TargetId { get; init; }

    /// <summary>What finds it when its id is not known, or what else names it (a landing-zone path, row keys in runs).</summary>
    public string? Locator { get; init; }

    /// <summary>The version the unit wrote, when it wrote one.</summary>
    public long? Version { get; init; }

    /// <summary>The version the write replaced, for a version of a record that existed.</summary>
    public long? PriorVersion { get; init; }

    /// <summary>
    /// <see cref="ArtifactStatus.Intent"/> before the call, <see cref="ArtifactStatus.Pending"/> once it is created, or, for an
    /// artifact the route settled itself before its unit ended: <see cref="ArtifactStatus.Removed"/> (a by-reference manifest
    /// soft-deleted once its run settled), <see cref="ArtifactStatus.Kept"/> (a dataset the call found already there and took
    /// over, not the unit's to undo) or <see cref="ArtifactStatus.Gone"/> (an intent the call answered without making what it
    /// stood for, or whose object another slot names now).
    /// </summary>
    public ArtifactStatus Status { get; init; } = ArtifactStatus.Pending;

    /// <summary>What the route has to say about it, kept with it.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// An intent: the call that creates the artifact is about to go, and <paramref name="locator"/> finds what it creates if
    /// its answer is lost. <paramref name="targetId"/> is given when the route chose the id itself.
    /// </summary>
    public static TargetArtifact Intent(string slot, string role, string? locator, string? targetId = null)
        => new() { Slot = slot, Role = role, Locator = locator, TargetId = targetId, Status = ArtifactStatus.Intent };

    /// <summary>An artifact the call created, under the id the target answered with.</summary>
    public static TargetArtifact Created(string slot, string role, string targetId, long? version = null, string? locator = null)
        => new() { Slot = slot, Role = role, TargetId = targetId, Version = version, Locator = locator };

    /// <summary>The slot every route reports the record itself under.</summary>
    public const string RecordSlot = "record";

    /// <summary>
    /// The record itself, written before a later call of the unit: <see cref="ArtifactRoles.Record"/> when the ledger held no
    /// version of it (<paramref name="existingVersion"/> null), else <see cref="ArtifactRoles.Version"/> naming the version the
    /// write replaced, which an undo writes back.
    /// </summary>
    public static TargetArtifact RecordWritten(string targetId, long? version, long? existingVersion) => existingVersion is null
        ? new() { Slot = RecordSlot, Role = ArtifactRoles.Record, TargetId = targetId, Version = version }
        : new() { Slot = RecordSlot, Role = ArtifactRoles.Version, TargetId = targetId, Version = version, PriorVersion = existingVersion };

    /// <summary>
    /// Checks what the ledger will keep: a slot and a known role, an id unless it is an intent, and a state a route may report.
    /// A route that reports anything else is a defect, refused before it reaches the ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The artifact is not one the ledger can keep.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Slot) || Slot.Length > MaxSlotLength)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"An artifact names no slot, or one longer than {MaxSlotLength} characters ('{Slot}')."));
        }

        if (!ArtifactRoles.IsKnown(Role))
        {
            throw new DeliveryException($"The artifact in slot '{Slot}' has the role '{Role}', which is not one of {string.Join(", ", ArtifactRoles.All)}.");
        }

        if (Status is not (ArtifactStatus.Intent or ArtifactStatus.Pending or ArtifactStatus.Removed or ArtifactStatus.Kept or ArtifactStatus.Gone))
        {
            throw new DeliveryException($"The artifact in slot '{Slot}' is reported {ArtifactStatuses.Name(Status)}; a route reports an intent, a created artifact, or one it settled itself (removed, kept or gone).");
        }

        if (Status != ArtifactStatus.Intent && string.IsNullOrWhiteSpace(TargetId))
        {
            throw new DeliveryException($"The artifact in slot '{Slot}' is reported {ArtifactStatuses.Name(Status)} without the id the target gave it.");
        }

        if (Status == ArtifactStatus.Intent && string.IsNullOrWhiteSpace(TargetId) && string.IsNullOrWhiteSpace(Locator))
        {
            throw new DeliveryException($"The intent in slot '{Slot}' names neither an id nor what finds the object if the call's answer is lost.");
        }
    }

    /// <summary>The longest slot the ledger keeps.</summary>
    public const int MaxSlotLength = 200;
}

/// <summary>
/// The unit of work a delivery belongs to (docs/atomic-delivery-plan.md): one delivery of one record's pending work, across
/// every try while the record stays pending. It is kept with the record's completed steps under <see cref="StepName"/>, so it
/// is dropped exactly when they are, and a later try of the same work resumes the same unit.
/// </summary>
/// <param name="Id">The unit's id, which every artifact it creates carries.</param>
/// <param name="StartedUtc">When it began: what an undo compares OSDU's creation time of a record with.</param>
public sealed record DeliveryUnit(Guid Id, DateTime StartedUtc)
{
    /// <summary>The name the unit is kept under among the completed steps; no route names a step so.</summary>
    public const string StepName = "$unit";

    /// <summary>The unit kept among <paramref name="steps"/>, or null when they hold none (the unit has not begun).</summary>
    public static DeliveryUnit? FromSteps(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (!steps.TryGetValue(StepName, out var values)
            || !values.TryGetValue("id", out var id) || !Guid.TryParse(id, out var unit)
            || !values.TryGetValue("startedUtc", out var started)
            || !DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedUtc))
        {
            return null;
        }

        return new DeliveryUnit(unit, DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc));
    }

    /// <summary>The unit as it is kept among the completed steps.</summary>
    public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["id"] = Id.ToString("D"),
        ["startedUtc"] = StartedUtc.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>A step a route completed, what the target returned for it, and what it created in OSDU.</summary>
/// <param name="Step">The step's name.</param>
/// <param name="Returned">What the target returned.</param>
/// <param name="Artifacts">What the step created, or is about to create (an intent).</param>
public sealed record StepReport(string Step, IReadOnlyDictionary<string, string> Returned, IReadOnlyList<TargetArtifact> Artifacts);

/// <summary>Why a unit is undone (docs/atomic-delivery-plan.md, The unit of work).</summary>
public enum UndoReason
{
    /// <summary>A try of the unit ended held.</summary>
    Held,

    /// <summary>The unit spent its retry budget.</summary>
    Failed,

    /// <summary>Its steps were dropped while it was unfinished: newer work was staged, the planner held the record, or a newer completion superseded it.</summary>
    Abandoned,

    /// <summary>The record is being removed from OSDU, or its ledger deleted.</summary>
    Removed,
}

/// <summary>How undo reasons are written in an undo's attempt.</summary>
public static class UndoReasons
{
    public static string Name(UndoReason reason) => reason switch
    {
        UndoReason.Held => "held",
        UndoReason.Failed => "failed",
        UndoReason.Abandoned => "abandoned",
        UndoReason.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown undo reason."),
    };
}

/// <summary>One artifact an undo takes, as the ledger keeps it.</summary>
/// <param name="ArtifactId">The ledger's number for it.</param>
/// <param name="Artifact">What the route reported.</param>
/// <param name="UnitId">The unit that created it.</param>
/// <param name="UnitStartedUtc">When that unit began.</param>
public sealed record UndoItem(long ArtifactId, TargetArtifact Artifact, Guid UnitId, DateTime UnitStartedUtc);

/// <summary>What an undo takes for one record: the artifacts of its aborted units, and what the record's committed deliveries left.</summary>
public sealed record UndoWork
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    public required DeliveryKey Key { get; init; }

    /// <summary>The record's OSDU id.</summary>
    public required string TargetId { get; init; }

    /// <summary>What the record's committed deliveries left: the ids a delivered record names (its datasets), never to be undone.</summary>
    public IReadOnlyDictionary<string, string> TargetState { get; init; } = NoValues;

    /// <summary>The version the ledger holds of the record; null when it holds none.</summary>
    public long? CommittedVersion { get; init; }

    public required UndoReason Reason { get; init; }

    /// <summary>
    /// Newer work writes the record's metadata again, so the undo leaves the record itself: an artifact of the record itself is
    /// answered <see cref="ArtifactStatus.Superseded"/>, and only what the unit made beside it is undone. A route whose next
    /// write reads back what the unit wrote (a dataset record's write carries the <c>DatasetProperties</c> storage holds) undoes
    /// the record all the same.
    /// </summary>
    public bool KeepRecord { get; init; }

    /// <summary>The artifacts to undo, in the order their units created them.</summary>
    public required IReadOnlyList<UndoItem> Items { get; init; }
}

/// <summary>What an undo did to one artifact: one of the states an undo settles in, and what it has to say.</summary>
/// <param name="Item">The artifact.</param>
/// <param name="Outcome">Removed, restored, gone, kept, superseded (the record left for newer work) or failed.</param>
/// <param name="Note">Why it was kept, what failed, or what the target answered; redacted before it is stored.</param>
public sealed record UndoResult(UndoItem Item, ArtifactStatus Outcome, string? Note)
{
    /// <summary>For a version written back, the version it was written back from, and the version OSDU gave the write.</summary>
    public (long WrittenBack, long NewVersion)? Rewrite { get; init; }

    public static UndoResult Removed(UndoItem item, string? note = null) => new(item, ArtifactStatus.Removed, note);

    public static UndoResult Restored(UndoItem item, string? note = null) => new(item, ArtifactStatus.Restored, note);

    public static UndoResult Gone(UndoItem item, string? note = null) => new(item, ArtifactStatus.Gone, note ?? "OSDU no longer holds it");

    public static UndoResult Kept(UndoItem item, string note) => new(item, ArtifactStatus.Kept, note);

    public static UndoResult Superseded(UndoItem item, string note) => new(item, ArtifactStatus.Superseded, note);

    public static UndoResult Failed(UndoItem item, string note) => new(item, ArtifactStatus.Failed, note);
}
