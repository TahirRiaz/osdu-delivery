using Xunit;

namespace SqlFlow.Delivery.Order.Tests;

public sealed class OsduEntityTests
{
    [Theory]
    [InlineData("osdu:wks:master-data--Wellbore:1.3.0", "master-data--Wellbore")]
    [InlineData(" acme:custom:master-data--Wellbore:2.0.0 ", "master-data--Wellbore")]
    [InlineData("osdu:wks:dataset--File.Generic:1.0.0", "dataset--File.Generic")]
    [InlineData("osdu:wks:reference-data--UnitOfMeasure:*", "reference-data--UnitOfMeasure")]
    public void A_kind_names_its_entity_type_whatever_its_authority_source_or_version(string kind, string entity)
        => Assert.Equal(entity, OsduEntity.OfKind(kind));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("osdu:wks:master-data--Wellbore")]
    [InlineData("osdu:wks:Wellbore:1.0.0")]
    [InlineData("osdu:wks:--Wellbore:1.0.0")]
    [InlineData("osdu:wks:master-data--:1.0.0")]
    [InlineData("osdu:wks:master data--Wellbore:1.0.0")]
    [InlineData("a:b:c--d:e:f")]
    public void Text_that_is_no_kind_with_an_entity_type_names_none(string? kind) => Assert.Null(OsduEntity.OfKind(kind));

    [Theory]
    [InlineData("dev:master-data--Wellbore:abc", "master-data--Wellbore")]
    [InlineData("dev:master-data--Wellbore:abc:1700000000000", "master-data--Wellbore")]
    [InlineData("dev:reference-data--LogCurveType:WellDB:GR:", "reference-data--LogCurveType")]
    [InlineData("dev:reference-data--UnitOfMeasure:g%2Fcm3", "reference-data--UnitOfMeasure")]
    public void An_id_names_the_entity_type_of_its_record(string id, string entity) => Assert.Equal(entity, OsduEntity.OfId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("dev:master-data--Wellbore")]
    [InlineData("dev:master-data--Wellbore:")]
    [InlineData(":master-data--Wellbore:abc")]
    [InlineData("dev::abc")]
    [InlineData("dev:Wellbore:abc")]
    public void Text_that_is_no_id_names_no_entity_type(string? id) => Assert.Null(OsduEntity.OfId(id));

    [Fact]
    public void Groups_rank_in_the_order_their_records_are_delivered_and_any_other_group_last()
    {
        Assert.Equal(0, OsduEntity.RankOf("reference-data--UnitOfMeasure"));
        Assert.Equal(1, OsduEntity.RankOf("master-data--Wellbore"));
        Assert.Equal(2, OsduEntity.RankOf("dataset--File.Generic"));
        Assert.Equal(3, OsduEntity.RankOf("work-product-component--WellLog"));
        Assert.Equal(4, OsduEntity.RankOf("work-product--WorkProduct"));
        Assert.Equal(5, OsduEntity.RankOf("custom-group--Thing"));
        Assert.Equal(2, OsduEntity.RankOf("dataset"));
    }

    [Theory]
    [InlineData("master-data--Wellbore", "master-data--Wellbore", true)]
    [InlineData("MASTER-DATA--wellbore", "master-data--Wellbore", true)]
    [InlineData("master-data--Wellbore", "master-data--Well", false)]
    [InlineData("dataset", "dataset--File.Generic", true)]
    [InlineData("dataset", "master-data--Wellbore", false)]
    [InlineData("Wellbore", "master-data--Wellbore", true)]
    [InlineData("Well", "master-data--Wellbore", false)]
    public void A_relationship_target_names_an_entity_type_by_its_whole_name_its_group_or_its_entity(string target, string entity, bool names)
        => Assert.Equal(names, OsduEntity.Names(target, entity));
}
