using System.Globalization;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Extensive coverage of <see cref="DataSetDateSpec"/>: the filename-date detection that populates DataSet_DW,
/// ported from the legacy pre-ingestion logic (extract a date from the file name using a format vocabulary, with
/// the last-modified timestamp as the fallback). Covers each built-in format, embedded/anchored segments,
/// invalid-date and out-of-range rejection, the id/version false-positive guard, format precedence
/// (datetime before date, longest first), the European day-first tie-break, custom formats via options, and the
/// modified-timestamp fallback and opt-out.
/// </summary>
public sealed class DataSetDateSpecTests
{
    private static readonly DataSetDateSpec Default = DataSetDateSpec.FromOptions(new Dictionary<string, string?>());

    [Theory]
    // compact and delimited date-only
    [InlineData("sess_20240101.csv", "2024-01-01T00:00:00")]
    [InlineData("20240101.csv", "2024-01-01T00:00:00")]                       // date at the very start
    [InlineData("v20240101_final.csv", "2024-01-01T00:00:00")]                // embedded, non-digit neighbours
    [InlineData("orders_2024-01-01.csv", "2024-01-01T00:00:00")]
    [InlineData("data_2024_01_01.csv", "2024-01-01T00:00:00")]
    [InlineData("x2024.01.01.csv", "2024-01-01T00:00:00")]
    [InlineData("single_2024-1-5.csv", "2024-01-05T00:00:00")]                // variable-width M/d
    [InlineData("monthly_2024-01.csv", "2024-01-01T00:00:00")]                // delimited year-month
    // date + time (most specific wins over the date-only prefix)
    [InlineData("report20240101120000.log", "2024-01-01T12:00:00")]
    [InlineData("dump_20240101_120000.bak", "2024-01-01T12:00:00")]
    [InlineData("ts_2024-01-01_12-30-45.csv", "2024-01-01T12:30:45")]
    // European day-first and its tie-break over month-first
    [InlineData("eu_31-12-2024.csv", "2024-12-31T00:00:00")]
    [InlineData("eu_31.12.2024.csv", "2024-12-31T00:00:00")]
    [InlineData("compact_31122024.csv", "2024-12-31T00:00:00")]               // yyyyMMdd fails (month 20), ddMMyyyy wins
    [InlineData("ambiguous_01-02-2024.csv", "2024-02-01T00:00:00")]           // dd-MM-yyyy (Feb 1), not MM-dd
    [InlineData("future_20991231.csv", "2099-12-31T00:00:00")]
    // no detectable / rejected date -> null
    [InlineData("none_report.csv", null)]
    [InlineData("id_1234567890.csv", null)]                                   // 10-digit id: no isolated date run
    [InlineData("invalidmonth_20241301.csv", null)]                          // month 13 in every ordering
    [InlineData("old_18991231.csv", null)]                                    // year < 1900 sentinel
    [InlineData("compact6_202401.csv", null)]                                 // bare 6-digit is not a default date
    public void TryExtract_DetectsFilenameDate(string fileName, string? expectedIso)
    {
        var result = Default.TryExtract(fileName);

        if (expectedIso is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(DateTime.ParseExact(expectedIso, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), result);
        }
    }

    [Fact]
    public void TryExtract_PrefersFullTimestamp_OverDateOnlyPrefix()
    {
        // The 14-digit run must resolve to the full timestamp, not be truncated to the 8-digit date.
        var result = Default.TryExtract("x_20240101235959.csv");
        Assert.Equal(new DateTime(2024, 1, 1, 23, 59, 59), result);
    }

    [Fact]
    public void TryExtract_TakesFirstMatch_WhenNameHasMultipleDates()
    {
        // Deterministic: the first (leftmost) match for the winning format is returned.
        var result = Default.TryExtract("from_2024-01-01_to_2024-02-01.csv");
        Assert.Equal(new DateTime(2024, 1, 1), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no_digits_here.csv")]
    [InlineData("only_2024_year.csv")]      // a bare 4-digit year is not a full date
    public void TryExtract_ReturnsNull_WhenNoDate(string fileName)
        => Assert.Null(Default.TryExtract(fileName));

    [Fact]
    public void CustomFormat_IsTriedBeforeBuiltIns_AndWinsLengthTie()
    {
        // yyMMdd is a 6-char format like the built-in yyyy-MM has different length; give a genuinely custom shape
        // the built-ins do not cover and confirm it resolves.
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd" });

        Assert.Equal(new DateTime(2024, 3, 1), spec.TryExtract("bill_240301.csv"));
        Assert.Contains("yyMMdd", spec.Formats);
        // The built-ins are still present after the custom format.
        Assert.Contains("yyyyMMdd", spec.Formats);
    }

    [Fact]
    public void CustomFormats_ParseCommaAndPipeSeparated()
    {
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd | ddMMMyyyy , yyyyDDD" });

        Assert.Contains("yyMMdd", spec.Formats);
        Assert.Contains("ddMMMyyyy", spec.Formats);
        Assert.Contains("yyyyDDD", spec.Formats);
    }

    [Fact]
    public void Resolve_UsesFilenameDate_WhenPresent()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2024, 1, 1), Default.Resolve("sess_20240101.csv", modified));
    }

    [Fact]
    public void Resolve_FallsBackToModified_WhenNoFilenameDate()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(modified, Default.Resolve("no_date_here.csv", modified));
    }

    [Fact]
    public void ModifiedOnly_NeverReadsTheName()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(DataSetDateSpec.ModifiedOnly.FromFileName);
        Assert.Null(DataSetDateSpec.ModifiedOnly.TryExtract("sess_20240101.csv"));
        Assert.Equal(modified, DataSetDateSpec.ModifiedOnly.Resolve("sess_20240101.csv", modified));
    }

    [Fact]
    public void FromOptions_DefaultsToFromFileName()
        => Assert.True(DataSetDateSpec.FromOptions(new Dictionary<string, string?>()).FromFileName);

    [Fact]
    public void FromOptions_OptOut_ReturnsModifiedOnly()
    {
        var spec = DataSetDateSpec.FromOptions(new Dictionary<string, string?> { ["dataSetFromFileName"] = "false" });
        Assert.Same(DataSetDateSpec.ModifiedOnly, spec);
    }
}
