using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route to a Well Delivery DDMS (osdu/specs/well-delivery-ddms/INTEGRATION.md) against a fake of the service
/// built from its brief: one entity per write under a version recorded before the write, references given the versions
/// the DDMS holds so its index sees them, a content rewrite in place, the rules checked before anything is sent, and a
/// removal that takes the copy the deployment keeps in Storage.
/// </summary>
public sealed class WellDeliveryRouteTests
{
    private const string WellId = "opendes:master-data--Well:welldemo2";
    private const string WellboreId = "opendes:master-data--Wellbore:wbdemo2";
    private const string WellKind = "osdu:wks:master-data--Well:1.0.0";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.0.0";
    private const string Planned = "opendes:reference-data--ExistenceKind:Planned:";
    private const string Storage = "/api/storage/v2/records/";
    private const string Entities = FakeOsduPlatform.WellDeliveryRoot + "/storage/v1/";

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, WellDeliverySettings? settings = null, TimeProvider? time = null)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            var client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "opendes" });
            var service = new DdmsService("welldelivery", FakeOsduPlatform.WellDeliveryRoot, DdmsShape.WellDeliveryV1, DdmsCatalog.WellDeliveryCollections)
            {
                WellDelivery = settings ?? new WellDeliverySettings(),
            };
            var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.OsduWellLog, Ddms = [service] }, "wells");
            Protocol = new OsduWellLogProtocol(client, new ProtocolOptions(), NullLogger.Instance, time: time, routing: DdmsRouting.Of(flow));
        }

        public HttpRuntime Runtime { get; }

        public OsduWellLogProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    private static JsonObject Entity(string id, string kind, JsonObject? data = null)
    {
        var entity = FakeOsduPlatform.Record(id, kind, data ?? new JsonObject { ["FacilityName"] = "Well 2" });
        entity["data"]!["ExistenceKind"] ??= Planned;
        return entity;
    }

    private static JsonObject Well() => Entity(WellId, WellKind);

    private static JsonObject Wellbore() => Entity(WellboreId, WellboreKind, new JsonObject
    {
        ["FacilityName"] = "Wellbore 2",
        ["WellID"] = WellId + ":",
        ["NameAliases"] = new JsonArray(new JsonObject { ["AliasNameTypeID"] = "opendes:reference-data--AliasNameType:Borehole:" }),
    });

    /// <summary>One delivery of <paramref name="document"/>; the steps it reports are appended to <paramref name="reported"/>.</summary>
    private static DeliveryWork Work(
        JsonObject document,
        long? existing = null,
        IReadOnlyDictionary<string, string>? state = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null,
        List<(string Step, IReadOnlyDictionary<string, string> Values, int CallsSoFar)>? reported = null,
        FakeOsduPlatform? platform = null) => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("well-delivery", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = false,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            StepCompleted = (step, values, _) =>
            {
                reported?.Add((step, values, platform?.Calls.Count ?? 0));
                return Task.CompletedTask;
            },
        };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

    private static JsonObject SentBody(FakeOsduPlatform platform, int index) => JsonNode.Parse(platform.Calls[index].Body!)!.AsObject();

    /// <summary>
    /// The one difference the brief records between the Well Delivery contract and the service that a write crosses: the
    /// contract's EntityID pattern admits only its example type, and the service takes any (section 9, item 2).
    /// </summary>
    private static bool EntityIdExample(string violation)
        => violation.Contains("PUT /storage/v1/{type} body.id", StringComparison.Ordinal) && violation.Contains("ExampleType", StringComparison.Ordinal);

    /// <summary>The service's version information, which its code serves and its contract does not declare (section 9, item 6).</summary>
    private static bool InfoRoute(FakeHttpHandler.Request request) => request.Uri.AbsolutePath == FakeOsduPlatform.WellDeliveryRoot + "/info";

    [Fact]
    public async Task A_wellbore_cites_the_version_the_ddms_holds_for_its_well_under_a_version_recorded_before_the_write()
    {
        var platform = new FakeOsduPlatform();
        var reported = new List<(string Step, IReadOnlyDictionary<string, string> Values, int CallsSoFar)>();
        using var rig = new Rig(platform);

        var well = await rig.Protocol.DeliverAsync(Work(Well(), reported: reported, platform: platform));
        Assert.True(well.Succeeded, well.Failure?.Message);
        var wellVersion = well.TargetVersion!.Value;
        Assert.InRange(wellVersion, 1_000_000_000_000, 9_999_999_999_999);

        // The version is on the ledger before the write that carries it.
        Assert.Equal(["version", "metadata"], reported.Select(r => r.Step));
        Assert.Equal(0, reported[0].CallsSoFar);
        Assert.Equal("new", reported[0].Values["write"]);
        Assert.Equal(wellVersion.ToString(CultureInfo.InvariantCulture), reported[0].Values["version"]);
        Assert.Equal(wellVersion, SentBody(platform, 0)["version"]!.GetValue<long>());
        Assert.Equal(WellId, well.Returned[WellDeliveryShape.StorageIdKey]);
        Assert.Equal("true", well.Returned["wellDelivery.valid"]);

        // The wellbores are another interface, with a protocol of its own, which reads the version of the well it cites.
        using var wellbores = new Rig(platform);
        var wellbore = await wellbores.Protocol.DeliverAsync(Work(Wellbore()));
        Assert.True(wellbore.Succeeded, wellbore.Failure?.Message);
        Assert.Null(wellbore.Detail);
        Assert.Equal("1", wellbore.Returned["wellDelivery.pinned"]);

        // A protocol that wrote an entity knows its version without asking.
        var calls = platform.Calls.Count;
        var section = Entity("opendes:master-data--HoleSection:hs3", "osdu:wks:master-data--HoleSection:1.0.0", new JsonObject { ["WellboreID"] = WellboreId + ":" });
        await wellbores.Protocol.DeliverAsync(Work(section));
        Assert.Equal(["PUT " + Entities + "holesection"], Sent(platform, calls));
        Assert.Equal([$"{WellboreId}:{wellbore.TargetVersion}"], platform.WellDeliveryIndex.Single(i => i.Key.StartsWith("holesection|hs3|", StringComparison.Ordinal)).Value.ToArray());

        Assert.Equal(
            ["PUT " + Entities + "well", "GET " + Entities + "well/welldemo2", "PUT " + Entities + "wellbore", "PUT " + Entities + "holesection"],
            Sent(platform));
        var sent = SentBody(platform, 2);
        Assert.Equal($"{WellId}:{wellVersion}", sent["data"]!["WellID"]!.GetValue<string>());
        Assert.Equal(Planned, sent["data"]!["ExistenceKind"]!.GetValue<string>());
        Assert.Equal("opendes:reference-data--AliasNameType:Borehole:", sent["data"]!["NameAliases"]![0]!["AliasNameTypeID"]!.GetValue<string>());
        Assert.Equal("application/json", platform.Calls[2].ContentType);

        // The DDMS indexed the pinned reference, and copied both entities into storage.
        var wellboreKey = $"wellbore|wbdemo2|{wellbore.TargetVersion}";
        Assert.Equal([$"{WellId}:{wellVersion}"], platform.WellDeliveryIndex[wellboreKey]);
        Assert.Equal(WellboreId, platform.Records[WellboreId]["data"]!["origId"]!.GetValue<string>());

        Assert.Equal(
            ["well-delivery-ddms GET /storage/v1/{type}/{id}", "well-delivery-ddms PUT /storage/v1/{type}"],
            OsduContracts.AssertConform(platform.Calls, InfoRoute, EntityIdExample, OsduContracts.WellDeliveryDdms, OsduContracts.Storage));
    }

    [Fact]
    public async Task A_retry_sends_the_recorded_version_again_and_a_lost_answer_is_settled_by_reading_that_version()
    {
        var platform = new FakeOsduPlatform { WellDeliveryFailsAfterWrite = true };
        using var rig = new Rig(platform);

        var lost = await rig.Protocol.DeliverAsync(Work(Well()));
        Assert.True(lost.Succeeded, lost.Failure?.Message);
        var version = lost.TargetVersion!.Value;
        Assert.Equal(
            ["PUT " + Entities + "well", "GET " + Entities + $"well/welldemo2/{version}"],
            Sent(platform));

        // A try that recorded its version and stopped before the write: the retry sends that version, which the DDMS replaces in place.
        var completed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["version"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = version.ToString(CultureInfo.InvariantCulture), ["write"] = "new" },
        };
        var retry = await rig.Protocol.DeliverAsync(Work(Well(), completed: completed));
        Assert.True(retry.Succeeded, retry.Failure?.Message);
        Assert.Equal(version, retry.TargetVersion);
        Assert.Single(platform.WellDeliveryEntities.Keys, k => k.StartsWith("well|welldemo2|", StringComparison.Ordinal));

        // A try that wrote the entity is not written again.
        var written = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["metadata"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = WellId, ["version"] = version.ToString(CultureInfo.InvariantCulture) },
        };
        var calls = platform.Calls.Count;
        var resumed = await rig.Protocol.DeliverAsync(Work(Well(), completed: written));
        Assert.Equal(version, resumed.TargetVersion);
        Assert.Equal(calls, platform.Calls.Count);
    }

    [Fact]
    public async Task Content_delivered_before_is_written_again_in_place_and_picks_up_references_to_entities_written_since()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var first = await rig.Protocol.DeliverAsync(Work(Wellbore()));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Equal(WellId + ":", first.Returned["wellDelivery.unpinned"]);
        Assert.Contains("1 reference(s) to entities the DDMS does not hold were sent without a version", first.Detail, StringComparison.Ordinal);
        Assert.Empty(platform.WellDeliveryIndex[$"wellbore|wbdemo2|{first.TargetVersion}"]);

        var well = await rig.Protocol.DeliverAsync(Work(Well()));
        Assert.True(well.Succeeded, well.Failure?.Message);

        // A redelivery of the same content goes back under the version the ledger holds, now citing the well's version.
        var reported = new List<(string Step, IReadOnlyDictionary<string, string> Values, int CallsSoFar)>();
        var again = await rig.Protocol.DeliverAsync(Work(Wellbore(), first.TargetVersion, first.Returned, reported: reported));
        Assert.True(again.Succeeded, again.Failure?.Message);
        Assert.Equal(first.TargetVersion, again.TargetVersion);
        Assert.Equal("in place", reported[0].Values["write"]);
        Assert.Equal([$"{WellId}:{well.TargetVersion}"], platform.WellDeliveryIndex[$"wellbore|wbdemo2|{first.TargetVersion}"]);
        Assert.Single(platform.WellDeliveryEntities.Keys, k => k.StartsWith("wellbore|", StringComparison.Ordinal));

        // Changed content takes a new version above the one the ledger holds.
        var changed = Wellbore();
        changed["data"]!["FacilityName"] = "Wellbore 2 renamed";
        var next = await rig.Protocol.DeliverAsync(Work(changed, again.TargetVersion, again.Returned));
        Assert.True(next.TargetVersion > again.TargetVersion);
    }

    [Fact]
    public async Task On_ibm_an_entity_is_never_written_again_under_the_same_version()
    {
        var platform = new FakeOsduPlatform { WellDeliveryCloudant = true };
        var clock = new TestClock();
        using var rig = new Rig(platform, new WellDeliverySettings { Provider = DdmsProvider.Ibm }, clock);

        var first = await rig.Protocol.DeliverAsync(Work(Well()));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var again = await rig.Protocol.DeliverAsync(Work(Well(), first.TargetVersion, first.Returned));
        Assert.True(again.Succeeded, again.Failure?.Message);
        Assert.True(again.TargetVersion > first.TargetVersion);
        Assert.Equal(2, platform.WellDeliveryEntities.Keys.Count(k => k.StartsWith("well|welldemo2|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_entity_the_ddms_stores_with_schema_findings_is_delivered_with_the_findings_kept()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var odd = Well();
        odd["data"]!["SchemaInvalid"] = true;

        var outcome = await rig.Protocol.DeliverAsync(Work(odd));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal("false", outcome.Returned["wellDelivery.valid"]);
        Assert.Contains("SchemaInvalid", outcome.Returned["wellDelivery.schemaFindings"], StringComparison.Ordinal);
        Assert.Contains("stored with the JSON-schema findings", outcome.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> Refused => new()
    {
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]},"data":{"FacilityName":"w"}}""", "data.ExistenceKind is not given" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]},"data":{"ExistenceKind":"opendes:reference-data--ExistenceKind:Planned"}}""", "is not in the reference form" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]},"data":{"ExistenceKind":"a:reference-data--ExistenceKind:Planned:","StartDateTime":"2021-05-18T10:00:00.000Z"}}""", "data.StartDateTime '2021-05-18T10:00:00.000Z' is in a form the Well Delivery DDMS cannot parse" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["data.default.owners"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]},"data":{"ExistenceKind":"a:reference-data--ExistenceKind:Planned:"}}""", "acl.owners names 'data.default.owners' without a domain" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":[],"otherRelevantDataCountries":["NO"]},"data":{"ExistenceKind":"a:reference-data--ExistenceKind:Planned:"}}""", "legal.legaltags is empty" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"]},"data":{"ExistenceKind":"a:reference-data--ExistenceKind:Planned:"}}""", "legal.otherRelevantDataCountries is empty" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]},"data":{"ExistenceKind":"a:reference-data--ExistenceKind:Planned:"},"meta":{"kind":"Unit"}}""", "meta is not an array of objects" },
        { WellId, """{"kind":"k:s:t:1.0.0","acl":{"owners":["o@x"],"viewers":["v@x"]},"legal":{"legaltags":["t"],"otherRelevantDataCountries":["NO"]}}""", "has no data object" },
        { "opendes:master-data--Well:a:b", """{"kind":"k:s:t:1.0.0"}""", "has characters other than letters, digits" },
        { "opendes:master-data--Well_Plan:x", """{"kind":"k:s:t:1.0.0"}""", "has characters the Well Delivery DDMS takes on a write and refuses on every read" },
        { "opendes-Well-x", """{"kind":"k:s:t:1.0.0"}""", "does not match the id the Well Delivery DDMS takes" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_rules_the_ddms_applies_are_checked_before_anything_is_sent(string id, string document, string expected)
    {
        var problem = WellDeliveryShape.RecordProblem(id, TestSchema.Doc(document));
        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_entity_is_held_with_nothing_sent_and_a_valid_one_passes_every_rule()
    {
        Assert.Null(WellDeliveryShape.RecordProblem(WellId, Well()));
        var dated = Well();
        dated["data"]!["StartDateTime"] = "2021-05-18T10:00:00Z";
        dated["data"]!["EndDateTime"] = "2021-05-18";
        Assert.Null(WellDeliveryShape.RecordProblem(WellId, dated));

        var tagged = Well();
        tagged["legal"]!["legaltags"] = new JsonArray(Enumerable.Range(0, 26).Select(i => (JsonNode?)("tag-" + i.ToString(CultureInfo.InvariantCulture))).ToArray());
        Assert.Contains("at most 25", WellDeliveryShape.RecordProblem(WellId, tagged), StringComparison.Ordinal);

        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(tagged)));
        Assert.Contains("legal tags", held.Message, StringComparison.Ordinal);

        var payload = Work(Well()) with { DeliverPayload = true, Payload = new MemoryFiles(("x.parquet", "x")) };
        var bulk = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(payload));
        Assert.Contains("which holds records alone and takes no bulk data", bulk.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task A_removal_takes_the_entity_and_its_storage_copy_at_the_same_scope()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var other = Entity("osdu:master-data--Well:w9", WellKind);
        var delivered = await rig.Protocol.DeliverAsync(Work(Well()));
        var foreign = await rig.Protocol.DeliverAsync(Work(other));

        var calls = platform.Calls.Count;
        var removed = await rig.Protocol.DeleteAsync(WellId, RemovalScope.Record, delivered.Returned);
        Assert.True(removed.Deleted);
        Assert.Equal($"removed from the Well Delivery DDMS (reversible: a write of the same version restores it), and its Storage copy {WellId} removed (reversible)", removed.Detail);
        Assert.Equal(["DELETE " + Entities + "well/welldemo2", "POST " + Storage + WellId + ":delete"], Sent(platform, calls));
        Assert.Equal("application/json", platform.Calls[calls].ContentType);
        Assert.Contains(WellId, platform.Removed);
        Assert.All(platform.WellDeliveryEntities.Where(e => e.Key.StartsWith("well|welldemo2|", StringComparison.Ordinal)), e => Assert.True(e.Value.Deleted));
        Assert.Equal(VerifyOutcome.Missing, (await rig.Protocol.VerifyAsync(WellId, delivered.TargetVersion)).Outcome);

        // The copy of an entity named under another namespace is stored under the partition.
        Assert.Equal("opendes:master-data--Well:w9", foreign.Returned[WellDeliveryShape.StorageIdKey]);
        calls = platform.Calls.Count;
        var purged = await rig.Protocol.DeleteAsync("osdu:master-data--Well:w9", RemovalScope.Everything, foreign.Returned);
        Assert.Equal("purged from the Well Delivery DDMS (every version), and its Storage copy opendes:master-data--Well:w9 purged", purged.Detail);
        Assert.Equal(["DELETE " + Entities + "well/w9:purge", "DELETE " + Storage + "opendes:master-data--Well:w9"], Sent(platform, calls));
        Assert.DoesNotContain(platform.WellDeliveryEntities.Keys, k => k.StartsWith("well|w9|", StringComparison.Ordinal));
        Assert.Contains("opendes:master-data--Well:w9", platform.Purged);

        var gone = await rig.Protocol.DeleteAsync("osdu:master-data--Well:w9", RemovalScope.Everything, foreign.Returned);
        Assert.True(gone.AlreadyGone);

        var history = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeleteAsync(WellId, RemovalScope.History));
        Assert.Contains("purging its earlier versions would cut those references", history.Message, StringComparison.Ordinal);

        OsduContracts.AssertConform(platform.Calls, InfoRoute, EntityIdExample, OsduContracts.WellDeliveryDdms, OsduContracts.Storage);
    }

    [Fact]
    public async Task A_deployment_without_a_storage_copy_is_removed_from_the_ddms_alone()
    {
        var platform = new FakeOsduPlatform { WellDeliveryMirror = false };
        using var rig = new Rig(platform, new WellDeliverySettings { Mirror = false });
        var delivered = await rig.Protocol.DeliverAsync(Work(Well()));
        Assert.False(delivered.Returned.ContainsKey(WellDeliveryShape.StorageIdKey));
        Assert.Empty(platform.Records);

        var calls = platform.Calls.Count;
        var removed = await rig.Protocol.DeleteAsync(WellId, RemovalScope.Record, delivered.Returned);
        Assert.Equal("removed from the Well Delivery DDMS (reversible: a write of the same version restores it)", removed.Detail);
        Assert.Equal(["DELETE " + Entities + "well/welldemo2"], Sent(platform, calls));
    }

    [Fact]
    public async Task Verify_reads_the_latest_version_and_the_probe_asks_for_the_version_information()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Well()));

        Assert.Equal(VerifyOutcome.Match, (await rig.Protocol.VerifyAsync(WellId, delivered.TargetVersion)).Outcome);
        Assert.Equal(VerifyOutcome.Drifted, (await rig.Protocol.VerifyAsync(WellId, delivered.TargetVersion - 1)).Outcome);
        Assert.Equal(WellId, (await rig.Protocol.ReadAsync(WellId))!["id"]!.GetValue<string>());

        var probe = await rig.Protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal(FakeOsduPlatform.WellDeliveryRoot + "/info", probe.Path);

        Assert.Equal(
            ["well-delivery-ddms GET /storage/v1/{type}/{id}", "well-delivery-ddms PUT /storage/v1/{type}"],
            OsduContracts.AssertConform(platform.Calls, InfoRoute, EntityIdExample, OsduContracts.WellDeliveryDdms));
    }
}
