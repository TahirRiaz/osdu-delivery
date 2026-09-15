using System.Text;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// The three-part name of an ingestion table, <c>[database].[schema].[table]</c>: parsed from what a flow declares
/// (plain parts, or bracketed parts that may hold a '.' or an escaped ']'), and quoted back for the SQL a read sends.
/// </summary>
public sealed record SourceObjectName(string Database, string Schema, string Name)
{
    /// <summary>The longest part SQL Server accepts in an identifier.</summary>
    public const int MaxPartLength = 128;

    /// <summary>The name bracket-quoted for SQL: <c>[database].[schema].[table]</c>, every ']' doubled.</summary>
    public string Quoted => $"{Quote(Database)}.{Quote(Schema)}.{Quote(Name)}";

    /// <summary>The name as a flow writes it, parts separated by '.'; a part holding '.' or ']' is bracketed.</summary>
    public override string ToString() => $"{Display(Database)}.{Display(Schema)}.{Display(Name)}";

    /// <summary>Bracket-quotes one identifier, doubling every ']' inside it.</summary>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Parses a declared three-part name; throws <see cref="DeliveryException"/> with the reason.</summary>
    public static SourceObjectName Parse(string text)
        => TryParse(text, out var name, out var problem)
            ? name
            : throw new DeliveryException($"'{text}' is not a three-part name [database].[schema].[table]: {problem}");

    /// <summary>Parses a declared three-part name, or says why it is not one.</summary>
    public static bool TryParse(string? text, out SourceObjectName name, out string problem)
    {
        name = new SourceObjectName(string.Empty, string.Empty, string.Empty);
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "the name is empty";
            return false;
        }

        var parts = new List<string>(3);
        var current = new StringBuilder();
        var i = 0;
        var trimmed = text.Trim();
        while (i < trimmed.Length)
        {
            var c = trimmed[i];
            if (c == '[')
            {
                if (current.Length > 0)
                {
                    problem = "a '[' opens in the middle of a part";
                    return false;
                }

                i++;
                var closed = false;
                while (i < trimmed.Length)
                {
                    if (trimmed[i] == ']')
                    {
                        if (i + 1 < trimmed.Length && trimmed[i + 1] == ']')
                        {
                            current.Append(']');
                            i += 2;
                            continue;
                        }

                        closed = true;
                        i++;
                        break;
                    }

                    current.Append(trimmed[i]);
                    i++;
                }

                if (!closed)
                {
                    problem = "a '[' is never closed";
                    return false;
                }

                if (i < trimmed.Length && trimmed[i] != '.')
                {
                    problem = "a bracketed part is followed by something other than '.'";
                    return false;
                }

                continue;
            }

            if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
                continue;
            }

            if (c == ']' || char.IsControl(c))
            {
                problem = c == ']' ? "a ']' appears outside brackets" : "the name holds a control character";
                return false;
            }

            current.Append(c);
            i++;
        }

        parts.Add(current.ToString());
        if (parts.Count != 3)
        {
            problem = $"it has {parts.Count} part(s)";
            return false;
        }

        if (parts.Any(p => p.Trim().Length == 0))
        {
            problem = "a part is empty";
            return false;
        }

        if (parts.Any(p => p.Length > MaxPartLength))
        {
            problem = $"a part is longer than {MaxPartLength} characters";
            return false;
        }

        if (parts.Any(p => p.Contains('{', StringComparison.Ordinal)))
        {
            problem = "a part holds a '{' token; an ingestion table is named without parameters";
            return false;
        }

        name = new SourceObjectName(parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
        return true;
    }

    private static string Display(string part)
        => part.Contains('.', StringComparison.Ordinal) || part.Contains(']', StringComparison.Ordinal) || part.Contains('[', StringComparison.Ordinal)
            ? Quote(part)
            : part;
}
