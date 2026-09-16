using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One input, two OSDU pipelines on different versions of one schema. The sample well log rows (and the curve files they
/// point at) are loaded once into the ingestion tables. The sample flow delivers them as WellLog 1.4.0; a second flow
/// renders the very same rows with the very same mapping entries as WellLog 1.5.0, the next version the Open Group
/// publishes (saved from the data definitions as a fixture, Fixtures/templates). Each pipeline keeps its own ledger over the
/// shared rows. Two versions of one kind name the same OSDU record for a row in a partition, so the second pipeline delivers
/// to a partition of its own; in the first pipeline's partition it is held, and the way to move that partition to the new
/// version is the first pipeline's own mapping, which keeps each record's OSDU id and history.
/// </summary>
public sealed class SchemaVersionPipelinesTests : IDisposable
{
    private const string CurrentKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private const string NextKind = "osdu:wks:work-product-component--WellLog:1.5.0";

    /// <summary>The content version of the WellLog 1.5.0 fixture, as <c>sqlflow template capture</c> saved it.</summary>
    private const string NextTemplateVersion = "2f8a99cb38d32480";

    private const string NextOrigin = "OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/work-product-component/WellLog.1.5.0.json";

    /// <summary>The partition the WellLog 1.5.0 pipeline delivers to, beside the sample partition the 1.4.0 pipeline uses.</summary>
    private const string NextPartition = "opendes-next";

    private const string CurrentFlowName = "recall-welllog";

    private const string NextFlowName = "recall-welllog-next";

    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task One_input_feeds_two_pipelines_on_two_versions_of_the_schema_and_each_keeps_its_own_ledger()
    {
        var estate = await EstateAsync();
        using var current = await PipelineAsync(estate, Samples.LocalFlow(_root));
        using var next = await PipelineAsync(estate, NextFlow(NextFlowName, NextPartition));
        Assert.Equal(CurrentFlowName, current.Runtime.Flow.Name);

        var (currentRun, currentSubmission) = await RunAsync(current, estate.Ledger);
        var (nextRun, nextSubmission) = await RunAsync(next, estate.Ledger);
        Assert.Equal((3, 3), (currentRun.Delivered, nextRun.Delivered));

        // The same three rows, each sent twice: as WellLog 1.4.0 into the sample partition and as WellLog 1.5.0 into the
        // next one, with the same curve files and, through each partition's own cache, each partition's own references.
        for (var i = 0; i < 3; i++)
        {
            var key = SampleEstate.Key(i);
            var sentCurrent = Assert.Single(current.Protocol.Deliveries, w => w.Key == key);
            var sentNext = Assert.Single(next.Protocol.Deliveries, w => w.Key == key);
            Assert.Equal((CurrentKind, NextKind), (Text(sentCurrent.Document, "kind"), Text(sentNext.Document, "kind")));
            Assert.Equal(TargetId.Compose(Samples.SampleCacheScope, "work-product-component--WellLog", key), sentCurrent.TargetId);
            Assert.Equal(TargetId.Compose(NextPartition, "work-product-component--WellLog", key), sentNext.TargetId);
            Assert.StartsWith(Samples.SampleCacheScope + ":master-data--Wellbore:", Text(sentCurrent.Document["data"], "WellboreID"), StringComparison.Ordinal);
            Assert.StartsWith(NextPartition + ":master-data--Wellbore:", Text(sentNext.Document["data"], "WellboreID"), StringComparison.Ordinal);
            Assert.Equal(Text(sentCurrent.Document["data"], "Name"), Text(sentNext.Document["data"], "Name"));
            Assert.True(sentCurrent.DeliverPayload && sentNext.DeliverPayload);

            // Each pipeline's record says which schema version, mapping and partition cache built what it delivered, and
            // both name the same ingestion file and row.
            var currentRecord = await estate.Ledger.GetRecordAsync(current.Runtime.Flow.Id, key);
            var nextRecord = await estate.Ledger.GetRecordAsync(next.Runtime.Flow.Id, key);
            Assert.Equal((RecordStatus.Delivered, RecordStatus.Delivered), (currentRecord!.Status, nextRecord!.Status));
            Assert.Equal(("WellLog@1.4.0", Samples.SampleTemplate(CurrentKind).Version, Samples.SampleCacheScope), Rendered(currentRecord));
            Assert.Equal(("WellLog@1.5.0", NextTemplateVersion, NextPartition), Rendered(nextRecord));
            Assert.Equal((sentCurrent.TargetId, sentNext.TargetId), (currentRecord.ClaimedTargetId, nextRecord.ClaimedTargetId));
            Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (currentRecord.SourceFileName, currentRecord.SourceRowNumber));
            Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (nextRecord.SourceFileName, nextRecord.SourceRowNumber));
            Assert.Equal(currentSubmission, Assert.Single(await estate.Ledger.ListAttemptsAsync(current.Runtime.Flow.Id, key, 10)).SubmissionId);
            Assert.Equal(nextSubmission, Assert.Single(await estate.Ledger.ListAttemptsAsync(next.Runtime.Flow.Id, key, 10)).SubmissionId);
            Assert.Equal(2, (await estate.Ledger.LookupAsync(key.Value.ToString(), 10)).Count);
        }

        var submissions = (await estate.Ledger.GetSubmissionAsync(currentSubmission), await estate.Ledger.GetSubmissionAsync(nextSubmission));
        Assert.Equal(("WellLog@1.4.0", "WellLog@1.5.0"), (submissions.Item1!.MappingReference, submissions.Item2!.MappingReference));
        Assert.Equal(submissions.Item1.SourceObject, submissions.Item2.SourceObject);
        Assert.Equal((3L, 3L), ((await estate.Ledger.StatsAsync(current.Runtime.Flow.Id, Now)).Delivered, (await estate.Ledger.StatsAsync(next.Runtime.Flow.Id, Now)).Delivered));

        // A change to the one input reaches both pipelines, and each records it in its own history.
        current.Protocol.Deliveries.Clear();
        next.Protocol.Deliveries.Clear();
        _clock.Advance(TimeSpan.FromMinutes(10));
        SampleEstate.Change(estate.Tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc.AddHours(2));
        Assert.Equal(1, (await RunAsync(current, estate.Ledger)).Work.Delivered);
        Assert.Equal(1, (await RunAsync(next, estate.Ledger)).Work.Delivered);
        Assert.Equal(("HAL", "HAL"), (Text(Assert.Single(current.Protocol.Deliveries).Document["data"], "ActivityType"), Text(Assert.Single(next.Protocol.Deliveries).Document["data"], "ActivityType")));
        Assert.Equal(2, (await estate.Ledger.ListAttemptsAsync(current.Runtime.Flow.Id, SampleEstate.Key(0), 10)).Count);
        Assert.Equal(2, (await estate.Ledger.ListAttemptsAsync(next.Runtime.Flow.Id, SampleEstate.Key(0), 10)).Count);

        // Taking the 1.5.0 records out of OSDU takes only those: the 1.4.0 records stay delivered where they are.
        next.Runtime.Actor = "gui:tahir";
        var removal = await next.Runtime.RemoveAsync(RemovalSelection.Of([.. Enumerable.Range(0, 3).Select(SampleEstate.Key)]), RemovalScope.Record);
        Assert.Equal(3, removal.Removed);
        Assert.All(next.Protocol.Deletes, d => Assert.StartsWith(NextPartition + ":", d.TargetId, StringComparison.Ordinal));
        Assert.Empty(current.Protocol.Deletes);
        Assert.Equal(3, (await estate.Ledger.StatsAsync(next.Runtime.Flow.Id, Now)).Deleted);
        Assert.Equal(3, (await estate.Ledger.StatsAsync(current.Runtime.Flow.Id, Now)).Delivered);
    }

    [Fact]
    public async Task A_second_version_of_the_kind_in_the_same_partition_is_held_because_it_names_the_same_osdu_records()
    {
        var estate = await EstateAsync();
        using var current = await PipelineAsync(estate, Samples.LocalFlow(_root));
        using var clash = await PipelineAsync(estate, NextFlow(NextFlowName, Samples.SampleCacheScope));
        Assert.Equal(3, (await RunAsync(current, estate.Ledger)).Work.Delivered);

        // WellLog 1.5.0 in the sample partition renders the ids the 1.4.0 pipeline owns: nothing is sent, and every record
        // is held with the owner named, so the reason is on the record rather than in OSDU as alternating kinds.
        var (run, submissionId) = await RunAsync(clash, estate.Ledger);
        Assert.Equal(0, run.Processed);
        Assert.Empty(clash.Protocol.Deliveries);
        Assert.Equal(3L, (await estate.Ledger.GetSubmissionAsync(submissionId))!.Held);
        for (var i = 0; i < 3; i++)
        {
            var held = await estate.Ledger.GetRecordAsync(clash.Runtime.Flow.Id, SampleEstate.Key(i));
            Assert.Equal((RecordStatus.Held, (string?)null), (held!.Status, held.ClaimedTargetId));
            Assert.Contains($"already claimed by flow '{CurrentFlowName}'", held.LastError, StringComparison.Ordinal);
            var owner = await estate.Ledger.GetRecordAsync(current.Runtime.Flow.Id, SampleEstate.Key(i));
            Assert.Equal(("WellLog@1.4.0", RecordStatus.Delivered), (Rendered(owner!).Mapping, owner!.Status));
        }
    }

    [Fact]
    public async Task Moving_a_pipeline_to_the_next_version_updates_its_records_in_place_and_keeps_their_history()
    {
        var estate = await EstateAsync();
        using var before = await PipelineAsync(estate, Samples.LocalFlow(_root));
        var (_, firstSubmission) = await RunAsync(before, estate.Ledger);

        // The same flow, pinned to the WellLog 1.5.0 mapping. Nothing changed in the input; the schema did, so every record
        // is rendered again, and only the document is sent: the same OSDU record gets a new version of the new kind.
        _clock.Advance(TimeSpan.FromMinutes(10));
        using var after = await PipelineAsync(estate, NextFlow(CurrentFlowName, Samples.SampleCacheScope));
        Assert.Equal(before.Runtime.Flow.Id, after.Runtime.Flow.Id);
        var (upgrade, secondSubmission) = await RunAsync(after, estate.Ledger);
        Assert.Equal(3, upgrade.Delivered);
        foreach (var sent in after.Protocol.Deliveries)
        {
            var earlier = Assert.Single(before.Protocol.Deliveries, w => w.Key == sent.Key);
            Assert.Equal((NextKind, earlier.TargetId), (Text(sent.Document, "kind"), sent.TargetId));
            Assert.True(sent.DeliverMetadata);
            Assert.False(sent.DeliverPayload);
            Assert.NotNull(sent.ExistingVersion);
        }

        // One record per row, whose history says which schema version each delivery carried.
        for (var i = 0; i < 3; i++)
        {
            var key = SampleEstate.Key(i);
            var record = await estate.Ledger.GetRecordAsync(after.Runtime.Flow.Id, key);
            Assert.Equal(("WellLog@1.5.0", NextTemplateVersion, Samples.SampleCacheScope), Rendered(record!));
            var attempts = await estate.Ledger.ListAttemptsAsync(after.Runtime.Flow.Id, key, 10);
            Assert.Equal([secondSubmission, firstSubmission], attempts.Select(a => a.SubmissionId!.Value));
            Assert.Equal("metadata", attempts[0].Phase);
        }

        Assert.Equal(
            ["WellLog@1.5.0", "WellLog@1.4.0"],
            (await estate.Ledger.ListSubmissionsAsync(after.Runtime.Flow.Id, 10)).Select(s => s.MappingReference));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The shared input and what both pipelines render with: the templates of both versions and both partitions' caches.</summary>
    private async Task<Estate> EstateAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

        var templates = _db.Templates(_clock);
        await Samples.ImportSampleTemplatesAsync(templates);
        var next = Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates", NextKind.Replace(':', '_') + ".json");
        var saved = await templates.SaveAsync(
            TemplateSources.FromBundledJson(File.ReadAllText(next), NextKind, new DateTimeOffset(2026, 9, 16, 12, 21, 35, TimeSpan.Zero), next),
            NextOrigin,
            "tests");
        Assert.Equal(NextTemplateVersion, saved.Template.Version);

        var caches = _db.Caches();
        var cacheFlow = new DeliveryDocumentLoader().LoadCache(Samples.CacheFlow);
        await ImportCacheAsync(caches, cacheFlow, Samples.SampleCacheScope, Samples.References);
        await ImportCacheAsync(caches, cacheFlow, NextPartition, PartitionReferences(NextPartition));
        return new Estate(tables, templates, caches, _db.Ledger(_clock));
    }

    /// <summary>Declares the sample cache flow's types for <paramref name="scope"/> and imports its records as that partition's cache.</summary>
    private async Task ImportCacheAsync(ICacheStore caches, CacheDefinition flow, string scope, string directory)
    {
        await _db.DeclareCacheAsync(scope, flow.Name, [.. flow.Types]);
        var builder = new SnapshotBuilder(caches, scope, flow.Name, new TestClock(Samples.SampleCacheCaptured), Samples.Logger<SnapshotBuilder>());
        await builder.ImportDirectoryAsync(directory, flow.Types, new CacheCapture(null, "tests", "sample files for " + scope));
    }

    /// <summary>The sample cache records as another partition holds them: the same records under that partition's ids.</summary>
    private string PartitionReferences(string partition)
    {
        var directory = Path.Combine(_root, "references-" + partition);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(Samples.References, "*.json"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains(Samples.SampleCacheScope + ":", text, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, Path.GetFileName(file)), text.Replace(Samples.SampleCacheScope + ":", partition + ":", StringComparison.Ordinal));
        }

        return directory;
    }

    /// <summary>
    /// The sample flow turned into a WellLog 1.5.0 pipeline named <paramref name="name"/> delivering to
    /// <paramref name="partition"/>: the sample mapping with only its schema version changed, so both pipelines render the
    /// same entries from the same rows.
    /// </summary>
    private FlowDefinition NextFlow(string name, string partition)
    {
        var mappings = Path.Combine(_root, "next-mappings-" + partition);
        Directory.CreateDirectory(mappings);
        File.WriteAllText(Path.Combine(mappings, "WellLog@1.5.0.yaml"), NextMapping(partition));
        var flow = Samples.LocalFlow(_root);
        return flow with
        {
            Name = name,
            Render = flow.Render with
            {
                Mapping = "WellLog@1.5.0",
                MappingsDirectory = mappings,
                Parameters = new Dictionary<string, string>(flow.Render.Parameters, StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = partition },
            },
            Target = flow.Target with
            {
                Headers = new Dictionary<string, string>(flow.Target.Headers, StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = partition },
            },
        };
    }

    /// <summary>
    /// The sample WellLog 1.4.0 mapping pinned to WellLog 1.5.0 instead, with its entries untouched. Its fixtures, the
    /// regression suite the preflight renders against the partition's cache, are written for <paramref name="partition"/>:
    /// a render there reads that partition's references. Every edit is checked to have applied.
    /// </summary>
    private static string NextMapping(string partition)
    {
        var text = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml")).ReplaceLineEndings("\n");
        text = Replace(text, "# Mapping: Recall well logs into the WellLog 1.4.0 template", "# Mapping: Recall well logs into the WellLog 1.5.0 template", 1);
        text = Replace(text, "\nversion: 1.4.0\n", "\nversion: 1.5.0\n", 1);
        text = Replace(text, $"  kind: {CurrentKind}\n  version: 26a3c3441882db4f\n", $"  kind: {NextKind}\n  version: {NextTemplateVersion}\n", 1);
        text = Replace(text, $"\"kind\": \"{CurrentKind}\"", $"\"kind\": \"{NextKind}\"", 2);
        if (partition == Samples.SampleCacheScope)
        {
            return text;
        }

        var at = text.IndexOf("\nfixtures:\n", StringComparison.Ordinal);
        Assert.True(at > 0, "The sample WellLog mapping has no fixtures section: the derived 1.5.0 mapping no longer follows it.");
        var fixtures = Replace(text[at..], $"dataPartition: {Samples.SampleCacheScope} }}", $"dataPartition: {partition} }}", 2);
        fixtures = Replace(fixtures, $"\"{Samples.SampleCacheScope}:", $"\"{partition}:", expected: null);
        return text[..at] + fixtures;
    }

    /// <summary>Replaces <paramref name="from"/>, which must occur <paramref name="expected"/> times, or at least once when that is null.</summary>
    private static string Replace(string text, string from, string to, int? expected)
    {
        var found = text.Split(from).Length - 1;
        Assert.True(
            expected is { } count ? found == count : found > 0,
            $"The sample WellLog mapping holds '{from.ReplaceLineEndings(" ")}' {found} time(s), not {expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "at least once"}: the derived 1.5.0 mapping no longer follows it.");
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    private async Task<Pipeline> PipelineAsync(Estate estate, FlowDefinition flow)
    {
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(estate.Ledger, _clock, new FakeProtocolFactory(protocol), estate.Templates, estate.Caches, estate.Tables);
        return new Pipeline(await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values), protocol);
    }

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<(WorkerSummary Work, Guid SubmissionId)> RunAsync(Pipeline pipeline, OsduLedger ledger)
    {
        var runtime = pipeline.Runtime;
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        if (intake.NothingToDo)
        {
            return (WorkerSummary.Empty, intake.Submission.SubmissionId);
        }

        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, pipeline.Protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), runtime.Flow.Name + "-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return (summary, intake.Submission.SubmissionId);
    }

    /// <summary>What built a record's delivered document: the mapping, the schema version and the partition whose cache it read.</summary>
    private static (string Mapping, string Schema, string Cache) Rendered(RecordState record)
    {
        var context = JsonNode.Parse(record.RenderContext ?? throw new InvalidOperationException($"Record {record.DeliveryKey} holds no render context."))!;
        return (Text(context, "mapping"), Text(context, "schema"), Text(context, "cache"));
    }

    private static string Text(JsonNode? node, string name)
        => node?[name]?.GetValue<string>() ?? throw new InvalidOperationException($"The document has no '{name}'.");

    private sealed record Estate(MemoryIngestionTables Tables, ITemplateStore Templates, ICacheStore Caches, OsduLedger Ledger);

    private sealed record Pipeline(FlowRuntime Runtime, FakeProtocol Protocol) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();
    }
}
