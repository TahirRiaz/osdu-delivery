using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The template a test's fields are checked against, and the problems the check found.</summary>
/// <param name="Template">The template, or null when the test reads no field, or none could be found.</param>
/// <param name="Version">The template version checked against, or null.</param>
/// <param name="Problems">Each place the test does not fit the template: a reason it is not evaluated.</param>
public sealed record TemplateFit(OsduTemplate? Template, string? Version, IReadOnlyList<string> Problems)
{
    public static TemplateFit NotNeeded { get; } = new(null, null, []);
}

/// <summary>
/// Checks a test against the template of the kind it reads (docs/assertions-design.md section 4): every path a test reads
/// is a property of the kind's schema, every operator fits the property's type, and every value compared with could be a
/// value of it. A test that names a property its kind does not have would otherwise fail on every record for a reason
/// that has nothing to do with the data, so it is stopped here, with the property it probably meant.
/// </summary>
public static partial class AssertionTemplates
{
    /// <summary>
    /// The template each test's fields are checked against: the version the test pins, or the newest saved for its kind.
    /// A test that reads no field needs none; a test that reads fields of a kind no template is saved for gets a problem.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, TemplateFit>> FitAsync(
        IReadOnlyList<AssertionTest> tests, ITemplateStore? store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tests);
        var fits = new Dictionary<string, TemplateFit>(StringComparer.OrdinalIgnoreCase);
        var needing = tests.Where(ReadsFields).ToList();
        foreach (var test in tests.Where(t => !ReadsFields(t)))
        {
            fits[test.Name] = TemplateFit.NotNeeded;
        }

        if (needing.Count == 0)
        {
            return fits;
        }

        if (store is null)
        {
            foreach (var test in needing)
            {
                fits[test.Name] = new TemplateFit(null, null, [$"The fields of '{test.Kind}' cannot be checked against its template: {DeliveryServices.NoLedgerMessage}"]);
            }

            return fits;
        }

        var saved = await store.ListAsync(ct).ConfigureAwait(false);
        var loaded = new Dictionary<TemplateReference, OsduTemplate?>();
        foreach (var test in needing)
        {
            var versions = saved.Where(t => string.Equals(t.Kind, test.Kind, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.CapturedUtc).ToList();
            var chosen = test.Template is { } pinned
                ? versions.FirstOrDefault(t => string.Equals(t.Version, pinned, StringComparison.OrdinalIgnoreCase))
                : versions.FirstOrDefault();
            if (chosen is null)
            {
                fits[test.Name] = new TemplateFit(null, test.Template, [
                    test.Template is { } version
                        ? $"Template version '{version}' of {test.Kind} is not saved{(versions.Count == 0 ? "; no version of the kind is" : $"; the saved versions are {string.Join(", ", versions.Select(v => v.Version))}")}. Capture it on the Templates page, or pin a saved version."
                        : $"No template of {test.Kind} is saved, so the test's fields cannot be checked against the kind's schema. Capture it on the Templates page (or with 'sqlflow template capture --kind {test.Kind}'), then run the test again.",
                ]);
                continue;
            }

            if (!loaded.TryGetValue(chosen.Reference, out var template))
            {
                var schema = await store.LoadAsync(chosen.Reference, ct).ConfigureAwait(false);
                template = schema is null ? null : OsduTemplate.From(schema);
                loaded[chosen.Reference] = template;
            }

            fits[test.Name] = template is null
                ? new TemplateFit(null, chosen.Version, [$"Template {chosen.Reference} was listed but could not be read back; capture it again."])
                : new TemplateFit(template, chosen.Version, Check(test, template));
        }

        return fits;
    }

    /// <summary>
    /// Whether a test reads fields of its records, and so needs its kind's template: a test that only counts, asks how
    /// records were indexed or reads bulk data needs none.
    /// </summary>
    public static bool ReadsFields(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return test.Sort.Count > 0 || test.Spatial is not null || test.Assertions.Any(a => a switch
        {
            ValueAssertion { Target.IsColumn: false } => true,
            AggregateAssertion { Target.IsColumn: false } => true,
            UniqueAssertion or GroupAssertion or RecordSetAssertion or ConformsAssertion or MappingRulesAssertion => true,
            _ => false,
        });
    }

    /// <summary>Every place <paramref name="test"/> does not fit <paramref name="template"/>; empty when it fits.</summary>
    public static IReadOnlyList<string> Check(AssertionTest test, OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<string>();
        var where = $"{test.Kind} (template {template.Version})";
        foreach (var sort in test.Sort)
        {
            _ = Resolve(template, sort.Field, "sort", where, problems);
        }

        if (test.Spatial?["field"]?.GetValue<string>() is { } geo)
        {
            _ = Resolve(template, geo, "spatial.field", where, problems);
        }

        foreach (var assertion in test.Assertions)
        {
            var at = $"'{assertion.Label}'";
            switch (assertion)
            {
                case ValueAssertion { Target.IsColumn: false } value:
                    if (Resolve(template, value.Target.Path, at, where, problems) is { } variable)
                    {
                        CheckCondition(variable, value.Target.Path, value.Condition, at, where, problems);
                    }

                    foreach (var filter in value.Where)
                    {
                        if (Resolve(template, filter.Target.Path, at + " where", where, problems) is { } filtered)
                        {
                            CheckCondition(filtered, filter.Target.Path, filter.Condition, at + " where", where, problems);
                        }
                    }

                    break;
                case AggregateAssertion { Target.IsColumn: false } aggregate:
                    if (Resolve(template, aggregate.Target.Path, at, where, problems) is { } measured)
                    {
                        CheckAggregate(measured, aggregate, at, problems);
                    }

                    break;
                case UniqueAssertion unique:
                    foreach (var field in unique.Fields)
                    {
                        _ = Resolve(template, field, at, where, problems);
                    }

                    break;
                case GroupAssertion group:
                    _ = Resolve(template, group.Field, at, where, problems);
                    break;
                case RecordSetAssertion set:
                    foreach (var column in set.Columns)
                    {
                        _ = Resolve(template, column, at, where, problems);
                    }

                    break;
                case MappingRulesAssertion rules:
                    CheckMapping(test, rules, template, at, where, problems);
                    break;
            }
        }

        return problems;
    }

    /// <summary>
    /// The variable a path names, or null with a problem: an exact property, a key under an object with free keys
    /// (<c>tags.Name</c>), or a path below a property the schema does not break down (a choice of shapes), which is taken as
    /// it is since nothing below it can be checked.
    /// </summary>
    internal static TemplateVariable? Resolve(OsduTemplate template, string path, string at, string where, ICollection<string> problems)
    {
        var dotted = Steps().Replace(path, string.Empty);
        var variable = template.Variables.FirstOrDefault(v => string.Equals(v.Path.SchemaPath, dotted, StringComparison.Ordinal));
        if (variable is not null)
        {
            return variable;
        }

        var segments = dotted.Split('.');
        for (var length = segments.Length - 1; length >= 1; length--)
        {
            var prefix = string.Join('.', segments.Take(length));
            var holder = template.Variables.FirstOrDefault(v => string.Equals(v.Path.SchemaPath, prefix, StringComparison.Ordinal));
            if (holder is null)
            {
                continue;
            }

            if (holder.KeyValueType is { } valueType && length == segments.Length - 1)
            {
                return holder with { Path = holder.Path, Type = valueType, Shape = TemplateVariableShape.Value, KeyValueType = null };
            }

            if (holder.Shape == TemplateVariableShape.Whole || holder.Type == "any")
            {
                return holder;
            }

            break;
        }

        var suggestion = Closest(dotted, template.Variables.Select(v => v.Path.SchemaPath));
        problems.Add($"{at}: '{path}' is not a property of {where}{(suggestion is null ? "." : $"; did you mean '{suggestion}'?")}");
        return null;
    }

    private static void CheckCondition(TemplateVariable variable, string path, ValueCondition condition, string at, string where, ICollection<string> problems)
    {
        if (variable.Shape == TemplateVariableShape.Whole || variable.Type == "any")
        {
            return;
        }

        var intoItems = path.EndsWith(']');
        var isList = variable.Shape is TemplateVariableShape.ValueList or TemplateVariableShape.GroupList || variable.Type == "array";
        var op = condition.Operator;
        if (isList && !intoItems)
        {
            if (op is not (ValueOperator.Contains or ValueOperator.NotContains or ValueOperator.Length or ValueOperator.Empty or ValueOperator.Exists
                or ValueOperator.Type or ValueOperator.Resolves))
            {
                problems.Add(
                    $"{at}: '{path}' is a list in {where}; {AssertionText.Of(op)} compares one value, so test each item with '{path}[*]', or ask what the list contains.");
            }

            if (op is ValueOperator.Contains or ValueOperator.NotContains && variable.ItemType is { } itemType)
            {
                CheckOperand(itemType, variable.Format, variable.Pattern, condition.Operands[0], path + "[*]", at, problems);
            }

            return;
        }

        var type = isList ? variable.ItemType ?? "any" : variable.Type;
        if (type == "object" || variable.Shape == TemplateVariableShape.Group)
        {
            if (op is not (ValueOperator.Exists or ValueOperator.Empty or ValueOperator.Type))
            {
                problems.Add($"{at}: '{path}' is an object in {where}; compare a property inside it.");
            }

            return;
        }

        var numeric = type is "number" or "integer";
        switch (op)
        {
            case ValueOperator.Matches or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith when numeric || type == "boolean":
                problems.Add($"{at}: '{path}' is {Article(type)} {type} in {where}; {AssertionText.Of(op)} compares text.");
                break;
            case ValueOperator.Contains or ValueOperator.NotContains or ValueOperator.Length when numeric || type == "boolean":
                problems.Add($"{at}: '{path}' is {Article(type)} {type} in {where}; {AssertionText.Of(op)} looks in text or a list.");
                break;
            case ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan or ValueOperator.Between when type == "boolean":
                problems.Add($"{at}: '{path}' is a boolean in {where}; it has no order.");
                break;
            case ValueOperator.Resolves when type != "string":
                problems.Add($"{at}: '{path}' is {Article(type)} {type} in {where}; a reference is text.");
                break;
            case ValueOperator.Resolves when condition.EntityType is { } entityType && variable.Relationships.Count > 0
                && !variable.Relationships.Any(r => string.Equals(r, entityType, StringComparison.OrdinalIgnoreCase)):
                problems.Add($"{at}: '{path}' refers to {string.Join(" or ", variable.Relationships)} in {where}, never to {entityType}.");
                break;
            case ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn
                or ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan or ValueOperator.Between:
                foreach (var operand in condition.Operands)
                {
                    CheckOperand(type, variable.Format, op is ValueOperator.EqualTo or ValueOperator.In ? variable.Pattern : null, operand, path, at, problems);
                }

                break;
        }
    }

    private static void CheckOperand(string type, string? format, string? pattern, ExpectedValue operand, string path, string at, ICollection<string> problems)
    {
        switch (type)
        {
            case "number" or "integer" when operand.Kind == ExpectedValueKind.Text && !RecordValues.TryParseNumber(operand.Text, out _):
                problems.Add($"{Prefix(at)}'{path}' is {Article(type)} {type}, and {operand} is not a number.");
                break;
            case "boolean" when operand.Kind != ExpectedValueKind.Boolean
                && !(operand.Kind == ExpectedValueKind.Text && operand.Text is "true" or "false"):
                problems.Add($"{Prefix(at)}'{path}' is a boolean, and {operand} is not true or false.");
                break;
            case "string" when format is "date" or "date-time" && operand.Kind == ExpectedValueKind.Number:
                problems.Add($"{Prefix(at)}'{path}' is a {format}, and {operand} is a number; write the date, such as \"2026-01-01T00:00:00Z\".");
                break;
            case "string" when format is "date" or "date-time" && operand.Kind == ExpectedValueKind.Text && !RecordValues.TryInstant(operand.Text, out _)
                && !operand.Text.Contains('{', StringComparison.Ordinal):
                problems.Add($"{Prefix(at)}'{path}' is a {format}, and {operand} is not an ISO 8601 date.");
                break;
            case "string" when pattern is not null && operand.Kind == ExpectedValueKind.Text && !operand.Text.Contains('{', StringComparison.Ordinal)
                && !Fits(pattern, operand.Text):
                problems.Add($"{Prefix(at)}{operand} cannot be a value of '{path}', whose schema pattern is {pattern}.");
                break;
        }
    }

    /// <summary>
    /// Every place a mapping's assertion does not fit the variable it is written beside, or the template its <c>where</c>
    /// fields read (osdu/docs/reference/flow/mapping-assertions.md): a record-stage condition has to suit the type of the
    /// value the record carries there (a list judged value by value, except by the conditions that judge a list whole; a
    /// list of objects only by whether it is there, empty, or how long), its operands have to be values of that type, and
    /// every field a condition reads has to be a property of the template, its condition suiting that property. An incoming
    /// condition judges what the row gives, whatever the template types it as, so only its fields are checked.
    /// </summary>
    internal static IReadOnlyList<string> CheckAssertion(TemplateVariable variable, NodeAssertion assertion, OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(template);
        var problems = new List<string>();
        var where = $"{template.Kind} (template {template.Version})";
        if (assertion.Stage == AssertionStage.Record)
        {
            CheckJudged(variable, assertion.Condition, Validation.MappingAssertionJudge.At(variable.Path), where, problems);
        }

        foreach (var field in assertion.Where.Select(filter => (filter.Field, filter.Condition)).Where(filter => filter.Field is not null))
        {
            if (Resolve(template, field.Field!, "where", where, problems) is { } filtered)
            {
                CheckCondition(filtered, field.Field!, field.Condition, "where", where, problems);
            }
        }

        return problems;
    }

    /// <summary>
    /// Whether a mapping a test holds its records to fits the test: it was read, it renders the kind the test reads, it asserts
    /// something of the values a record carries, and every such assertion fits the template the test reads (its property is
    /// one, its condition suits the property's type).
    /// </summary>
    private static void CheckMapping(AssertionTest test, MappingRulesAssertion rules, OsduTemplate template, string at, string where, ICollection<string> problems)
    {
        if (rules.Definition is not { } mapping)
        {
            problems.Add($"{at}: {rules.Unread ?? $"mapping {rules.Mapping} was not read"}.");
            return;
        }

        if (!string.Equals(mapping.Kind, test.Kind, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{at}: mapping {rules.Mapping} renders {mapping.Kind}, and the test reads {test.Kind}; a test holds the records of the kind a mapping renders to its assertions.");
            return;
        }

        var judged = mapping.Assertions().Where(a => a.Assertion.Stage == AssertionStage.Record).ToList();
        if (judged.Count == 0)
        {
            problems.Add(
                $"{at}: mapping {rules.Mapping} asserts nothing of the values a record carries (stage: record), so there is nothing to hold the records to; its incoming assertions judge the rows records were rendered from, which OSDU does not hold.");
            return;
        }

        foreach (var (entry, assertion) in judged)
        {
            if (template.Find(entry.Target) is not { } variable)
            {
                problems.Add($"{at}: {assertion.Location} asserts on {entry.Target.Text}, which is not a property of {where}.");
                continue;
            }

            foreach (var problem in CheckAssertion(variable, assertion, template))
            {
                problems.Add($"{at}: {assertion.Location}: {problem}");
            }
        }
    }

    /// <summary>Whether a record-stage condition suits the value a mapping's property carries.</summary>
    private static void CheckJudged(TemplateVariable variable, ValueCondition condition, string path, string where, ICollection<string> problems)
    {
        if (variable.Shape == TemplateVariableShape.Whole || variable.Type == "any")
        {
            return;
        }

        var op = condition.Operator;
        if (variable.Shape == TemplateVariableShape.GroupList)
        {
            if (op is not (ValueOperator.Exists or ValueOperator.Empty or ValueOperator.Length))
            {
                problems.Add(
                    $"'{path}' is a list of objects in {where}; an assertion on the list asks whether it is there (exists), whether it is empty (empty) or how many items it holds (length), "
                    + $"and {AssertionText.Of(op)} on a property of its items is written beside that property.");
            }

            return;
        }

        if (variable.Shape == TemplateVariableShape.Group || variable.Type == "object")
        {
            if (op is not (ValueOperator.Exists or ValueOperator.Empty))
            {
                problems.Add($"'{path}' is an object in {where}; assert on a property inside it.");
            }

            return;
        }

        var isList = variable.Shape == TemplateVariableShape.ValueList || variable.Type == "array";
        if (isList && op is ValueOperator.Exists or ValueOperator.Empty or ValueOperator.Length)
        {
            return;
        }

        if (isList && op is ValueOperator.Contains or ValueOperator.NotContains)
        {
            if (variable.ItemType is { } itemType)
            {
                CheckOperand(itemType, variable.Format, variable.Pattern, condition.Operands[0], path, string.Empty, problems);
            }

            return;
        }

        var type = isList ? variable.ItemType ?? "any" : variable.Type;
        if (type == "any")
        {
            return;
        }

        var numeric = type is "number" or "integer";
        var subject = isList ? $"'{path}' is a list of {type} values in {where}, each judged on its own" : $"'{path}' is {Article(type)} {type} in {where}";
        switch (op)
        {
            case ValueOperator.Matches or ValueOperator.NotMatches or ValueOperator.StartsWith or ValueOperator.EndsWith when numeric || type == "boolean":
                problems.Add($"{subject}; {AssertionText.Of(op)} compares text.");
                break;
            case ValueOperator.Contains or ValueOperator.NotContains or ValueOperator.Length when numeric || type == "boolean":
                problems.Add($"{subject}; {AssertionText.Of(op)} looks in text or a list.");
                break;
            case ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan or ValueOperator.Between when type == "boolean":
                problems.Add($"{subject}; a boolean has no order.");
                break;
            case ValueOperator.EqualTo or ValueOperator.NotEqualTo or ValueOperator.In or ValueOperator.NotIn
                or ValueOperator.AtLeast or ValueOperator.AtMost or ValueOperator.GreaterThan or ValueOperator.LessThan or ValueOperator.Between:
                foreach (var operand in condition.Operands)
                {
                    CheckOperand(type, variable.Format, op is ValueOperator.EqualTo or ValueOperator.In ? variable.Pattern : null, operand, path, string.Empty, problems);
                }

                break;
        }
    }

    private static string Prefix(string at) => at.Length == 0 ? string.Empty : at + ": ";

    /// <summary>The article a JSON Schema type takes in a sentence: an integer, an object, an array, a string.</summary>
    private static string Article(string type) => type.Length > 0 && "aeiou".Contains(type[0], StringComparison.Ordinal) ? "an" : "a";

    private static void CheckAggregate(TemplateVariable variable, AggregateAssertion aggregate, string at, ICollection<string> problems)
    {
        if (variable.Shape == TemplateVariableShape.Whole || variable.Type == "any")
        {
            return;
        }

        var type = variable.Shape is TemplateVariableShape.ValueList ? variable.ItemType ?? "any" : variable.Type;
        var numeric = type is "number" or "integer";
        var dated = type == "string" && variable.Format is "date" or "date-time";
        switch (aggregate.Function)
        {
            case AggregateFunction.Sum or AggregateFunction.Avg when !numeric && type != "any":
                problems.Add($"{at}: '{aggregate.Target}' is {Article(type)} {type}; {AssertionText.Of(aggregate.Function)} adds up numbers.");
                break;
            case AggregateFunction.Min or AggregateFunction.Max when !numeric && !dated && type != "any":
                problems.Add($"{at}: '{aggregate.Target}' is {Article(type)} {type}; {AssertionText.Of(aggregate.Function)} is taken of numbers or dates.");
                break;
            case AggregateFunction.Min or AggregateFunction.Max when dated && aggregate.Comparison.Terms.Any(t => t.Value.Kind == ExpectedValueKind.Number):
                problems.Add($"{at}: '{aggregate.Target}' is a {variable.Format}, and it is compared with a number; write the date.");
                break;
            case AggregateFunction.Min or AggregateFunction.Max when numeric && aggregate.Comparison.Terms.Any(t => t.Value.Kind == ExpectedValueKind.Text):
                problems.Add($"{at}: '{aggregate.Target}' is {Article(type)} {type}, and it is compared with text.");
                break;
        }
    }

    /// <summary>Whether text meets a schema pattern; a pattern .NET cannot read, or that takes too long, is not held against it.</summary>
    private static bool Fits(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>The property path nearest to <paramref name="wanted"/>, when one is near enough to be what was meant.</summary>
    private static string? Closest(string wanted, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Distance(wanted, candidate);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best is not null && bestDistance <= Math.Max(2, wanted.Length / 4) ? best : null;
    }

    /// <summary>The edit distance between two paths, ignoring case: how many letters differ.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    [GeneratedRegex(@"\[(\*|\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Steps();
}
