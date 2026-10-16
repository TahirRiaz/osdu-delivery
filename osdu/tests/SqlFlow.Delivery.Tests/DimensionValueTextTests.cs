using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a dimension writes and orders its values: in the order the index keeps each type of field, and in one text whether
/// an aggregation's key or a scanned record gave the value.
/// </summary>
public class DimensionValueTextTests
{
    [Fact]
    public void Text_is_ordered_by_code_point_which_is_the_order_of_its_utf8_bytes()
    {
        var order = DimensionValueText.Order(OsduFieldIndex.Text);

        // U+1F600 is written as a surrogate pair, whose first unit (U+D83D) sorts before U+FF5E: ordinal comparison of
        // .NET strings puts it first, and code point order, like the index, puts it last.
        Assert.True(string.CompareOrdinal("\U0001F600", "～") < 0);
        Assert.True(order.Compare("\U0001F600", "～") > 0);
        Assert.True(order.Compare("A", "a") < 0);
        Assert.True(order.Compare("ab", "abc") < 0);
        Assert.Equal(0, order.Compare("same", "same"));
        Assert.True(order.Compare("æ", "z") > 0);
    }

    [Fact]
    public void A_lone_surrogate_orders_as_itself_rather_than_failing()
    {
        var order = DimensionValueText.Order(OsduFieldIndex.Keyword);
        Assert.True(order.Compare("\uD800", "￿") < 0);
        Assert.True(order.Compare("a\uDC00", "a") > 0);
    }

    [Theory]
    [InlineData("12.50", "12.5")]
    [InlineData("1.25E1", "12.5")]
    [InlineData("-0", "0")]
    [InlineData("0.000", "0")]
    [InlineData("1000", "1000")]
    [InlineData("1E+3", "1000")]
    [InlineData("-3.5", "-3.5")]
    [InlineData("1E+40", "1E+40")]
    [InlineData("0.1", "0.1")]
    public void A_number_has_one_text_however_it_was_written(string written, string canonical)
        => Assert.Equal(canonical, DimensionValueText.CanonicalNumber(written));

    [Theory]
    [InlineData("deep")]
    [InlineData("1,000")]
    [InlineData(" 12")]
    [InlineData("NaN")]
    [InlineData("")]
    public void Text_that_is_no_number_has_no_canonical_number(string written)
        => Assert.Null(DimensionValueText.CanonicalNumber(written));

    [Fact]
    public void Numbers_are_ordered_by_value_not_by_text()
    {
        var order = DimensionValueText.Order(OsduFieldIndex.Number);
        Assert.True(order.Compare("9", "10") < 0);
        Assert.True(order.Compare("-10", "-9") < 0);
        Assert.True(order.Compare("1E+40", "1") > 0);
    }

    [Theory]
    [InlineData("2020-01-02T00:00:00Z")]
    [InlineData("2020-01-02T00:00:00.000Z")]
    [InlineData("2020-01-02T01:00:00+01:00")]
    [InlineData("1577923200000")]
    public void A_date_has_one_text_whatever_form_it_was_written_in(string written)
        => Assert.Equal("2020-01-02T00:00:00.000Z", DimensionValueText.CanonicalDate(written));

    [Fact]
    public void Dates_are_ordered_by_instant_and_text_that_is_no_date_after_them()
    {
        var order = DimensionValueText.Order(OsduFieldIndex.Date);
        Assert.True(order.Compare("2020-01-02T00:00:00.000Z", "2019-12-31T00:00:00.000Z") > 0);
        Assert.True(order.Compare("not a date", "2020-01-02T00:00:00.000Z") > 0);
    }

    [Fact]
    public void Booleans_are_false_then_true()
    {
        var order = DimensionValueText.Order(OsduFieldIndex.Boolean);
        Assert.True(order.Compare("false", "true") < 0);
        Assert.Equal("true", DimensionValueText.CanonicalBoolean("1"));
        Assert.Equal("false", DimensionValueText.CanonicalBoolean("0"));
        Assert.Null(DimensionValueText.CanonicalBoolean("yes"));
    }

    [Fact]
    public void The_text_null_of_a_text_field_is_no_value_and_of_a_keyword_is_one()
    {
        Assert.Equal(ValueReading.Null, DimensionValueText.FromKey(OsduFieldIndex.Text, "null").Reading);
        Assert.Equal(ValueReading.Value, DimensionValueText.FromKey(OsduFieldIndex.Text, "NULL").Reading);
        Assert.Equal(ValueReading.Value, DimensionValueText.FromKey(OsduFieldIndex.Keyword, "null").Reading);
    }

    [Fact]
    public void A_scanned_value_reads_as_the_key_of_the_same_value_would()
    {
        Assert.Equal((ValueReading.Value, "12.5"), DimensionValueText.FromScan(OsduFieldIndex.Number, ScannedValue.OfNumber("12.50")));
        Assert.Equal((ValueReading.Value, "12.5"), DimensionValueText.FromScan(OsduFieldIndex.Number, ScannedValue.OfString("12.5")));
        Assert.Equal((ValueReading.Value, "true"), DimensionValueText.FromScan(OsduFieldIndex.Boolean, ScannedValue.OfBoolean(true)));
        Assert.Equal((ValueReading.Value, "true"), DimensionValueText.FromScan(OsduFieldIndex.Keyword, ScannedValue.OfBoolean(true)));
        Assert.Equal((ValueReading.Value, "7"), DimensionValueText.FromScan(OsduFieldIndex.Keyword, ScannedValue.OfNumber("7")));
        Assert.Equal(ValueReading.Null, DimensionValueText.FromScan(OsduFieldIndex.Text, ScannedValue.Null).Reading);
        Assert.Equal(ValueReading.Unreadable, DimensionValueText.FromScan(OsduFieldIndex.Keyword, ScannedValue.Null).Reading);
        Assert.Equal(ValueReading.Unreadable, DimensionValueText.FromScan(OsduFieldIndex.Text, ScannedValue.Composite).Reading);
        Assert.Equal(ValueReading.Unreadable, DimensionValueText.FromScan(OsduFieldIndex.Number, ScannedValue.OfString("deep")).Reading);
    }

    [Fact]
    public void A_date_far_beyond_what_a_date_can_hold_is_unreadable_rather_than_failing()
        => Assert.Equal(ValueReading.Unreadable, DimensionValueText.FromScan(OsduFieldIndex.Date, ScannedValue.OfNumber("9223372036854775807")).Reading);
}
