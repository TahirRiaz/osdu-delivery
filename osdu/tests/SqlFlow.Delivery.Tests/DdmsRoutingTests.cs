using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Where the ddms route sends a record (<see cref="DdmsRouting"/>): the DDMSs a flow declares, then the Wellbore DDMS,
/// by the entity type every record id names; the paths a flow names itself; and what the preflight refuses.
/// </summary>
public sealed class DdmsRoutingTests
{
    private const string WellLog = "work-product-component--WellLog";
    private const string Trajectory = "work-product-component--WellboreTrajectory";
    private const string Wellbore = "master-data--Wellbore";

    private static FlowDefinition Flow(ProtocolOptions? options = null, IReadOnlyList<DdmsService>? ddms = null, string? interfaceName = null)
        => Samples.Targeting(
            new FlowTarget
            {
                Endpoint = "https://osdu.example.com",
                Protocol = DeliveryProtocol.OsduWellLog,
                ProtocolOptions = options ?? new ProtocolOptions(),
                Ddms = ddms ?? [],
            },
            interfaceName);

    private static DdmsService Declared(string name, string? root, params DdmsCollectionEntry[] collections)
        => new(name, root, DdmsShape.WellboreDdmsV3, collections);

    [Fact]
    public void A_flow_that_names_no_ddms_reaches_the_wellbore_ddms_at_its_endpoint()
    {
        var routing = DdmsRouting.Of(Flow());
        var paths = routing.For(WellLog);

        Assert.Equal("/ddms/v3/welllogs", paths.Records);
        Assert.Equal("/ddms/v3/welllogs/{id}", paths.Record);
        Assert.Equal("/ddms/v3/welllogs/{id}", paths.Delete);
        Assert.Equal("/ddms/v3/welllogs/{id}/data", paths.Data);
        Assert.Equal("/ddms/v3/welllogs/{id}/sessions", paths.Sessions);
        Assert.Equal("/ddms/v3/welllogs/{id}/sessions/{sessionId}/data", paths.SessionData);
        Assert.Equal("/ddms/v3/welllogs/{id}/sessions/{sessionId}", paths.Session);
        Assert.True(paths.Bulk);
        Assert.Equal(DdmsBulkColumns.CurveIdsAndWidths, paths.Columns);

        // The endpoint is the DDMS itself, so neither storage nor legal is reachable by a default path.
        Assert.False(routing.PlatformEndpoint);
        Assert.Null(routing.HistoryPath);
        Assert.Null(routing.StoragePurgePath);
        Assert.Null(routing.LegalValidatePath);
        Assert.Equal(["/about"], routing.ProbePaths);
    }

    [Fact]
    public void Each_entity_type_goes_to_its_own_collection_under_the_ddms_root()
    {
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }));

        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/wellboretrajectories/{id}/data", routing.For(Trajectory).Data);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/ppfgdataset", routing.For("work-product-component--PPFGDataset").Records);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogacquisition/{id}", routing.For("master-data--WellLogAcquisition").Record);
        Assert.True(routing.PlatformEndpoint);
        Assert.Equal("/api/storage/v2/records/{id}/versions", routing.HistoryPath);
        Assert.Equal("/api/storage/v2/records/{id}", routing.StoragePurgePath);
        Assert.Equal("/api/legal/v1/legaltags:validate", routing.LegalValidatePath);
        Assert.Equal(["/api/os-wellbore-ddms/about"], routing.ProbePaths);
    }

    [Fact]
    public void A_record_collection_has_no_bulk_paths_and_refuses_a_bulk_part_before_the_run()
    {
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, interfaceName: "wellbores"));
        var paths = routing.For(Wellbore);

        Assert.False(paths.Bulk);
        Assert.Equal(DdmsBulkColumns.Unchecked, paths.Columns);
        Assert.Null(paths.Data);
        Assert.Null(paths.Sessions);
        Assert.Null(routing.Problem(Wellbore, sendsBulk: false));

        var problem = routing.Problem(Wellbore, sendsBulk: true);
        Assert.Contains("the wellbores collection of the DDMS 'wellbore' (/api/os-wellbore-ddms), which holds records alone", problem, StringComparison.Ordinal);
        Assert.Contains("interfaces.wellbores.bulk would never be sent", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_ddms_takes_the_types_it_serves_and_the_wellbore_ddms_the_rest()
    {
        var petro = Declared("petro", "/petro", new DdmsCollectionEntry(WellLog, "logs", Bulk: true) { Columns = DdmsBulkColumns.CurveIds });
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, [petro]));

        var logs = routing.For(WellLog);
        Assert.Equal("/petro/ddms/v3/logs", logs.Records);
        Assert.Equal(DdmsBulkColumns.CurveIds, logs.Columns);
        Assert.Equal("the logs collection of the DDMS 'petro' (/petro)", logs.Describe());
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/wellboretrajectories", routing.For(Trajectory).Records);
        Assert.Equal(["/petro/about", "/api/os-wellbore-ddms/about"], routing.ProbePaths);

        // Without ddmsRoot, a source's interface reaches only what it declares.
        var alone = DdmsRouting.Of(Flow(ddms: [petro], interfaceName: "logs"));
        Assert.Equal("/petro/ddms/v3/logs", alone.For(WellLog).Records);
        var unserved = Assert.Throws<DeliveryException>(() => alone.For(Trajectory));
        Assert.Contains($"no DDMS this flow reaches serves {Trajectory}: it reaches the DDMS 'petro' (/petro), serving {WellLog}", unserved.Message, StringComparison.Ordinal);
        Assert.Contains("Declare the DDMS that serves it under target.ddms", unserved.Message, StringComparison.Ordinal);
        Assert.StartsWith("targeting (interface 'logs'): ", unserved.Message, StringComparison.Ordinal);
        Assert.Equal(unserved.Message, alone.Problem(Trajectory, sendsBulk: false));
    }

    [Fact]
    public void A_declared_ddms_without_a_root_is_the_endpoint_itself()
    {
        var routing = DdmsRouting.Of(Flow(ddms: [Declared("own", null, new DdmsCollectionEntry(WellLog, "welllogs", Bulk: true))]));

        Assert.Equal("/ddms/v3/welllogs", routing.For(WellLog).Records);
        Assert.False(routing.PlatformEndpoint);
        Assert.Contains("no DDMS this flow reaches serves", Assert.Throws<DeliveryException>(() => routing.For(Trajectory)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_that_names_its_paths_has_them_used_and_the_collection_supplies_the_rest()
    {
        var options = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms", RecordPath = "/petrodb/welllogs", DataPath = "/petrodb/welllogs/{id}/bulk" };
        var routing = DdmsRouting.Of(Flow(options));
        var paths = routing.For(WellLog);

        Assert.True(routing.NamesPaths);
        Assert.Equal("/petrodb/welllogs", paths.Records);
        Assert.Equal("/petrodb/welllogs/{id}/bulk", paths.Data);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}", paths.Record);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}/sessions", paths.Sessions);

        // A type no DDMS serves has nowhere to take the paths the flow leaves out from.
        var bare = DdmsRouting.Of(Flow(new ProtocolOptions { RecordPath = "/petrodb/things" }, interfaceName: "things"));
        Assert.Contains("names its own DDMS paths but not verifyPath", Assert.Throws<DeliveryException>(() => bare.For("work-product-component--Thing")).Message, StringComparison.Ordinal);

        var named = DdmsRouting.Of(Flow(
            new ProtocolOptions { RecordPath = "/petrodb/things", VerifyPath = "/petrodb/things/{id}", DeletePath = "/petrodb/things/{id}" },
            interfaceName: "things"));
        var thing = named.For("work-product-component--Thing");
        Assert.Null(thing.Route);
        Assert.True(thing.Bulk);
        Assert.Null(named.Problem("work-product-component--Thing", sendsBulk: false));
        Assert.Contains("Name dataPath, sessionPath, sessionDataPath, sessionCommitPath", named.Problem("work-product-component--Thing", sendsBulk: true), StringComparison.Ordinal);
        Assert.Equal("the paths the flow names (/petrodb/things)", thing.Describe());
        Assert.Empty(named.ProbePaths);
    }

    [Theory]
    [InlineData("dev:work-product-component--WellLog:abc", WellLog)]
    [InlineData("dev:master-data--Wellbore:abc:", Wellbore)]
    [InlineData("dev:master-data--Wellbore:abc:12", Wellbore)]
    [InlineData("abc", null)]
    [InlineData("dev::abc", null)]
    [InlineData("dev:WellLog", null)]
    public void A_record_id_names_its_entity_type(string id, string? entityType)
        => Assert.Equal(entityType, DdmsRouting.EntityTypeOf(id));

    [Fact]
    public void A_record_routes_by_its_id()
    {
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/wdms" }));
        Assert.Equal("/wdms/ddms/v3/wellboretrajectories/{id}", routing.ForRecord("dev:" + Trajectory + ":t-1").Record);
        Assert.Contains("names no entity type", Assert.Throws<DeliveryException>(() => routing.ForRecord("t-1")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ddms_named_by_registration_is_not_routed_to_until_its_registration_is_read()
    {
        var registered = Declared("wellbore", null) with { Registration = "wellbore" };
        var routing = DdmsRouting.Of(Flow(ddms: [registered], interfaceName: "logs"));

        Assert.Equal(["wellbore"], routing.Unread);
        Assert.Empty(routing.Services);
        Assert.True(routing.PlatformEndpoint);

        // The preflight lets the type through; the protocol, which reads the registration, is what routes it.
        Assert.Null(routing.Problem(WellLog, sendsBulk: true));
        Assert.Contains("its registration in the Register service has not been read yet", Assert.Throws<DeliveryException>(() => routing.For(WellLog)).Message, StringComparison.Ordinal);
        Assert.Contains("read from the Register service when the flow runs", routing.Explain("osdu:wks:" + WellLog + ":1.4.0"), StringComparison.Ordinal);

        var read = registered with { Root = "/api/os-wellbore-ddms", Collections = DdmsCatalog.WellboreDdmsCollections, Discovered = true };
        var resolved = DdmsRouting.Of(Flow(ddms: [read], interfaceName: "logs"));
        Assert.Empty(resolved.Unread);
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs", resolved.For(WellLog).Records);
        Assert.Equal(
            $"{WellLog} records go to the welllogs collection of the DDMS 'wellbore' (/api/os-wellbore-ddms).",
            resolved.Explain("osdu:wks:" + WellLog + ":1.4.0"));
    }

    private static readonly DdmsService WellDelivery = new("welldelivery", "/api/well-delivery", DdmsShape.WellDeliveryV1, DdmsCatalog.WellDeliveryCollections)
    {
        WellDelivery = new WellDeliverySettings(),
    };

    private static readonly DdmsService Rafs = new("rafs", "/api/rafs-ddms", DdmsShape.RafsV2, DdmsCatalog.RafsCollections);

    [Fact]
    public void A_well_delivery_entity_is_written_under_its_type_and_read_by_its_entity_id()
    {
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, [WellDelivery, Rafs], "entities"));
        var wellbores = routing.For(Wellbore);

        Assert.Equal(DdmsShape.WellDeliveryV1, wellbores.Shape);
        Assert.Equal("/api/well-delivery/storage/v1/wellbore", wellbores.Records);
        Assert.Equal("/api/well-delivery/storage/v1/wellbore/{entityId}", wellbores.Record);
        Assert.Equal("/api/well-delivery/storage/v1/wellbore/{entityId}", wellbores.Delete);
        Assert.Null(wellbores.Data);
        Assert.Null(wellbores.Sessions);
        Assert.False(wellbores.Bulk);
        Assert.Equal("/api/well-delivery/storage/v1/bharun", routing.For("master-data--BHARun").Records);

        // Declared first, the Well Delivery DDMS takes the well logs the Wellbore DDMS under ddmsRoot would have kept.
        Assert.Equal("/api/well-delivery/storage/v1/welllog", routing.For(WellLog).Records);
        var bulk = routing.Problem(WellLog, sendsBulk: true);
        Assert.Contains("the welllog collection of the DDMS 'welldelivery' (/api/well-delivery), which holds records alone", bulk, StringComparison.Ordinal);
        Assert.Contains("target.ddms.welldelivery serves the type because its shape (wellDeliveryV1) serves it unless the flow lists other collections", bulk, StringComparison.Ordinal);

        var listed = WellDelivery with { Collections = [DdmsCatalog.WellDeliveryCollection(Wellbore)], DeclaresCollections = true };
        var chosen = DdmsRouting.Of(Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, [listed], "entities"));
        Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs", chosen.For(WellLog).Records);
        Assert.EndsWith("which holds records alone and takes no bulk data, so interfaces.entities.bulk would never be sent. Deliver those records without it.", chosen.Problem(Wellbore, sendsBulk: true), StringComparison.Ordinal);

        Assert.Equal(["/api/well-delivery/info", "/api/rafs-ddms/info", "/api/rafs-ddms/v2/samplesanalysis/analysistypes", "/api/os-wellbore-ddms/about"], routing.ProbePaths);
        Assert.Equal("/api/storage/v2/records/{id}:delete", routing.StorageDeletePath);
    }

    [Fact]
    public void A_rafs_record_takes_its_content_under_its_type_where_the_collection_holds_several()
    {
        var routing = DdmsRouting.Of(Flow(ddms: [Rafs], interfaceName: "samples"));

        var analysis = routing.For("work-product-component--SamplesAnalysis");
        Assert.Equal(DdmsShape.RafsV2, analysis.Shape);
        Assert.Equal("/api/rafs-ddms/v2/samplesanalysis", analysis.Records);
        Assert.Equal("/api/rafs-ddms/v2/samplesanalysis/{id}", analysis.Record);
        Assert.Equal("/api/rafs-ddms/v2/samplesanalysis/{id}/data/{contentType}", analysis.Data);
        Assert.Null(analysis.Sessions);
        Assert.Null(routing.Problem("work-product-component--SamplesAnalysis", sendsBulk: true));

        Assert.Equal("/api/rafs-ddms/v2/depthshift/{id}/data", routing.For("work-product-component--DepthShift").Data);
        var sample = routing.For("master-data--Sample");
        Assert.Equal("/api/rafs-ddms/v2/masterdata", sample.Records);
        Assert.Null(sample.Data);
        Assert.Contains("which holds records alone", routing.Problem("master-data--Sample", sendsBulk: true), StringComparison.Ordinal);
        Assert.Equal(
            "work-product-component--FluidModel records go to the fluidmodel collection of the DDMS 'rafs' (/api/rafs-ddms).",
            routing.Explain("osdu:wks:work-product-component--FluidModel:1.0.0"));
    }

    [Fact]
    public void Paths_a_flow_names_for_a_wellbore_ddms_facade_are_refused_for_a_ddms_of_another_shape()
    {
        var routing = DdmsRouting.Of(Flow(new ProtocolOptions { VerifyPath = "/petrodb/{id}", DataPath = "/petrodb/{id}/bulk" }, [Rafs], "samples"));
        var refused = Assert.Throws<DeliveryException>(() => routing.For("work-product-component--DepthShift"));
        Assert.Contains("the flow names DDMS paths of its own (verifyPath, dataPath under target.protocolOptions of interface 'samples')", refused.Message, StringComparison.Ordinal);
        Assert.Contains("whose shape (rafsV2) says every call they take", refused.Message, StringComparison.Ordinal);
        Assert.Equal(refused.Message, routing.Problem("work-product-component--DepthShift", sendsBulk: true));
    }

    [Fact]
    public void The_removal_endpoints_follow_the_shape_of_the_ddms()
    {
        var wells = Flow(ddms: [WellDelivery], interfaceName: "wells");
        var entity = RemovalEndpoints.Of(wells, "osdu:wks:master-data--Well:1.0.0");
        Assert.Equal("/api/well-delivery/storage/v1/well/{entityId}", entity.Record);
        Assert.Equal(RemovalEndpoints.HistoryRefusedByWellDelivery, entity.History);
        Assert.Equal("/api/well-delivery/storage/v1/well/{entityId}:purge", entity.Everything);

        var samples = RemovalEndpoints.Of(Flow(ddms: [Rafs], interfaceName: "samples"), "osdu:wks:work-product-component--SamplesAnalysis:1.0.0");
        Assert.Equal("/api/rafs-ddms/v2/samplesanalysis/{id}", samples.Record);
        Assert.Equal("/api/storage/v2/records/{id}/versions", samples.History);
        Assert.Equal("/api/storage/v2/records/{id}", samples.Everything);
    }

    [Fact]
    public void The_route_check_refuses_what_the_route_cannot_deliver()
    {
        var logs = Flow(new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms" }, interfaceName: "logs");
        RouteChecks.Check(logs, "osdu:wks:" + WellLog + ":1.4.0");
        RouteChecks.Check(logs, "osdu:wks:" + Wellbore + ":1.3.0");

        var unserved = Assert.Throws<DeliveryException>(() => RouteChecks.Check(logs, "osdu:wks:work-product-component--SeismicTraceData:1.3.0"));
        Assert.Contains("no DDMS this flow reaches serves work-product-component--SeismicTraceData", unserved.Message, StringComparison.Ordinal);
        Assert.EndsWith("(interfaces.logs.mapping Targeting@1.0.0 renders osdu:wks:work-product-component--SeismicTraceData:1.3.0.)", unserved.Message, StringComparison.Ordinal);

        var bulk = logs with { Target = logs.Target with { ProtocolOptions = logs.Target.ProtocolOptions with { Payload = "bulk" } } };
        Assert.Contains("holds records alone", Assert.Throws<DeliveryException>(() => RouteChecks.Check(bulk, "osdu:wks:" + Wellbore + ":1.3.0")).Message, StringComparison.Ordinal);
        Assert.Contains("names no entity type", Assert.Throws<DeliveryException>(() => RouteChecks.Check(logs, "osdu:wks:*:1.0.0")).Message, StringComparison.Ordinal);

        // Another route checks nothing here.
        RouteChecks.Check(logs with { Target = logs.Target with { Protocol = DeliveryProtocol.OsduRecord } }, "osdu:wks:work-product-component--SeismicTraceData:1.3.0");
    }
}
