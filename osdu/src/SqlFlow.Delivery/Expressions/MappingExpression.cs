using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Expressions;

/// <summary>
/// An expression of a mapping (osdu/docs/mapping-templates.md, "Expressions"): the value a <c>$expr</c> node computes,
/// the condition a <c>$when</c> tests, or the row filter a <c>$forEach</c> node's <c>$where</c> applies. The language is
/// small on purpose. It reads columns and parameters, compares, chooses and combines values, and calls a fixed set of
/// functions named after their SQL counterparts (<see cref="ExpressionFunctions"/>); anything heavier is computed where
/// the data is made ready, in the ingestion SQL. It is read once, when the mapping is, and everything it reads is known
/// from then on: the columns (checked against the source like any other column) and the parameters.
/// </summary>
/// <remarks>
/// An expression is a pure function of the row, the dataset's own row and the parameters: it reads no clock, no random
/// source and nothing outside the render, so a record renders the same every time its inputs are the same.
/// </remarks>
public sealed class MappingExpression : IEquatable<MappingExpression>
{
    /// <summary>The longest expression the mapping language reads; a longer one belongs in the ingestion SQL.</summary>
    public const int MaxLength = 2000;

    private readonly ExpressionNode _root;

    private MappingExpression(string text, string? child, ExpressionNode root, IReadOnlyList<DatasetColumn> columns, IReadOnlyList<string> parameters)
    {
        Text = text;
        Child = child;
        _root = root;
        Columns = columns;
        Parameters = parameters;
    }

    /// <summary>The expression as the mapping writes it.</summary>
    public string Text { get; }

    /// <summary>The child dataset whose row a bare column name reads, or null for the dataset's own row.</summary>
    public string? Child { get; }

    /// <summary>The columns the expression reads, each once, in the order it first names them.</summary>
    public IReadOnlyList<DatasetColumn> Columns { get; }

    /// <summary>The parameters the expression reads, each once, in the order it first names them.</summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>True when the expression gives true or false whatever the row: a comparison, a test, or a join of them.</summary>
    public bool IsCondition => _root.Kind == ExpressionKind.Boolean;

    /// <summary>
    /// Reads an expression, or gives null with the reason it is refused: a syntax error, a name the language does not
    /// have, a function called with the wrong number of values, or a value used where it cannot be (text joined with
    /// <c>and</c>, say).
    /// </summary>
    /// <param name="text">The expression as the mapping writes it.</param>
    /// <param name="child">The child dataset whose rows a bare column name reads, under a <c>$forEach</c>; null for the dataset's own row.</param>
    /// <param name="problem">Why the expression is refused, or null.</param>
    public static MappingExpression? TryParse(string? text, string? child, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "the expression is empty";
            return null;
        }

        if (text.Length > MaxLength)
        {
            problem = $"the expression is {text.Length} characters long, and an expression is at most {MaxLength}; compute a value this involved in the ingestion SQL";
            return null;
        }

        ExpressionNode root;
        try
        {
            root = new ExpressionParser(text, child).Parse();
        }
        catch (ExpressionSyntaxException ex)
        {
            problem = ex.Message;
            return null;
        }

        var columns = new List<DatasetColumn>();
        var parameters = new List<string>();
        foreach (var node in root.DescendantsAndSelf())
        {
            switch (node)
            {
                case ColumnNode column when !columns.Contains(column.Column):
                    columns.Add(column.Column);
                    break;
                case ParameterNode parameter when !parameters.Contains(parameter.Name, StringComparer.Ordinal):
                    parameters.Add(parameter.Name);
                    break;
            }
        }

        return new MappingExpression(text.Trim(), child, root, columns, parameters);
    }

    /// <summary>
    /// The value the expression gives for a row: text, a number (a <see cref="decimal"/>, or a <see cref="double"/> where
    /// the row held one), true or false, a date as the row held it, or null for no value. False, with the reason, when the
    /// row holds a value the expression cannot work with (text where it computes with numbers, say).
    /// </summary>
    public bool TryEvaluate(ExpressionInput input, out object? value, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            value = _root.Evaluate(input);
            problem = null;
            return true;
        }
        catch (ExpressionEvaluationException ex)
        {
            value = null;
            problem = $"{Text}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Whether a condition holds for a row. No value counts as false, so a condition on a column the row leaves empty
    /// does not hold. False, with the reason, when the row holds a value the condition cannot test.
    /// </summary>
    public bool TryTest(ExpressionInput input, out bool holds, out string? problem)
    {
        holds = false;
        if (!TryEvaluate(input, out var value, out problem))
        {
            return false;
        }

        try
        {
            holds = ExpressionValues.Truth(value, _root);
            return true;
        }
        catch (ExpressionEvaluationException ex)
        {
            problem = $"{Text}: {ex.Message}";
            return false;
        }
    }

    public override string ToString() => Text;

    public bool Equals(MappingExpression? other)
        => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal) && string.Equals(Child, other.Child, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is MappingExpression other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Text), Child is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Child));
}

/// <summary>What an expression reads while it is evaluated.</summary>
/// <param name="Column">The value of a column: of the dataset's own row, or of the child row a node is evaluated for.</param>
/// <param name="Parameter">The value of a parameter, or null when the flow gives none.</param>
public sealed record ExpressionInput(Func<DatasetColumn, object?> Column, Func<string, string?> Parameter);
