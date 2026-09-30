using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a dimension cleans its originals into members: each step in order, a map matched by the cache's own rules, and a
/// clean value trimmed at the end, never empty and never longer than a member's may be.
/// </summary>
public class DimensionCleanerTests
{
    private static readonly DictionaryDefinition Operators = new()
    {
        Name = "Operators",
        Key = "key",
        Fields = [DictionaryDefinition.ValueField],
        IsPairs = true,
        Entries =
        [
            new("Statoil", new Dictionary<string, string?> { ["value"] = "Equinor ASA" }),
            new("EQUINOR", new Dictionary<string, string?> { ["value"] = "Equinor ASA" }),
            new("Retired", new Dictionary<string, string?> { ["value"] = null }),
            new("ft", new Dictionary<string, string?> { ["value"] = "foot" }),
            new("fT", new Dictionary<string, string?> { ["value"] = "femtotesla" }),
        ],
    };

    private static readonly DictionaryDefinition Curves = new()
    {
        Name = "Curves",
        Key = "mnemonic",
        Fields = ["family", "unit"],
        Entries = [new("GR", new Dictionary<string, string?> { ["family"] = "Gamma Ray", ["unit"] = "gAPI" })],
    };

    private static DimensionCleaner Cleaner(params CleanStep[] steps)
        => DimensionCleaner.Build(steps, name => name switch
        {
            "Operators" => Operators,
            "Curves" => Curves,
            _ => throw new DeliveryException($"Dictionary '{name}' was not found."),
        });

    private static CleanStep Step(CleanStepKind kind) => new() { Kind = kind };

    [Fact]
    public void Without_steps_the_original_is_its_own_clean_value_trimmed()
    {
        Assert.Equal(CleanResult.Member("GR"), DimensionCleaner.Identity.Clean("  GR "));
        Assert.Equal(CleanOutcome.Empty, DimensionCleaner.Identity.Clean("   ").Outcome);
    }

    [Theory]
    [InlineData(CleanStepKind.Trim, "  a  b ", "a  b")]
    [InlineData(CleanStepKind.CollapseSpaces, "a \t\n b", "a b")]
    [InlineData(CleanStepKind.Upper, "gamma ray", "GAMMA RAY")]
    [InlineData(CleanStepKind.Lower, "GAMMA Ray", "gamma ray")]
    [InlineData(CleanStepKind.FoldSeparators, "NO 15/9-19 SR", "no-15-9-19-sr")]
    [InlineData(CleanStepKind.Nfkc, "ＧＲ", "GR")]
    [InlineData(CleanStepKind.Nfc, "é", "é")]
    public void Each_plain_step_does_what_its_name_says(CleanStepKind kind, string original, string clean)
        => Assert.Equal(clean, Cleaner(Step(kind)).Clean(original).Value);

    [Fact]
    public void Case_is_changed_by_the_invariant_culture_whatever_the_machine()
        => Assert.Equal("TITLE", Cleaner(Step(CleanStepKind.Upper)).Clean("title").Value);

    [Fact]
    public void Steps_run_in_order()
    {
        var cleaner = Cleaner(Step(CleanStepKind.Upper), new CleanStep { Kind = CleanStepKind.Replace, Pattern = "^GR$", With = "GAMMA" });
        Assert.Equal("GAMMA", cleaner.Clean("gr").Value);
    }

    [Fact]
    public void A_replace_can_name_the_groups_it_matched()
    {
        var cleaner = Cleaner(new CleanStep { Kind = CleanStepKind.Replace, Pattern = @"^(\w+)_(\d+)$", With = "$2-$1" });
        Assert.Equal("15-NO", cleaner.Clean("NO_15").Value);
    }

    [Theory]
    [InlineData(@"(a)\1")]
    [InlineData("(?=a)b")]
    [InlineData("(?<!a)b")]
    [InlineData("(?>a)")]
    public void A_pattern_that_needs_backtracking_is_refused(string pattern)
    {
        var refused = Assert.Throws<ArgumentException>(() => DimensionCleaner.CompilePattern(pattern));
        Assert.Contains("non-backtracking", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pattern_that_is_no_regular_expression_is_refused()
        => Assert.ThrowsAny<ArgumentException>(() => DimensionCleaner.CompilePattern("(unclosed"));

    [Fact]
    public void A_pattern_that_backtracks_badly_runs_in_linear_time()
    {
        // (a+)+$ against a long run of a with a trailing b takes exponential time on a backtracking engine.
        var cleaner = Cleaner(new CleanStep { Kind = CleanStepKind.Replace, Pattern = "(a+)+$", With = "x" });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = cleaner.Clean(new string('a', 5_000) + "b");
        watch.Stop();

        Assert.Equal(CleanOutcome.TooLong, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Cleaning_that_leaves_nothing_leaves_the_original_out_of_every_member()
    {
        var result = Cleaner(new CleanStep { Kind = CleanStepKind.Replace, Pattern = "^-+$", With = "" }).Clean("---");
        Assert.Equal(CleanOutcome.Empty, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public void A_clean_value_longer_than_a_member_may_hold_is_left_out_with_its_length()
    {
        var result = DimensionCleaner.Identity.Clean(new string('x', DimensionSpec.MaxCleanLength + 1));
        Assert.Equal(CleanOutcome.TooLong, result.Outcome);
        Assert.Contains("257", result.Note, StringComparison.Ordinal);
        Assert.Equal(CleanOutcome.Member, DimensionCleaner.Identity.Clean(new string('x', DimensionSpec.MaxCleanLength)).Outcome);
    }

    [Fact]
    public void Text_with_no_normal_form_goes_on_as_it_is_and_says_so()
    {
        var result = Cleaner(Step(CleanStepKind.Nfc)).Clean("a\uD800b");
        Assert.Equal(CleanOutcome.Member, result.Outcome);
        Assert.Equal("a\uD800b", result.Value);
        Assert.Contains("no normal form", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_replaces_a_listed_value_with_the_dictionarys_and_matches_case_when_that_finds_one_entry()
    {
        var cleaner = Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators" });
        Assert.Equal("Equinor ASA", cleaner.Clean("Statoil").Value);
        Assert.Equal("Equinor ASA", cleaner.Clean("statoil").Value);
        Assert.Equal("Equinor ASA", cleaner.Clean("Equinor").Value);
    }

    [Fact]
    public void A_map_that_finds_several_entries_once_case_is_ignored_picks_none()
    {
        var result = Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators" }).Clean("FT");

        // ft is the foot and fT the femtotesla: FT could be either, so it is not mapped, and the note says why.
        Assert.Equal(CleanResult.Member("FT", result.Note), result);
        Assert.Contains("picking one would be a guess", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlisted_value_is_kept_dropped_or_replaced_as_the_map_says()
    {
        Assert.Equal(CleanResult.Member("Aker BP"), Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators" }).Clean("Aker BP"));

        var dropped = Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators", Otherwise = MapOtherwise.LeaveOut }).Clean("Aker BP");
        Assert.Equal(CleanOutcome.Dropped, dropped.Outcome);
        Assert.Contains("does not list it", dropped.Note, StringComparison.Ordinal);

        var other = Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators", Otherwise = MapOtherwise.Text, OtherwiseText = "Other" }).Clean("Aker BP");
        Assert.Equal("Other", other.Value);
    }

    [Fact]
    public void An_entry_that_gives_no_value_leaves_the_original_out()
    {
        var result = Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators" }).Clean("Retired");
        Assert.Equal(CleanOutcome.Dropped, result.Outcome);
        Assert.Contains("maps it to no value", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_reads_the_field_it_names_and_needs_one_where_an_entry_holds_several()
    {
        Assert.Equal("Gamma Ray", Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Curves", Field = "family" }).Clean("GR").Value);

        var unnamed = Assert.Throws<DeliveryException>(() => Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Curves" }));
        Assert.Contains("family, unit", unnamed.Message, StringComparison.Ordinal);

        var unknown = Assert.Throws<DeliveryException>(() => Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Curves", Field = "colour" }));
        Assert.Contains("'colour'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_map_through_a_dictionary_that_cannot_be_read_fails_the_build_of_its_cleaner()
        => Assert.Throws<DeliveryException>(() => Cleaner(new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Missing" }));

    [Fact]
    public void A_step_that_leaves_the_original_out_stops_the_steps_after_it()
    {
        var cleaner = Cleaner(
            new CleanStep { Kind = CleanStepKind.Map, Dictionary = "Operators", Otherwise = MapOtherwise.LeaveOut },
            new CleanStep { Kind = CleanStepKind.Replace, Pattern = ".*", With = "anything" });
        Assert.Equal(CleanOutcome.Dropped, cleaner.Clean("Unlisted").Outcome);
    }
}
