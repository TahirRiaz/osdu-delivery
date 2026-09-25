using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Expressions;

/// <summary>Why an expression cannot be read, with where in the text.</summary>
internal sealed class ExpressionSyntaxException(string message) : Exception(message);

/// <summary>
/// Reads an expression into its tree. The grammar, loosest binding first:
/// <code>
/// expression  := or
/// or          := and ('or' and)*
/// and         := not ('and' not)*
/// not         := 'not' not | comparison
/// comparison  := join (('=' | '!=' | '&lt;' | '&lt;=' | '&gt;' | '&gt;=') join | 'not'? 'in' '[' expression (',' expression)* ']')?
/// join        := sum ('&amp;' sum)*
/// sum         := product (('+' | '-') product)*
/// product     := unary (('*' | '/') unary)*
/// unary       := '-' unary | primary
/// primary     := number | "text" | 'text' | true | false | null | column | `column` | $dataset.column | $param.name
///              | function '(' (expression (',' expression)*)? ')' | '(' expression ')'
/// </code>
/// <c>iif(condition, value, other)</c> is written like a function and chooses, as SQL's IIF does. There is no
/// <c>? :</c> operator, since a YAML value cannot hold <c>": "</c> unquoted.
/// Keywords and function names are read ignoring case, as SQL reads them. A column whose name is a keyword, starts with a
/// digit or holds a hyphen is written in backticks.
/// </summary>
internal sealed class ExpressionParser
{
    /// <summary>How deeply an expression may nest; deeper is not an expression a mapping should hold, and it bounds the stack.</summary>
    private const int MaxDepth = 48;

    /// <summary>The choice, which reads like a function and takes a condition first.</summary>
    internal const string ChoiceName = "iif";

    /// <summary>How the choice is called.</summary>
    internal const string ChoiceSignature = "iif(condition, value, other)";

    private static readonly HashSet<string> Keywords = new(["and", "or", "not", "in", "true", "false", "null"], StringComparer.OrdinalIgnoreCase);

    private readonly string _text;
    private readonly string? _child;
    private readonly List<Token> _tokens;
    private int _next;
    private int _depth;

    public ExpressionParser(string text, string? child)
    {
        _text = text;
        _child = child;
        _tokens = Tokenize(text);
    }

    public ExpressionNode Parse()
    {
        var root = Expression();
        if (Peek.Kind != TokenKind.End)
        {
            throw Unexpected(Peek, "after a complete expression");
        }

        return root;
    }

    private Token Peek => _tokens[_next];

    private Token Take() => _tokens[_next++];

    private bool IsSymbol(string symbol) => Peek.Kind == TokenKind.Symbol && Peek.Value == symbol;

    private bool IsKeyword(string keyword) => Peek.Kind == TokenKind.Name && string.Equals(Peek.Value, keyword, StringComparison.OrdinalIgnoreCase);

    private bool IsKeywordAt(int offset, string keyword)
        => _next + offset < _tokens.Count && _tokens[_next + offset] is { Kind: TokenKind.Name } token && string.Equals(token.Value, keyword, StringComparison.OrdinalIgnoreCase);

    private string Slice(int start) => _text[start..(_next == 0 ? start : _tokens[_next - 1].End)].Trim();

    private ExpressionNode Expression()
    {
        Enter();
        var node = Or();
        Leave();
        return node;
    }

    private ExpressionNode Or()
    {
        var start = Peek.Position;
        var left = And();
        while (IsKeyword("or"))
        {
            Take();
            Condition(left, "the left side of 'or'");
            var right = And();
            Condition(right, "the right side of 'or'");
            left = new LogicNode(Slice(start), isAnd: false, left, right);
        }

        return left;
    }

    private ExpressionNode And()
    {
        var start = Peek.Position;
        var left = Not();
        while (IsKeyword("and"))
        {
            Take();
            Condition(left, "the left side of 'and'");
            var right = Not();
            Condition(right, "the right side of 'and'");
            left = new LogicNode(Slice(start), isAnd: true, left, right);
        }

        return left;
    }

    private ExpressionNode Not()
    {
        if (!IsKeyword("not"))
        {
            return Comparison();
        }

        Enter();
        var start = Take().Position;
        var operand = Not();
        Condition(operand, "what 'not' negates");
        Leave();
        return new NotNode(Slice(start), operand);
    }

    private ExpressionNode Comparison()
    {
        var start = Peek.Position;
        var left = Join();
        if (Peek.Kind == TokenKind.Symbol && Peek.Value is "=" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Take().Value;
            var right = Join();
            NotCondition(left, $"a side of '{op}'");
            NotCondition(right, $"a side of '{op}'");
            if (Peek.Kind == TokenKind.Symbol && Peek.Value is "=" or "!=" or "<" or "<=" or ">" or ">=")
            {
                throw new ExpressionSyntaxException(
                    $"'{Slice(start)} {Peek.Value} ...' chains comparisons (at character {Peek.Position + 1}); compare two values at a time and join the comparisons with and, such as a > 1 and a < 5");
            }

            return new ComparisonNode(Slice(start), op, left, right);
        }

        var negated = IsKeyword("not") && IsKeywordAt(1, "in");
        if (negated || IsKeyword("in"))
        {
            if (negated)
            {
                Take();
            }

            Take();
            NotCondition(left, "the value 'in' looks for");
            var items = List();
            return new InNode(Slice(start), left, items, negated);
        }

        if (Peek.Kind == TokenKind.Name && string.Equals(Peek.Value, "is", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExpressionSyntaxException(
                $"'is' (at character {Peek.Position + 1}) is not an operator of the expression language: compare with = or != (status = \"FINAL\", status != \"FINAL\"), and test for a value with empty(status) or not empty(status)");
        }

        return left;
    }

    private List<ExpressionNode> List()
    {
        if (!IsSymbol("["))
        {
            throw Unexpected(Peek, "where 'in' takes its list of values, such as status in [\"FINAL\", \"APPROVED\"]");
        }

        Take();
        var items = new List<ExpressionNode>();
        if (IsSymbol("]"))
        {
            throw new ExpressionSyntaxException($"the list at character {Peek.Position} is empty; 'in' looks for a value among at least one");
        }

        while (true)
        {
            var item = Expression();
            NotCondition(item, "an item of the list");
            items.Add(item);
            if (IsSymbol(","))
            {
                Take();
                continue;
            }

            if (IsSymbol("]"))
            {
                Take();
                return items;
            }

            throw Unexpected(Peek, "in a list, where a ',' or the closing ']' belongs");
        }
    }

    private ExpressionNode Join()
    {
        var start = Peek.Position;
        var left = Sum();
        while (IsSymbol("&"))
        {
            Take();
            var right = Sum();
            NotCondition(left, "a side of '&'");
            NotCondition(right, "a side of '&'");
            left = new JoinNode(Slice(start), left, right);
        }

        return left;
    }

    private ExpressionNode Sum()
    {
        var start = Peek.Position;
        var left = Product();
        while (IsSymbol("+") || IsSymbol("-"))
        {
            var op = Take().Value[0];
            var right = Product();
            Numeric(left, op);
            Numeric(right, op);
            left = new ArithmeticNode(Slice(start), op, left, right);
        }

        return left;
    }

    private ExpressionNode Product()
    {
        var start = Peek.Position;
        var left = Unary();
        while (IsSymbol("*") || IsSymbol("/"))
        {
            var op = Take().Value[0];
            var right = Unary();
            Numeric(left, op);
            Numeric(right, op);
            left = new ArithmeticNode(Slice(start), op, left, right);
        }

        return left;
    }

    private ExpressionNode Unary()
    {
        if (!IsSymbol("-"))
        {
            return Primary();
        }

        Enter();
        var start = Take().Position;
        var operand = Unary();
        Numeric(operand, '-');
        Leave();
        return new NegateNode(Slice(start), operand);
    }

    private ExpressionNode Primary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Take();
                return new LiteralNode(token.Value, decimal.Parse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture), ExpressionKind.Number);
            case TokenKind.Text:
                Take();
                return new LiteralNode(_text[token.Position..token.End], token.Value, ExpressionKind.Text);
            case TokenKind.Quoted:
                Take();
                return Column(token.Value, _text[token.Position..token.End], token);
            case TokenKind.Dollar:
                return Reference();
            case TokenKind.Symbol when token.Value == "(":
            {
                Enter();
                Take();
                var inner = Expression();
                if (!IsSymbol(")"))
                {
                    throw Unexpected(Peek, $"where the ')' closing the '(' at character {token.Position + 1} belongs");
                }

                Take();
                Leave();
                return inner;
            }

            case TokenKind.Name:
                return Name();
            case TokenKind.End:
                throw new ExpressionSyntaxException(_next == 0 ? "the expression is empty" : $"the expression ends after '{_text.Trim()}' where a value belongs");
            default:
                throw Unexpected(token, "where a value belongs");
        }
    }

    private ExpressionNode Name()
    {
        var token = Take();
        var word = token.Value;
        switch (word.ToLowerInvariant())
        {
            case "true":
                return new LiteralNode(word, true, ExpressionKind.Boolean);
            case "false":
                return new LiteralNode(word, false, ExpressionKind.Boolean);
            case "null":
                return new LiteralNode(word, null, ExpressionKind.Any);
        }

        if (Keywords.Contains(word))
        {
            throw new ExpressionSyntaxException($"'{word}' (at character {token.Position + 1}) is a word of the expression language where a value belongs; a column named {word} is written in backticks, `{word}`");
        }

        if (IsSymbol("("))
        {
            return Call(token);
        }

        if (IsSymbol("."))
        {
            var dotted = _next + 1 < _tokens.Count && _tokens[_next + 1].Kind is TokenKind.Name ? _tokens[_next + 1].Value : "<column>";
            throw new ExpressionSyntaxException(string.Equals(word, DatasetColumn.Prefix, StringComparison.Ordinal)
                ? $"'{word}.{dotted}' (at character {token.Position + 1}) reads a column the way the mapping language no longer writes it: a column of the dataset's own row is $dataset.{dotted}, and a column of the row the node reads is {dotted} alone"
                : _child is not null && string.Equals(word, _child, StringComparison.OrdinalIgnoreCase)
                    ? $"'{word}.{dotted}' (at character {token.Position + 1}) names {_child} again; under the $forEach over {_child} a bare name reads its row, so write {dotted}"
                    : $"'{word}.' (at character {token.Position + 1}) reads a path, and an expression reads one column by its name, a column of the dataset's own row as $dataset.<column>, and a parameter as $param.<name>");
        }

        if (ExpressionFunctions.Find(word) is not null || string.Equals(word, ChoiceName, StringComparison.OrdinalIgnoreCase))
        {
            // A function's name alone is most likely a call missing its brackets; a column of that name is still readable in backticks.
            throw new ExpressionSyntaxException(
                $"'{word}' (at character {token.Position + 1}) is a function, called as {ExpressionFunctions.Find(word)?.Signature ?? ChoiceSignature}; a column named {word} is written in backticks, `{word}`");
        }

        return Column(word, word, token);
    }

    private ColumnNode Column(string name, string written, Token token)
    {
        if (!ColumnName(name))
        {
            throw new ExpressionSyntaxException($"'{written}' (at character {token.Position + 1}) is not a column name; a column name holds letters, digits, '_' and '-'");
        }

        return new ColumnNode(written, new DatasetColumn(_child, name));
    }

    private ExpressionNode Reference()
    {
        var token = Take();
        var word = token.Value;
        if (!IsSymbol("."))
        {
            throw new ExpressionSyntaxException(ExpressionFunctions.Find(word) is { } function
                ? $"'${word}' (at character {token.Position + 1}): a function is written without '$', such as {function.Signature}"
                : $"'${word}' (at character {token.Position + 1}) is not a reference: an expression reads a column by its name, a column of the dataset's own row as $dataset.<column>, and a parameter as $param.<name>");
        }

        Take();
        var name = Peek;
        if (name.Kind is not (TokenKind.Name or TokenKind.Quoted))
        {
            throw Unexpected(name, $"where the name after '${word}.' belongs");
        }

        Take();
        var written = _text[token.Position..name.End];
        switch (word)
        {
            case DatasetColumn.Prefix:
                if (!ColumnName(name.Value))
                {
                    throw new ExpressionSyntaxException($"'{written}' (at character {token.Position + 1}) does not name a column; a column name holds letters, digits, '_' and '-'");
                }

                return new ColumnNode(written, new DatasetColumn(null, name.Value));
            case "param":
                if (name.Kind != TokenKind.Name || name.Value.Contains('-', StringComparison.Ordinal))
                {
                    throw new ExpressionSyntaxException($"'{written}' (at character {token.Position + 1}) does not name a parameter; a parameter name holds letters, digits and '_'");
                }

                return new ParameterNode(written, name.Value);
            default:
                throw new ExpressionSyntaxException(
                    $"'{written}' (at character {token.Position + 1}) reads nothing the expression language knows: a column of the dataset's own row is $dataset.<column>, and a parameter is $param.<name>");
        }
    }

    private ExpressionNode Call(Token name)
    {
        if (string.Equals(name.Value, ChoiceName, StringComparison.OrdinalIgnoreCase))
        {
            return Choice(name);
        }

        var function = ExpressionFunctions.Find(name.Value);
        if (function is null && string.Equals(name.Value, "concat", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExpressionSyntaxException($"'{name.Value}' (at character {name.Position + 1}): an expression joins text with '&', such as well & \" \" & run");
        }

        if (function is null)
        {
            var nearest = ExpressionFunctions.Nearest(name.Value);
            throw new ExpressionSyntaxException(nearest is not null
                ? $"'{name.Value}' (at character {name.Position + 1}) is not a function of the expression language. Did you mean {nearest.Signature}?"
                : name.Value.ToLowerInvariant() is "if" or "case" or "when" or "choose"
                    ? $"'{name.Value}' (at character {name.Position + 1}): an expression chooses with {ChoiceSignature}"
                    : $"'{name.Value}' (at character {name.Position + 1}) is not a function of the expression language, which has {ChoiceName}, {ExpressionFunctions.NameList}");
        }

        Enter();
        Take();
        var arguments = new List<ExpressionNode>();
        if (!IsSymbol(")"))
        {
            while (true)
            {
                arguments.Add(Expression());
                if (IsSymbol(","))
                {
                    Take();
                    continue;
                }

                if (IsSymbol(")"))
                {
                    break;
                }

                throw Unexpected(Peek, $"in the call of {function.Name}, where a ',' or the closing ')' belongs");
            }
        }

        Take();
        Leave();
        var written = Slice(name.Position);
        if (arguments.Count < function.MinArguments || arguments.Count > function.MaxArguments)
        {
            var takes = function.MinArguments == function.MaxArguments
                ? $"{function.MinArguments} value{(function.MinArguments == 1 ? string.Empty : "s")}"
                : function.MaxArguments == int.MaxValue ? $"at least {function.MinArguments} values" : $"{function.MinArguments} to {function.MaxArguments} values";
            throw new ExpressionSyntaxException($"'{written}' gives {function.Name} {arguments.Count} value{(arguments.Count == 1 ? string.Empty : "s")}, and it takes {takes}: {function.Signature}");
        }

        foreach (var argument in arguments)
        {
            NotCondition(argument, $"a value given to {function.Name}");
        }

        return new CallNode(written, function, arguments);
    }

    /// <summary><c>iif(condition, value, other)</c>: value where the condition holds, other where it does not.</summary>
    private ConditionalNode Choice(Token name)
    {
        Enter();
        Take();
        var arguments = new List<ExpressionNode>();
        while (!IsSymbol(")"))
        {
            arguments.Add(Expression());
            if (IsSymbol(","))
            {
                Take();
                continue;
            }

            if (!IsSymbol(")"))
            {
                throw Unexpected(Peek, $"in the call of {ChoiceName}, where a ',' or the closing ')' belongs");
            }
        }

        Take();
        Leave();
        var written = Slice(name.Position);
        if (arguments.Count != 3)
        {
            throw new ExpressionSyntaxException($"'{written}' gives {ChoiceName} {arguments.Count} value{(arguments.Count == 1 ? string.Empty : "s")}, and it takes 3: {ChoiceSignature}");
        }

        Condition(arguments[0], $"the condition {ChoiceName} tests");
        return new ConditionalNode(written, arguments[0], arguments[1], arguments[2]);
    }

    /// <summary>A node where true or false is needed: a comparison, a test, or a value only the row decides.</summary>
    private static void Condition(ExpressionNode node, string where)
    {
        if (node.Kind is not (ExpressionKind.Boolean or ExpressionKind.Any))
        {
            throw new ExpressionSyntaxException(
                $"{where} is '{node.Written}', which gives {Describe(node.Kind)}, not true or false; compare it, such as {node.Written} = \"...\", or test it with empty({node.Written})");
        }
    }

    /// <summary>A node where a value is needed: true or false is not one to compare or combine.</summary>
    private static void NotCondition(ExpressionNode node, string where)
    {
        if (node.Kind == ExpressionKind.Boolean && node is not LiteralNode)
        {
            throw new ExpressionSyntaxException($"{where} is '{node.Written}', a condition; a condition decides with and, or, not or '?', and is not compared or combined as a value");
        }
    }

    private static void Numeric(ExpressionNode node, char op)
    {
        if (node.Kind is ExpressionKind.Boolean or ExpressionKind.Text)
        {
            throw new ExpressionSyntaxException(
                $"'{node.Written}' gives {Describe(node.Kind)}, and '{op}' computes with numbers; join text with '&', such as a & \"-\" & b");
        }
    }

    private static string Describe(ExpressionKind kind) => kind switch
    {
        ExpressionKind.Boolean => "true or false",
        ExpressionKind.Number => "a number",
        ExpressionKind.Text => "text",
        _ => "a value",
    };

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw new ExpressionSyntaxException($"the expression nests more than {MaxDepth} levels deep; compute a value this involved in the ingestion SQL");
        }
    }

    private void Leave() => _depth--;

    private ExpressionSyntaxException Unexpected(Token token, string where) => token.Kind == TokenKind.End
        ? new ExpressionSyntaxException($"the expression ends {where}")
        : new ExpressionSyntaxException($"'{_text[token.Position..token.End]}' at character {token.Position + 1} is not expected {where}");

    private static bool ColumnName(string name)
        => name.Length > 0 && name.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c))
            {
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }

                if (i < text.Length && text[i] is 'e' or 'E')
                {
                    var exponent = i + 1;
                    if (exponent < text.Length && text[exponent] is '+' or '-')
                    {
                        exponent++;
                    }

                    if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
                    {
                        i = exponent;
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                        {
                            i++;
                        }
                    }
                }

                if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '_'))
                {
                    throw new ExpressionSyntaxException(
                        $"'{text[start..(i + 1)]}' at character {start + 1} runs a number into a name; a column whose name starts with a digit is written in backticks, such as `2d_flag`");
                }

                var number = text[start..i];
                if (!decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new ExpressionSyntaxException($"the number {number} at character {start + 1} is out of range");
                }

                tokens.Add(new Token(TokenKind.Number, number, start, i));
                continue;
            }

            if (c is '"' or '\'')
            {
                var value = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new ExpressionSyntaxException($"the text starting at character {start + 1} is not closed with {c}");
                    }

                    var d = text[i];
                    if (d == c)
                    {
                        i++;
                        break;
                    }

                    if (d == '\\')
                    {
                        if (i + 1 >= text.Length)
                        {
                            throw new ExpressionSyntaxException($"the text starting at character {start + 1} ends in a '\\' that escapes nothing");
                        }

                        value.Append(text[i + 1] switch
                        {
                            '\\' => '\\',
                            '"' => '"',
                            '\'' => '\'',
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            var other => throw new ExpressionSyntaxException(
                                $"'\\{other}' at character {i + 1} is not an escape; a text escapes \\\\, \\\", \\', \\n, \\t and \\r"),
                        });
                        i += 2;
                        continue;
                    }

                    value.Append(d);
                    i++;
                }

                tokens.Add(new Token(TokenKind.Text, value.ToString(), start, i));
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close < 0)
                {
                    throw new ExpressionSyntaxException($"the column name starting at character {start + 1} is not closed with `");
                }

                tokens.Add(new Token(TokenKind.Quoted, text[(i + 1)..close], start, close + 1));
                i = close + 1;
                continue;
            }

            if (c == '$')
            {
                i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                if (i == start + 1)
                {
                    throw new ExpressionSyntaxException(
                        $"the '$' at character {start + 1} starts no reference; a column of the dataset's own row is $dataset.<column>, and a parameter is $param.<name>");
                }

                tokens.Add(new Token(TokenKind.Dollar, text[(start + 1)..i], start, i));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Name, text[start..i], start, i));
                continue;
            }

            var pair = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            switch (pair)
            {
                case "!=" or "<=" or ">=":
                    tokens.Add(new Token(TokenKind.Symbol, pair, start, i + 2));
                    i += 2;
                    continue;
                case "==":
                    throw new ExpressionSyntaxException($"'==' at character {start + 1}: an expression compares with a single '=', such as status = \"FINAL\"");
                case "<>":
                    throw new ExpressionSyntaxException($"'<>' at character {start + 1}: an expression writes 'is not equal' as '!=', such as status != \"FINAL\"");
                case "&&":
                    throw new ExpressionSyntaxException($"'&&' at character {start + 1}: an expression joins conditions with the word and");
                case "||":
                    throw new ExpressionSyntaxException($"'||' at character {start + 1}: an expression joins conditions with the word or, and joins text with '&'");
                case "??":
                    throw new ExpressionSyntaxException($"'??' at character {start + 1}: an expression takes the first value that is there with coalesce(a, b)");
            }

            switch (c)
            {
                case '(' or ')' or '[' or ']' or ',' or '&' or '+' or '-' or '*' or '/' or '=' or '<' or '>' or '.':
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString(), start, i + 1));
                    i++;
                    continue;
                case '?':
                    throw new ExpressionSyntaxException($"'?' at character {start + 1}: an expression chooses with {ChoiceSignature}");
                case '!':
                    throw new ExpressionSyntaxException($"'!' at character {start + 1}: an expression negates a condition with the word not");
                default:
                    throw new ExpressionSyntaxException($"'{c}' at character {start + 1} is not part of the expression language");
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length, text.Length));
        return tokens;
    }

    private enum TokenKind
    {
        End,
        Number,
        Text,
        Name,
        Quoted,
        Dollar,
        Symbol,
    }

    private readonly record struct Token(TokenKind Kind, string Value, int Position, int End);
}
