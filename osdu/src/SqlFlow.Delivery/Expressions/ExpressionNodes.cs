using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Expressions;

/// <summary>What an expression gives whatever the row, as far as its text says; <see cref="Any"/> when only the row decides.</summary>
internal enum ExpressionKind
{
    Any,
    Boolean,
    Number,
    Text,
}

/// <summary>Why an expression could not be evaluated for a row; the message names the part of the expression and the value.</summary>
internal sealed class ExpressionEvaluationException(string message) : Exception(message);

/// <summary>One part of an expression, with the text it was written as, so a message can quote it.</summary>
internal abstract class ExpressionNode(string written)
{
    public string Written { get; } = written;

    public abstract ExpressionKind Kind { get; }

    public virtual IEnumerable<ExpressionNode> Children => [];

    public abstract object? Evaluate(ExpressionInput input);

    public IEnumerable<ExpressionNode> DescendantsAndSelf()
    {
        var pending = new Stack<ExpressionNode>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            foreach (var child in node.Children.Reverse())
            {
                pending.Push(child);
            }
        }
    }
}

internal sealed class LiteralNode(string written, object? value, ExpressionKind kind) : ExpressionNode(written)
{
    public object? Value { get; } = value;

    public override ExpressionKind Kind => kind;

    public override object? Evaluate(ExpressionInput input) => Value;
}

internal sealed class ColumnNode(string written, DatasetColumn column) : ExpressionNode(written)
{
    public DatasetColumn Column { get; } = column;

    public override ExpressionKind Kind => ExpressionKind.Any;

    public override object? Evaluate(ExpressionInput input) => ExpressionValues.Normalize(input.Column(Column));
}

/// <summary>A parameter: text as the flow gives it, which compares and computes as a number where it is one.</summary>
internal sealed class ParameterNode(string written, string name) : ExpressionNode(written)
{
    public string Name { get; } = name;

    public override ExpressionKind Kind => ExpressionKind.Any;

    public override object? Evaluate(ExpressionInput input) => input.Parameter(Name);
}

internal sealed class NegateNode(string written, ExpressionNode operand) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Number;

    public override IEnumerable<ExpressionNode> Children => [operand];

    public override object? Evaluate(ExpressionInput input)
    {
        var value = operand.Evaluate(input);
        if (ExpressionValues.IsEmpty(value))
        {
            return null;
        }

        return ExpressionValues.Number(value, operand, "'-' negates") switch
        {
            decimal exact => -exact,
            var real => -(double)real,
        };
    }
}

internal sealed class NotNode(string written, ExpressionNode operand) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Boolean;

    public override IEnumerable<ExpressionNode> Children => [operand];

    public override object? Evaluate(ExpressionInput input) => !ExpressionValues.Truth(operand.Evaluate(input), operand);
}

/// <summary><c>and</c> and <c>or</c>, which read their right side only when the left does not decide.</summary>
internal sealed class LogicNode(string written, bool isAnd, ExpressionNode left, ExpressionNode right) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Boolean;

    public override IEnumerable<ExpressionNode> Children => [left, right];

    public override object? Evaluate(ExpressionInput input)
    {
        var first = ExpressionValues.Truth(left.Evaluate(input), left);
        if (isAnd != first)
        {
            return first;
        }

        return ExpressionValues.Truth(right.Evaluate(input), right);
    }
}

internal sealed class ConditionalNode(string written, ExpressionNode condition, ExpressionNode then, ExpressionNode otherwise) : ExpressionNode(written)
{
    public override ExpressionKind Kind => then.Kind == otherwise.Kind ? then.Kind : ExpressionKind.Any;

    public override IEnumerable<ExpressionNode> Children => [condition, then, otherwise];

    public override object? Evaluate(ExpressionInput input)
        => ExpressionValues.Truth(condition.Evaluate(input), condition) ? then.Evaluate(input) : otherwise.Evaluate(input);
}

internal sealed class ComparisonNode(string written, string op, ExpressionNode left, ExpressionNode right) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Boolean;

    public override IEnumerable<ExpressionNode> Children => [left, right];

    public override object? Evaluate(ExpressionInput input)
    {
        var a = left.Evaluate(input);
        var b = right.Evaluate(input);
        return op switch
        {
            "=" => ExpressionValues.Same(a, b),
            "!=" => !ExpressionValues.Same(a, b),
            _ => ExpressionValues.Order(a, b, left, right, op) is { } order && op switch
            {
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                _ => order >= 0,
            },
        };
    }
}

internal sealed class InNode(string written, ExpressionNode operand, IReadOnlyList<ExpressionNode> items, bool negated) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Boolean;

    public override IEnumerable<ExpressionNode> Children => [operand, .. items];

    public override object? Evaluate(ExpressionInput input)
    {
        var value = operand.Evaluate(input);
        var found = items.Any(item => ExpressionValues.Same(value, item.Evaluate(input)));
        return found != negated;
    }
}

/// <summary><c>a &amp; b</c>: the text of both, no value reading as no text.</summary>
internal sealed class JoinNode(string written, ExpressionNode left, ExpressionNode right) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Text;

    public override IEnumerable<ExpressionNode> Children => [left, right];

    public override object? Evaluate(ExpressionInput input)
        => (ExpressionValues.Text(left.Evaluate(input)) ?? string.Empty) + (ExpressionValues.Text(right.Evaluate(input)) ?? string.Empty);
}

internal sealed class ArithmeticNode(string written, char op, ExpressionNode left, ExpressionNode right) : ExpressionNode(written)
{
    public override ExpressionKind Kind => ExpressionKind.Number;

    public override IEnumerable<ExpressionNode> Children => [left, right];

    public override object? Evaluate(ExpressionInput input)
    {
        var a = left.Evaluate(input);
        var b = right.Evaluate(input);
        if (ExpressionValues.IsEmpty(a) || ExpressionValues.IsEmpty(b))
        {
            // As in SQL, arithmetic on no value gives no value; coalesce says what to use instead.
            return null;
        }

        var what = $"'{op}' computes with numbers";
        return ExpressionValues.Arithmetic(op, ExpressionValues.Number(a, left, what), ExpressionValues.Number(b, right, what), this);
    }
}

internal sealed class CallNode(string written, ExpressionFunction function, IReadOnlyList<ExpressionNode> arguments) : ExpressionNode(written)
{
    public override ExpressionKind Kind => function.Kind;

    public override IEnumerable<ExpressionNode> Children => arguments;

    public override object? Evaluate(ExpressionInput input)
    {
        var values = new object?[arguments.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = arguments[i].Evaluate(input);
        }

        return function.Invoke(new FunctionCall(function, values, arguments));
    }
}
