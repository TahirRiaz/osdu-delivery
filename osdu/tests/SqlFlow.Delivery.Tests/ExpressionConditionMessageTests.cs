using SqlFlow.Delivery.Expressions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The refusal of a condition used where a value belongs names only what the expression language has: and, or, not, and
/// iif(condition, value, other) to choose between values. It never offers '?', which the language refuses as an operator.
/// </summary>
public sealed class ExpressionConditionMessageTests
{
    [Theory]
    [InlineData("upper(a = 1)", "a value given to upper is 'a = 1', a condition")]
    [InlineData("(a = 1) & b", "a side of '&' is 'a = 1', a condition")]
    [InlineData("(a = 1) = b", "a side of '=' is 'a = 1', a condition")]
    public void A_condition_used_as_a_value_is_told_how_conditions_are_used_in_the_language(string text, string says)
    {
        Assert.Null(MappingExpression.TryParse(text, null, out var problem));

        Assert.Contains(says, problem, StringComparison.Ordinal);
        Assert.Contains("joined with and, or and not", problem, StringComparison.Ordinal);
        Assert.Contains("iif(condition, value, other)", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("'?'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_refusal_advises_parses_and_what_it_no_longer_advises_is_refused()
    {
        // The advice is the language's own: a condition chooses between values with iif, and '?' is refused as an operator.
        Assert.NotNull(MappingExpression.TryParse("upper(iif(a = 1, b, c))", null, out var advised));
        Assert.Null(advised);

        Assert.Null(MappingExpression.TryParse("upper(a = 1 ? b : c)", null, out var refused));
        Assert.Contains("'?' at character", refused, StringComparison.Ordinal);
        Assert.Contains("iif(condition, value, other)", refused, StringComparison.Ordinal);
    }
}
