using System.Text.Json.Serialization;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Validation;

/// <summary>Whether what a mapping writes reaches a variable on every row, on some rows, or never.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoverageState>))]
public enum CoverageState
{
    /// <summary>Nothing fills it, and nothing fills anything it holds.</summary>
    Empty,

    /// <summary>What fills it may leave it out: <c>required: false</c>, or an <c>appliesWhen</c> that does not hold on every row.</summary>
    Sometimes,

    /// <summary>It is filled on every row: a static value, or a required entry that always applies.</summary>
    Always,
}

/// <summary>How a mapping fills one variable of the template it pins.</summary>
/// <param name="Target">The variable's path, <c>osdu.data.FacilityName</c>.</param>
/// <param name="State">Whether the mapping reaches it on every row, on some rows, or never.</param>
/// <param name="Direct">True when an entry targets the variable itself; false for a holder filled through what it holds.</param>
/// <param name="Required">Whether the schema requires the property in the object that holds it.</param>
public sealed record VariableCoverage(string Target, CoverageState State, bool Direct, bool Required);

/// <summary>What a mapping fills of the template it pins, variable by variable, and what it leaves required and empty.</summary>
/// <param name="Variables">Every variable a mapping may fill, in template order, parents before their children.</param>
/// <param name="Issues">What the mapping leaves required and empty. The errors are the ones the delivery gate raises.</param>
public sealed record CoverageReport(IReadOnlyList<VariableCoverage> Variables, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// What a mapping covers of its template. The gate's own rule on required properties lives here
/// (<see cref="RequiredIssues"/>, which <see cref="Preflight"/> runs as its check 5), so what a view of the template shows
/// and what stops a delivery are one rule, never two that can drift apart.
/// </summary>
public static class MappingCoverage
{
    /// <summary>
    /// Whether an entry writes its target on every row: a static value, or a required entry that always applies. An entry
    /// that may be left out (<c>required: false</c>) or that only applies to some rows (<c>appliesWhen</c>) does not.
    /// </summary>
    public static bool FillsEveryRow(MappingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.AppliesWhen is null && (entry.IsStatic || entry.Required);
    }

    /// <summary>
    /// The gate's rule: every property the schema requires of <c>data</c> has an entry that is allowed to be empty only if
    /// the schema allows it. An object the schema requires (a WellboreTrajectory's VerticalMeasurement) is filled by the
    /// entries for its properties when it has no entry of its own, and one of them has to render on every row: a static
    /// value, or a required entry without appliesWhen, which holds the record when its value is empty.
    /// </summary>
    /// <param name="mapping">The mapping to judge.</param>
    /// <param name="schema">The template version it pins.</param>
    /// <param name="where">What the messages name the mapping by: its path in the repository, else its reference.</param>
    public static IReadOnlyList<ValidationIssue> RequiredIssues(MappingDefinition mapping, SchemaSnapshot schema, string where)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);

        var issues = new List<ValidationIssue>();
        foreach (var required in schema.RequiredAt("data"))
        {
            var target = $"{TemplatePath.Prefix}.data.{required}";
            var entry = mapping.Entries.FirstOrDefault(e => e.Target.Text == target);
            if (entry is not null)
            {
                if (!entry.IsStatic && !entry.Required)
                {
                    issues.Add(ValidationIssue.Error(
                        $"{where}: {entry.Where} is required: false, but the template requires {target}, so a record without it cannot be sent.", target));
                }

                continue;
            }

            var properties = mapping.Entries.Where(e => e.Target.Text.StartsWith(target + ".", StringComparison.Ordinal)).ToList();
            if (properties.Count == 0)
            {
                issues.Add(ValidationIssue.Error($"{where}: template {mapping.Template.Kind} requires {target}, which the mapping does not fill.", target));
            }
            else if (!properties.Any(FillsEveryRow))
            {
                issues.Add(ValidationIssue.Error(
                    $"{where}: template {mapping.Template.Kind} requires {target}, and every entry filling its properties ({string.Join(", ", properties.Select(p => p.Where))}) "
                    + $"may leave it out (required: false or appliesWhen), so a record without it cannot be sent. Make one of them required, or fill {target} itself.",
                    target));
            }
        }

        return issues;
    }

    /// <summary>
    /// What the mapping fills of the template, variable by variable, with what it leaves required and empty. The gate's
    /// errors (<see cref="RequiredIssues"/>) come first and are never contradicted; every other required variable the
    /// mapping leaves empty is a warning, because the gate does not stop a delivery for it.
    /// </summary>
    /// <remarks>
    /// A required variable inside an object nothing fills is not reported: the record holds no such object, so nothing is
    /// missing from it. Only the variables a mapping may fill are covered; what OSDU Delivery writes, what OSDU sets, and a
    /// list inside a repeated item are left out, because no mapping fills those. What each variable shows is rolled up
    /// from what it holds (<see cref="Rolled"/>); what the findings are judged on is what the mapping itself writes.
    /// </remarks>
    public static CoverageReport Of(MappingDefinition mapping, OsduTemplate template)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(template);
        var where = mapping.SourcePath ?? mapping.Reference.ToString();

        // What each entry writes, and what that makes of every object on the way to it: a holder is reached by the best of
        // what it holds, so an object with no entry of its own still reads as filled when its properties are filled.
        var own = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        var inside = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        foreach (var entry in mapping.Entries)
        {
            var state = FillsEveryRow(entry) ? CoverageState.Always : CoverageState.Sometimes;
            own[entry.Target.Text] = Best(own, entry.Target.Text, state);
            for (var holder = entry.Target.Parent; holder is not null; holder = holder.Parent)
            {
                inside[holder.Text] = Best(inside, holder.Text, state);
            }
        }

        // Whether the mapping reaches a variable at all, by itself or through anything it holds. This is what the
        // findings are judged on, so a required variable is missing when nothing in the document writes it.
        var states = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        var listed = new List<(string Path, string? Holder, bool Required)>();
        foreach (var variable in template.Fillable)
        {
            var path = variable.Path.Text;
            states[path] = Better(own.GetValueOrDefault(path, CoverageState.Empty), inside.GetValueOrDefault(path, CoverageState.Empty));
            listed.Add((path, variable.Path.Parent?.Text, variable.Required));
        }

        // A free key of an object that takes them (osdu.tags.DeliveredBy) is no variable of the template, but it is a
        // target the mapping fills, so it is covered the same way and a view can show it under the object that holds it.
        foreach (var entry in mapping.Entries)
        {
            var target = entry.Target.Text;
            if (states.ContainsKey(target) || template.Find(entry.Target) is not { Role: TemplateVariableRole.Mapping, Nested: false } key)
            {
                continue;
            }

            states[target] = own[target];
            listed.Add((target, entry.Target.Parent?.Text, key.Required));
        }

        var variables = Rolled(listed, states, own);

        var issues = new List<ValidationIssue>(RequiredIssues(mapping, template.Schema, where));
        var gated = new HashSet<string>(issues.Select(i => i.Target).OfType<string>(), StringComparer.Ordinal);
        foreach (var variable in template.Fillable)
        {
            var path = variable.Path.Text;
            var state = states[path];
            if (!variable.Required || state == CoverageState.Always || gated.Contains(path) || !Reached(variable, states))
            {
                continue;
            }

            issues.Add(state == CoverageState.Empty
                ? ValidationIssue.Warning($"{where}: template {template.Kind} requires {path}, which the mapping does not fill.", path)
                : ValidationIssue.Warning(
                    $"{where}: template {template.Kind} requires {path}, and what fills it may leave it out (required: false or appliesWhen).", path));
        }

        return new CoverageReport(variables, issues);
    }

    /// <summary>
    /// What each variable shows: an object is only as good as what it promises, so it takes the weakest state among the
    /// variables it holds that the mapping fills or the schema requires. One required property missing makes the object
    /// holding it missing, however many of its siblings are filled, and one filled on some rows only makes it that.
    /// A property nothing fills and nothing requires promises nothing, so it is passed over rather than dragging its
    /// object down; an object promising nothing at all is left to its own entry, which is all there is to say about it.
    /// </summary>
    /// <remarks>
    /// The states the findings are judged on are <paramref name="states"/>, not these: a required property is missing
    /// because nothing writes it, never because something beside it is.
    /// </remarks>
    private static List<VariableCoverage> Rolled(
        List<(string Path, string? Holder, bool Required)> listed,
        IReadOnlyDictionary<string, CoverageState> states,
        IReadOnlyDictionary<string, CoverageState> own)
    {
        // The variables each object holds, and then the objects read back to front, so what they hold is settled first.
        var held = new Dictionary<string, List<(string Path, bool Required)>>(StringComparer.Ordinal);
        foreach (var (path, holder, required) in listed)
        {
            if (holder is not null && states.ContainsKey(holder))
            {
                (held.TryGetValue(holder, out var siblings) ? siblings : held[holder] = []).Add((path, required));
            }
        }

        var shown = new Dictionary<string, CoverageState>(StringComparer.Ordinal);
        for (var i = listed.Count - 1; i >= 0; i--)
        {
            var path = listed[i].Path;
            var promised = held.GetValueOrDefault(path, [])
                .Where(child => states[child.Path] != CoverageState.Empty || child.Required)
                .Select(child => shown[child.Path])
                .ToList();
            shown[path] = promised.Count == 0
                ? own.GetValueOrDefault(path, CoverageState.Empty)
                : promised.Aggregate(CoverageState.Always, (worst, state) => state < worst ? state : worst);
        }

        return listed.Select(v => new VariableCoverage(v.Path, shown[v.Path], own.ContainsKey(v.Path), v.Required)).ToList();
    }

    /// <summary>
    /// Whether a required variable is reachable at all: every object on the way to it is filled. A property required inside
    /// an object the mapping never fills is not missing, because the record holds no such object.
    /// </summary>
    private static bool Reached(TemplateVariable variable, IReadOnlyDictionary<string, CoverageState> states)
    {
        for (var holder = variable.Path.Parent; holder is not null; holder = holder.Parent)
        {
            if (states.TryGetValue(holder.Text, out var state) && state == CoverageState.Empty)
            {
                return false;
            }
        }

        return true;
    }

    private static CoverageState Best(Dictionary<string, CoverageState> states, string path, CoverageState state)
        => Better(states.GetValueOrDefault(path, CoverageState.Empty), state);

    /// <summary>The stronger of two states: what reaches every row over what reaches some, and either over nothing.</summary>
    private static CoverageState Better(CoverageState left, CoverageState right) => left > right ? left : right;
}
