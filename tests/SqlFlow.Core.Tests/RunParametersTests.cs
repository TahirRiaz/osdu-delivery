using SqlFlow.Core;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The per-run substitution parameters contract (the built-in backfill): the validation matrix at the trust
/// boundary, the default detection that keeps an unparameterized run untouched, and the human description. These
/// run everywhere (pure, no database), so the validation the API and CLI both depend on is always exercised.
/// </summary>
public sealed class RunParametersTests
{
    [Fact]
    public void None_IsDefault_AndDescribesAsNone()
    {
        Assert.True(RunParameters.None.IsDefault);
        Assert.Equal("none", RunParameters.None.Describe());
        RunParameters.None.Validate(); // never throws
    }

    [Theory]
    [InlineData(true, false, false, false)]  // full load only
    [InlineData(false, true, false, false)]  // from only
    [InlineData(false, true, true, false)]   // full window
    [InlineData(false, false, false, true)]  // file pattern only
    public void IsDefault_IsFalse_WhenAnyParameterIsSet(bool full, bool from, bool to, bool pattern)
    {
        var parameters = new RunParameters
        {
            FullLoad = full,
            BackfillFrom = from ? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            BackfillTo = to ? new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            FilePattern = pattern ? "orders*.csv" : null,
        };
        Assert.False(parameters.IsDefault);
    }

    [Fact]
    public void FullLoadWithAWindow_IsRejected()
    {
        var parameters = new RunParameters
        {
            FullLoad = true,
            BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("mutually exclusive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedWindow_IsRejected()
    {
        var parameters = new RunParameters
        {
            BackfillFrom = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            BackfillTo = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void EqualWindowBounds_AreRejected()
    {
        var instant = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var parameters = new RunParameters { BackfillFrom = instant, BackfillTo = instant };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void UpperBoundWithoutLowerBound_IsRejected()
    {
        var parameters = new RunParameters { BackfillTo = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var error = Assert.Throws<SqlFlowException>(parameters.Validate);
        Assert.Contains("requires backfillFrom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenEndedWindow_FromWithNoTo_IsValid()
    {
        var parameters = new RunParameters { BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        parameters.Validate(); // a lower bound with no upper bound is a legitimate "everything since" backfill
        Assert.Contains("from 2023-01-01", parameters.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFilePattern_IsRejected(string pattern)
    {
        Assert.Throws<SqlFlowException>(new RunParameters { FilePattern = pattern }.Validate);
    }

    [Fact]
    public void OversizedFilePattern_IsRejected()
    {
        var parameters = new RunParameters { FilePattern = new string('a', RunParameters.MaxFilePatternLength + 1) };
        Assert.Throws<SqlFlowException>(parameters.Validate);
    }

    [Fact]
    public void ControlCharacterInFilePattern_IsRejected()
    {
        Assert.Throws<SqlFlowException>(new RunParameters { FilePattern = "orders\t.csv" }.Validate);
    }

    [Fact]
    public void MaxLengthFilePattern_IsAccepted()
    {
        new RunParameters { FilePattern = new string('a', RunParameters.MaxFilePatternLength) }.Validate();
    }

    [Fact]
    public void Describe_RendersEveryComponent()
    {
        var parameters = new RunParameters
        {
            BackfillFrom = new DateTime(2023, 1, 1, 6, 0, 0, DateTimeKind.Utc),
            BackfillTo = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            FilePattern = "orders*.csv",
        };
        var description = parameters.Describe();
        Assert.Contains("window 2023-01-01 06:00:00 .. 2023-02-01 00:00:00", description, StringComparison.Ordinal);
        Assert.Contains("files 'orders*.csv'", description, StringComparison.Ordinal);
    }
}
