namespace SqlFlow.Delivery.Model;

/// <summary>Which value of a node an assertion of a mapping judges (osdu/docs/reference/flow/mapping-assertions.md).</summary>
public enum AssertionStage
{
    /// <summary>
    /// The value the record carries at the node's property, after the modifiers and the conversion to the property's type:
    /// what OSDU holds, so the same assertion is judged on a rendered document and on a record OSDU stores.
    /// </summary>
    Record,

    /// <summary>The value the row gives the node before its modifiers: the column it reads, or what its expression computes.</summary>
    Incoming,
}

/// <summary>What a record that breaks an assertion of its mapping does.</summary>
public enum AssertionAction
{
    /// <summary>The record is not sent: its document is kept and held under an issue naming the assertion, until the row or the mapping changes or a release accepts it.</summary>
    Hold,

    /// <summary>The record is sent, and the failure recorded with its verdict.</summary>
    Report,

    /// <summary>The value that fails is left out of the record, and the failure recorded with its verdict.</summary>
    Omit,
}

/// <summary>The words a mapping writes stages and actions with, and its verdicts and answers carry.</summary>
public static class AssertionWords
{
    public const string Record = "record";
    public const string Incoming = "incoming";
    public const string Hold = "hold";
    public const string Report = "report";
    public const string Omit = "omit";

    public static string Of(AssertionStage stage) => stage == AssertionStage.Incoming ? Incoming : Record;

    public static string Of(AssertionAction action) => action switch
    {
        AssertionAction.Report => Report,
        AssertionAction.Omit => Omit,
        _ => Hold,
    };

    /// <summary>The stage a word names, or null for one that names none.</summary>
    public static AssertionStage? Stage(string? word) => word switch
    {
        Record => AssertionStage.Record,
        Incoming => AssertionStage.Incoming,
        _ => null,
    };

    /// <summary>The action a word names, or null for one that names none.</summary>
    public static AssertionAction? Action(string? word) => word switch
    {
        Hold => AssertionAction.Hold,
        Report => AssertionAction.Report,
        Omit => AssertionAction.Omit,
        _ => null,
    };
}

/// <summary>
/// One condition an assertion of a mapping is judged under: on the record stage a field of the record (a field inside the
/// repeated array the property is in reads the same item), on the incoming stage a column of the row the node reads.
/// </summary>
/// <param name="Field">The path into the record the condition reads, for the record stage; null for the incoming stage.</param>
/// <param name="Column">The column the condition reads, for the incoming stage; null for the record stage.</param>
/// <param name="Condition">What the value read has to meet for the assertion to be judged.</param>
public sealed record AssertionFilter(string? Field, DatasetColumn? Column, ValueCondition Condition)
{
    public override string ToString() => $"{(Field ?? Column?.ToString())} {Condition}";
}

/// <summary>
/// One assertion a mapping states beside a property (<c>$assert</c>, osdu/docs/reference/flow/mapping-assertions.md): a
/// condition the assertion flows share (<see cref="ValueCondition"/>), the value it judges, when it is judged, and what a
/// record that breaks it does.
/// </summary>
public sealed record NodeAssertion
{
    /// <summary>The longest name an assertion is given.</summary>
    public const int MaxLabel = 200;

    /// <summary>What messages, the ledger and the reports call it: its <c>name</c>, or a label read off its condition.</summary>
    public required string Label { get; init; }

    /// <summary>True when the mapping gives the assertion its name, false when the label is read off its condition.</summary>
    public bool Named { get; init; }

    public string? Description { get; init; }

    /// <summary>Where the mapping writes it (<c>record.data.TopMeasuredDepth.$assert[0]</c>), for messages.</summary>
    public required string Location { get; init; }

    public AssertionStage Stage { get; init; } = AssertionStage.Record;

    public AssertionAction OnFail { get; init; } = AssertionAction.Hold;

    public required ValueCondition Condition { get; init; }

    /// <summary>For a property holding several values: true holds when one of them meets the condition, false (the default) only when every one does.</summary>
    public bool AnyValue { get; init; }

    /// <summary>The conditions the assertion is judged under: every one of them has to hold.</summary>
    public IReadOnlyList<AssertionFilter> Where { get; init; } = [];

    /// <summary>What the assertion expects, as findings quote it.</summary>
    public string Expected => Condition
        + (AnyValue ? " for one of its values" : string.Empty)
        + (Where.Count == 0 ? string.Empty : " where " + string.Join(" and ", Where));
}
