using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Templates;

/// <summary>One step of a template path: a property name, and whether the path steps into the items of the array it names.</summary>
public readonly record struct TemplatePathSegment(string Name, bool IntoArray)
{
    public override string ToString() => IntoArray ? Name + "[]" : Name;
}

/// <summary>
/// The address of a template variable: <c>osdu.</c> followed by the property path in the OSDU record, with <c>[]</c>
/// marking the step into an array of objects (<c>osdu.data.Curves[].CurveID</c>). A path steps into at most one array:
/// a repeater inside a repeated item is not supported.
/// </summary>
public sealed partial class TemplatePath : IEquatable<TemplatePath>
{
    /// <summary>The first word of every template path.</summary>
    public const string Prefix = "osdu";

    private TemplatePath(IReadOnlyList<TemplatePathSegment> segments)
    {
        Segments = segments;
        Text = Prefix + "." + string.Join('.', segments.Select(s => s.ToString()));
        SchemaPath = string.Join('.', segments.Select(s => s.Name));
    }

    public IReadOnlyList<TemplatePathSegment> Segments { get; }

    /// <summary>The path as a mapping writes it: <c>osdu.data.Curves[].CurveID</c>.</summary>
    public string Text { get; }

    /// <summary>The dotted path from the record root the schema resolves, arrays stepped into implicitly: <c>data.Curves.CurveID</c>.</summary>
    public string SchemaPath { get; }

    /// <summary>The first property name after <c>osdu.</c>: <c>data</c>, <c>acl</c>, <c>legal</c>, <c>tags</c>.</summary>
    public string Root => Segments[0].Name;

    /// <summary>True when the path steps into an array of objects.</summary>
    public bool IsRepeated => Segments.Any(s => s.IntoArray);

    /// <summary>The array a repeated path steps into (<c>osdu.data.Curves</c> for <c>osdu.data.Curves[].CurveID</c>), or null.</summary>
    public TemplatePath? Repeater
    {
        get
        {
            var index = IndexOfArrayStep();
            return index < 0
                ? null
                : new TemplatePath(Segments.Take(index + 1).Select((s, i) => i == index ? s with { IntoArray = false } : s).ToList());
        }
    }

    /// <summary>The property names inside the repeated item (<c>CurveID</c>), or the whole path when it is not repeated.</summary>
    public IReadOnlyList<string> WithinItem
    {
        get
        {
            var index = IndexOfArrayStep();
            return Segments.Skip(index + 1).Select(s => s.Name).ToList();
        }
    }

    /// <summary>The path one level up, or null at the record root.</summary>
    public TemplatePath? Parent => Segments.Count <= 1
        ? null
        : new TemplatePath(Segments.Take(Segments.Count - 1).Select((s, i) => i == Segments.Count - 2 ? s with { IntoArray = false } : s).ToList());

    /// <summary>The last property name.</summary>
    public string Leaf => Segments[^1].Name;

    /// <summary>A path from segments, which must be valid names; used where the segments come from a schema.</summary>
    public static TemplatePath Of(IReadOnlyList<TemplatePathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException("A template path needs at least one property name.", nameof(segments));
        }

        return new TemplatePath(segments.ToList());
    }

    /// <summary>Parses a path as a mapping writes it, or says why it is not one.</summary>
    public static bool TryParse(string? text, out TemplatePath? path, out string? error)
    {
        path = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "a target is required, such as osdu.data.Name";
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Prefix + ".", StringComparison.Ordinal))
        {
            error = $"target '{trimmed}' must start with '{Prefix}.', the template of the OSDU record, such as osdu.data.Name";
            return false;
        }

        var parts = trimmed[(Prefix.Length + 1)..].Split('.');
        var segments = new List<TemplatePathSegment>(parts.Length);
        foreach (var part in parts)
        {
            var match = SegmentPattern().Match(part);
            if (!match.Success)
            {
                error = $"target '{trimmed}' has an invalid property name '{part}'";
                return false;
            }

            segments.Add(new TemplatePathSegment(match.Groups["name"].Value, match.Groups["array"].Success));
        }

        if (segments.Count(s => s.IntoArray) > 1)
        {
            error = $"target '{trimmed}' steps into more than one array; a repeater inside a repeated item is not supported";
            return false;
        }

        if (segments[^1].IntoArray)
        {
            var array = string.Join('.', segments.Select(s => s.Name));
            error = $"target '{trimmed}' names the items of an array; target a property inside them, such as {Prefix}.{array}[].Name, or the array itself as {Prefix}.{array}";
            return false;
        }

        path = new TemplatePath(segments);
        return true;
    }

    public bool Equals(TemplatePath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TemplatePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    private int IndexOfArrayStep()
    {
        for (var i = 0; i < Segments.Count; i++)
        {
            if (Segments[i].IntoArray)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^(?<name>[^\.\[\]\s]+)(?<array>\[\])?$")]
    private static partial Regex SegmentPattern();
}
