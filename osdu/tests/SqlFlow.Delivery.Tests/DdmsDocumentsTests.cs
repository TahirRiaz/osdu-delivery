using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The DDMSs a flow document declares under <c>target.ddms</c> (docs/documents.md), the route type
/// <c>target.protocol</c> names, and every declaration the loader refuses.
/// </summary>
public sealed class DdmsDocumentsTests
{
    private readonly DeliveryDocumentLoader _loader = new();

    /// <summary>A flow in the single form; <paramref name="ddms"/> is YAML indented two spaces, under target.</summary>
    private static string Single(string ddms, string protocol = "ddms", string options = "") => ($$"""
        flowType: delivery
        name: logs
        source:
          connection: ${env:LOGS_DB}
          record: { object: Logs.ing.WellLog, key: [log_id] }
          work: work
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }
          protocol: {{protocol}}
          protocolOptions: { batchSize: 10{{options}} }
        {{ddms}}
        """).ReplaceLineEndings("\n");

    /// <summary>A source with a storage interface and a ddms one; <paramref name="ddms"/> is YAML indented two spaces, under target.</summary>
    private static string Source(string ddms, string logs = "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }") => ($$"""
        flowType: delivery
        name: estate
        source:
          connection: ${env:ESTATE_DB}
          work: work
        target:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: opendes }
        {{ddms}}
        interfaces:
          wellbores:
            record: { object: Estate.ing.Wellbore, key: [uwi] }
            mapping: Wellbore@1.0.0
          logs:
            record: { object: Estate.ing.WellLog, key: [log_id] }
            {{logs}}
            mapping: WellLog@1.4.0
        """).ReplaceLineEndings("\n");

    [Fact]
    public void A_flow_declares_each_ddms_with_its_root_shape_and_collections()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                petro:
                  root: /petro/
                  collections:
                    work-product-component--WellLog: { path: logs, bulk: true, columns: curveIds }
                    master-data--Wellbore: { path: wellbores }
                wellbore:
                  root: /api/os-wellbore-ddms
                  shape: wellbore-ddms-v3
                  collections:
                    work-product-component--WellboreTrajectory: { path: wellboretrajectories, bulk: true, columns: trajectoryStations }
            """), "logs.yaml");

        Assert.Equal(DeliveryProtocol.OsduWellLog, flow.Target.Protocol);
        Assert.Equal(["petro", "wellbore"], flow.Target.Ddms.Select(d => d.Name));
        var petro = flow.Target.Ddms[0];
        Assert.Equal("/petro", petro.Root);
        Assert.Equal(DdmsShape.WellboreDdmsV3, petro.Shape);
        Assert.Null(petro.Registration);
        Assert.Equal(
            ["work-product-component--WellLog:logs:True:CurveIds", "master-data--Wellbore:wellbores:False:Unchecked"],
            petro.Collections.Select(c => $"{c.EntityType}:{c.Segment}:{c.Bulk}:{c.Columns}"));
        Assert.Equal(DdmsBulkColumns.TrajectoryStations, flow.Target.Ddms[1].CollectionFor("work-product-component--WellboreTrajectory")!.Columns);
    }

    [Fact]
    public void A_ddms_that_lists_no_collections_serves_those_of_its_shape()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  root: /api/os-wellbore-ddms
            """), "logs.yaml");

        Assert.Same(DdmsCatalog.WellboreDdmsCollections, flow.Target.Ddms.Single().Collections);

        // Declared with no body, a DDMS is the endpoint itself, with its shape's collections.
        var bare = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
            """), "logs.yaml");
        Assert.Null(bare.Target.Ddms.Single().Root);
        Assert.Same(DdmsCatalog.WellboreDdmsCollections, bare.Target.Ddms.Single().Collections);
    }

    [Fact]
    public void A_ddms_named_by_its_registration_leaves_what_it_does_not_declare_to_the_register_service()
    {
        var flow = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  register: wellbore
            """, options: ", registerPath: '/api/register/v1/ddms/{id}'"), "logs.yaml");

        var wellbore = flow.Target.Ddms.Single();
        Assert.Equal("wellbore", wellbore.Registration);
        Assert.True(wellbore.AwaitsDiscovery);
        Assert.Null(wellbore.Root);
        Assert.Empty(wellbore.Collections);
        Assert.Equal("/api/register/v1/ddms/{id}", flow.Target.ProtocolOptions.RegisterPath);

        // With a root, the registration supplies the collections; with collections, the root.
        var rooted = _loader.ParseFlow(Single("""
              ddms:
                wellbore:
                  register: wellbore
                  root: /wdms
            """), "logs.yaml");
        Assert.Equal("/wdms", rooted.Target.Ddms.Single().Root);
        Assert.Empty(rooted.Target.Ddms.Single().Collections);
    }

    [Theory]
    [InlineData("ddms", DeliveryProtocol.OsduWellLog)]
    [InlineData("Ddms", DeliveryProtocol.OsduWellLog)]
    [InlineData("storage", DeliveryProtocol.OsduRecord)]
    [InlineData("file", DeliveryProtocol.OsduFile)]
    [InlineData("manifest", DeliveryProtocol.OsduManifest)]
    [InlineData("osduWellLog", DeliveryProtocol.OsduWellLog)]
    [InlineData("osduRecord", DeliveryProtocol.OsduRecord)]
    public void The_protocol_names_a_route_type_or_the_protocol_it_maps_onto(string value, DeliveryProtocol expected)
        => Assert.Equal(expected, _loader.ParseFlow(Single(string.Empty, value), "logs.yaml").Target.Protocol);

    [Theory]
    [InlineData("teleport")]
    [InlineData("1")]
    public void A_protocol_that_is_neither_is_refused(string value)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(string.Empty, value), "logs.yaml"));
        Assert.Equal(
            $"logs.yaml: 'target.protocol' value '{value}' is not one of storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms, workflow "
            + "(or the protocols they map onto: osduRecord, osduWellLog, osduFile, osduManifest, osduDataset, osduFileAndDdms, osduManifestAndDdms, osduWorkflow).",
            refused.Message);
    }

    public static TheoryData<string, string, string, string> RefusedDeclarations => new()
    {
        { "  ddms:\n    petro: { root: /petro }", "storage", "", "target.ddms declares the DDMSs the ddms route delivers to, and this flow's protocol is OsduRecord" },
        { "  ddms: {}", "ddms", "", "target.ddms declares no DDMS" },
        { "  ddms:\n    2petro: { root: /petro }", "ddms", "", "target.ddms names a DDMS '2petro'; a DDMS name is a letter followed by letters, digits, '_' and '-'" },
        { "  ddms:\n    petro: { root: /a, collections: { master-data--Well: { path: wells } } }\n    Petro: { root: /b, collections: { master-data--Wellbore: { path: wellbores } } }", "ddms", "", "target.ddms declares 'Petro' more than once (DDMS names are compared ignoring case)" },
        { "  ddms:\n    petro: { root: /petro, shape: etp }", "ddms", "", "'target.ddms.petro.shape' value 'etp' is not one of wellboreDdmsV3" },
        { "  ddms:\n    petro: { root: api/petro }", "ddms", "", "target.ddms.petro.root 'api/petro' must be a path under the endpoint starting with '/'" },
        { "  ddms:\n    petro: { root: 'https://petro.example.com/ddms' }", "ddms", "", "target.ddms.petro.root 'https://petro.example.com/ddms' must be a path" },
        { "  ddms:\n    petro: { root: '/petro/{id}' }", "ddms", "", "target.ddms.petro.root '/petro/{id}' must be a path" },
        { "  ddms:\n    petro: { root: / }", "ddms", "", "target.ddms.petro.root '/' must be a path" },
        { "  ddms:\n    petro: { root: /petro, collections: {} }", "ddms", "", "target.ddms.petro.collections lists no collection" },
        { "  ddms:\n    petro: { root: /petro, collections: { WellLog: { path: welllogs } } }", "ddms", "", "target.ddms.petro.collections names 'WellLog', which is not an OSDU entity type with its group" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: a }, MASTER-DATA--WELL: { path: b } } }", "ddms", "", "target.ddms.petro.collections lists MASTER-DATA--WELL more than once" },
        { "  ddms:\n    petro:\n      root: /petro\n      collections:\n        master-data--Well:", "ddms", "", "target.ddms.petro.collections.master-data--Well declares nothing" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells/x } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.path 'wells/x' must be the collection's path segment" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { bulk: true } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.path '' must be the collection's path segment" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells, columns: curveIds } } }", "ddms", "", "target.ddms.petro.collections.master-data--Well.columns says what bulk data columns are checked against, and the collection holds records alone (bulk is false)" },
        { "  ddms:\n    petro: { root: /petro, collections: { master-data--Well: { path: wells, bulk: true, columns: mnemonics } } }", "ddms", "", "'target.ddms.petro.collections.master-data--Well.columns' value 'mnemonics' is not one of" },
        { "  ddms:\n    a: { root: /a, collections: { work-product-component--WellLog: { path: logs } } }\n    b: { root: /b }", "ddms", "", "target.ddms serves work-product-component--WellLog from both 'a' and 'b'" },
        { "  ddms:\n    a: { collections: { work-product-component--WellLog: { path: logs } } }\n    b: { root: /b, collections: { master-data--Well: { path: wells } } }", "ddms", "", "target.ddms.a names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS. Give every DDMS its root" },
        { "  ddms:\n    a: { collections: { work-product-component--WellLog: { path: logs } } }", "ddms", ", ddmsRoot: /api/os-wellbore-ddms", "target.ddms.a names no root, which makes the flow's endpoint that DDMS itself, so the flow reaches no other DDMS and target.protocolOptions.ddmsRoot places none under it" },
        { "  ddms:\n    a: { register: w }", "ddms", "", "target.ddms.a.register 'w' is not an id the Register service keeps a DDMS under" },
        { "  ddms:\n    a: { register: wellbore, root: /wdms, collections: { master-data--Well: { path: wells } } }", "ddms", "", "target.ddms.a declares its root and its collections, so its registration has nothing to supply" },
        { "  ddms:\n    a: { root: /wdms }", "ddms", ", registerPath: '/api/register/v1/ddms/{id}'", "target.protocolOptions.registerPath says where the Register service reads a DDMS registration, and no DDMS under target.ddms names one with register" },
        { "  ddms:\n    a: { register: wellbore }", "ddms", ", registerPath: /api/register/v1/ddms", "target.protocolOptions.registerPath '/api/register/v1/ddms' must be a path under the endpoint starting with '/', or an absolute http(s) URL, with {id} where the registration's id goes" },
        { "  ddms:\n    a: { register: wellbore }", "ddms", ", registerPath: 'register/{id}'", "target.protocolOptions.registerPath 'register/{id}' must be a path" },
        { "  ddms:\n    a: { rooot: /wdms }", "ddms", "", "invalid YAML" },
    };

    [Theory]
    [MemberData(nameof(RefusedDeclarations))]
    public void A_declaration_that_cannot_mean_what_it_says_is_refused(string ddms, string protocol, string options, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseFlow(Single(ddms, protocol, options), "logs.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("logs.yaml: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_sends_its_ddms_interfaces_to_the_ddms_it_declares()
    {
        var source = _loader.ParseSource(Source("""
              ddms:
                wellbore:
                  root: /api/os-wellbore-ddms
            """), "estate.yaml");

        var logs = source.Interface("logs");
        Assert.Equal(DeliveryProtocol.OsduWellLog, logs.Target.Protocol);
        Assert.Null(logs.Target.ProtocolOptions.DdmsRoot);
        Assert.Equal("/api/os-wellbore-ddms", logs.Target.Ddms.Single().Root);

        // The storage interface has no use for the DDMSs.
        Assert.Empty(source.Interface("wellbores").Target.Ddms);

        // A registration says where its DDMS is, so it needs no root.
        var registered = _loader.ParseSource(Source("""
              ddms:
                wellbore:
                  register: wellbore
            """), "estate.yaml");
        Assert.Equal("wellbore", registered.Interface("logs").Target.Ddms.Single().Registration);
    }

    [Theory]
    [InlineData("  ddms:\n    wellbore:\n      collections: { work-product-component--WellLog: { path: welllogs, bulk: true } }", "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }", "target.ddms.wellbore.root is required. The source's endpoint is the platform its interfaces reach every service under")]
    [InlineData("", "bulk: { root: curves, locationColumn: folder, hashColumn: curve_hash }", "declare it under target.ddms with its root, or set target.protocolOptions.ddmsRoot or interfaces.logs.protocolOptions.ddmsRoot")]
    [InlineData("  ddms:\n    wellbore: { root: /api/os-wellbore-ddms }", "route: storage", "target.ddms declares the DDMSs the source's interfaces are delivered to, and no interface is delivered through one")]
    public void A_source_that_does_not_say_where_its_ddms_is_or_declares_one_it_does_not_use_is_refused(string ddms, string logs, string expected)
    {
        var refused = Assert.Throws<FlowValidationException>(() => _loader.ParseSource(Source(ddms, logs), "estate.yaml"));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }
}
