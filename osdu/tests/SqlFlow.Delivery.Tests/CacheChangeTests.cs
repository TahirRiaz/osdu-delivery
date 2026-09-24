using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What happens to delivered records when the cache moves under them: the dependency set a render leaves behind,
/// the changes a new cache version raises against it, the approval gate, and the batched rollout that carries an
/// approved change out without taking the estate with it.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class CacheChangeTests : IDisposable
{
    /// <summary>The partition whose cache the records under test were built from.</summary>
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");
    private readonly OsduLedger _ledger;

    public CacheChangeTests() => _ledger = _db.Ledger(_clock);

    private OsduLedger Ledger => _ledger;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    private static ReferenceType Units(string metreName, params string[] extraAliases) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of(metreName),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse($"""["metre"{(extraAliases.Length > 0 ? ", " + string.Join(", ", extraAliases.Select(a => $"\"{a}\"")) : string.Empty)}]""")!),
            }),
        ]);

    private static CacheUsage Reads(string path, string value, CacheUsageKind kind = CacheUsageKind.Value)
        => new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", path, value, kind);

    /// <summary>Delivers <paramref name="count"/> records that all read the same cached values.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(int count, params CacheUsage[] usages) => DeliveredAsync("WELL", count, usages);

    /// <summary>Delivers <paramref name="count"/> records keyed under <paramref name="prefix"/> that all read the same cached values.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(string prefix, int count, params CacheUsage[] usages) => DeliveredAsync(Scope, prefix, count, usages);

    /// <summary>Delivers <paramref name="count"/> records keyed under <paramref name="prefix"/> that all read the same values of the cache of partition <paramref name="scope"/>.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(string scope, string prefix, int count, params CacheUsage[] usages)
        => DeliveredAsync(_flow, "test-flow", "x", scope, prefix, count, usages);

    /// <summary>
    /// Delivers <paramref name="count"/> records of the flow <paramref name="flowName"/>, keyed under <paramref name="prefix"/>
    /// and written to OSDU ids of the entity type <paramref name="entityType"/>, that all read the same cached values.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(
        Guid flow, string flowName, string entityType, string scope, string prefix, int count, params CacheUsage[] usages)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = flow,
            FlowName = flowName,
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = Now,
        });

        var setId = usages.Length == 0 ? (long?)null : await Ledger.EnsureCacheSetAsync(scope, usages);
        var keys = new List<DeliveryKey>();
        var records = new List<RecordState>();
        for (var i = 0; i < count; i++)
        {
            var key = DeliveryKey.Derive("test", [$"{prefix}-{i}"]);
            keys.Add(key);
            records.Add(new RecordState
            {
                DeliveryKey = key,
                FlowId = flow,
                SourceKey = $"{prefix}-{i}",
                MappingName = "Thing",
                TargetId = $"dev:{entityType}:{prefix}-{i}",
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
                CacheSetId = setId,
            });
        }

        await Ledger.UpsertPendingAsync(flow, records);
        return keys;
    }

    private Task<CacheImpactResult> AnalyzeAsync(ReferenceType? previous, ReferenceType current, CacheChangeMode mode, string scope = Scope)
        => new CacheImpactAnalyzer(Ledger, _clock, NullLogger.Instance).AnalyzeAsync(scope, previous, current, mode, "v1", "v2");

    [Fact]
    public async Task A_change_to_one_partition_s_cache_never_reaches_records_built_from_another_partition_s_cache()
    {
        // Every partition's cache can hold a UnitOfMeasure type; a set is a set of one partition's cached values, and a
        // refresh of another partition's cache finds nothing built from it.
        await DeliveredAsync("other-partition", "OTHER", 2, Reads("Name", "metre"));
        var elsewhere = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(0, elsewhere.AffectedRecords);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());

        var there = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve, scope: "other-partition");
        Assert.Equal(2, there.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("other-partition", tag.Scope);

        // The cache page lists one partition's changes: the other partition has none to show or count.
        Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0, "other-partition"));
        Assert.Equal(1, await Ledger.CountTagsAsync("pending", "other-partition"));
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0, Scope));
        Assert.Equal(0, await Ledger.CountTagsAsync(null, Scope));
    }

    [Fact]
    public void A_render_records_what_it_read_out_of_the_cache()
    {
        var mapping = TestSchema.Mapping("""
              - target: osdu.data.Unit
                source: cache.UnitOfMeasure.id
                findBy: cache.UnitOfMeasure.Code = dataset.unit
              - target: osdu.data.Symbol
                source: cache.UnitOfMeasure.Name
                findBy: cache.UnitOfMeasure.Code = dataset.unit
            """);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), new ReferenceSnapshot("refs-1", DateTimeOffset.UnixEpoch, [Units("metre")]), TestSchema.Context());

        var result = renderer.Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "1", ["unit"] = "m" }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
        });

        Assert.False(result.IsHeld);

        // What it matched by, and what it took: both are dependencies, for different reasons.
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Match && u.Path == "Code" && u.Value == "m");
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Value && u.Path == "id");
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Value && u.Path == "Name" && u.Value == "metre");
    }

    [Fact]
    public async Task Records_reading_the_same_values_share_one_dependency_set()
    {
        var keys = await DeliveredAsync(3, Reads("Name", "metre"));
        var first = await Ledger.GetRecordAsync(_flow, keys[0]);
        var last = await Ledger.GetRecordAsync(_flow, keys[^1]);

        Assert.NotNull(first!.CacheSetId);
        Assert.Equal(first.CacheSetId, last!.CacheSetId);

        // The same values from a second run resolve to the same set, so the trail never grows with the estate.
        var again = await Ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        Assert.Equal(first.CacheSetId, again);

        var entries = await Ledger.ListCacheSetAsync(again);
        var entry = Assert.Single(entries);
        Assert.Equal("Name", entry.Path);
        Assert.Equal("metre", entry.ValueText);
    }

    [Fact]
    public async Task A_changed_value_raises_one_change_covering_every_record_built_from_it()
    {
        await DeliveredAsync(5, Reads("Name", "metre"));

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(5, impact.AffectedRecords);

        // One decision, not five: the tag names the value that moved and how far it reaches.
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("changed", tag.Change);
        Assert.Equal("metre", tag.OldValue);
        Assert.Equal("meter", tag.NewValue);
        Assert.Equal(5, tag.AffectedRecords);
        Assert.Contains("Name changed from 'metre' to 'meter'", tag.Describe(), StringComparison.Ordinal);
        Assert.True(tag.WaitsForApproval);

        // The gate is on the set, so holding five million records back would cost the same as holding five.
        Assert.Equal(tag.SetIds, await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_change_names_the_value_the_replaced_version_held_not_one_a_set_left_from_an_earlier_render_holds()
    {
        // Seen live: a set built before the last change still holds the value before last, and the tag named that one.
        await Ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        await DeliveredAsync(2, Reads("Name", "meter"));

        var impact = await AnalyzeAsync(Units("meter"), Units("metres"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("meter", tag.OldValue);
        Assert.Contains("Name changed from 'meter' to 'metres'", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_set_is_judged_by_the_value_it_holds_so_one_already_holding_the_new_value_hides_no_other()
    {
        // Records rendered against two versions of the cache: one set already holds the new value, the other still holds
        // the old one. Only the second is touched, and it is not missed because the lookup lists the first ahead of it.
        await DeliveredAsync("NEWER", 3, Reads("Name", "meter"));
        var older = await DeliveredAsync("OLDER", 2, Reads("Name", "metre"));
        var olderSet = (await Ledger.GetRecordAsync(_flow, older[0]))!.CacheSetId!.Value;

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("metre", tag.OldValue);
        Assert.Equal(2, tag.AffectedRecords);
        Assert.Equal(olderSet, Assert.Single(tag.SetIds));
        Assert.Equal(olderSet, Assert.Single(await Ledger.GatedCacheSetsAsync()));
    }

    [Fact]
    public async Task A_value_no_record_read_changes_nothing()
    {
        await DeliveredAsync(2, Reads("id", "dev:reference-data--UnitOfMeasure:m"));

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(0, impact.Changes);
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_matched_value_that_survives_under_another_alias_is_not_tagged()
    {
        await DeliveredAsync(1, Reads("Alias", "metre", CacheUsageKind.Match));

        var impact = await AnalyzeAsync(Units("metre"), Units("metre", "meter"), CacheChangeMode.Approve);
        Assert.Equal(0, impact.Changes);
    }

    [Fact]
    public async Task A_match_that_no_longer_resolves_is_tagged_unmatched()
    {
        await DeliveredAsync(1, Reads("Alias", "metre", CacheUsageKind.Match));

        var current = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of("metre"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["meter"]""")!),
            }),
        ]);

        await AnalyzeAsync(Units("metre"), current, CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("unmatched", tag.Change);
        Assert.Contains("no longer matches", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cached_record_that_disappears_is_tagged_removed()
    {
        await DeliveredAsync(1, Reads("Name", "metre"));

        await AnalyzeAsync(Units("metre"), new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", []), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("removed", tag.Change);
        Assert.Null(tag.NewValue);
    }

    [Fact]
    public async Task Auto_approves_as_it_is_written_and_gates_nothing()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);

        var tag = Assert.Single(await Ledger.ListTagsAsync("approved", 10, 0));
        Assert.Equal("auto", tag.Mode);
        Assert.Equal("system:auto", tag.DecidedBy);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task Approving_releases_the_gate_and_rejecting_leaves_osdu_alone()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));

        Assert.Equal(1, await Ledger.DecideTagsAsync([tag.TagId], approve: true, "tahir", Now));
        Assert.Equal("tahir", Assert.Single(await Ledger.ListTagsAsync("approved", 10, 0)).DecidedBy);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());

        // A second change, rejected this time: the gate opens again and OSDU keeps what it has.
        await AnalyzeAsync(Units("meter"), Units("metres"), CacheChangeMode.Approve);
        var second = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(1, await Ledger.DecideTagsAsync([second.TagId], approve: false, "tahir", Now));
        Assert.Single(await Ledger.ListTagsAsync("rejected", 10, 0));
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_value_that_moves_again_after_approval_reopens_the_question()
    {
        await DeliveredAsync(1, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        await Ledger.DecideTagsAsync([tag.TagId], approve: true, "tahir", Now);

        await AnalyzeAsync(Units("metre"), Units("metres"), CacheChangeMode.Approve);
        var reopened = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(tag.TagId, reopened.TagId);
        Assert.Equal("metres", reopened.NewValue);
        Assert.Null(reopened.DecidedBy);
        Assert.NotEmpty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task An_approved_change_rolls_out_in_batches_and_resumes_where_it_stopped()
    {
        var keys = await DeliveredAsync(5, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(5, tag.AffectedRecords);

        // Two at a time: the pass is bounded, and what it marked is metadata only.
        var first = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(2, first.Marked);
        Assert.False(first.Completed);
        Assert.Equal("rolling", Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0)).Status);

        var second = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(2, second.Marked);
        Assert.Equal(4, second.Processed);

        var third = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(1, third.Marked);
        Assert.True(third.Completed);
        Assert.Equal(5, third.Processed);

        // Every record is now due for a metadata redelivery, and no payload was touched.
        foreach (var key in keys)
        {
            var state = await Ledger.GetRecordAsync(_flow, key);
            Assert.Null(state!.MetadataHash);
            Assert.Contains("cache change", state.LastError!, StringComparison.Ordinal);
        }

        // Nothing is left to do, and a further pass is a no-op rather than a rescan.
        Assert.Empty(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(0, (await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now)).Marked);
    }

    [Fact]
    public async Task A_rollout_marks_every_flow_s_record_of_a_shared_row_and_resumes_between_them()
    {
        // Two flows read the same rows into different OSDU kinds, and both read the metre's name: each flow's records are
        // its own, and the rollout reaches all of them, even when a batch ends between two flows' records of one key.
        var other = FlowId.Of("test-flow-wellbores");
        var keys = await DeliveredAsync(_flow, "test-flow", "x", Scope, "WELL", 2, Reads("Name", "metre"));
        var same = await DeliveredAsync(other, "test-flow-wellbores", "y", Scope, "WELL", 2, Reads("Name", "metre"));
        Assert.Equal(keys, same);
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(4, tag.AffectedRecords);

        var marked = 0L;
        for (var pass = 0; pass < 4; pass++)
        {
            var batch = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 1, Now);
            Assert.Equal(1, batch.Marked);
            marked += batch.Marked;
        }

        Assert.Equal(4, marked);
        Assert.True((await Ledger.RollOutTagAsync(tag.TagId, batchSize: 1, Now)).Completed);
        foreach (var flow in new[] { _flow, other })
        {
            foreach (var key in keys)
            {
                var state = await Ledger.GetRecordAsync(flow, key);
                Assert.Contains("cache change", state!.LastError!, StringComparison.Ordinal);
                Assert.NotNull(state.PlanRequestedUtc);
            }
        }
    }

    [Fact]
    public async Task A_rejected_change_is_never_rolled_out()
    {
        var keys = await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        await Ledger.DecideTagsAsync([tag.TagId], approve: false, "tahir", Now);

        Assert.Empty(await Ledger.ListRolloutQueueAsync(10));
        var batch = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 10, Now);
        Assert.Equal(0, batch.Marked);

        // Nothing was marked for redelivery, so no record carries a cache-change note.
        foreach (var key in keys)
        {
            var state = await Ledger.GetRecordAsync(_flow, key);
            Assert.DoesNotContain("cache change", state!.LastError ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
