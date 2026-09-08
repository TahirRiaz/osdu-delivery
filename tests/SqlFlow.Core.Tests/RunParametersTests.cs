using System;
using System.Collections.Generic;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The per-run parameters contract: the validation matrix at the trust boundary, the default detection that keeps
/// an unparameterized run untouched, the round trip through the stored JSON form, and the human description. These
/// run everywhere (pure, no database), so the validation the API, the CLI and the node all depend on is always
/// exercised.
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
    [InlineData("deliver")]
    [InlineData("verify")]
    [InlineData("plan")]
    [InlineData("known-state")]
    [InlineData("Verify")]
    public void Operation_AcceptsEveryKnownOperation_CaseInsensitively(string operation)
        => new RunParameters { Operation = operation }.Validate();

    [Theory]
    [InlineData("backfill")]
    [InlineData("")]
    [InlineData("deliver now")]
    public void Operation_RejectsAnythingElse(string operation)
        => Assert.Throws<SqlFlowException>(() => new RunParameters { Operation = operation }.Validate());

    [Fact]
    public void IsDefault_IsFalse_WhenAnyParameterIsSet()
    {
        Assert.False(new RunParameters { Operation = RunParameters.VerifyOperation }.IsDefault);
        Assert.False(new RunParameters { Force = true }.IsDefault);
        Assert.False(new RunParameters { Values = new Dictionary<string, string> { ["logSource"] = "north" } }.IsDefault);
        Assert.False(new RunParameters { Drop = "abfss://drops@lake/recall/2026-09-01" }.IsDefault);
        Assert.False(new RunParameters { SubmissionId = Guid.NewGuid() }.IsDefault);
        Assert.False(new RunParameters { RecordKeys = [Guid.NewGuid()] }.IsDefault);
        Assert.False(new RunParameters { PublishTo = "/known-state" }.IsDefault);
    }

    [Theory]
    [InlineData("logSource")]
    [InlineData("_private")]
    [InlineData("a1")]
    public void Values_AcceptIdentifierNames(string name)
        => new RunParameters { Values = new Dictionary<string, string> { [name] = "x" } }.Validate();

    [Theory]
    [InlineData("log source")]
    [InlineData("1st")]
    [InlineData("a-b")]
    [InlineData("")]
    public void Values_RejectNonIdentifierNames(string name)
        => Assert.Throws<SqlFlowException>(() => new RunParameters { Values = new Dictionary<string, string> { [name] = "x" } }.Validate());

    [Fact]
    public void Values_RejectControlCharacters_AndOverlongValues()
    {
        Assert.Throws<SqlFlowException>(() => new RunParameters { Values = new Dictionary<string, string> { ["a"] = "line\nbreak" } }.Validate());
        Assert.Throws<SqlFlowException>(() => new RunParameters { Values = new Dictionary<string, string> { ["a"] = new string('x', RunParameters.MaxValueLength + 1) } }.Validate());
    }

    [Fact]
    public void Drop_RejectsBlank_AndOverlong()
    {
        Assert.Throws<SqlFlowException>(() => new RunParameters { Drop = "   " }.Validate());
        Assert.Throws<SqlFlowException>(() => new RunParameters { Drop = new string('d', RunParameters.MaxDropLength + 1) }.Validate());
    }

    [Fact]
    public void SubmissionId_RejectsTheEmptyGuid()
        => Assert.Throws<SqlFlowException>(() => new RunParameters { SubmissionId = Guid.Empty }.Validate());

    [Fact]
    public void RecordKeys_RejectEmptyKeys_AndTooMany()
    {
        Assert.Throws<SqlFlowException>(() => new RunParameters { RecordKeys = [Guid.Empty] }.Validate());
        var keys = new List<Guid>();
        for (var i = 0; i <= RunParameters.MaxRecordKeys; i++)
        {
            keys.Add(Guid.NewGuid());
        }

        Assert.Throws<SqlFlowException>(() => new RunParameters { RecordKeys = keys }.Validate());
    }

    [Fact]
    public void KnownState_TakesNoSubmissionOrRecordScope()
    {
        new RunParameters { Operation = RunParameters.KnownStateOperation, PublishTo = "/known-state" }.Validate();
        Assert.Throws<SqlFlowException>(() => new RunParameters { Operation = RunParameters.KnownStateOperation, SubmissionId = Guid.NewGuid() }.Validate());
        Assert.Throws<SqlFlowException>(() => new RunParameters { Operation = RunParameters.KnownStateOperation, RecordKeys = [Guid.NewGuid()] }.Validate());
    }

    [Fact]
    public void WritesTarget_IsTrueOnlyForDeliver()
    {
        Assert.True(new RunParameters().WritesTarget);
        Assert.False(new RunParameters { Operation = RunParameters.VerifyOperation }.WritesTarget);
        Assert.False(new RunParameters { Operation = RunParameters.PlanOperation }.WritesTarget);
        Assert.False(new RunParameters { Operation = RunParameters.KnownStateOperation }.WritesTarget);
    }

    [Fact]
    public void Json_RoundTrips_EveryField()
    {
        var submission = Guid.NewGuid();
        var key = Guid.NewGuid();
        var original = new RunParameters
        {
            Operation = RunParameters.VerifyOperation,
            Force = true,
            Values = new Dictionary<string, string> { ["logSource"] = "north", ["region"] = "NO" },
            Drop = "abfss://drops@lake/recall/2026-09-01",
            SubmissionId = submission,
            RecordKeys = [key],
            PublishTo = "/known-state",
        };

        var restored = RunParameters.FromJson(original.ToJson());

        Assert.Equal("verify", restored.Operation);
        Assert.True(restored.Force);
        Assert.Equal("north", restored.Values["logSource"]);
        Assert.Equal("NO", restored.Values["region"]);
        Assert.Equal(original.Drop, restored.Drop);
        Assert.Equal(submission, restored.SubmissionId);
        Assert.Equal([key], restored.RecordKeys);
        Assert.Equal("/known-state", restored.PublishTo);
        restored.Validate();
    }

    [Fact]
    public void FromJson_TreatsBlankAsDefault_AndRejectsGarbage()
    {
        Assert.True(RunParameters.FromJson(null).IsDefault);
        Assert.True(RunParameters.FromJson("  ").IsDefault);
        Assert.Throws<SqlFlowException>(() => RunParameters.FromJson("{not json"));
    }

    [Fact]
    public void Describe_NamesWhatWasAsked()
    {
        var description = new RunParameters
        {
            Operation = RunParameters.DeliverOperation,
            Force = true,
            Values = new Dictionary<string, string> { ["logSource"] = "north" },
            RecordKeys = [Guid.NewGuid(), Guid.NewGuid()],
        }.Describe();

        Assert.Contains("operation=deliver", description, StringComparison.Ordinal);
        Assert.Contains("force", description, StringComparison.Ordinal);
        Assert.Contains("logSource=north", description, StringComparison.Ordinal);
        Assert.Contains("records=2", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseValues_ReadsNameEqualsValue_AndRejectsTheRest()
    {
        var values = RunParameters.ParseValues(["logSource=north", "note=a=b"]);
        Assert.Equal("north", values["logSource"]);
        Assert.Equal("a=b", values["note"]);
        Assert.Throws<SqlFlowException>(() => RunParameters.ParseValues(["novalue"]));
        Assert.Throws<SqlFlowException>(() => RunParameters.ParseValues(["=x"]));
    }
}
