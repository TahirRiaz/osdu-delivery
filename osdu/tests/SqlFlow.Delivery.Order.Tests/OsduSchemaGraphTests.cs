using Xunit;
using static SqlFlow.Delivery.Order.Tests.OrderTestKit;

namespace SqlFlow.Delivery.Order.Tests;

public sealed class OsduSchemaGraphTests
{
    [Fact]
    public void A_graph_refuses_a_text_that_is_no_kind_and_a_schema_said_to_be_of_another_entity_type()
    {
        Assert.Throws<ArgumentException>(() => OsduSchemaGraph.Of([new SchemaNode("Wellbore", "master-data--Wellbore", [])]));
        Assert.Throws<ArgumentException>(() => OsduSchemaGraph.Of([new SchemaNode(Wellbore, "master-data--Well", [])]));
    }

    [Fact]
    public void A_kind_given_twice_must_say_the_same_of_its_relationships()
    {
        var once = Schema(Wellbore, ("osdu.data.WellID", ["master-data--Well"]));
        var graph = OsduSchemaGraph.Of([once, Schema(Wellbore, ("osdu.data.WellID", ["MASTER-DATA--WELL"]))]);
        Assert.Single(graph.Nodes);
        Assert.Throws<ArgumentException>(() => OsduSchemaGraph.Of([once, Schema(Wellbore, ("osdu.data.WellID", ["master-data--Rig"]))]));
    }

    [Fact]
    public void A_property_is_kept_once_and_a_relationship_without_targets_is_dropped()
    {
        var graph = OsduSchemaGraph.Of([Schema(WellLog,
            ("osdu.data.WellboreID", ["master-data--Wellbore", " master-data--Wellbore ", ""]),
            ("osdu.data.WellboreID", ["master-data--Well"]),
            ("osdu.data.Name", []))]);
        var relationship = Assert.Single(graph.Find(WellLog)!.Relationships);
        Assert.Equal("osdu.data.WellboreID", relationship.Property);
        Assert.Equal(["master-data--Wellbore"], relationship.Targets);
        Assert.Throws<ArgumentException>(() => OsduSchemaGraph.Of([Schema(WellLog, (" ", ["master-data--Wellbore"]))]));
    }

    [Fact]
    public void The_relationships_of_a_kind_are_those_its_mapping_fills_or_all_of_them_when_the_mapping_is_not_known()
    {
        var graph = Graph();
        Assert.Equal(4, graph.RelationshipsOf(WellLog, null)!.Count);
        Assert.Equal(["osdu.data.WellboreID"], graph.RelationshipsOf(WellLog, ["osdu.data.WellboreID", "osdu.data.Name"])!.Select(r => r.Property));
        Assert.Empty(graph.RelationshipsOf(WellLog, [])!);
        Assert.Null(graph.RelationshipsOf("osdu:wks:master-data--Rig:1.0.0", null));
        Assert.NotNull(graph.Find(" " + WellLog.ToUpperInvariant() + " "));
    }

    [Fact]
    public void OSDU_s_lineage_by_entity_type_leaves_a_type_s_references_to_itself_out_and_fans_a_group_out_to_its_types()
    {
        var references = Graph().EntityReferences();
        Assert.Equal(["master-data--Well", "work-product-component--WellboreTrajectory"], references["master-data--Wellbore"]);
        Assert.Equal(["dataset--File.Generic", "master-data--Organisation", "master-data--Wellbore", "reference-data--UnitOfMeasure"], references["work-product-component--WellLog"]);
        Assert.Empty(references["reference-data--UnitQuantity"]);
    }
}
