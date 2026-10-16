using Xunit;

namespace SqlFlow.Delivery.Search.Tests;

/// <summary>
/// A value written as a bare term (<see cref="LuceneText.Escape"/>), the form a prefix search takes in front of its star: every
/// character the query parser reserves escaped, whitespace too, and a value no escape can carry refused.
/// </summary>
public class LuceneTextTests
{
    [Fact]
    public void An_id_is_escaped_at_every_colon_and_dash_so_the_star_after_it_is_the_only_syntax()
    {
        Assert.Equal(@"dev\:master\-data\-\-Wellbore\:abc", LuceneText.Escape("dev:master-data--Wellbore:abc"));
    }

    [Fact]
    public void Every_reserved_character_and_whitespace_is_escaped_and_nothing_else()
    {
        Assert.Equal(@"a\+b\!c\(d\)e\^f\[g\]h\""i\{j\}k\~l\*m\?n\|o\&p\/q\=r\\s\ t", LuceneText.Escape("a+b!c(d)e^f[g]h\"i{j}k~l*m?n|o&p/q=r\\s t"));
        Assert.Equal("Wellbore_01.A", LuceneText.Escape("Wellbore_01.A"));
    }

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\u0001b")]
    public void A_value_no_escape_carries_is_refused_rather_than_sent_as_a_range(string value)
    {
        Assert.False(LuceneText.IsEscapable(value));
        Assert.Throws<ArgumentException>(() => LuceneText.Escape(value));
    }
}
