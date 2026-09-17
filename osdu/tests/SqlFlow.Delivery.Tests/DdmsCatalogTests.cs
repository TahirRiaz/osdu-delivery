using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The Wellbore DDMS collections OSDU Delivery knows (<see cref="DdmsCatalog"/>), each checked against the pinned
/// contract (osdu/specs/wellbore-ddms/openapi.json): every collection the contract serves is catalogued, and each row's
/// bulk flag, purge and entity type are the contract's.
/// </summary>
public sealed partial class DdmsCatalogTests
{
    private static ContractOperation? Operation(string method, string template)
        => OsduContracts.WellboreDdms.Operations.SingleOrDefault(o => o.Method == method && o.Template == template);

    private static IEnumerable<JsonObject> Parameters(ContractOperation operation)
        => (operation.Definition["parameters"] as JsonArray ?? []).OfType<JsonObject>();

    [Fact]
    public void Every_collection_the_contract_serves_is_catalogued_once()
    {
        var served = OsduContracts.WellboreDdms.Operations
            .Where(o => o.Method == "POST")
            .Select(o => CollectionPost().Match(o.Template))
            .Where(m => m.Success)
            .Select(m => m.Groups["segment"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(9, served.Count);
        Assert.Equal(served, DdmsCatalog.WellboreDdmsCollections.Select(c => c.Segment).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(9, DdmsCatalog.WellboreDdmsCollections.Select(c => c.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_bulk_collection_has_the_bulk_and_session_routes_and_purges_and_a_record_collection_has_neither()
    {
        foreach (var collection in DdmsCatalog.WellboreDdmsCollections)
        {
            var root = DdmsCatalog.WellboreDdmsV3Prefix + collection.Segment;
            Assert.NotNull(Operation("POST", root));
            Assert.NotNull(Operation("GET", root + "/{record_id}"));
            var delete = Operation("DELETE", root + "/{record_id}");
            Assert.NotNull(delete);

            var purges = Parameters(delete!).Any(p => (string?)p["name"] == "purge" && (string?)p["in"] == "query");
            Assert.True(purges == collection.Bulk, $"{collection.Segment}: DELETE {(purges ? "takes" : "does not take")} purge, and the catalog says bulk is {collection.Bulk}.");

            foreach (var (method, suffix) in new[]
            {
                ("POST", "/{record_id}/data"),
                ("GET", "/{record_id}/data"),
                ("POST", "/{record_id}/sessions"),
                ("POST", "/{record_id}/sessions/{session_id}/data"),
                ("PATCH", "/{record_id}/sessions/{session_id}"),
                ("GET", "/{record_id}/sessions/{session_id}"),
            })
            {
                var present = Operation(method, root + suffix) is not null;
                Assert.True(present == collection.Bulk, $"{collection.Segment}: {method} {suffix} is {(present ? "served" : "not served")}, and the catalog says bulk is {collection.Bulk}.");
            }

            Assert.True(collection.Bulk == (collection.Columns != DdmsBulkColumns.Unchecked), $"{collection.Segment}: only a bulk collection checks columns, and every one does.");
        }
    }

    [Fact]
    public void The_record_id_pattern_of_each_collection_takes_its_entity_type_and_no_other()
    {
        var patterned = 0;
        foreach (var collection in DdmsCatalog.WellboreDdmsCollections)
        {
            var get = Operation("GET", DdmsCatalog.WellboreDdmsV3Prefix + collection.Segment + "/{record_id}")!;
            var pattern = Parameters(get)
                .Where(p => (string?)p["name"] == "record_id")
                .Select(p => (string?)p["schema"]?["pattern"])
                .SingleOrDefault();
            if (pattern is null)
            {
                // ppfgdataset and wellpressuretestrawmeasurement state no pattern; the service enforces the same family in
                // code (app/model/osdu_record_id.py), which osdu/specs/wellbore-ddms/INTEGRATION.md section 2 cites.
                Assert.Contains(collection.Segment, new[] { "ppfgdataset", "wellpressuretestrawmeasurement" });
                continue;
            }

            patterned++;
            var regex = new Regex(pattern, RegexOptions.CultureInvariant);
            Assert.Matches(regex, $"dev:{collection.EntityType}:abc");
            foreach (var other in DdmsCatalog.WellboreDdmsCollections.Where(o => o != collection))
            {
                Assert.DoesNotMatch(regex, $"dev:{other.EntityType}:abc");
            }
        }

        Assert.Equal(7, patterned);
    }

    [Fact]
    public void The_column_rules_are_the_services_consistency_checks()
    {
        // app/consistency: WellLog and WellPressureTestRawMeasurement compare NumberOfColumns, PPFGDataset does not, and a
        // trajectory's columns are its station properties (osdu/specs/wellbore-ddms/INTEGRATION.md section 4.3).
        var columns = DdmsCatalog.WellboreDdmsCollections.Where(c => c.Bulk).ToDictionary(c => c.Segment, c => c.Columns, StringComparer.Ordinal);
        Assert.Equal(DdmsBulkColumns.CurveIdsAndWidths, columns["welllogs"]);
        Assert.Equal(DdmsBulkColumns.TrajectoryStations, columns["wellboretrajectories"]);
        Assert.Equal(DdmsBulkColumns.CurveIds, columns["ppfgdataset"]);
        Assert.Equal(DdmsBulkColumns.CurveIdsAndWidths, columns["wellpressuretestrawmeasurement"]);
    }

    [Theory]
    [InlineData("wellbore", true)]
    [InlineData("Wellbore_DDMS-2", true)]
    [InlineData("2wellbore", false)]
    [InlineData("well bore", false)]
    [InlineData("wellbore\n", false)]
    [InlineData("", false)]
    public void A_ddms_name_is_a_letter_then_letters_digits_underscores_and_hyphens(string name, bool valid)
        => Assert.Equal(valid, DdmsCatalog.IsName(name));

    [Theory]
    [InlineData("work-product-component--WellLog", true)]
    [InlineData("master-data--Wellbore", true)]
    [InlineData("WellLog", false)]
    [InlineData("osdu:wks:master-data--Wellbore:1.0.0", false)]
    [InlineData("master-data--Wellbore\n", false)]
    public void An_entity_type_names_its_group(string entityType, bool valid)
        => Assert.Equal(valid, DdmsCatalog.IsEntityType(entityType));

    [Theory]
    [InlineData("wellbore", true)]
    [InlineData("wellbore-ddms-2", true)]
    [InlineData("w", false)]
    [InlineData("wellbore_ddms", false)]
    [InlineData("wellbore\n", false)]
    public void A_registration_id_is_what_the_register_service_keeps(string id, bool valid)
        => Assert.Equal(valid, DdmsCatalog.IsRegistration(id));

    [Fact]
    public void A_collection_a_registration_names_by_its_type_alone_serves_that_type_in_any_group()
    {
        var registered = new DdmsService("registered", "/ddms", DdmsShape.WellboreDdmsV3, [new DdmsCollectionEntry("wellbore", "wellbores", Bulk: false)]);
        Assert.NotNull(registered.CollectionFor("master-data--Wellbore"));
        Assert.Null(registered.CollectionFor("master-data--Well"));

        // A declared collection names its group, and serves that group only.
        var declared = new DdmsService("declared", "/ddms", DdmsShape.WellboreDdmsV3, [new DdmsCollectionEntry("master-data--Wellbore", "wellbores", Bulk: false)]);
        Assert.NotNull(declared.CollectionFor("MASTER-DATA--WELLBORE"));
        Assert.Null(declared.CollectionFor("reference-data--Wellbore"));
        Assert.Equal("wellboretrajectories", DdmsCatalog.KnownCollection(DdmsShape.WellboreDdmsV3, "wellboretrajectories")?.Segment);
        Assert.Null(DdmsCatalog.KnownCollection(DdmsShape.WellboreDdmsV3, "wellboretrajectory"));
    }

    [GeneratedRegex(@"^/ddms/v3/(?<segment>[^/{}]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPost();
}
