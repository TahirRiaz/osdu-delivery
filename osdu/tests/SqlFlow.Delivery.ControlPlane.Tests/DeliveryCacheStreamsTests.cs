using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A partition's cache as its streams, read from what the catalog already holds (the cache page's Streams tab): lineage
/// walked upstream from each cached type to where its content starts, the delivery flows that read it, each cache flow's
/// last refresh in the partition read from its runs' results, and the notices a version that shrank or dropped a type, a
/// failed load, a type no cache flow fills and changes that wait deserve. Everything is seeded under names of its own.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryCacheStreamsTests
{
    private const string Server = "${env:osdu_data_db}";

    [Fact]
    public async Task A_partition_s_cache_reads_as_streams_from_the_files_to_the_readers_with_what_needs_attention()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var s = Guid.NewGuid().ToString("N")[..10];
        var partition = "s" + s;
        var repoId = FlowIdentity.FromName("repo/cache-streams-" + s);
        var (pre, ing, lookups, reference, delivery) = ($"st-pre-{s}", $"st-ing-{s}", $"st-lookups-{s}", $"st-reference-{s}", $"st-delivery-{s}");
        var header = $"st-header-{s}";
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        var ids = new Dictionary<string, Guid>();
        var scheduleId = Guid.NewGuid();
        var (lookupsRun, referenceRun) = (Guid.NewGuid(), Guid.NewGuid());

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cache-streams-" + s, RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now });
            foreach (var (name, kind, path) in new[]
            {
                (pre, "file", "stream/cache/pre.yaml"), (ing, "ing", "stream/cache/ing.yaml"), (lookups, "cache", "stream/cache/lookups.yaml"),
                (reference, "cache", "shared/cache/reference.yaml"), (delivery, "delivery", "stream/flows/delivery.yaml"),
                (header, "cache", "stream/cache/header.yaml"),
            })
            {
                ids[name] = CatalogIdentity.Pipeline(repoId, name);
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = ids[name], RepoId = repoId, Name = name, Kind = kind, RelativePath = path, ContentHash = new string('0', 64), Yaml = "{}",
                    DefinitionJson = "{}", Active = true, FirstSeenUtc = now, LastSeenUtc = now,
                });
            }

            // The chain as lineage records it: the pre flow lands a file, the ing flow keys it into a table, the lookups
            // cache flow captures that table into the partition's cache, the reference cache flow searches OSDU, and the
            // delivery flow reads both types and a third that nothing fills.
            foreach (var (flow, relation, key) in new[]
            {
                (pre, "Reads", "file|||stream/cache/data/units"),
                (pre, "Writes", $"{Server}|osdudata|pre|units_{s}"),
                (ing, "Reads", $"{Server}|osdudata|pre|units_{s}"),
                (ing, "Writes", $"{Server}|osdudata|arc|units_{s}"),
                (lookups, "Reads", $"{Server}|osdudata|arc|units_{s}"),
                (lookups, "Writes", $"dataset:osdu-cache|{partition}|cache|units"),
                (reference, "Reads", $"dataset:osdu-type:${{env:osdu_url}}|{partition}|reference-data|osdu:wks:reference-data--unitofmeasure:*"),
                (reference, "Writes", $"dataset:osdu-cache|{partition}|cache|unitofmeasure"),
                (delivery, "Reads", $"dataset:osdu-cache|{partition}|cache|units"),
                (delivery, "Reads", $"dataset:osdu-cache|{partition}|cache|unitofmeasure"),
                (delivery, "Reads", $"dataset:osdu-cache|{partition}|cache|logcurvefamily"),

                // A cache flow that still names its partition in its header: lineage keys its node by the reference as written,
                // and a delivery flow written the same way reads it.
                (header, "Writes", "dataset:osdu-cache|${env:osdu_data_partition}|cache|curvedictionary"),
                (delivery, "Reads", "dataset:osdu-cache|${env:osdu_data_partition}|cache|curvedictionary"),

                // The reference flow also captures the wellbores the delivery flow delivers, at the one version it writes, so
                // that type's content passes through the delivery flow. The delivery flow reads its table and the mapping it
                // renders with, which is a node of its own.
                (reference, "Reads", $"dataset:osdu-type:${{env:osdu_url}}|{partition}|master-data|osdu:wks:master-data--wellbore:1.3.0"),
                (reference, "Writes", $"dataset:osdu-cache|{partition}|cache|wellbore"),
                (delivery, "Writes", $"dataset:osdu-type:${{env:osdu_url}}|{partition}|master-data|osdu:wks:master-data--wellbore:1.3.0"),
                (delivery, "Reads", $"{Server}|osdudata|arc|wellbore_{s}"),
                (delivery, "Reads", $"dataset:osdu-mapping:${{env:osdu_url}}|{partition}|stream/mappings|wellbore@1.0.0"),
            })
            {
                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId, Flow = flow, PipelineId = ids[flow], Relation = relation, ObjectKey = key, ObjectName = key.Split('|')[^1], Tier = "Declared",
                });
            }

            // The pre flow's last run failed; the lookups cache flow wrote the current version; the reference cache flow,
            // refreshing every partition it names, found nothing to change after a refresh that failed, and a later run of
            // the lookups flow reached another partition only.
            db.Runs.AddRange(
                Run(ids[pre], repoId, pre, "file", now.AddHours(-3), RunStatuses.Failed, error: "The folder holds no file matching units_*.csv."),
                Run(ids[ing], repoId, ing, "ing", now.AddHours(-4), RunStatuses.Succeeded),
                Run(ids[lookups], repoId, lookups, "cache", now.AddHours(-2), RunStatuses.Succeeded, runId: lookupsRun,
                    result: $$"""{"operation":"refresh","scope":"{{partition}}","flow":"{{lookups}}","version":"v2-{{s}}","written":true}"""),
                Run(ids[lookups], repoId, lookups, "cache", now.AddHours(-1), RunStatuses.Succeeded,
                    result: $$"""{"operation":"refresh","scope":"other-{{s}}","flow":"{{lookups}}","version":"x","written":true}"""),
                Run(ids[reference], repoId, reference, "cache", now.AddHours(-6), RunStatuses.Failed,
                    result: $$"""{"operation":"refresh","flow":"{{reference}}","partitions":[{"partition":"{{partition}}","outcome":null,"error":"OSDU answered 401."}]}"""),
                Run(ids[reference], repoId, reference, "cache", now.AddMinutes(-30), RunStatuses.Succeeded, runId: referenceRun,
                    result: $$"""{"operation":"refresh","flow":"{{reference}}","partitions":[{"partition":"{{partition}}","outcome":{"written":false,"version":"v2-{{s}}"},"error":null}]}"""));

            db.Schedules.Add(new CatalogSchedule { Id = scheduleId, RepoId = repoId, Name = "st-sched-" + s, Cron = "30 23 * * *" });
            db.ScheduleMembers.Add(new CatalogScheduleMember { ScheduleId = scheduleId, PipelineId = ids[lookups], RepoId = repoId, FlowName = lookups });
            await db.SaveChangesAsync();
        }

        await using (var osdu = SampleEstate.Context(cs))
        {
            osdu.DeliveryCacheDefinitions.AddRange(
                new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(), RepoId = repoId, FlowName = lookups, Scope = partition, DeclaresPartitions = true, Origin = "table",
                    Connection = "${env:OSDU_DATA_DB}", SourceObject = $"OsduData.arc.units_{s}", KeyField = "source_unit", RelativePath = "stream/cache/lookups.yaml",
                    Name = "Units", EntityType = "lookup--Units", FieldsJson = "[]", FirstSeenUtc = now, LastSeenUtc = now,
                },
                new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(), RepoId = repoId, FlowName = header, Scope = partition, DeclaresPartitions = false, Origin = "dictionary",
                    DictionaryPath = "stream/dictionaries/CurveDictionary.yaml", RelativePath = "stream/cache/header.yaml",
                    Name = "CurveDictionary", EntityType = "lookup--CurveDictionary", FieldsJson = "[]", FirstSeenUtc = now, LastSeenUtc = now,
                },
                new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(), RepoId = repoId, FlowName = reference, Scope = partition, DeclaresPartitions = true, Origin = "osdu",
                    Endpoint = "${env:OSDU_URL}", Kind = "osdu:wks:reference-data--UnitOfMeasure:*", RelativePath = "shared/cache/reference.yaml",
                    Name = "UnitOfMeasure", EntityType = "reference-data--UnitOfMeasure", FieldsJson = "[]", FirstSeenUtc = now, LastSeenUtc = now,
                },
                new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(), RepoId = repoId, FlowName = reference, Scope = partition, DeclaresPartitions = true, Origin = "osdu",
                    Endpoint = "${env:OSDU_URL}", Kind = "osdu:wks:master-data--Wellbore:1.3.0", RelativePath = "shared/cache/reference.yaml",
                    Name = "Wellbore", EntityType = "master-data--Wellbore", FieldsJson = "[]", FirstSeenUtc = now, LastSeenUtc = now,
                });

            // The version before held a type the current one dropped, and four times the units it holds now.
            osdu.DeliveryCacheVersions.AddRange(
                Version(partition, $"v1-{s}", 1, null, current: false, now.AddDays(-1),
                    """[{"name":"LogType","entityType":"reference-data--LogType","items":12},{"name":"UnitOfMeasure","entityType":"reference-data--UnitOfMeasure","items":100},{"name":"Units","entityType":"lookup--Units","items":40}]"""),
                Version(partition, $"v2-{s}", 2, $"v1-{s}", current: true, now.AddHours(-2),
                    """[{"name":"UnitOfMeasure","entityType":"reference-data--UnitOfMeasure","items":100},{"name":"Units","entityType":"lookup--Units","items":10}]"""));
            osdu.DeliveryInterfaces.Add(new DeliveryInterface
            {
                Id = Guid.NewGuid(), RepoId = repoId, FlowName = delivery, Interface = "", Partition = partition, LedgerFlowId = Guid.NewGuid(),
                LedgerName = delivery + "@" + partition, Route = "storage", MappingReference = "WellLog@1.4.0", RecordObject = "OsduData.arc.WellLog",
                RelativePath = "stream/flows/delivery.yaml", FirstSeenUtc = now, LastSeenUtc = now,
            });
            osdu.DeliveryUpdateTags.Add(new DeliveryUpdateTag
            {
                Scope = partition, TypeName = "Units", ItemId = "GAPI", Path = "osdu_unit", OldValue = "\"API\"", NewValue = "\"gAPI\"", FromVersion = $"v1-{s}",
                ToVersion = $"v2-{s}", DetectedUtc = now,
            });
            await osdu.SaveChangesAsync();
        }

        try
        {
            DeliveryCacheStreamsDto streams;
            await using (var db = CatalogDatabase.Create(cs))
            await using (var osdu = SampleEstate.Context(cs))
            {
                streams = await DeliveryCacheStreams.DescribeAsync(db, osdu, partition, CancellationToken.None);
            }

            Assert.Equal((partition, $"v2-{s}", $"v1-{s}"), (streams.Partition, streams.CurrentVersion, streams.PreviousVersion));
            Assert.Equal(["CurveDictionary", "UnitOfMeasure", "Units", "Wellbore"], streams.Types.Select(t => t.Type));

            // The header flow's type is found through the node it writes, however it spells the partition, and starts at its
            // dictionary; the version does not hold it yet.
            var dictionary = streams.Types.Single(t => t.Type == "CurveDictionary");
            Assert.Equal(delivery, Assert.Single(dictionary.Readers).Flow);
            Assert.Equal([new DeliveryStreamInputDto("dictionary", "stream/dictionaries/CurveDictionary.yaml")], dictionary.Origins);
            Assert.Equal("missing", dictionary.State);

            // The table type: every flow from the file to the cache flow, upstream first, with each flow's last run; the
            // failed load makes the type failed, and says why.
            var units = streams.Types.Single(t => t.Type == "Units");
            Assert.Equal([(pre, 2), (ing, 1), (lookups, 0)], units.Stages.Select(st => (st.Flow, st.Depth)));
            Assert.Equal(["stream"], units.Projects);
            Assert.Equal([new DeliveryStreamInputDto("file", "stream/cache/data/units")], units.Origins);
            Assert.Equal(RunStatuses.Failed, units.Stages[0].LastRun!.Status);
            Assert.Equal("st-sched-" + s, Assert.Single(units.Stages[2].Schedules).Name);
            Assert.Equal((10L, 40L), (units.Items!.Value, units.PreviousItems!.Value));
            Assert.Equal("failed", units.State);
            Assert.Contains($"The last run of {pre}", units.StateReason, StringComparison.Ordinal);
            var reader = Assert.Single(units.Readers);
            Assert.Equal((delivery, ids[delivery], "stream"), (reader.Flow, reader.PipelineId, reader.Project));
            Assert.Equal(["WellLog@1.4.0"], reader.Mappings);

            // The OSDU type starts at the kind the cache flow searches, and its newest refresh, which changed nothing, stands.
            var measures = streams.Types.Single(t => t.Type == "UnitOfMeasure");
            Assert.Equal([(reference, 0)], measures.Stages.Select(st => (st.Flow, st.Depth)));
            Assert.Equal([new DeliveryStreamInputDto("osdu", "osdu:wks:reference-data--unitofmeasure:*")], measures.Origins);
            Assert.Equal(["shared"], measures.Projects);
            Assert.Equal("fresh", measures.State);

            // The wellbores pass through the delivery flow that delivers them, and through everything that flow reads. Their
            // content starts at the table the delivery flow reads and at the file behind a lookup table it translates through.
            // The mapping it renders with and the cache type nothing fills are nodes it reads, not places content starts: the
            // one is how it reads, the other is the unfilled notice below.
            var wellbores = streams.Types.Single(t => t.Type == "Wellbore");
            Assert.Equal(
                [(pre, 4), (ing, 3), (header, 2), (lookups, 2), (delivery, 1), (reference, 0)],
                wellbores.Stages.Select(st => (st.Flow, st.Depth)));
            Assert.Equal(
                [new DeliveryStreamInputDto("file", "stream/cache/data/units"), new DeliveryStreamInputDto("table", $"osdudata.arc.wellbore_{s}")],
                wellbores.Origins);

            // Each cache flow's last refresh of this partition, from its runs: a later run that reached another partition only
            // is passed over; the partition was last checked by the refresh that found nothing to change.
            var checks = streams.Checks.ToDictionary(c => c.Flow);
            Assert.Equal(("written", $"v2-{s}", lookupsRun), (checks[lookups].Outcome, checks[lookups].Version, checks[lookups].RunId));
            Assert.Equal(("unchanged", referenceRun), (checks[reference].Outcome, checks[reference].RunId));
            Assert.Equal(now.AddMinutes(-30), streams.LastCheckedUtc);

            Assert.Equal([new DeliveryCacheLeftTypeDto("LogType", "reference-data--LogType", 12, $"v1-{s}")], streams.Left);
            var notices = streams.Notices.ToDictionary(n => n.Kind + "|" + (n.Type ?? n.Flow ?? string.Empty));
            Assert.Contains("LogType", notices["removed|"].Message, StringComparison.Ordinal);
            Assert.Equal("history", notices["removed|"].Action);
            Assert.Contains("down from 40", notices["shrunk|Units"].Message, StringComparison.Ordinal);
            Assert.Equal("warning", notices["failed|Units"].Severity);
            Assert.Equal(("error", delivery), (notices["unfilled|logcurvefamily"].Severity, notices["unfilled|logcurvefamily"].Flow));
            Assert.DoesNotContain(streams.Notices, n => n.Kind == "unfilled" && n.Type == "curvedictionary");
            Assert.Equal("deliveries", notices["pending|"].Action);
            Assert.DoesNotContain(streams.Notices, n => n.Kind == "failed" && n.Flow == reference);

            // A partition no cache flow fills says so, and holds nothing.
            await using (var db = CatalogDatabase.Create(cs))
            await using (var osdu = SampleEstate.Context(cs))
            {
                var empty = await DeliveryCacheStreams.DescribeAsync(db, osdu, "none-" + s, CancellationToken.None);
                Assert.Empty(empty.Types);
                Assert.Equal("none", Assert.Single(empty.Notices).Kind);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryCacheDefinitions.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryCacheVersions.Where(v => v.Scope == partition).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryUpdateTags.Where(t => t.Scope == partition).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ScheduleMembers.Where(m => m.ScheduleId == scheduleId).ExecuteDeleteAsync();
                await db.Schedules.Where(x => x.Id == scheduleId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
                await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static CatalogRun Run(
        Guid pipelineId, Guid repoId, string flow, string kind, DateTime ended, string status, string? result = null, string? error = null, Guid? runId = null)
        => new()
        {
            RunId = runId ?? Guid.NewGuid(), PipelineId = pipelineId, RepoId = repoId, FlowName = flow, FlowKind = kind, Success = status == RunStatuses.Succeeded,
            Status = status, EnqueuedUtc = ended.AddMinutes(-5), StartUtc = ended.AddMinutes(-4), EndUtc = ended, WrittenUtc = ended, ResultJson = result, Error = error,
            RequestedBy = "schedule:st", TriggerSource = "schedule",
        };

    private static DeliveryCacheVersion Version(string scope, string version, int sequence, string? previous, bool current, DateTime captured, string types)
        => new()
        {
            Id = Guid.NewGuid(), Scope = scope, FlowName = "st", Version = version, Sequence = sequence, CapturedUtc = captured,
            ContentHash = new string('0', 64), PreviousVersion = previous, Current = current, CapturedBy = "tests", Origin = "tests", TypesJson = types,
            Items = JsonNode.Parse(types)!.AsArray().Sum(type => type!["items"]!.GetValue<long>()),
        };
}
