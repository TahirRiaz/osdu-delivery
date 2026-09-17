using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Workflows;

/// <summary>
/// The values a workflow template is filled from for one record (docs/interfaces-design.md section 5.9): the partition,
/// the anchor record as it was written, the datasets registered for its inputs, what earlier stages produced, and the
/// secrets, resolved only for the request that carries them.
/// </summary>
public sealed class WorkflowValues
{
    private readonly Dictionary<string, IReadOnlyList<string>> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public WorkflowValues(string partition, string appKey, JsonObject record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(record);
        Partition = partition;
        AppKey = appKey;
        Record = record;
        RecordId = record["id"] is JsonValue id && id.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new DeliveryException("the anchor record has no id, so no workflow value can be derived from it");
    }

    public string Partition { get; }

    public string AppKey { get; }

    /// <summary>The anchor record as the route wrote it.</summary>
    public JsonObject Record { get; }

    public string RecordId { get; }

    /// <summary>The run id of the stage being triggered.</summary>
    public string? RunId { get; set; }

    /// <summary>
    /// A stable tag for the anchor: a short hash of its id, so the records a workflow copies it onto can be searched for
    /// without the id's reserved characters.
    /// </summary>
    public string AnchorTag => Tag(RecordId);

    public static string Tag(string recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return "osdu-delivery-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(recordId)))[..24];
    }

    /// <summary>The id of a <c>dataset--File.Generic</c> record the route or a workflow writes for the anchor, derived from its id.</summary>
    public string DatasetId(string suffix) => DerivedDatasetId(RecordId, "dataset--File.Generic", suffix);

    /// <summary>
    /// A dataset id derived from a record id: the record's partition, the dataset's entity type, and the record's own
    /// unique segment with <paramref name="suffix"/> after it. The Dataset service keeps an id it is given when its
    /// entity type matches the kind (osdu/specs/core/INTEGRATION.md section 2.5), so a registration made again lands on
    /// the same record.
    /// </summary>
    public static string DerivedDatasetId(string recordId, string datasetEntityType, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetEntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        var first = recordId.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : recordId.IndexOf(':', first + 1);
        if (first <= 0 || second < 0 || second == recordId.Length - 1)
        {
            throw new DeliveryException($"record id '{recordId}' is not partition:type:name, so no dataset id can be derived from it");
        }

        return $"{recordId[..first]}:{datasetEntityType}:{recordId[(second + 1)..].TrimEnd(':')}-{suffix}";
    }

    public void SetInput(string name, IReadOnlyList<string> datasetIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(datasetIds);
        _inputs[name] = datasetIds;
    }

    public IReadOnlyList<string>? Input(string name) => _inputs.TryGetValue(name, out var ids) ? ids : null;

    public void SetOutput(int stage, string name, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _outputs[Key(stage, name)] = value?.DeepClone();
    }

    public bool TryOutput(int stage, string name, out JsonNode? value) => _outputs.TryGetValue(Key(stage, name), out value);

    public void SetSecret(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _secrets[name] = value;
    }

    public bool TrySecret(string name, out string value)
    {
        if (_secrets.TryGetValue(name, out var found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Key(int stage, string name) => stage.ToString(CultureInfo.InvariantCulture) + "." + name;
}

/// <summary>
/// The placeholder language of a workflow route's templates (docs/interfaces-design.md section 5.9). A string value of an
/// execution context, a result query or an output is text with placeholders:
/// <list type="bullet">
/// <item><c>{partition}</c>, <c>{appKey}</c>, <c>{runId}</c>, <c>{anchorTag}</c>;</item>
/// <item><c>{record:path}</c>, a value of the anchor record (<c>id</c>, <c>data.Datasets[0]</c>,
/// <c>data.Parameters[Title=work_product_id].DataObjectParameter</c>, <c>data.Components[*]</c>);</item>
/// <item><c>{input:name}</c>, the ids of the datasets registered for an input (<c>{input:name[0]}</c> for the first);</item>
/// <item><c>{dataset:suffix}</c>, a <c>dataset--File.Generic</c> id derived from the anchor's;</item>
/// <item><c>{stage:n.output}</c>, what stage n produced;</item>
/// <item><c>{secret:name}</c>, a secret the route declares, resolved only when the request is sent.</item>
/// </list>
/// Modifiers follow a bar: <c>|id</c> drops a record reference's version (<c>id:</c> or <c>id:123</c> becomes <c>id</c>),
/// <c>|ref</c> makes it a latest-version reference (<c>id:</c>), <c>|list</c> makes a single value a list, <c>|first</c>
/// takes a list's first item, <c>|json</c> writes a value as JSON text. A string that is exactly one placeholder takes the
/// value's own JSON shape, so <c>"{input:h5}"</c> is a list; text around a placeholder takes a scalar. <c>{{</c> and
/// <c>}}</c> are literal braces.
/// </summary>
public static partial class WorkflowTemplate
{
    public const string Redacted = "***";

    public static readonly IReadOnlyList<string> Names = ["partition", "appKey", "runId", "anchorTag", "record", "input", "dataset", "stage", "secret"];

    public static readonly IReadOnlyList<string> Modifiers = ["id", "ref", "list", "first", "json"];

    /// <summary>The placeholders a text holds, or the problem that keeps it from being read.</summary>
    public static IReadOnlyList<Placeholder> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<Placeholder>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                i += 2;
                continue;
            }

            if (c == '}')
            {
                throw new FormatException($"'{text}' has a '}}' that closes nothing; write '}}}}' for a literal brace");
            }

            if (c != '{')
            {
                i++;
                continue;
            }

            var end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                throw new FormatException($"'{text}' opens a placeholder that is never closed; write '{{{{' for a literal brace");
            }

            var match = PlaceholderPattern().Match(text[(i + 1)..end]);
            if (!match.Success)
            {
                throw new FormatException($"'{{{text[(i + 1)..end]}}}' is not a placeholder: name[:argument][|modifier], with a name among {string.Join(", ", Names)}");
            }

            var name = match.Groups["name"].Value;
            if (!Names.Contains(name, StringComparer.Ordinal))
            {
                throw new FormatException($"'{{{text[(i + 1)..end]}}}' names '{name}', which is not one of {string.Join(", ", Names)}");
            }

            var argument = match.Groups["arg"].Success ? match.Groups["arg"].Value.Trim() : null;
            var takesArgument = name is "record" or "input" or "dataset" or "stage" or "secret";
            if (takesArgument != (argument is { Length: > 0 }))
            {
                throw new FormatException(takesArgument
                    ? $"'{{{name}}}' needs an argument: {{{name}:...}}"
                    : $"'{{{text[(i + 1)..end]}}}': {name} takes no argument");
            }

            var modifiers = match.Groups["mod"].Captures.Select(m => m.Value).ToList();
            foreach (var modifier in modifiers.Where(m => !Modifiers.Contains(m, StringComparer.Ordinal)))
            {
                throw new FormatException($"'{{{text[(i + 1)..end]}}}' uses the modifier '{modifier}', which is not one of {string.Join(", ", Modifiers)}");
            }

            var placeholder = new Placeholder(i, end - i + 1, name, argument, modifiers);
            CheckArgument(placeholder);
            found.Add(placeholder);
            i = end + 1;
        }

        return found;
    }

    /// <summary>Every problem in a template's strings, with where it is; empty when all of them read.</summary>
    public static IReadOnlyList<string> Problems(JsonNode? template, string where)
    {
        var problems = new List<string>();
        foreach (var (path, text) in Strings(template, string.Empty))
        {
            try
            {
                Parse(text);
            }
            catch (FormatException ex)
            {
                problems.Add($"{where}{path}: {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>The placeholders a template's strings hold, with where each is.</summary>
    public static IEnumerable<(string Path, Placeholder Placeholder)> Placeholders(JsonNode? template)
    {
        foreach (var (path, text) in Strings(template, string.Empty))
        {
            foreach (var placeholder in Parse(text))
            {
                yield return (path, placeholder);
            }
        }
    }

    /// <summary>
    /// The template with every placeholder filled. A value that cannot be filled (an absent record property, an output an
    /// earlier stage did not produce) holds the record: the run would fail on it. Secrets are filled only when
    /// <paramref name="revealSecrets"/> is true, which only the request that carries them asks for; otherwise they read
    /// <see cref="Redacted"/>.
    /// </summary>
    public static JsonNode? Render(JsonNode? template, WorkflowValues values, bool revealSecrets)
    {
        ArgumentNullException.ThrowIfNull(values);
        return template switch
        {
            null => null,
            JsonObject obj => new JsonObject(obj.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Render(kv.Value, values, revealSecrets)))),
            JsonArray array => new JsonArray(array.Select(item => Render(item, values, revealSecrets)).ToArray()),
            JsonValue value when value.TryGetValue<string>(out var text) => RenderString(text, values, revealSecrets),
            _ => template.DeepClone(),
        };
    }

    /// <summary>A template string with its placeholders filled, as the shape a lone placeholder gives, or as text.</summary>
    public static JsonNode? RenderString(string text, WorkflowValues values, bool revealSecrets)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        var placeholders = Parse(text);
        if (placeholders.Count == 1 && placeholders[0].Start == 0 && placeholders[0].Length == text.Length)
        {
            return Fill(placeholders[0], values, revealSecrets);
        }

        var result = new StringBuilder(text.Length);
        var at = 0;
        foreach (var placeholder in placeholders)
        {
            result.Append(Literal(text[at..placeholder.Start]));
            var filled = Fill(placeholder, values, revealSecrets);
            result.Append(filled switch
            {
                JsonValue scalar when scalar.TryGetValue<string>(out var s) => s,
                JsonValue scalar => scalar.ToJsonString(),
                null => throw new RecordHeldException($"{placeholder} has no value to write into '{text}'"),
                _ => throw new RecordHeldException($"{placeholder} is a list or an object, and '{text}' writes it into text; make the placeholder the whole value, or take one item with |first or its JSON with |json"),
            });
            at = placeholder.Start + placeholder.Length;
        }

        result.Append(Literal(text[at..]));
        return JsonValue.Create(result.ToString());
    }

    /// <summary>A rendered template's text value; throws when it is not text.</summary>
    public static string RenderText(string text, WorkflowValues values)
        => RenderString(text, values, revealSecrets: false) is JsonValue value && value.TryGetValue<string>(out var rendered)
            ? rendered
            : throw new RecordHeldException($"'{text}' renders to a list or an object where text is needed");

    private static JsonNode? Fill(Placeholder placeholder, WorkflowValues values, bool revealSecrets)
    {
        JsonNode? value = placeholder.Name switch
        {
            "partition" => JsonValue.Create(values.Partition),
            "appKey" => JsonValue.Create(values.AppKey),
            "runId" => JsonValue.Create(values.RunId ?? throw new RecordHeldException("{runId} is used outside a stage's run")),
            "anchorTag" => JsonValue.Create(values.AnchorTag),
            "record" => RecordValue(values.Record, placeholder),
            "input" => InputValue(values, placeholder),
            "dataset" => JsonValue.Create(values.DatasetId(placeholder.Argument!)),
            "stage" => StageValue(values, placeholder),
            "secret" => revealSecrets
                ? JsonValue.Create(values.TrySecret(placeholder.Argument!, out var secret) ? secret : throw new RecordHeldException($"{placeholder} names a secret the route does not declare"))
                : JsonValue.Create(Redacted),
            _ => throw new RecordHeldException($"{placeholder} is not a placeholder"),
        };

        foreach (var modifier in placeholder.Modifiers)
        {
            value = Apply(modifier, value, placeholder);
        }

        return value;
    }

    private static JsonNode? RecordValue(JsonObject record, Placeholder placeholder)
    {
        var selected = JsonNodePath.Select(record, placeholder.Argument!);
        if (selected.Wildcard)
        {
            return new JsonArray(selected.Nodes.Select(n => n?.DeepClone()).ToArray());
        }

        return selected.Nodes.Count == 0 || selected.Nodes[0] is null
            ? throw new RecordHeldException($"{placeholder} reads a value the record does not have")
            : selected.Nodes[0]!.DeepClone();
    }

    private static JsonNode InputValue(WorkflowValues values, Placeholder placeholder)
    {
        var argument = placeholder.Argument!;
        var index = -1;
        var bracket = argument.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0)
        {
            index = int.Parse(argument[(bracket + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture);
            argument = argument[..bracket];
        }

        var ids = values.Input(argument) ?? throw new RecordHeldException($"{placeholder} names an input the route has not registered");
        if (index < 0)
        {
            return new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }

        return index < ids.Count
            ? JsonValue.Create(ids[index])
            : throw new RecordHeldException(string.Create(CultureInfo.InvariantCulture, $"{placeholder} asks for dataset {index} of the input, which registered {ids.Count}"));
    }

    private static JsonNode? StageValue(WorkflowValues values, Placeholder placeholder)
    {
        var argument = placeholder.Argument!;
        var dot = argument.IndexOf('.', StringComparison.Ordinal);
        var stage = int.Parse(argument[..dot], NumberStyles.None, CultureInfo.InvariantCulture);
        return values.TryOutput(stage, argument[(dot + 1)..], out var value)
            ? value?.DeepClone()
            : throw new RecordHeldException($"{placeholder} reads an output stage {stage} has not produced");
    }

    private static JsonNode? Apply(string modifier, JsonNode? value, Placeholder placeholder)
    {
        switch (modifier)
        {
            case "list":
                return value is JsonArray ? value : new JsonArray(value);
            case "first":
                return value is JsonArray items
                    ? (items.Count > 0 ? items[0]?.DeepClone() : throw new RecordHeldException($"{placeholder} takes the first item of an empty list"))
                    : value;
            case "json":
                return JsonValue.Create(value?.ToJsonString() ?? "null");
            case "id":
            case "ref":
                return value switch
                {
                    JsonArray references => new JsonArray(references.Select(item => Apply(modifier, item, placeholder)).ToArray()),
                    JsonValue scalar when scalar.TryGetValue<string>(out var id) => JsonValue.Create(modifier == "id" ? TargetId.WithoutVersion(id) : TargetId.WithoutVersion(id) + ":"),
                    _ => throw new RecordHeldException($"{placeholder} applies |{modifier} to something that is not a record id"),
                };
            default:
                throw new RecordHeldException($"{placeholder} uses an unknown modifier '{modifier}'");
        }
    }

    private static void CheckArgument(Placeholder placeholder)
    {
        var argument = placeholder.Argument;
        switch (placeholder.Name)
        {
            case "record":
                JsonNodePath.Validate(argument!);
                break;
            case "input" when !InputArgument().IsMatch(argument!):
                throw new FormatException($"'{placeholder}' names an input as name or name[index]");
            case "dataset" when !Suffix().IsMatch(argument!):
                throw new FormatException($"'{placeholder}' takes a suffix of letters, digits, '_', '-' and '.'");
            case "stage" when !StageArgument().IsMatch(argument!):
                throw new FormatException($"'{placeholder}' names an earlier stage's output as n.output, stages counted from 1");
            case "secret" when !Suffix().IsMatch(argument!):
                throw new FormatException($"'{placeholder}' names a secret the route declares under secrets");
        }
    }

    private static string Literal(string text) => text.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

    private static IEnumerable<(string Path, string Text)> Strings(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    foreach (var found in Strings(value, path + "." + key))
                    {
                        yield return found;
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var found in Strings(array[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"))
                    {
                        yield return found;
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return (path, text);
                break;
        }
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z]+)(?::(?<arg>[^|]+))?(?:\|(?<mod>[a-z]+))*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]*(\[[0-9]{1,4}\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex InputArgument();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();

    [GeneratedRegex(@"^[1-9][0-9]?\.[A-Za-z][A-Za-z0-9_\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StageArgument();
}

/// <summary>One placeholder of a template string: where it is, what it names, and how its value is changed.</summary>
public sealed record Placeholder(int Start, int Length, string Name, string? Argument, IReadOnlyList<string> Modifiers)
{
    public override string ToString()
        => "{" + Name + (Argument is null ? string.Empty : ":" + Argument) + string.Concat(Modifiers.Select(m => "|" + m)) + "}";
}

/// <summary>
/// A path into a JSON record: properties separated by dots, <c>[n]</c> for an item, <c>[*]</c> for every item, and
/// <c>[Property=value]</c> for the items of a list whose property has that text.
/// </summary>
public static partial class JsonNodePath
{
    public sealed record Selection(IReadOnlyList<JsonNode?> Nodes, bool Wildcard);

    private abstract record Step;

    private sealed record PropertyStep(string Name) : Step;

    private sealed record IndexStep(int Index) : Step;

    private sealed record AllStep : Step;

    private sealed record MatchStep(string Property, string Value) : Step;

    public static Selection Select(JsonNode root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        var steps = Parse(path);
        IReadOnlyList<JsonNode?> current = [root];
        var wildcard = false;
        foreach (var step in steps)
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                switch (step)
                {
                    case PropertyStep property when node is JsonObject obj && obj.TryGetPropertyValue(property.Name, out var child):
                        next.Add(child);
                        break;
                    case IndexStep index when node is JsonArray array && index.Index < array.Count:
                        next.Add(array[index.Index]);
                        break;
                    case AllStep when node is JsonArray array:
                        next.AddRange(array);
                        break;
                    case MatchStep match when node is JsonArray array:
                        next.AddRange(array.Where(item => item is JsonObject o
                            && o[match.Property] is JsonValue v
                            && string.Equals(v.TryGetValue<string>(out var s) ? s : v.ToJsonString(), match.Value, StringComparison.Ordinal)));
                        break;
                }
            }

            wildcard |= step is AllStep or MatchStep;
            current = next;
        }

        return new Selection(current, wildcard);
    }

    /// <summary>Checks a path's syntax; throws <see cref="FormatException"/> naming the problem.</summary>
    public static void Validate(string path) => Parse(path);

    private static List<Step> Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FormatException("a record path is empty");
        }

        var steps = new List<Step>();
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                if (i == 0 || i == path.Length - 1 || path[i - 1] == '.')
                {
                    throw new FormatException($"record path '{path}' has an empty property name");
                }

                i++;
                continue;
            }

            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                {
                    throw new FormatException($"record path '{path}' opens a '[' that is never closed");
                }

                var inner = path[(i + 1)..close];
                if (inner == "*")
                {
                    steps.Add(new AllStep());
                }
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    steps.Add(new IndexStep(index));
                }
                else if (MatchPattern().Match(inner) is { Success: true } match)
                {
                    steps.Add(new MatchStep(match.Groups["prop"].Value, match.Groups["value"].Value));
                }
                else
                {
                    throw new FormatException($"record path '{path}' has '[{inner}]', which is not [n], [*] or [Property=value]");
                }

                i = close + 1;
                continue;
            }

            var end = i;
            while (end < path.Length && path[end] is not ('.' or '['))
            {
                end++;
            }

            var name = path[i..end];
            if (!PropertyPattern().IsMatch(name))
            {
                throw new FormatException($"record path '{path}' has a property '{name}' that is not letters, digits, '_', '-' and '$'");
            }

            steps.Add(new PropertyStep(name));
            i = end;
        }

        return steps;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"^(?<prop>[A-Za-z0-9_\-\$]+)=(?<value>[^\]]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MatchPattern();
}
