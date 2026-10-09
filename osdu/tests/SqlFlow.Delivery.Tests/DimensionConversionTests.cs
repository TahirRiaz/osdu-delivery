using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Engine.Dimensions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The module's conversions (docs/dimension-plan.md, Views, Data types) run on SQL Server: each reads the text dimensions
/// hold as the reference's table says, gives null rather than an error for what it cannot read, never cuts a text or a
/// fraction, and answers the same whatever the reading session's date format or language.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DimensionConversionTests : IDisposable
{
    private const string Null = "(null)";

    private readonly OsduTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("float", "203.149", "203.149")]
    [InlineData("float", "-999.25", "-999.25")]
    [InlineData("float", "1.5E3", "1500")]
    [InlineData("float", "12,5", Null)]
    [InlineData("float", "NaN", Null)]
    [InlineData("decimal(18,2)", "203.149", "203.15")]
    [InlineData("decimal(18,2)", "1.5E3", "1500.00")]
    [InlineData("decimal(5,2)", "123456", Null)]
    [InlineData("int", "7", "7")]
    [InlineData("int", "7.0", "7")]
    [InlineData("int", "7E0", "7")]
    [InlineData("int", " 7 ", "7")]
    [InlineData("int", "7.5", Null)]
    [InlineData("int", "99999999999", Null)]
    [InlineData("bigint", "99999999999", "99999999999")]
    [InlineData("bit", "true", "True")]
    [InlineData("bit", "FALSE", "False")]
    [InlineData("bit", "1", "True")]
    [InlineData("bit", "yes", Null)]
    [InlineData("bit", "2", Null)]
    [InlineData("datetime2(3)", "2013-03-22T11:16:03.123Z", "2013-03-22T11:16:03.1230000")]
    [InlineData("datetime2(3)", "2013-03-22T11:16:03+02:00", "2013-03-22T09:16:03.0000000")]
    [InlineData("datetime2(0)", "2013-03-22", "2013-03-22T00:00:00.0000000")]
    [InlineData("datetime2(0)", "03/04/2013", Null)]
    [InlineData("datetimeoffset(0)", "2013-03-22T11:16:03+02:00", "2013-03-22T11:16:03.0000000+02:00")]
    [InlineData("date", "2013-03-22T23:30:00-05:00", "2013-03-22T00:00:00.0000000")]
    [InlineData("time(0)", "2013-03-22T11:16:03+02:00", "11:16:03")]
    [InlineData("nvarchar(3)", "abc", "abc")]
    [InlineData("nvarchar(3)", "ab ", "ab ")]
    [InlineData("nvarchar(3)", "abc ", Null)]
    [InlineData("nvarchar(3)", "abcd", Null)]
    [InlineData("uniqueidentifier", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("uniqueidentifier", "x", Null)]
    public async Task A_text_converts_as_the_reference_says_in_every_date_format(string type, string text, string expected)
    {
        var sql = DimensionConversions.Convert("@v", ViewValue.Text, DimensionConversions.Parse(type));

        // The same answer whatever the session reads an ambiguous date as, and whatever its language writes a number in.
        foreach (var session in new[] { "SET DATEFORMAT dmy;", "SET DATEFORMAT mdy;", "SET LANGUAGE French;" })
        {
            Assert.Equal(expected, await ScalarAsync(session, sql, text));
        }
    }

    [Theory]
    [InlineData("nvarchar(40)", "CAST(203.1491234 AS float)", "2.0314912340000001e+002")]
    [InlineData("nvarchar(40)", "CAST(203.1491234 AS decimal(18,7))", "203.1491234")]
    [InlineData("nvarchar(4)", "CAST(12345 AS int)", Null)]
    [InlineData("int", "CAST(7.5 AS float)", Null)]
    [InlineData("int", "CAST(7.0 AS float)", "7")]
    [InlineData("bit", "CAST(2 AS int)", Null)]
    public async Task A_number_converts_without_losing_a_digit_or_a_fraction(string type, string value, string expected)
        => Assert.Equal(expected, await ScalarAsync(string.Empty, DimensionConversions.Convert(value, ViewValue.Number, DimensionConversions.Parse(type)), null));

    [Fact]
    public async Task A_division_by_zero_a_root_of_a_negative_and_a_negative_length_are_null()
    {
        var scope = new ViewScope();
        scope.Add(ViewScope.FromAlias, "T", [new ViewColumnSource(string.Empty, "t", "Text", ViewValue.Text, "nvarchar(256)")]);
        foreach (var expression in new[] { "1 / 0", "10 % 0", "SQRT(-1)", "LOG(0)", "LOG(10, 1)", "POWER(-8, 0.5)", "POWER(0, -1)", "LEFT(N'abc', -1)", "SUBSTRING(N'abc', 1, -1)" })
        {
            var sql = DimensionViewExpressions.Compile(expression, scope).Sql.Replace("[t].[Text]", "N'x'", StringComparison.Ordinal);
            Assert.Equal(Null, await ScalarAsync(string.Empty, sql, null));
        }
    }

    private async Task<string> ScalarAsync(string session, string sql, string? text)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"{session} SELECT {sql};";
        command.Parameters.Add(new SqlParameter("@v", System.Data.SqlDbType.NVarChar, 4000) { Value = (object?)text ?? DBNull.Value });
        var value = await command.ExecuteScalarAsync();
        return value switch
        {
            null or DBNull => Null,
            DateTime date => date.ToString("o", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("o", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };
    }
}
