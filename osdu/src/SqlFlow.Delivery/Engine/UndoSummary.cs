using System.Globalization;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What one sweep of a flow's unfinished deliveries undid (docs/atomic-delivery-plan.md, When the undo runs): the records it
/// took up and what became of their artifacts. Every count is read from the undos it wrote; the ledger keeps each artifact's
/// own outcome on the record's undo attempt.
/// </summary>
public sealed record UndoSummary(int Records, int Removed, int Restored, int Gone, int Kept, int Superseded, int Failed)
{
    public static UndoSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Whether the sweep found nothing to undo.</summary>
    public bool Idle => Records == 0;

    /// <summary>Artifacts the sweep took up.</summary>
    public int Artifacts => Removed + Restored + Gone + Kept + Superseded + Failed;

    /// <summary>The summary of <paramref name="undos"/>.</summary>
    public static UndoSummary Of(IReadOnlyList<RecordUndo> undos)
    {
        ArgumentNullException.ThrowIfNull(undos);
        var outcomes = undos.SelectMany(u => u.Settlements).Select(s => s.Status).ToList();
        int Count(ArtifactStatus status) => outcomes.Count(o => o == status);
        return new UndoSummary(
            undos.Count,
            Count(ArtifactStatus.Removed),
            Count(ArtifactStatus.Restored),
            Count(ArtifactStatus.Gone),
            Count(ArtifactStatus.Kept),
            Count(ArtifactStatus.Superseded),
            Count(ArtifactStatus.Failed));
    }

    public UndoSummary Add(UndoSummary other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new UndoSummary(
            Records + other.Records, Removed + other.Removed, Restored + other.Restored, Gone + other.Gone, Kept + other.Kept,
            Superseded + other.Superseded, Failed + other.Failed);
    }

    /// <summary>The line the audit trail and the run log carry: only the counts that are not zero.</summary>
    public string Describe() => Idle
        ? "nothing left by an unfinished delivery to undo"
        : string.Create(CultureInfo.InvariantCulture, $"{Records} record(s) with what unfinished deliveries left: ")
            + CountLine.Of(
                "nothing settled",
                (Removed, "removed"),
                (Restored, "restored"),
                (Gone, "already gone"),
                (Kept, "kept"),
                (Superseded, "left for newer work"),
                (Failed, "still to undo"));
}
