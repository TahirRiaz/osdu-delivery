using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A partition's system properties: the settings its indexer and search service report for it, read by every capture of
/// its cache and kept with the version, apart from the cached records. A read that fails never fails the capture and never
/// changes what the cache knew; a version without them hashes as versions always did.
/// </summary>
public sealed class SystemPropertyTests : IDisposable
{
    private const string Scope = "dev";

    private readonly SqliteOsdu _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private static SystemProperty Flag(string name, SystemPropertyState state, string service = SystemProperties.Indexer, string? source = "dataPartition")
        => new(service, name, state, source, null);

    private static readonly SystemProperty KeywordLowerOn = Flag(SystemProperties.KeywordLower, SystemPropertyState.Enabled);

    private static ReferenceType Units(params string[] codes)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", codes.Select(c => ReferenceItem.FromText($"dev:reference-data--UnitOfMeasure:{c}", new Dictionary<string, string> { ["Code"] = c })));

    [Fact]
    public void A_service_that_answered_replaces_every_property_it_reported_before()
    {
        var current = new[] { Flag("featureFlag.bagOfWords.enabled", SystemPropertyState.Enabled), KeywordLowerOn };

        var merged = SystemProperties.Merge(current, [SystemPropertyReading.Read(SystemProperties.Indexer, [Flag(SystemProperties.KeywordLower, SystemPropertyState.Disabled)])]);

        // What the indexer no longer reports is gone, and what it reports now is what it says.
        Assert.Equal([Flag(SystemProperties.KeywordLower, SystemPropertyState.Disabled)], merged);
    }

    [Fact]
    public void A_service_that_could_not_be_asked_changes_nothing_the_cache_knew_of_it()
    {
        var current = new[] { KeywordLowerOn, Flag("featureFlag.policy.enabled", SystemPropertyState.Enabled, SystemProperties.Search) };

        var merged = SystemProperties.Merge(current,
        [
            SystemPropertyReading.Failed(SystemProperties.Indexer, "GET /api/indexer/v2/info failed: HTTP 503"),
            SystemPropertyReading.Read(SystemProperties.Search, [Flag("featureFlag.policy.enabled", SystemPropertyState.Disabled, SystemProperties.Search)]),
        ]);

        Assert.Equal(
            [KeywordLowerOn, Flag("featureFlag.policy.enabled", SystemPropertyState.Disabled, SystemProperties.Search)],
            merged);
    }

    [Theory]
    [InlineData(true, "the indexer could not be asked: HTTP 404")]
    [InlineData(false, "the indexer does not report it for the partition")]
    public void A_property_the_engine_reads_is_named_unknown_with_the_reason_when_no_service_said(bool failed, string detail)
    {
        var reading = failed
            ? SystemPropertyReading.Failed(SystemProperties.Indexer, "HTTP 404")
            : SystemPropertyReading.Read(SystemProperties.Indexer, []);

        var merged = SystemProperties.Merge([], [reading]);

        var keywordLower = Assert.Single(merged);
        Assert.Equal((SystemProperties.KeywordLower, SystemPropertyState.Unknown, detail), (keywordLower.Name, keywordLower.State, keywordLower.Detail));
    }

    [Fact]
    public void An_import_asks_no_platform_and_keeps_the_properties_exactly()
    {
        Assert.Empty(SystemProperties.Merge([], []));
        Assert.Equal([KeywordLowerOn], SystemProperties.Merge([KeywordLowerOn], []));
    }

    [Fact]
    public void An_info_document_gives_the_partitions_own_states_and_the_ones_it_names_no_partition_for()
    {
        var info = (JsonObject)JsonNode.Parse("""
            {
              "artifactId": "indexer",
              "featureFlagStates": [
                { "name": "featureFlag.keywordLower.enabled", "enabled": false, "partition": "other", "source": "dataPartition" },
                { "name": "featureFlag.keywordLower.enabled", "enabled": true, "partition": "dev", "source": "dataPartition" },
                { "name": "featureFlag.bagOfWords.enabled", "enabled": false, "source": "runtime" },
                { "name": "featureFlag.bagOfWords.enabled", "enabled": true, "partition": "dev", "source": "dataPartition" },
                { "name": "collaborations-enabled", "enabled": false },
                { "name": "featureFlag.asIngestedCoordinates.enabled", "partition": "dev" },
                { "enabled": true, "partition": "dev" }
              ]
            }
            """)!;

        var reading = SystemPropertyCapture.Parse(SystemProperties.Indexer, SystemPropertyCapture.IndexerInfoPath, "dev", info);

        Assert.Null(reading.Unread);
        Assert.Equal(
            [
                ("collaborations-enabled", SystemPropertyState.Disabled, (string?)null),
                ("featureFlag.asIngestedCoordinates.enabled", SystemPropertyState.Unknown, null),
                ("featureFlag.bagOfWords.enabled", SystemPropertyState.Enabled, "dataPartition"),
                (SystemProperties.KeywordLower, SystemPropertyState.Enabled, "dataPartition"),
            ],
            SystemProperties.Ordered(reading.Properties!).Select(p => (p.Name, p.State, p.Source)));
    }

    [Fact]
    public void A_service_that_publishes_no_feature_flags_is_a_reading_that_could_not_be_taken()
    {
        var reading = SystemPropertyCapture.Parse(SystemProperties.Search, SystemPropertyCapture.SearchInfoPath, "dev", new JsonObject { ["artifactId"] = "search" });

        Assert.Null(reading.Properties);
        Assert.Contains("reports no featureFlagStates", reading.Unread, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_without_system_properties_hashes_as_every_version_did_before_they_were_recorded()
    {
        var units = Units("m", "ft");
        var snapshot = new ReferenceSnapshot("v1", DateTimeOffset.UnixEpoch, [units]);

        // The hash every version written before system properties carried: the types alone, by name.
        var before = Hashing.ContentHash.Of(CanonicalJson.ToBytes(new JsonObject { [units.Name] = units.ToJson() }));

        Assert.Equal(before, snapshot.ContentHash());
    }

    [Fact]
    public void A_property_enters_the_hash_by_its_state_and_not_by_how_it_was_explained()
    {
        var units = Units("m");
        var on = new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [units], [KeywordLowerOn]);
        var explained = new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [units], [KeywordLowerOn with { Source = "runtime", Detail = "read again" }]);
        var off = new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [units], [KeywordLowerOn with { State = SystemPropertyState.Disabled }]);

        Assert.Equal(on.ContentHash(), explained.ContentHash());
        Assert.NotEqual(on.ContentHash(), off.ContentHash());
        Assert.NotEqual(new ReferenceSnapshot("v", DateTimeOffset.UnixEpoch, [units]).ContentHash(), on.ContentHash());
    }

    [Theory]
    [InlineData(SystemPropertyState.Enabled, SystemProperties.Indexer, true)]
    [InlineData(SystemPropertyState.Disabled, SystemProperties.Indexer, false)]
    [InlineData(SystemPropertyState.Unknown, SystemProperties.Indexer, false)]
    [InlineData(SystemPropertyState.Enabled, SystemProperties.Search, false)]
    public void Only_the_indexer_reporting_keyword_lower_on_turns_case_insensitive_lookups_on(SystemPropertyState state, string service, bool expected)
    {
        Assert.Equal(expected, SystemProperties.KeywordLowerOn([Flag(SystemProperties.KeywordLower, state, service)]));
        Assert.Equal(expected, Context(SystemProperties.Pinned([Flag(SystemProperties.KeywordLower, state, service)])).KeywordLower);
    }

    [Fact]
    public void A_render_is_pinned_to_the_state_of_every_property_the_engine_reads_and_nothing_else()
    {
        var pinned = SystemProperties.Pinned([KeywordLowerOn with { Detail = "read at the last capture" }, Flag("featureFlag.bagOfWords.enabled", SystemPropertyState.Enabled)]);

        Assert.Equal([new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, SystemPropertyState.Enabled, null, null)], pinned);

        // A partition no capture has asked is pinned to unknown, which asks exact questions alone.
        Assert.Equal([new SystemProperty(SystemProperties.Indexer, SystemProperties.KeywordLower, SystemPropertyState.Unknown, null, null)], SystemProperties.Pinned([]));
    }

    [Fact]
    public void A_context_that_pins_system_properties_carries_them_through_its_canonical_form()
    {
        var context = Context(SystemProperties.Pinned([KeywordLowerOn]));

        var canonical = context.Canonical();

        Assert.Contains("\"systemProperties\":{\"indexer\":{\"featureFlag.keywordLower.enabled\":\"Enabled\"}}", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, RenderContext.Parse(canonical).Canonical());
        Assert.True(RenderContext.Parse(canonical).KeywordLower);
        Assert.NotEqual(canonical, Context(SystemProperties.Pinned([KeywordLowerOn with { State = SystemPropertyState.Disabled }])).Canonical());
    }

    [Fact]
    public void A_context_that_pins_none_is_the_context_every_mapping_without_searches_always_had()
    {
        // The canonical form before system properties existed, which every record delivered since holds in the ledger.
        const string Before = """{"cacheVersion":"none","mapping":"M@1","parameters":{"dataPartition":"dev"},"schema":"s"}""";

        Assert.Equal(Before, Context([]).Canonical());
    }

    [Theory]
    [InlineData("""{"indexer":{"featureFlag.keywordLower.enabled":"1"}}""")]
    [InlineData("""{"indexer":{"featureFlag.keywordLower.enabled":"enabled"}}""")]
    [InlineData("""{"indexer":{"featureFlag.keywordLower.enabled":true}}""")]
    [InlineData("""{"indexer":["featureFlag.keywordLower.enabled"]}""")]
    [InlineData("""["indexer"]""")]
    public void A_context_whose_pinned_state_is_not_one_the_engine_writes_is_refused(string properties)
    {
        var canonical = """{"cacheVersion":"none","mapping":"M@1","parameters":{},"schema":"s","systemProperties":""" + properties + "}";

        Assert.Throws<DeliveryException>(() => RenderContext.Parse(canonical));
    }

    private static RenderContext Context(IReadOnlyList<SystemProperty> pinned) => new()
    {
        MappingReference = "M@1",
        CacheVersion = ReferenceSnapshot.Empty.Version,
        SchemaSnapshotVersion = "s",
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
        SystemProperties = pinned,
    };

    private static bool KeywordLowerOf(ReferenceSnapshot snapshot) => SystemProperties.KeywordLowerOn(snapshot.SystemProperties);

    [Fact]
    public async Task The_properties_are_written_with_the_version_and_read_back_apart_from_the_records()
    {
        var store = _catalog.Caches();
        var readings = new[]
        {
            SystemPropertyReading.Read(SystemProperties.Indexer, [KeywordLowerOn, Flag("custom-index-analyzer-enabled", SystemPropertyState.Disabled)]),
            SystemPropertyReading.Failed(SystemProperties.Search, "GET /api/search/v2/info failed: HTTP 503"),
        };

        var write = await store.MergeAsync(Scope, "units", [Units("m")], new CacheCapture(null, "tests", "osdu"), DateTimeOffset.UnixEpoch, readings);

        var loaded = (await store.LoadAsync(Scope, write.Snapshot.Version))!;
        Assert.True(KeywordLowerOf(loaded));
        Assert.Equal(
            ["custom-index-analyzer-enabled", SystemProperties.KeywordLower],
            loaded.SystemProperties.Select(p => p.Name));
        Assert.Equal(["UnitOfMeasure"], loaded.Types.Select(t => t.Name));
        Assert.Equal(loaded.SystemProperties, Assert.Single(await store.ListVersionsAsync(Scope)).SystemProperties);

        // One version's row answers without its records: the current one, or one by name.
        Assert.Equal(loaded.SystemProperties, (await store.VersionAsync(Scope, null))!.SystemProperties);
        Assert.Equal(loaded.SystemProperties, (await store.VersionAsync(Scope, write.Snapshot.Version))!.SystemProperties);
        Assert.Null(await store.VersionAsync(Scope, "20000101T000000Z"));
        Assert.Null(await store.VersionAsync("elsewhere", null));
    }

    [Fact]
    public async Task A_read_that_fails_after_one_that_succeeded_writes_no_version()
    {
        var store = _catalog.Caches();
        var capture = new CacheCapture(null, "tests", "osdu");
        var first = await store.MergeAsync(Scope, "units", [Units("m")], capture, DateTimeOffset.UnixEpoch, [SystemPropertyReading.Read(SystemProperties.Indexer, [KeywordLowerOn])]);

        var again = await store.MergeAsync(
            Scope, "units", [Units("m")], capture, DateTimeOffset.UnixEpoch.AddHours(1), [SystemPropertyReading.Failed(SystemProperties.Indexer, "timed out")]);

        // One request for the settings failing says nothing new about them, so nothing renders again for it.
        Assert.False(again.Written);
        Assert.Equal(first.Snapshot.Version, again.Snapshot.Version);
        Assert.True(KeywordLowerOf((await store.LoadAsync(Scope, again.Snapshot.Version))!));
    }

    [Fact]
    public async Task A_property_the_platform_changed_writes_a_new_version()
    {
        var store = _catalog.Caches();
        var capture = new CacheCapture(null, "tests", "osdu");
        await store.MergeAsync(Scope, "units", [Units("m")], capture, DateTimeOffset.UnixEpoch, [SystemPropertyReading.Read(SystemProperties.Indexer, [KeywordLowerOn])]);

        var changed = await store.MergeAsync(
            Scope, "units", [Units("m")], capture, DateTimeOffset.UnixEpoch.AddHours(1),
            [SystemPropertyReading.Read(SystemProperties.Indexer, [Flag(SystemProperties.KeywordLower, SystemPropertyState.Disabled)])]);

        Assert.True(changed.Written);
        Assert.False(KeywordLowerOf((await store.LoadAsync(Scope, changed.Snapshot.Version))!));
    }

    [Fact]
    public async Task An_import_onto_a_captured_cache_keeps_its_properties()
    {
        var store = _catalog.Caches();
        var capture = new CacheCapture(null, "tests", "osdu");
        await store.MergeAsync(Scope, "units", [Units("m")], capture, DateTimeOffset.UnixEpoch, [SystemPropertyReading.Read(SystemProperties.Indexer, [KeywordLowerOn])]);

        var imported = await store.MergeAsync(Scope, "units", [Units("m", "ft")], new CacheCapture(null, "cli:tests", "files"), DateTimeOffset.UnixEpoch.AddHours(1));

        Assert.True(imported.Written);
        Assert.True(KeywordLowerOf((await store.LoadAsync(Scope, imported.Snapshot.Version))!));
    }

    [Fact]
    public async Task A_capture_reads_the_platforms_settings_and_a_service_that_cannot_answer_does_not_fail_it()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/query_with_cursor", HttpStatusCode.OK, """{"results":[{"id":"dev:reference-data--UnitOfMeasure:m","data":{"Code":"m"}}]}""")
            .On(HttpMethod.Get, "/api/indexer/v2/info", HttpStatusCode.OK, """
                {"artifactId":"indexer","featureFlagStates":[
                  {"name":"featureFlag.keywordLower.enabled","enabled":true,"partition":"dev","source":"dataPartition"},
                  {"name":"featureFlag.bagOfWords.enabled","enabled":false,"partition":"dev","source":"dataPartition"}]}
                """)
            .On(HttpMethod.Get, "/api/search/v2/info", HttpStatusCode.ServiceUnavailable, """{"message":"busy"}""");
        using var osdu = await OsduConnection.CreateAsync(
            "http://localhost/osdu",
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = Scope },
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]),
            handler,
            allowLoopback: true);
        var units = new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure",
            EntityType = "reference-data--UnitOfMeasure",
            Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
            Fields = [new ReferenceFieldSpec("data.Code")],
        };
        var builder = new SnapshotBuilder(_catalog.Caches(), Scope, "units", new TestClock(), Samples.Logger<SnapshotBuilder>());

        var write = await builder.CaptureAsync(osdu, new ReferenceCaptureSpec { Types = [units] }, new CacheCapture(null, "tests", "osdu"));

        Assert.True(write.Written);
        Assert.True(KeywordLowerOf(write.Snapshot));
        Assert.Equal(
            [("featureFlag.bagOfWords.enabled", SystemPropertyState.Disabled), (SystemProperties.KeywordLower, SystemPropertyState.Enabled)],
            write.Snapshot.SystemProperties.Select(p => (p.Name, p.State)));

        // Both info endpoints were asked with the partition, as the OpenAPI documents have it.
        Assert.All(
            handler.Calls.Where(c => c.Method == HttpMethod.Get),
            c => Assert.Equal(Scope, c.Headers[CacheScope.PartitionHeader]));
        Assert.Equal(2, handler.Calls.Count(c => c.Method == HttpMethod.Get));
    }
}
