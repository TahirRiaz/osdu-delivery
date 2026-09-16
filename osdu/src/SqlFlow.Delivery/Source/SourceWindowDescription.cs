using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlFlow.Delivery.Source;

/// <summary>One slice of a plan's key space, as the submission records it: its index and the key bounds it runs between.</summary>
/// <param name="Slice">The slice index members name in their run payload.</param>
/// <param name="From">The exclusive lower bound's key parts, or null for the start of the key space.</param>
/// <param name="To">The inclusive upper bound's key parts, or null for the end of it.</param>
public sealed record SourceSliceBound(int Slice, IReadOnlyList<string>? From, IReadOnlyList<string>? To);

/// <summary>
/// What bounded a plan's read, recorded on its submission (<c>Submission.SourceWindowJson</c>) so every later run of that
/// submission reads exactly the same rows: the selection it was made for, the window it fixed, the key slices it was cut
/// into for its fan-out, and, for a key-scoped submission, the record keys it covers. A member run, a re-run and a
/// drain all rebuild their read from this rather than deciding a window of their own.
/// </summary>
public sealed record SourceWindowDescription
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The selection the plan was made for: incremental, full or keys.</summary>
    public required string Selection { get; init; }

    /// <summary>The window's exclusive lower bound, when it had one.</summary>
    public DateTime? LowerUtc { get; init; }

    /// <summary>The window's inclusive upper bound.</summary>
    public DateTime? UpperUtc { get; init; }

    /// <summary>The key slices the plan was cut into; empty when it ran as one.</summary>
    public IReadOnlyList<SourceSliceBound> Slices { get; init; } = [];

    /// <summary>The record keys a key-scoped submission covers, each as its key parts in key order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Keys { get; init; } = [];

    /// <summary>The description of one opened read and the slices it was cut into.</summary>
    public static SourceWindowDescription Of(SourceHeader header, IReadOnlyList<KeyRange> slices)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(slices);
        return new SourceWindowDescription
        {
            Selection = Name(header.Selection.Kind),
            LowerUtc = header.Window.LowerUtc,
            UpperUtc = header.Window.UpperUtc,
            Slices = slices.Count <= 1 ? [] : slices.Select(s => new SourceSliceBound(s.Slice, s.From?.Values, s.To?.Values)).ToList(),
            Keys = header.Selection.Keys.Select(k => k.Values).ToList(),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a recorded description back; null when the submission carries none.</summary>
    public static SourceWindowDescription? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SourceWindowDescription>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"A submission's recorded source window is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>The window the read reopens with, or null when the description carries no upper bound.</summary>
    public SourceWindow? Window() => UpperUtc is { } upper ? new SourceWindow(LowerUtc, upper) : null;

    /// <summary>The selection the plan was made for, rebuilt with the keys it covered.</summary>
    public SourceSelection ToSelection()
    {
        var keys = Keys.Select(k => new KeyTuple(k)).ToList();
        return Selection switch
        {
            "full" => SourceSelection.Full(),
            "keys" => SourceSelection.ForKeys(keys),
            _ => SourceSelection.Incremental(LowerUtc),
        };
    }

    /// <summary>The key range of one slice, or null when the plan was not cut into slices or does not hold that one.</summary>
    public KeyRange? Range(int slice)
    {
        var bound = Slices.FirstOrDefault(s => s.Slice == slice);
        return bound is null
            ? null
            : new KeyRange(slice, bound.From is null ? null : new KeyTuple(bound.From), bound.To is null ? null : new KeyTuple(bound.To));
    }

    /// <summary>The submission kind a selection is recorded as (<see cref="Ledger.SubmissionKinds"/>).</summary>
    public static string Name(SourceSelectionKind kind) => kind switch
    {
        SourceSelectionKind.Full => "full",
        SourceSelectionKind.Keys => "keys",
        _ => "incremental",
    };
}
