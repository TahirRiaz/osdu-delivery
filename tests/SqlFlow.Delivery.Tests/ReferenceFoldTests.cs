using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Matching a name a source spells its own way against the one OSDU holds: what folds together, what stays apart, that
/// the looser tier never decides a value an exact or case-insensitive comparison could have, and that a folded key
/// several records answer to resolves to none of them.
/// </summary>
public class ReferenceFoldTests
{
    [Theory]
    [InlineData("NO 15/9-19 SR", "no-15-9-19-sr")]
    [InlineData("NO_15_9-19_SR", "no-15-9-19-sr")]
    [InlineData("no-15-9-19-sr", "no-15-9-19-sr")]
    [InlineData("  NO   15 / 9 - 19   SR  ", "no-15-9-19-sr")]
    [InlineData("NO.15.9.19.SR", "no-15-9-19-sr")]
    public void The_same_name_spelled_different_ways_folds_to_one_key(string value, string key)
        => Assert.Equal(key, ReferenceKeyFold.Separators(value));

    [Theory]
    [InlineData("15/9-19", "15-9-19")]
    [InlineData("15/9-20", "15-9-20")]
    [InlineData("SLEIPNER ØST", "sleipner-øst")]
    [InlineData("Sleipner Øst", "sleipner-øst")]
    public void Letters_and_digits_decide_the_key_and_non_ascii_letters_survive(string value, string key)
        => Assert.Equal(key, ReferenceKeyFold.Separators(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("-_ .")]
    public void A_value_with_no_letter_or_digit_folds_to_nothing(string? value)
        => Assert.Equal(string.Empty, ReferenceKeyFold.Separators(value));

    [Fact]
    public void Two_genuinely_different_names_never_fold_together()
    {
        Assert.NotEqual(ReferenceKeyFold.Separators("NO 15/9-19"), ReferenceKeyFold.Separators("NO 15/9-20"));
        Assert.NotEqual(ReferenceKeyFold.Separators("GR"), ReferenceKeyFold.Separators("GRD"));
    }

    [Fact]
    public void A_folded_match_is_only_reached_when_the_mapping_asked_for_it()
    {
        var type = Wellbores(("w1", "NO_15_9-19_SR"));

        // Off by default, which is what keeps every mapping that never heard of the fold matching as it always did.
        Assert.Null(type.Match("FacilityName", "NO 15/9-19 SR"));
        Assert.Equal(ReferenceMatchKind.None, type.Find("FacilityName", "NO 15/9-19 SR").Kind);

        var found = type.Find("FacilityName", "NO 15/9-19 SR", ignoreSeparators: true);
        Assert.Equal("w1", found.Item?.Id);
        Assert.Equal(ReferenceMatchKind.IgnoringSeparators, found.Kind);
    }

    [Fact]
    public void An_exact_match_is_never_decided_by_a_looser_tier()
    {
        // 'NO 15/9-19' is held exactly by one record and folds to the same key as another. The exact holder wins, so
        // turning the fold on cannot move a value that already resolved.
        var type = Wellbores(("exact", "NO 15/9-19"), ("folds", "NO_15_9_19"));
        Assert.Equal("exact", type.Find("FacilityName", "NO 15/9-19", ignoreSeparators: true).Item?.Id);
        Assert.Equal(ReferenceMatchKind.Exact, type.Find("FacilityName", "NO 15/9-19", ignoreSeparators: true).Kind);

        // And a case-insensitive hit still beats the fold, for the same reason.
        var cased = Wellbores(("cased", "no 15/9-19"), ("folds", "NO_15_9_19"));
        var match = cased.Find("FacilityName", "NO 15/9-19", ignoreSeparators: true);
        Assert.Equal("cased", match.Item?.Id);
        Assert.Equal(ReferenceMatchKind.IgnoringCase, match.Kind);
    }

    [Fact]
    public void A_folded_key_several_records_answer_to_resolves_to_none_of_them()
    {
        var type = Wellbores(("w1", "NO_15_9-19"), ("w2", "NO 15/9 19"));
        var found = type.Find("FacilityName", "no-15-9-19", ignoreSeparators: true);
        Assert.Null(found.Item);
        Assert.True(found.IsCaseAmbiguous);
        Assert.Equal(["w1", "w2"], found.CaseVariants.Select(v => v.Id));
        Assert.Equal("case, punctuation and spacing are ignored", found.Loosening);
    }

    [Fact]
    public void A_field_holding_a_set_of_aliases_folds_every_one_of_them()
    {
        var item = new ReferenceItem("w1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alias"] = ReferenceValue.From(new System.Text.Json.Nodes.JsonArray("NO 15/9-19", "15/9-19 SR")),
        });
        var type = new ReferenceType("Wellbore", "master-data--Wellbore", [item]);

        Assert.Equal("w1", type.Find("Alias", "no_15_9_19", ignoreSeparators: true).Item?.Id);
        Assert.Equal("w1", type.Find("Alias", "15-9-19-sr", ignoreSeparators: true).Item?.Id);
        Assert.Null(type.Find("Alias", "no_15_9_20", ignoreSeparators: true).Item);
    }

    [Fact]
    public void A_value_that_is_only_punctuation_matches_nothing_however_loose_the_comparison()
    {
        var type = Wellbores(("w1", "---"), ("w2", "///"));
        Assert.Null(type.Find("FacilityName", "___", ignoreSeparators: true).Item);
        Assert.Empty(type.Find("FacilityName", "___", ignoreSeparators: true).CaseVariants);
    }

    [Fact]
    public void The_fold_belongs_on_a_transform_that_resolves_a_reference()
    {
        var mapping = Path.Combine(Samples.NewTempDirectory(), "Folded.mapping.yaml");
        File.WriteAllText(mapping, """
            documentType: mapping
            name: Folded
            version: 1.0.0
            kind: "osdu:wks:master-data--Wellbore:1.0.0"
            source:
              system: test
            identity:
              naturalKey: [data.FacilityName]
            envelope:
              legalTags: [opendes-reference-data-default]
              otherRelevantDataCountries: [NO]
              acl:
                owners: [data.default.owners@opendes.dataservices.energy]
                viewers: [data.default.viewers@opendes.dataservices.energy]
            properties:
              - target: data.FacilityName
                source: facility_name
                transform: upper
                config:
                  ignoreSeparators: true
            """);

        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().LoadMapping(mapping));
        Assert.Contains("ignoreSeparators", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reference or lookup", ex.Message, StringComparison.Ordinal);
    }

    private static ReferenceType Wellbores(params (string Id, string FacilityName)[] items)
        => new(
            "Wellbore",
            "master-data--Wellbore",
            items.Select(i => ReferenceItem.FromText(i.Id, new Dictionary<string, string> { ["FacilityName"] = i.FacilityName })));
}
