using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads a node's <c>$assert</c> (osdu/docs/reference/flow/mapping-assertions.md): a list of assertions, each a condition
/// in the assertion flows' own words (<see cref="ConditionReader"/>) with the value it judges (<c>stage</c>), what a record
/// that breaks it does (<c>onFail</c>), how a property holding several values is judged (<c>values</c>), the conditions it
/// is judged under (<c>where</c>), and its <c>name</c> and <c>description</c>. Its keys are words of the assertion, not of
/// the record, so they carry no <c>$</c>, as a modifier's settings do not.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>The most assertions one node states.</summary>
    internal const int MaxNodeAssertions = 20;

    /// <summary>The most conditions one assertion's <c>where</c> holds.</summary>
    internal const int MaxAssertionConditions = 10;

    internal const string StageSetting = "stage";
    internal const string OnFailSetting = "onFail";
    internal const string ValuesSetting = "values";
    internal const string WhereSetting = "where";
    internal const string NameSetting = "name";
    internal const string FieldSetting = "field";
    internal const string ColumnSetting = "column";

    /// <summary>The words an assertion takes beside its condition.</summary>
    internal static readonly IReadOnlyList<string> AssertionSettings =
        [StageSetting, OnFailSetting, ValuesSetting, WhereSetting, NameSetting, "description", "ignoreCase", "tolerance"];

    /// <summary>The words a condition of an assertion's <c>where</c> takes beside its operator.</summary>
    internal static readonly IReadOnlyList<string> AssertionFilterKeys = [FieldSetting, ColumnSetting, "ignoreCase", "tolerance"];

    /// <summary>The words <c>stage</c> takes.</summary>
    internal static readonly IReadOnlyList<string> AssertionStages = [AssertionWords.Record, AssertionWords.Incoming];

    /// <summary>The words <c>onFail</c> takes.</summary>
    internal static readonly IReadOnlyList<string> AssertionActions = [AssertionWords.Hold, AssertionWords.Report, AssertionWords.Omit];

    /// <summary>The words <c>values</c> takes.</summary>
    internal static readonly IReadOnlyList<string> AssertionQuantities = ["all", "any"];

    private const string AssertExample = AssertKey + ": [ { between: [0, 250] } ]";

    /// <summary>Why <c>resolves</c> is not a condition of a mapping, said after where it is written.</summary>
    private const string ResolvesInMapping =
        "asks whether a reference resolves, which a mapping does not assert: an id the id or ref modifier builds is looked up in the partition's cache, "
        + "and target.verifyReferences: storage looks every reference up in OSDU before the record is sent.";

    /// <summary>The operators an assertion of a mapping takes, as messages list them.</summary>
    private static string AssertionOperators => string.Join(", ", ConditionReader.Operators.Where(o => o != "resolves"));

    /// <summary>
    /// The assertions a node states under <c>$assert</c>, or none when it states none.
    /// </summary>
    /// <param name="map">The node.</param>
    /// <param name="notIncoming">
    /// Why the node's value is not the row's, which refuses the incoming stage (<c>this node's value comes from the
    /// partition's cache</c>); null for a node that reads the row, with <c>$from</c> or <c>$expr</c>.
    /// </param>
    /// <param name="scope">The row a column the incoming stage's conditions name reads.</param>
    /// <param name="required">The node's <c>$required</c>: <c>omit</c> needs a node that may be left out.</param>
    /// <param name="location">Where the document writes the node.</param>
    /// <param name="source">The file.</param>
    private static List<NodeAssertion> Assertions(
        IDictionary<object, object> map, string? notIncoming, TreeScope scope, bool required, string location, string source)
    {
        if (!Has(map, AssertKey))
        {
            return [];
        }

        var at = $"{source}: {location}";
        var items = Get(map, AssertKey) is IEnumerable<object> listed
            ? listed.ToList()
            : throw new FlowValidationException(
                $"{at}: {AssertKey} is a list of assertions, such as {AssertExample}; write one assertion as the list's only item.");
        if (items.Count == 0)
        {
            throw new FlowValidationException($"{at}: {AssertKey} lists no assertion; give it one, such as {AssertExample}, or remove it.");
        }

        if (items.Count > MaxNodeAssertions)
        {
            throw new FlowValidationException(
                $"{at}: {AssertKey} lists {items.Count.ToString(CultureInfo.InvariantCulture)} assertions, and a node states at most {MaxNodeAssertions.ToString(CultureInfo.InvariantCulture)}; "
                + "fold the ones that ask the same thing into one (in lists the values allowed, between a range).");
        }

        var assertions = new List<NodeAssertion>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var itemLocation = $"{location}.{AssertKey}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var assertion = Assertion(items[i], notIncoming, scope, required, itemLocation, source);
            var same = assertions.FindIndex(a => string.Equals(a.Label, assertion.Label, StringComparison.Ordinal));
            if (same >= 0)
            {
                throw new FlowValidationException(assertion.Named || assertions[same].Named
                    ? $"{source}: {itemLocation} is named '{assertion.Label}', as {assertions[same].Location} is; each assertion of a node has a name of its own, since what it finds is recorded under it."
                    : $"{source}: {itemLocation} asserts what {assertions[same].Location} asserts ({assertion.Label}); remove one of them.");
            }

            assertions.Add(assertion);
        }

        return assertions;
    }

    private static NodeAssertion Assertion(object? written, string? notIncoming, TreeScope scope, bool required, string location, string source)
    {
        var at = $"{source}: {location}";
        if (written is not IDictionary<object, object> { Count: > 0 } item)
        {
            throw new FlowValidationException(
                $"{at} is an assertion: one condition and its settings, such as {{ between: [0, 250] }} or {{ stage: incoming, notIn: [NONE], onFail: omit }}.");
        }

        foreach (var key in item.Keys.Select(KeyText))
        {
            if (ConditionReader.Operators.Contains(key, StringComparer.Ordinal) || AssertionSettings.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            var bare = key.StartsWith(Marker, StringComparison.Ordinal) ? key.TrimStart('$') : null;
            if (bare is not null && (ConditionReader.Operators.Contains(bare, StringComparer.Ordinal) || AssertionSettings.Contains(bare, StringComparer.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{at}: the words of an assertion carry no '{Marker}', as a modifier's settings do not, since they are not properties of the record; write {bare}, not {key}.");
            }

            throw new FlowValidationException(Nearest(key, ConditionReader.Operators.Concat(AssertionSettings)) is { } nearest
                ? $"{at}: '{key}' is not a word of an assertion. Did you mean '{nearest}'?"
                : $"{at}: '{key}' is not a word of an assertion, which takes one condition ({AssertionOperators}) and {SettingList(AssertionSettings)}.");
        }

        var stage = Word(item, StageSetting, AssertionStages, at) is { } stageWord ? AssertionWords.Stage(stageWord)!.Value : AssertionStage.Record;
        if (stage == AssertionStage.Incoming && notIncoming is not null)
        {
            throw new FlowValidationException(
                $"{at}: stage: incoming judges the value the row gives a node before its modifiers, and {notIncoming}; an assertion of this node judges the value the record carries (stage: record, the default).");
        }

        var onFail = Word(item, OnFailSetting, AssertionActions, at) is { } actionWord ? AssertionWords.Action(actionWord)!.Value : AssertionAction.Hold;
        var anyValue = Word(item, ValuesSetting, AssertionQuantities, at) == "any";
        if (Has(item, ValuesSetting) && stage == AssertionStage.Incoming)
        {
            throw new FlowValidationException(
                $"{at}: {ValuesSetting} says whether every value of a property holding several has to meet the assertion, or one of them, and stage: incoming judges the one value the row gives the node; remove {ValuesSetting}.");
        }

        if (onFail == AssertionAction.Omit && required)
        {
            throw new FlowValidationException(
                $"{at}: onFail: omit leaves the value out of the record, and the node is required ({RequiredKey} is true unless it says false); write {RequiredKey}: false beside it so the record may go without it, "
                + "or hold the record (onFail: hold) or send it as it is (onFail: report).");
        }

        if (onFail == AssertionAction.Omit && anyValue)
        {
            throw new FlowValidationException(
                $"{at}: onFail: omit leaves out each value that fails the assertion, and values: any judges the values together, so no one value fails it; write values: all, or hold or report the record.");
        }

        var condition = ConditionReader.Read(
            ConditionReader.OperatorsOf(item), Flag(item, "ignoreCase", at), Get(item, "tolerance"), ResolvesInMapping, location, source);
        var where = Filters(item, stage, scope, location, source);
        var name = Text(item, NameSetting, at)?.Trim();
        if (name is not null && (name.Length == 0 || name.Length > NodeAssertion.MaxLabel))
        {
            throw new FlowValidationException(
                $"{at}: {NameSetting} is what the assertion is called wherever what it finds is shown: 1 to {NodeAssertion.MaxLabel.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        var assertion = new NodeAssertion
        {
            Label = string.Empty,
            Location = location,
            Stage = stage,
            OnFail = onFail,
            Condition = condition,
            AnyValue = anyValue,
            Where = where,
            Description = Text(item, "description", at)?.Trim() is { Length: > 0 } description ? description : null,
        };
        return assertion with
        {
            Label = name ?? Clipped(assertion.Expected, NodeAssertion.MaxLabel),
            Named = name is not null,
        };
    }

    /// <summary>
    /// An assertion's <c>where</c>: the conditions it is judged under. On the record stage each reads a field of the record;
    /// on the incoming stage each reads a column of the row the node reads, or of the dataset's own row.
    /// </summary>
    private static List<AssertionFilter> Filters(IDictionary<object, object> item, AssertionStage stage, TreeScope scope, string location, string source)
    {
        if (!Has(item, WhereSetting))
        {
            return [];
        }

        var at = $"{source}: {location}";
        var example = stage == AssertionStage.Record
            ? "where: [ { field: data.ReferenceCurveID, equals: MD } ]"
            : "where: [ { column: depth_unit, equals: FT } ]";
        var listed = Get(item, WhereSetting) is IEnumerable<object> conditions
            ? conditions.ToList()
            : throw new FlowValidationException($"{at}.{WhereSetting} is a list of conditions, such as {example}.");
        if (listed.Count is 0 or > MaxAssertionConditions)
        {
            throw new FlowValidationException(
                $"{at}.{WhereSetting} lists between 1 and {MaxAssertionConditions.ToString(CultureInfo.InvariantCulture)} conditions, such as {example}.");
        }

        var filters = new List<AssertionFilter>(listed.Count);
        for (var j = 0; j < listed.Count; j++)
        {
            var filterLocation = $"{location}.{WhereSetting}[{j.ToString(CultureInfo.InvariantCulture)}]";
            var filterAt = $"{source}: {filterLocation}";
            if (listed[j] is not IDictionary<object, object> { Count: > 0 } filter)
            {
                throw new FlowValidationException($"{filterAt} is a condition: what it reads and one operator, such as {example}.");
            }

            foreach (var key in filter.Keys.Select(KeyText))
            {
                if (!ConditionReader.Operators.Contains(key, StringComparer.Ordinal) && !AssertionFilterKeys.Contains(key, StringComparer.Ordinal))
                {
                    throw new FlowValidationException(
                        $"{filterAt} selects when the assertion is judged and takes {(stage == AssertionStage.Record ? FieldSetting : ColumnSetting)}, one operator, ignoreCase and tolerance, not '{key}'.");
                }
            }

            string? field = null;
            DatasetColumn? column = null;
            if (stage == AssertionStage.Record)
            {
                if (Has(filter, ColumnSetting))
                {
                    throw new FlowValidationException(
                        $"{filterAt} reads a column of the row, and the assertion judges the value the record carries (stage: record), so its conditions read fields of the record, such as {example}; "
                        + "an assertion that judges the row's value is written with stage: incoming.");
                }

                field = ConditionReader.RecordPath(
                    Text(filter, FieldSetting, filterAt) ?? throw new FlowValidationException($"{filterAt} names no field; a condition of the record stage reads a field of the record, such as {example}."),
                    filterLocation + "." + FieldSetting,
                    source);
            }
            else
            {
                if (Has(filter, FieldSetting))
                {
                    throw new FlowValidationException(
                        $"{filterAt} reads a field of the record, and the assertion judges the value the row gives (stage: incoming), before the record is written, so its conditions read columns of the row, such as {example}.");
                }

                column = Column(
                    Text(filter, ColumnSetting, filterAt) ?? throw new FlowValidationException($"{filterAt} names no column; a condition of the incoming stage reads a column of the row, such as {example}."),
                    scope,
                    $"{filterAt}: {ColumnSetting}");
            }

            var condition = ConditionReader.Read(
                ConditionReader.OperatorsOf(filter), Flag(filter, "ignoreCase", filterAt), Get(filter, "tolerance"),
                "selects by whether a reference resolves, which a mapping does not assert.", filterLocation, source);
            filters.Add(new AssertionFilter(field, column, condition));
        }

        return filters;
    }

    /// <summary>One of <paramref name="words"/> a setting is written as, or null when it is not written.</summary>
    private static string? Word(IDictionary<object, object> item, string setting, IReadOnlyList<string> words, string at)
    {
        if (!Has(item, setting))
        {
            return null;
        }

        var written = Text(item, setting, at)?.Trim() ?? string.Empty;
        return words.Contains(written, StringComparer.Ordinal)
            ? written
            : throw new FlowValidationException(
                $"{at}: {setting} is '{written}'; write {string.Join(", ", words.Take(words.Count - 1))} or {words[^1]}{(setting == StageSetting ? StageHelp : setting == OnFailSetting ? ActionHelp : string.Empty)}.");
    }

    private const string StageHelp =
        " (record, the default, judges the value the record carries; incoming the value the row gives the node before its modifiers)";

    private const string ActionHelp =
        " (hold, the default, keeps the record from being sent; report sends it and records the failure; omit leaves the value out)";

    private static string Clipped(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
