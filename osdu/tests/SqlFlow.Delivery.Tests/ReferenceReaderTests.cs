using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a rendered record refers to (docs/interfaces-design.md section 7): the values of the properties the template
/// declares a relationship for, without their version, and nothing else.
/// </summary>
public class ReferenceReaderTests
{
    /// <summary>A schema with a relationship on a property, on a list of values, and on a property inside a repeated item.</summary>
    private static OsduTemplate Template()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["WellboreID"] = Relationship("master-data", "Wellbore"),
                        ["Name"] = new JsonObject { ["type"] = "string" },
                        ["Datasets"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = Relationship("dataset", null),
                        },
                        ["Curves"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["CurveID"] = new JsonObject { ["type"] = "string" },
                                    ["CurveUnit"] = Relationship("reference-data", "UnitOfMeasure"),
                                },
                            },
                        },
                    },
                },
            },
        };
        return OsduTemplate.From(new SchemaSnapshot("osdu:wks:work-product-component--WellLog:1.4.0", schema, DateTimeOffset.UnixEpoch));
    }

    private static JsonObject Relationship(string group, string? entity)
    {
        var declared = new JsonObject { ["GroupType"] = group };
        if (entity is not null)
        {
            declared["EntityType"] = entity;
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["x-osdu-relationship"] = new JsonArray(declared),
        };
    }

    [Fact]
    public void Every_relationship_the_record_fills_is_read_without_its_version()
    {
        var reader = ReferenceReader.Of(Template());
        var document = new JsonObject
        {
            ["id"] = "dev:work-product-component--WellLog:log-1",
            ["data"] = new JsonObject
            {
                ["WellboreID"] = "dev:master-data--Wellbore:w-1:",
                ["Name"] = "not a reference",
                ["Datasets"] = new JsonArray("dev:dataset--File.Generic:d-1:1614105463059152", "dev:dataset--File.Generic:d-2:"),
                ["Curves"] = new JsonArray(
                    new JsonObject { ["CurveID"] = "GR", ["CurveUnit"] = "dev:reference-data--UnitOfMeasure:m:" },
                    new JsonObject { ["CurveID"] = "DT", ["CurveUnit"] = "dev:reference-data--UnitOfMeasure:m:" }),
            },
        };

        var references = reader.Read(document, "dev:work-product-component--WellLog:log-1");

        Assert.Equal(
            ["dev:master-data--Wellbore:w-1", "dev:dataset--File.Generic:d-1", "dev:dataset--File.Generic:d-2", "dev:reference-data--UnitOfMeasure:m"],
            references.Select(r => r.Id));
        Assert.Equal("data.WellboreID", references[0].Property);
        Assert.Equal("data.Datasets", references[1].Property);
        Assert.Equal("data.Curves[].CurveUnit", references[3].Property);
    }

    [Fact]
    public void A_value_that_is_not_a_record_id_and_the_record_s_own_id_are_not_references()
    {
        var reader = ReferenceReader.Of(Template());
        var ownId = "dev:work-product-component--WellLog:log-1";
        var document = new JsonObject
        {
            ["id"] = ownId,
            ["data"] = new JsonObject
            {
                ["WellboreID"] = "a name, not an id",
                ["Datasets"] = new JsonArray(string.Empty, "dev:work-product-component--WellLog:log-1:", "no:colons--here"),
                ["Curves"] = new JsonArray(new JsonObject { ["CurveUnit"] = null }),
            },
        };

        Assert.Empty(reader.Read(document, ownId));
    }

    [Fact]
    public void A_template_with_no_relationship_reads_nothing()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["data"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["Name"] = new JsonObject { ["type"] = "string" } } } },
        };
        var reader = ReferenceReader.Of(OsduTemplate.From(new SchemaSnapshot("osdu:wks:master-data--Well:1.0.0", schema, DateTimeOffset.UnixEpoch)));

        Assert.Empty(reader.Read(new JsonObject { ["data"] = new JsonObject { ["Name"] = "dev:master-data--Wellbore:w-1:" } }, null));
        Assert.Empty(ReferenceReader.None.Read(new JsonObject(), null));
    }

    [Theory]
    [InlineData("dev:master-data--Wellbore:w-1:", true)]
    [InlineData("dev:master-data--Wellbore:w-1:1614105463059152", true)]
    [InlineData("dev:master-data--Wellbore:w-1", true)]
    [InlineData("dev:master-data--Wellbore:", false)]
    [InlineData("dev:wellbore:w-1", false)]
    [InlineData("dev:master-data--Wellbore w-1:x", false)]
    [InlineData("", false)]
    public void What_reads_as_a_reference_to_an_osdu_record(string value, bool expected)
        => Assert.Equal(expected, TargetId.IsRecordReference(value));
}
