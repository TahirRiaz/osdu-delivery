using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Model;

/// <summary>What one part of an id template writes.</summary>
public enum IdTokenKind
{
    /// <summary>Text written as it stands in the template.</summary>
    Text,

    /// <summary><c>{$value}</c>: the entry's value, after the modifiers before the id modifier.</summary>
    Value,

    /// <summary><c>{column}</c>, a column of the row the node reads, or <c>{$dataset.column}</c>, a column of the dataset's own row.</summary>
    Dataset,

    /// <summary><c>{$cache.Type.field}</c>: a field of the row of a cached lookup table whose key is the entry's value.</summary>
    Cache,

    /// <summary><c>{$param.name}</c>: a parameter of the mapping, as the flow supplies it.</summary>
    Parameter,
}

/// <summary>One part of an id template: text written as it stands, or a token whose value is written in its place.</summary>
public sealed record IdToken
{
    public required IdTokenKind Kind { get; init; }

    /// <summary>The literal text, or the token as the template writes it (<c>{wellbore_uwi}</c>).</summary>
    public required string Text { get; init; }

    /// <summary>For a dataset token: the column it reads.</summary>
    public DatasetColumn? Column { get; init; }

    /// <summary>For a cache token: the cached lookup table it reads.</summary>
    public string? CacheType { get; init; }

    /// <summary>For a cache token: the field, or a path into one, of the row it reads.</summary>
    public string? CacheField { get; init; }

    /// <summary>For a parameter token: the parameter's name.</summary>
    public string? Parameter { get; init; }

    public override string ToString() => Text;
}

/// <summary>
/// The template an <c>id</c> modifier builds an OSDU id from (<c>{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:</c>):
/// text written as it stands, and tokens that read the entry's value, a column of the row, a field of a cached lookup table
/// and a parameter of the mapping. A token of the mapping language starts with <c>$</c>, so a bare name is always a column:
/// <c>{curve_unit}</c> reads the row the node reads (under a <c>$forEach</c>, the item's row), and <c>{$dataset.log_id}</c>
/// the dataset's own row. A template is read once, when the mapping is, and refused there when it could never give an id:
/// a token it cannot read, text an id cannot carry, fewer parts than an id has, or no token that changes by record.
/// </summary>
public sealed partial record IdTemplate
{
    /// <summary>What a message says an id template reads.</summary>
    public const string TokenList = "{$value}, {<column>}, {$dataset.<column>}, {$cache.<Type>.<field>} and {$param.<name>}";

    /// <summary>The id template a message shows as an example.</summary>
    public const string Example = "\"{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:\"";

    /// <summary>
    /// The template a <c>ref</c> modifier stands for: a reference to a record of <paramref name="entityType"/> in the
    /// flow's partition, whose code is the node's value (<c>{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:</c>).
    /// </summary>
    public static IdTemplate Reference(string entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        var text = $"{{{Marker}{ParameterWord}.{Snapshots.RenderContext.DataPartitionParameter}}}:{entityType}:{{{Marker}{ValueWord}}}:";
        return TryParse(text, child: null, out var problem)
            ?? throw new ArgumentException($"'{entityType}' is not an entity type a reference can name: {problem}.", nameof(entityType));
    }

    /// <summary>Whether <paramref name="text"/> names an entity type in full, <c>group--Entity</c>, as a reference's template writes it.</summary>
    public static bool IsEntityType(string text) => !string.IsNullOrEmpty(text) && EntityTypePattern().IsMatch(text);

    private IdTemplate(string text, IReadOnlyList<IdToken> tokens, string? entityType)
    {
        Text = text;
        Tokens = tokens;
        EntityType = entityType;
    }

    /// <summary>The template as the mapping writes it.</summary>
    public string Text { get; }

    /// <summary>The template's parts, in order.</summary>
    public IReadOnlyList<IdToken> Tokens { get; }

    /// <summary>
    /// The entity type every id the template builds is of (<c>reference-data--UnitOfMeasure</c>), when the template writes
    /// it as text; null when a token gives it, so only a render knows it.
    /// </summary>
    public string? EntityType { get; }

    /// <summary>Whether the template writes the entry's value, <c>{$value}</c>.</summary>
    public bool ReadsValue => Tokens.Any(t => t.Kind == IdTokenKind.Value);

    /// <summary>The dataset columns the template reads, each once.</summary>
    public IEnumerable<DatasetColumn> Columns => Tokens.Where(t => t.Kind == IdTokenKind.Dataset).Select(t => t.Column!).Distinct();

    /// <summary>The cached lookup tables the template reads, each once.</summary>
    public IEnumerable<string> CacheTypes
        => Tokens.Where(t => t.Kind == IdTokenKind.Cache).Select(t => t.CacheType!).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The parameters the template reads, each once.</summary>
    public IEnumerable<string> Parameters => Tokens.Where(t => t.Kind == IdTokenKind.Parameter).Select(t => t.Parameter!).Distinct(StringComparer.Ordinal);

    public override string ToString() => Text;

    /// <summary>
    /// Reads an id template, or gives null with the reason it is refused. The literal text is what an OSDU id carries
    /// (ASCII letters, digits, <c>_ - . :</c> and percent-escapes such as <c>%2F</c>) and holds the colons that part an id:
    /// at least two, <c>partition:group--Entity:code</c>, and a third for a reference's version.
    /// </summary>
    /// <param name="text">The template as the mapping writes it.</param>
    /// <param name="child">
    /// The child dataset whose rows the node reads, when it sits under a <c>$forEach</c>; null for a node that reads the
    /// dataset's own row. A bare <c>{column}</c> reads that row.
    /// </param>
    /// <param name="problem">Why the template is refused, or null.</param>
    public static IdTemplate? TryParse(string? text, string? child, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = $"an id template is text with tokens, such as {Example}; this one is empty";
            return null;
        }

        var tokens = new List<IdToken>();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
            {
                tokens.Add(new IdToken { Kind = IdTokenKind.Text, Text = literal.ToString() });
                literal.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                var open = text.IndexOf('{', i + 1);
                if (close < 0 || (open >= 0 && open < close))
                {
                    problem = $"the '{{' at position {i + 1} opens a token that no '}}' closes";
                    return null;
                }

                Flush();
                var written = text[i..(close + 1)];
                if (Token(text[(i + 1)..close].Trim(), written, child, out problem) is not { } token)
                {
                    return null;
                }

                tokens.Add(token);
                i = close;
                continue;
            }

            if (c == '}')
            {
                problem = $"the '}}' at position {i + 1} closes a token that no '{{' opens";
                return null;
            }

            if (c == '%')
            {
                if (i + 2 >= text.Length || !Uri.IsHexDigit(text[i + 1]) || !Uri.IsHexDigit(text[i + 2]))
                {
                    problem = $"the '%' at position {i + 1} starts no percent-escape; write a '%' an id carries as %25";
                    return null;
                }

                literal.Append(text, i, 3);
                i += 2;
                continue;
            }

            if (!IsIdCharacter(c))
            {
                problem = $"'{Shown(c)}' at position {i + 1} is not a character an OSDU id carries; the text of an id is ASCII letters, digits, '_', '-', '.', ':' and percent-escapes such as %2F";
                return null;
            }

            literal.Append(c);
        }

        Flush();

        if (!tokens.Any(t => t.Kind is IdTokenKind.Value or IdTokenKind.Dataset or IdTokenKind.Cache))
        {
            problem = "the template reads nothing that changes from record to record, so every record would get the same id; write it as a literal, which takes {$param.<name>} too, or read "
                + "{$value}, a {<column>}, a {$dataset.<column>} or a {$cache.<Type>.<field>}";
            return null;
        }

        var colons = tokens.Where(t => t.Kind == IdTokenKind.Text).Sum(t => t.Text.Count(ch => ch == ':'));
        if (colons < 2)
        {
            problem = $"an OSDU id is <partition>:<group>--<Entity>:<code>, and a reference to one ends with ':' and the version, if any "
                + $"(dev:reference-data--UnitOfMeasure:m:); the template writes {colons} colon(s) of its own, and a token's value never parts an id";
            return null;
        }

        var entityType = LiteralEntityType(tokens, out var entityProblem);
        if (entityProblem is not null)
        {
            problem = entityProblem;
            return null;
        }

        return new IdTemplate(text, tokens, entityType);
    }

    /// <summary>Whether <paramref name="c"/> is written into an id as it stands: an ASCII letter or digit, '_', '-', '.' or ':'.</summary>
    public static bool IsIdCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>
    /// One token of an id template: a bare name is a column of the row the node reads, and a word of the mapping language
    /// starts with <c>$</c>, so no column's name is ever read as one.
    /// </summary>
    private static IdToken? Token(string inner, string written, string? child, out string? problem)
    {
        problem = null;
        if (!inner.StartsWith(Marker, StringComparison.Ordinal))
        {
            if (NamePattern().IsMatch(inner))
            {
                return new IdToken { Kind = IdTokenKind.Dataset, Text = written, Column = new DatasetColumn(child, inner) };
            }

            // A reader of the old vocabulary writes {value} or {param.x} without the marker; a dotted name is never a
            // column, so it is refused with the token it most likely meant.
            var first = inner.Split('.')[0];
            problem = first is ValueWord or ParameterWord or MappingSource.CachePrefix or DatasetColumn.Prefix
                ? $"{written} is not a column; the mapping's own tokens start with '$', so write {{{Marker}{inner}}}"
                : $"{written} is not a token; an id template reads {TokenList}";
            return null;
        }

        var parts = inner[Marker.Length..].Split('.');
        switch (parts[0])
        {
            case ValueWord when parts.Length == 1:
                return new IdToken { Kind = IdTokenKind.Value, Text = written };
            case DatasetColumn.Prefix when parts.Length == 2 && NamePattern().IsMatch(parts[1]):
                return new IdToken { Kind = IdTokenKind.Dataset, Text = written, Column = new DatasetColumn(null, parts[1]) };
            case DatasetColumn.Prefix:
                problem = $"{written} reads a column of the dataset's own row as {{{Marker}dataset.<column>}}, and a column of the row the node reads as {{<column>}}";
                return null;
            case MappingSource.CachePrefix when parts.Length >= 3 && NamePattern().IsMatch(parts[1]) && parts.Skip(2).All(p => FieldPattern().IsMatch(p)):
                return new IdToken
                {
                    Kind = IdTokenKind.Cache,
                    Text = written,
                    CacheType = parts[1],
                    CacheField = string.Join('.', parts.Skip(2)),
                };
            case MappingSource.CachePrefix:
                problem = $"{written} reads a cached lookup table as {{{Marker}cache.<Type>.<field>}}, such as {{{Marker}cache.CurveDictionary.log_curve_type_id}}";
                return null;
            case ParameterWord when parts.Length == 2 && ParameterPattern().IsMatch(parts[1]):
                return new IdToken { Kind = IdTokenKind.Parameter, Text = written, Parameter = parts[1] };
            case ParameterWord:
                problem = $"{written} reads a parameter as {{{Marker}param.<name>}}, with a name of letters, digits and '_'";
                return null;
            default:
                problem = $"{written} is not a token; an id template reads {TokenList}";
                return null;
        }
    }

    /// <summary>What starts every word of the mapping language.</summary>
    private const string Marker = "$";

    private const string ValueWord = "value";

    private const string ParameterWord = "param";

    /// <summary>
    /// The entity type between the first and second colon of the template's own text, when nothing but text writes it. A
    /// token there leaves it to the render. Text there that is no entity type (<c>group--Entity</c>) is refused.
    /// </summary>
    private static string? LiteralEntityType(IReadOnlyList<IdToken> tokens, out string? problem)
    {
        problem = null;
        var segment = new StringBuilder();
        var colons = 0;
        var tokenInside = false;
        foreach (var token in tokens)
        {
            if (token.Kind != IdTokenKind.Text)
            {
                if (colons == 1)
                {
                    tokenInside = true;
                }

                continue;
            }

            foreach (var c in token.Text)
            {
                if (c == ':')
                {
                    colons++;
                    if (colons == 2)
                    {
                        break;
                    }

                    continue;
                }

                if (colons == 1)
                {
                    segment.Append(c);
                }
            }

            if (colons >= 2)
            {
                break;
            }
        }

        if (tokenInside)
        {
            return null;
        }

        var entityType = segment.ToString();
        if (!EntityTypePattern().IsMatch(entityType))
        {
            problem = $"the part between the first and second colon is the entity type, <group>--<Entity> such as reference-data--UnitOfMeasure or master-data--Wellbore, and the template writes '{entityType}' there";
            return null;
        }

        return entityType;
    }

    private static string Shown(char c) => char.IsControl(c) || char.IsWhiteSpace(c)
        ? "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
        : c.ToString();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\$]+$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+--[A-Za-z0-9_\-\.]+$")]
    private static partial Regex EntityTypePattern();
}
