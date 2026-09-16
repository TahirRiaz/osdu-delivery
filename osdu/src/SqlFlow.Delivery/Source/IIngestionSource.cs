using System.Text.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Source;

/// <summary>Which records a run reads out of the flow's ingestion tables (docs/stage4-design.md section 2.1).</summary>
public enum SourceSelectionKind
{
    /// <summary>Everything changed since the flow's watermark, less the overlap, up to the moment the read opened.</summary>
    Incremental,

    /// <summary>Every record in scope, whenever it last changed.</summary>
    Full,

    /// <summary>Named records only, whenever they last changed: a redelivery, a release, a cache rollout or <c>--set recordKeys</c>.</summary>
    Keys,
}

/// <summary>
/// What a run asks the source for. Incremental carries the lower bound the watermark gives (null when there is none, which
/// reads as a full pass); Keys carries the record keys, in the flow's key order.
/// </summary>
public sealed record SourceSelection
{
    private SourceSelection()
    {
    }

    public required SourceSelectionKind Kind { get; init; }

    /// <summary>The exclusive lower bound of an incremental window, or null when the flow has no watermark yet.</summary>
    public DateTime? LowerUtc { get; init; }

    /// <summary>The records a <see cref="SourceSelectionKind.Keys"/> read is limited to.</summary>
    public IReadOnlyList<KeyTuple> Keys { get; init; } = [];

    /// <summary>True when the selection covers the whole scope, which is what may move the flow's watermark.</summary>
    public bool CoversScope => Kind is SourceSelectionKind.Incremental or SourceSelectionKind.Full;

    public static SourceSelection Incremental(DateTime? lowerUtc)
        => new() { Kind = SourceSelectionKind.Incremental, LowerUtc = lowerUtc };

    public static SourceSelection Full() => new() { Kind = SourceSelectionKind.Full };

    public static SourceSelection ForKeys(IReadOnlyList<KeyTuple> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new SourceSelection { Kind = SourceSelectionKind.Keys, Keys = [.. keys] };
    }

    /// <summary>One line for a log or a run outcome: what this read covers.</summary>
    public string Describe() => Kind switch
    {
        SourceSelectionKind.Incremental => LowerUtc is { } lower ? $"incremental since {lower:yyyy-MM-ddTHH:mm:ssZ}" : "incremental (no watermark yet)",
        SourceSelectionKind.Full => "full",
        _ => $"{Keys.Count} record key(s)",
    };
}

/// <summary>
/// One record's key, in the order <c>source.record.key</c> declares: every part as text, which is how the ledger stores it
/// (<c>Record.SourceKeyJson</c>) and how a member run or a redelivery names the records to read again.
/// </summary>
public sealed class KeyTuple : IEquatable<KeyTuple>
{
    private readonly string[] _values;

    public KeyTuple(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new DeliveryException("A record key has at least one column; an empty key tuple cannot name a record.");
        }

        _values = [.. values];
        Json = JsonSerializer.Serialize(_values);
    }

    /// <summary>The key as the ledger stores it: a JSON array of the parts, in key order.</summary>
    public string Json { get; }

    public IReadOnlyList<string> Values => _values;

    public static KeyTuple Of(params string[] values) => new(values);

    /// <summary>Reads a key tuple back from its stored JSON; throws <see cref="DeliveryException"/> when the text is not one.</summary>
    public static KeyTuple FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        string?[]? parts;
        try
        {
            parts = JsonSerializer.Deserialize<string?[]>(json);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A stored record key is not a JSON array of strings: {ex.Message}", ex);
        }

        if (parts is null || parts.Length == 0 || Array.Exists(parts, p => p is null))
        {
            throw new DeliveryException("A stored record key is not a JSON array of strings, or it holds a null part.");
        }

        return new KeyTuple(parts.Select(p => p!).ToList());
    }

    public bool Equals(KeyTuple? other) => other is not null && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => Equals(obj as KeyTuple);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var value in _values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(" | ", _values);
}

/// <summary>The window a read covers: <c>(LowerUtc, UpperUtc]</c> over the record rows' update time, with no lower bound on a full pass.</summary>
public sealed record SourceWindow(DateTime? LowerUtc, DateTime UpperUtc);

/// <summary>One key column of the record table, with the SQL type it holds, so window and key parameters keep the index seekable.</summary>
public sealed record SourceKeyColumn(string Name, string SqlType);

/// <summary>
/// A contiguous range of the order a read pages in, dealt to one run of a fan-out: records whose value is above
/// <paramref name="From"/> and at most <paramref name="To"/>, either bound open when it is null. Slices partition that
/// order, so a record a read meets falls in exactly one of them.
/// </summary>
/// <param name="Slice">The slice index members name in their run payload.</param>
/// <param name="From">The exclusive lower bound, or null for the start.</param>
/// <param name="To">The inclusive upper bound, or null for the end.</param>
/// <param name="On">
/// The identity primary key column the bounds are values of (<c>source.record.primaryKey</c>), or null when they are record
/// keys. A read refuses a range cut on another column than the one it pages by.
/// </param>
public readonly record struct KeyRange(int Slice, KeyTuple? From, KeyTuple? To, string? On = null);

/// <summary>
/// What an opened read knows before any row is read: the window it fixed, the columns each table holds, the key columns and
/// their SQL types, how many records it expects, and what the selection could not find.
/// </summary>
public sealed record SourceHeader
{
    public required SourceSelection Selection { get; init; }

    /// <summary>The window the read fixed at open; every page of this read uses it.</summary>
    public required SourceWindow Window { get; init; }

    /// <summary>The columns of the record table (under <see cref="SourceDatasets.Record"/>) and of each child dataset.</summary>
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> Columns { get; init; }

    /// <summary>The record key columns in key order, with the SQL type each holds.</summary>
    public required IReadOnlyList<SourceKeyColumn> KeyColumns { get; init; }

    /// <summary>The identity primary key the read pages and is cut by, or null when it pages by the record key.</summary>
    public string? PrimaryKey { get; init; }

    /// <summary>How many records the read expects to meet; an estimate, used to decide the fan-out and to size the slices.</summary>
    public long EstimatedCandidates { get; init; }

    /// <summary>False when nothing changed in the window, which lets the run skip without reading a row (tier 0).</summary>
    public bool HasChanges { get; init; }

    /// <summary>Keys the selection named that the record table does not hold at all.</summary>
    public IReadOnlyList<KeyTuple> MissingKeys { get; init; } = [];

    /// <summary>Keys the record table holds, but outside this run's scope, so another scope's run owns them.</summary>
    public IReadOnlyList<KeyTuple> OutOfScopeKeys { get; init; } = [];
}

/// <summary>
/// Opens a flow's ingestion tables for one run (docs/stage4-design.md section 2.1). The factory resolves the flow's
/// connection reference on the node; every source it hands out reads with that flow's identity and application name.
/// </summary>
public interface IIngestionSourceFactory
{
    /// <summary>The source for one flow and one run's parameter values; the values fill the scope predicate.</summary>
    IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values);
}

/// <summary>
/// Reads a flow's records out of its ingestion tables. A read opens once, fixing its window and learning the tables' shape,
/// and is then paged by record key, whole or one key range at a time, so a coordinating run and its members read disjoint
/// halves of the same window.
/// </summary>
public interface IIngestionSource
{
    /// <summary>
    /// Checks the tables without reading a record: that they are there and shaped as the flow declares (every column it
    /// names, the record key and its types, the identity primary key). A source's preflight asks it of every interface
    /// before any of them plans; a problem is a <see cref="DeliveryException"/> naming the table and the column.
    /// </summary>
    Task VerifyAsync(CancellationToken ct = default);

    /// <summary>
    /// Fixes the window and reads the tables' shape. <paramref name="stored"/> is the window a submission already recorded,
    /// which a member run or a drain reuses so every run of one submission reads the same rows.
    /// </summary>
    Task<SourceHeader> OpenAsync(SourceSelection selection, SourceWindow? stored, CancellationToken ct = default);

    /// <summary>
    /// Bounds that cut this read into at most <paramref name="slices"/> contiguous ranges of the record table's identity
    /// primary key, holding roughly as many candidates each. A read of one slice needs no bounds; a flow that names no
    /// primary key cannot be cut into more.
    /// </summary>
    Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(SourceHeader header, int slices, CancellationToken ct = default);

    /// <summary>
    /// The records of this read, whole or limited to one range, in the order the read pages in: the identity primary key
    /// when the flow names one, the record key otherwise. Each record carries its row, its child dataset rows, its origin,
    /// its ingestion fingerprint and its key as the source read it.
    /// </summary>
    IAsyncEnumerable<SourceRecord> ReadAsync(SourceHeader header, KeyRange? range, CancellationToken ct = default);
}
