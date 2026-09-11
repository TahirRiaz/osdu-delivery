using System.Globalization;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// How the chunks of one wellbore DDMS bulk session have to relate to each other, and what the committed log must hold.
/// </summary>
/// <remarks>
/// A session aggregates its chunks into one new version (openapi wellbore_ddms, POST
/// /ddms/v3/welllogs/{record_id}/sessions: "a new single version is created only at session completion aggregating all
/// updates"), and it aggregates them by row label: a chunk whose labels another chunk already used replaces those rows
/// instead of adding its own, and the commit still succeeds. Seen live on an M26 service: two chunks of five and four
/// rows that both numbered their rows from zero committed a log of five rows. So chunks that split a log's rows need
/// labels that continue from one chunk to the next, and chunks that split its curves carry the same labels for the
/// same rows. The labels are read from each chunk's footer (<see cref="ParquetScopeReader.ReadShapeAsync"/>).
/// </remarks>
public static class WellboreDdmsSessionChunks
{
    /// <summary>One chunk of a session as the check sees it: its ordinal, its file and its footer shape.</summary>
    public readonly record struct Chunk(int Index, string Path, ParquetShape Shape);

    /// <summary>
    /// Why these chunks cannot go into one session as they stand, or null when their labels allow it. Two chunks
    /// conflict when their labels overlap, unless they carry exactly the same labels for different curves (a log whose
    /// curves were split across chunks). A chunk whose labels the footer cannot tell is not compared.
    /// </summary>
    public static string? Conflict(IReadOnlyList<Chunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Shape.RowIndex is not { } first)
            {
                continue;
            }

            for (var j = i + 1; j < chunks.Count; j++)
            {
                if (chunks[j].Shape.RowIndex is not { } second || !first.Overlaps(second))
                {
                    continue;
                }

                var curvesSplit = first.SameLabels(second)
                    && chunks[i].Shape.Rows == chunks[j].Shape.Rows
                    && !chunks[i].Shape.ColumnNames.Order(StringComparer.Ordinal).SequenceEqual(chunks[j].Shape.ColumnNames.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                if (!curvesSplit)
                {
                    return
                        $"payload chunks {Text(chunks[i].Index)} ({Name(chunks[i])}: {Describe(first)}) and {Text(chunks[j].Index)} ({Name(chunks[j])}: {Describe(second)}) "
                        + "give the same row labels to different rows. A wellbore DDMS session aggregates its chunks by row label, so one chunk's rows would replace the "
                        + "other's and the committed log would silently lose rows. Write chunks that split a log's rows with a row index that continues from one chunk to "
                        + "the next (a stored index column, or a pandas RangeIndex that starts where the previous chunk ended), and chunks that split its curves with the "
                        + "same index for the same rows (drop-contract.md, Chunking the payload)";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The rows the committed log must hold: the rows of each distinct label range, counted once however many chunks
    /// split its curves. Null when a chunk's labels are unknown, since the aggregate cannot be counted then.
    /// </summary>
    public static long? ExpectedRows(IReadOnlyList<ParquetShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0 || shapes.Any(s => s.RowIndex is null))
        {
            return null;
        }

        return shapes
            .GroupBy(s => (s.RowIndex!.Value.First, s.RowIndex!.Value.Last))
            .Sum(range => range.Max(s => s.Rows));
    }

    /// <summary>The curves the committed log must hold: every data column any chunk carries.</summary>
    public static IReadOnlyList<string> ExpectedColumns(IReadOnlyList<ParquetShape> shapes)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        return shapes.SelectMany(s => s.ColumnNames).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Why the log a session committed does not hold what its chunks carried, or null when it does. The version the
    /// commit created is already what OSDU serves, so the message says so and says how to send the log again.
    /// </summary>
    public static string? Shortfall(
        string targetId, string sessionId, long? expectedRows, IReadOnlyList<string> expectedColumns, long committedRows, IReadOnlyCollection<string> committedColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedColumns);
        ArgumentNullException.ThrowIfNull(committedColumns);

        const string After = "OSDU now serves that incomplete log at the version the commit created. Re-prepare the chunks with row labels that do not collide (drop-contract.md, Chunking the payload) and release the record to send them again";
        if (expectedRows is { } rows && committedRows != rows)
        {
            return $"session {sessionId} for {targetId} was committed, but the log holds {Text(committedRows)} rows where its chunks carried {Text(rows)}: the session aggregated chunks whose row labels collided. {After}";
        }

        var missing = expectedColumns.Where(c => !committedColumns.Contains(c, StringComparer.Ordinal)).ToList();
        return missing.Count == 0
            ? null
            : $"session {sessionId} for {targetId} was committed, but the log lacks the curves {string.Join(", ", missing)} that its chunks carried. {After}";
    }

    private static string Describe(ParquetRowIndex labels) => labels.Source switch
    {
        ParquetRowIndexSource.Implicit => $"no row index, so a reader numbers its rows {Label(labels.First)} to {Label(labels.Last)}",
        ParquetRowIndexSource.Range => $"a range index from {Label(labels.First)} to {Label(labels.Last)}",
        _ => $"a stored index from {Label(labels.First)} to {Label(labels.Last)}",
    };

    private static string Name(Chunk chunk)
        => string.IsNullOrEmpty(chunk.Path) ? "chunk " + Text(chunk.Index) : System.IO.Path.GetFileName(chunk.Path);

    private static string Label(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
