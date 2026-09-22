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

    private static ContractOperation? RafsOperation(string method, string template)
        => OsduContracts.RafsDdms.Operations.SingleOrDefault(o => o.Method == method && o.Template == FakeOsduPlatform.RafsRoot + template);

    [Fact]
    public void Every_rafs_collection_the_contract_serves_is_catalogued_with_the_entity_types_its_record_id_takes()
    {
        var served = OsduContracts.RafsDdms.Operations
            .Where(o => o.Method == "POST")
            .Select(o => RafsCollectionPost().Match(o.Template))
            .Where(m => m.Success)
            .Select(m => m.Groups["segment"].Value)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(7, served.Count);
        Assert.Equal(served, DdmsCatalog.RafsCollections.Select(c => c.Segment).Distinct().Order(StringComparer.Ordinal).ToList());
        Assert.Equal(12, DdmsCatalog.RafsCollections.Select(c => c.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var segment in served)
        {
            var get = RafsOperation("GET", $"/v2/{segment}/{{record_id}}")!;
            Assert.NotNull(RafsOperation("DELETE", $"/v2/{segment}/{{record_id}}"));
            var pattern = new Regex(Parameters(get).Single(p => (string?)p["name"] == "record_id")["schema"]!["pattern"]!.GetValue<string>(), RegexOptions.CultureInvariant);
            var mine = DdmsCatalog.RafsCollections.Where(c => c.Segment == segment).ToList();
            Assert.All(mine, c => Assert.Matches(pattern, $"dev:{c.EntityType}:abc"));
            Assert.All(DdmsCatalog.RafsCollections.Where(c => c.Segment != segment), c => Assert.DoesNotMatch(pattern, $"dev:{c.EntityType}:abc"));
        }
    }

    [Fact]
    public void A_rafs_content_collection_has_the_content_write_its_shape_sends_and_a_record_collection_has_none()
    {
        foreach (var collection in DdmsCatalog.RafsCollections)
        {
            var single = RafsOperation("POST", $"/v2/{collection.Segment}/{{record_id}}/data");
            var typed = OsduContracts.RafsDdms.Operations.SingleOrDefault(o =>
                o.Method == "POST" && RafsTypedContent().Match(o.Template) is { Success: true } m && m.Groups["segment"].Value == collection.Segment);
            Assert.True(
                (single is not null) == (collection.Bulk && !collection.TypedContent),
                $"{collection.Segment}: the one-type content write is {(single is null ? "not " : string.Empty)}served, and the catalog says bulk {collection.Bulk}, typed {collection.TypedContent}.");
            Assert.True(
                (typed is not null) == collection.TypedContent,
                $"{collection.Segment}: the typed content write is {(typed is null ? "not " : string.Empty)}served, and the catalog says typed {collection.TypedContent}.");
            if (collection.Bulk)
            {
                var write = (single ?? typed)!;
                Assert.Contains(Parameters(write), p => (string?)p["name"] == "content_schema_version" && (string?)p["in"] == "query" && p["required"]!.GetValue<bool>());
                if (!collection.TypedContent)
                {
                    Assert.NotNull(RafsOperation("GET", $"/v2/{collection.Segment}/data/schema"));
                }
            }
        }

        // The type catalogues the shape reads for the two collections that hold several types.
        Assert.NotNull(RafsOperation("GET", "/v2/samplesanalysis/analysistypes"));
        Assert.NotNull(RafsOperation("GET", "/v2/fluidmodel/fluidmodeltypes"));
        Assert.NotNull(RafsOperation("GET", "/info"));
    }

    [Fact]
    public void Every_well_delivery_type_is_served_under_its_own_lowercased_name_including_every_type_the_contract_queries()
    {
        var types = DdmsCatalog.WellDeliveryCollections;
        Assert.All(types, c =>
        {
            Assert.False(c.Bulk);
            Assert.Equal(DdmsCatalog.WellDeliveryType(c.EntityType), c.Segment);
            Assert.Matches("^[0-9a-z-]+$", c.Segment);
            Assert.True(DdmsCatalog.IsEntityType(c.EntityType));
        });
        Assert.Equal(types.Count, types.Select(c => c.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The contract's query type lists; it spells one type wellboreArchitectory, which the service refuses
        // (osdu/specs/well-delivery-ddms/INTEGRATION.md section 9, item 7).
        var queried = OsduContracts.WellDeliveryDdms.Operations
            .SelectMany(o => (o.Definition["parameters"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(p => (string?)p["name"] == "type" && p["enum"] is JsonArray)
            .SelectMany(p => p["enum"]!.AsArray().Select(v => v!.GetValue<string>()))
            .Select(t => t == "wellboreArchitectory" ? "wellboreArchitecture" : t)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
        Assert.Equal(21, queried.Count);
        Assert.All(queried, t => Assert.Contains(t, types.Select(c => c.Segment)));

        // The storage routes every type goes through.
        foreach (var (method, template) in new[] { ("PUT", "/storage/v1/{type}"), ("GET", "/storage/v1/{type}/{id}"), ("GET", "/storage/v1/{type}/{id}/{version}"), ("DELETE", "/storage/v1/{type}/{id}"), ("DELETE", "/storage/v1/{type}/{id}:purge") })
        {
            Assert.Contains(OsduContracts.WellDeliveryDdms.Operations, o => o.Method == method && o.Template == template);
        }
    }

    [Fact]
    public void Each_shape_says_where_it_is_usually_deployed_and_how_it_is_probed()
    {
        Assert.Equal("/api/os-wellbore-ddms", DdmsCatalog.UsualRoot(DdmsShape.WellboreDdmsV3));
        Assert.Equal("/api/well-delivery", DdmsCatalog.UsualRoot(DdmsShape.WellDeliveryV1));
        Assert.Equal("/api/rafs-ddms", DdmsCatalog.UsualRoot(DdmsShape.RafsV2));
        Assert.Equal("wellDeliveryV1", DdmsCatalog.ShapeName(DdmsShape.WellDeliveryV1));
        Assert.Equal(["/wd/info"], DdmsCatalog.ProbePaths(new DdmsService("wd", "/wd", DdmsShape.WellDeliveryV1, [])));
        Assert.Equal(["/r/info", "/r/v2/samplesanalysis/analysistypes"], DdmsCatalog.ProbePaths(new DdmsService("r", "/r", DdmsShape.RafsV2, [])));
        Assert.Equal(["/about"], DdmsCatalog.ProbePaths(DdmsCatalog.WellboreDdms(null)));
        Assert.Equal("bharun", DdmsCatalog.WellDeliveryType("master-data--BHARun"));
        Assert.Equal("rig", DdmsCatalog.WellDeliveryCollection("master-data--Rig").Segment);
        Assert.Equal("/api/pddms/ingest/v1", DdmsCatalog.UsualRoot(DdmsShape.ProductionTimeSeriesV1));
        Assert.Equal("productionTimeSeriesV1", DdmsCatalog.ShapeName(DdmsShape.ProductionTimeSeriesV1));
        Assert.Equal(
            ["/h/info", "/q/info"],
            DdmsCatalog.ProbePaths(new DdmsService("h", "/h", DdmsShape.ProductionTimeSeriesV1, []) { TimeSeries = new TimeSeriesSettings { QueryRoot = "/q" } }));
        Assert.Equal(["/h/info", "/api/pddms/query/v1/info"], DdmsCatalog.ProbePaths(new DdmsService("h", "/h", DdmsShape.ProductionTimeSeriesV1, [])));
        Assert.Equal("/api/seismic-store/v3", DdmsCatalog.UsualRoot(DdmsShape.SeismicStoreV3));
        Assert.Equal("seismicStoreV3", DdmsCatalog.ShapeName(DdmsShape.SeismicStoreV3));
        Assert.Same(DdmsCatalog.SeismicStoreCollections, DdmsCatalog.DefaultCollections(DdmsShape.SeismicStoreV3));
        var seismic = new DdmsService("s", "/s", DdmsShape.SeismicStoreV3, []) { SeismicStore = new SeismicStoreSettings { Subproject = "raw" } };
        Assert.Equal(["/s/svcstatus", "/s/svcstatus/access", "/s/subproject/tenant/{partition}/subproject/raw"], DdmsCatalog.ProbePaths(seismic));
        Assert.Equal(
            "/s/subproject/tenant/osdu/subproject/raw",
            DdmsCatalog.SeismicSubprojectPath(seismic with { SeismicStore = new SeismicStoreSettings { Tenant = "osdu", Subproject = "raw" } }));
        Assert.Throws<ArgumentException>(() => DdmsCatalog.ProbePaths(seismic with { SeismicStore = null }));
        Assert.Null(DdmsCatalog.UsualRoot(DdmsShape.ReservoirManagement));
        Assert.Equal("reservoirManagement", DdmsCatalog.ShapeName(DdmsShape.ReservoirManagement));
        Assert.Same(DdmsCatalog.ReservoirManagementCollections, DdmsCatalog.DefaultCollections(DdmsShape.ReservoirManagement));
        Assert.Equal(
            ["/r/", "/r/ddms/estimated-volumes-det/header-entity/probe?header_entity_id=probe"],
            DdmsCatalog.ProbePaths(new DdmsService("r", "/r", DdmsShape.ReservoirManagement, [])));
    }

    [Fact]
    public void The_reservoir_management_header_collections_search_the_kinds_the_contract_reads_their_records_by()
    {
        var contract = OsduContracts.ReservoirManagementDdms;
        foreach (var header in ReservoirManagementTables.Headers)
        {
            Assert.Equal(header.EntityType, header.Kind.Split(':')[2]);

            // The read by id declares the pattern of the collection's ids, with the type's hyphens escaped.
            var read = Assert.Single(contract.Operations, o => o.Method == "GET" && o.Template.StartsWith($"/ddms/{header.Segment}/{{", StringComparison.Ordinal));
            var pattern = read.Definition["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "catalog_entity_id")!["schema"]!["pattern"]!.GetValue<string>();
            Assert.Contains(":" + header.EntityType + ":", pattern.Replace("\\", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains(contract.Operations, o => o.Method == "GET" && o.Template == $"/ddms/{header.Segment}/");
        }

        // Kr and Phi-K syntheses share their kind, so the defaults take the Phi-K synthesis, whose records the service can take in.
        Assert.Equal(8, DdmsCatalog.ReservoirManagementCollections.Count);
        Assert.DoesNotContain(DdmsCatalog.ReservoirManagementCollections, c => c.Segment == "kr-synthesis");
        Assert.Equal("phi-k-synthesis", DdmsCatalog.ReservoirManagementCollections.Single(c => c.EntityType == "work-product-component--PersistedCollection").Segment);
        Assert.Equal(
            ["estimated-volumes", "tank-datum", "fluid-synthesis", "phi-k-synthesis", "forecast"],
            DdmsCatalog.ReservoirManagementCollections.Where(c => c.Bulk).Select(c => c.Segment));
        Assert.All(DdmsCatalog.ReservoirManagementCollections, c => Assert.True(DdmsCatalog.IsEntityType(c.EntityType)));
    }

    [Fact]
    public void Seismic_stores_dataset_types_and_calls_are_the_ones_its_contract_and_clients_serve()
    {
        // The four dataset types Seismic Store's clients and its v4 service know, each keeping its files.
        Assert.Equal(
            ["dataset--FileCollection.SEGY:segy", "dataset--FileCollection.Slb.OpenZGY:openzgy", "dataset--FileCollection.Bluware.OpenVDS:openvds", "dataset--FileCollection.Generic:generic"],
            DdmsCatalog.SeismicStoreCollections.Select(c => $"{c.EntityType}:{c.Segment}"));
        Assert.All(DdmsCatalog.SeismicStoreCollections, c =>
        {
            Assert.StartsWith(DdmsCatalog.FileCollectionPrefix, c.EntityType, StringComparison.Ordinal);
            Assert.True(c.Bulk);
            Assert.False(c.TypedContent);
            Assert.Equal(DdmsBulkColumns.Unchecked, c.Columns);
            Assert.True(DdmsCatalog.IsEntityType(c.EntityType));
        });

        // Every call the shape makes is one the pinned contract declares.
        const string dataset = "/dataset/tenant/{tenantid}/subproject/{subprojectid}/dataset/{datasetid}";
        foreach (var (method, template) in new[]
        {
            ("POST", dataset), ("GET", dataset), ("PATCH", dataset), ("DELETE", dataset), ("PUT", dataset + "/lock"), ("PUT", dataset + "/unlock"),
            ("GET", "/utility/upload-connection-string"), ("GET", "/svcstatus"), ("GET", "/svcstatus/access"), ("GET", "/subproject/tenant/{tenantid}/subproject/{subprojectid}"),
        })
        {
            Assert.Contains(OsduContracts.SeismicDdms.Operations, o => o.Method == method && o.Template == template);
        }
    }

    [Theory]
    [InlineData("seismic", true)]
    [InlineData("seismic-raw", true)]
    [InlineData("s1", true)]
    [InlineData("s", false)]
    [InlineData("Seismic", false)]
    [InlineData("1seismic", false)]
    [InlineData("seismic-", false)]
    [InlineData("seis_mic", false)]
    [InlineData("", false)]
    public void A_seismic_subproject_name_is_the_one_its_contract_allows(string name, bool valid)
        => Assert.Equal(valid, DdmsCatalog.IsSubproject(name));

    [Theory]
    [InlineData("surveys", true)]
    [InlineData("surveys/north_2.b-c", true)]
    [InlineData("surveys//north", false)]
    [InlineData("/surveys", false)]
    [InlineData("surveys/", false)]
    [InlineData("north sea", false)]
    [InlineData("", false)]
    public void A_seismic_folder_is_segments_of_the_characters_a_dataset_path_takes(string folder, bool valid)
        => Assert.Equal(valid, DdmsCatalog.IsSeismicFolder(folder));

    [Theory]
    [InlineData("line-001", true)]
    [InlineData("a.b_c", true)]
    [InlineData("dev", true)]
    [InlineData("a/b", false)]
    [InlineData("line 001", false)]
    [InlineData("ключ", false)]
    [InlineData("", false)]
    public void A_seismic_dataset_or_tenant_name_takes_the_path_characters_without_a_slash(string name, bool valid)
    {
        Assert.Equal(valid, DdmsCatalog.IsSeismicDataset(name));
        Assert.Equal(valid, DdmsCatalog.IsSeismicTenant(name));
    }

    [Fact]
    public void The_historians_collection_and_roots_are_the_ones_both_of_its_contracts_serve()
    {
        var ingestion = OsduContracts.ProductionTimeSeriesIngestion;
        var query = OsduContracts.ProductionTimeSeries;
        Assert.Contains(DdmsCatalog.UsualRoot(DdmsShape.ProductionTimeSeriesV1), ingestion.BasePaths);
        Assert.Contains(DdmsCatalog.UsualTimeSeriesQueryRoot, query.BasePaths);

        var collection = Assert.Single(DdmsCatalog.TimeSeriesCollections);
        Assert.Equal("work-product-component--ProductionValues", collection.EntityType);
        Assert.Equal(DdmsCatalog.ProductionValues, collection.EntityType);
        Assert.True(collection.Bulk);
        Assert.False(collection.TypedContent);
        Assert.Equal(DdmsBulkColumns.Unchecked, collection.Columns);

        // The calls the shape makes: the single-record batch write, the read of one version, and both services' /info.
        Assert.Contains(ingestion.Operations, o => o.Method == "POST" && o.Template == $"/{collection.Segment}/{{recordId}}/timeseries");
        Assert.Contains(query.Operations, o => o.Method == "GET" && o.Template == $"/{collection.Segment}/{{recordId}}/timeseries/{{timeseriesId}}/versions/{{version}}");
        Assert.Contains(ingestion.Operations, o => o.Method == "GET" && o.Template == DdmsCatalog.TimeSeriesInfoPath);
        Assert.Contains(query.Operations, o => o.Method == "GET" && o.Template == DdmsCatalog.TimeSeriesInfoPath);

        // Neither contract has a delete (osdu/specs/production-timeseries/INTEGRATION.md section 6.3).
        Assert.DoesNotContain(ingestion.Operations, o => o.Method == "DELETE");
        Assert.DoesNotContain(query.Operations, o => o.Method == "DELETE");
    }

    [GeneratedRegex(@"^/ddms/v3/(?<segment>[^/{}]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPost();

    [GeneratedRegex(@"^/api/rafs-ddms/v2/(?<segment>[^/{}]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RafsCollectionPost();

    [GeneratedRegex(@"^/api/rafs-ddms/v2/(?<segment>[^/{}]+)/\{record_id\}/data/\{[^/{}]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex RafsTypedContent();
}
