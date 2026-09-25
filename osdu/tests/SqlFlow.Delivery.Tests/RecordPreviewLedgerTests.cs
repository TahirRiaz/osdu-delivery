using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The record preview against the module's ledger on SQL Server: a record the ledger holds is previewed with what the next
/// run would do with it and what the ledger holds for it, its document rendered even when the run would skip it, and the
/// keys only the ledger knows (a delivery key, the OSDU id a record was delivered as) find its row. The preview writes
/// nothing: the ledger reads the same before and after.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class RecordPreviewLedgerTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    /// <summary>The sample estate delivered once through a fake target, and a runtime over the same ledger to preview it with.</summary>
    private async Task<(FlowRuntime Runtime, OsduLedger Ledger, MemoryIngestionTables Tables)> DeliveredAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        return (runtime, ledger, tables);
    }

    [Fact]
    public async Task A_delivered_record_previews_as_unchanged_with_its_document_and_the_ledgers_state()
    {
        var (runtime, ledger, _) = await DeliveredAsync();
        using (runtime)
        {
            var before = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Delivered, before!.Status);

            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal("skip", preview.Decision!.Action);
            Assert.NotNull(preview.Decision.SkipTier);
            var held = preview.Decision.Ledger!;
            Assert.Equal("delivered", held.Status);
            Assert.Equal(before.TargetId, held.TargetId);
            Assert.Equal(before.TargetVersion, held.TargetVersion);
            Assert.True(held.SameDocument);

            // The run would not render it; the preview does, from the same row, to the document the ledger hashed.
            Assert.NotNull(preview.Document!.Rendered);
            Assert.Equal(before.MetadataHash, preview.Document.MetadataHash);

            // Nothing moved: the record reads as it did.
            var after = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(before.UpdatedUtc, after!.UpdatedUtc);
            Assert.Equal(before.LastSubmissionId, after.LastSubmissionId);
            Assert.Equal(before.AttemptCount, after.AttemptCount);
        }
    }

    [Fact]
    public async Task The_ledgers_keys_find_the_row_a_record_was_read_from()
    {
        var (runtime, ledger, _) = await DeliveredAsync();
        using (runtime)
        {
            var log = SampleEstate.Logs()[3];
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, log.Key);
            var previewer = new RecordPreviewer(runtime);

            var byDeliveryKey = await previewer.PreviewAsync(log.Key.ToString());
            var byOsduId = await previewer.PreviewAsync(record!.TargetId);
            var bySourceKey = await previewer.PreviewAsync(record.SourceKey);

            Assert.Equal(PreviewKeyForms.DeliveryKey, byDeliveryKey.Asked.How);
            Assert.Equal(PreviewKeyForms.OsduId, byOsduId.Asked.How);
            Assert.Equal(PreviewKeyForms.SourceKey, bySourceKey.Asked.How);
            Assert.All([byDeliveryKey, byOsduId, bySourceKey], p =>
            {
                Assert.True(p.Found, p.Reason);
                Assert.Equal(log.Key.Value, p.Source!.DeliveryKey);
                Assert.Equal([log.SourceProject, log.LogId], p.Asked.KeyParts);
            });
        }
    }

    [Fact]
    public async Task A_changed_row_previews_as_an_update_whose_document_differs_from_the_one_the_ledger_holds()
    {
        var (runtime, _, tables) = await DeliveredAsync();
        using (runtime)
        {
            SampleEstate.Change(tables.Records[0], "creator", "a new creator", Now);

            var preview = await new RecordPreviewer(runtime).PreviewAsync(SampleEstate.Key(0).ToString());

            Assert.True(preview.Found, preview.Reason);
            Assert.Equal("updateMetadata", preview.Decision!.Action);
            Assert.True(preview.Decision.DeliverMetadata);
            Assert.False(preview.Decision.Ledger!.SameDocument);
        }
    }

    [Fact]
    public async Task An_id_of_another_flows_kind_is_read_as_a_key_not_taken_for_one_this_flow_delivered()
    {
        var (runtime, _, _) = await DeliveredAsync();
        using (runtime)
        {
            // Not an id this flow delivers to, so it is neither found in the ledger nor refused as one of this flow's ids:
            // it is read as a source key, which the flow's two key columns cannot split it into.
            var preview = await new RecordPreviewer(runtime).PreviewAsync("dev:master-data--Wellbore:NO-15-5-7-AT2");

            Assert.False(preview.Found);
            Assert.Equal(PreviewKeyForms.SourceKey, preview.Asked.How);
            Assert.Contains("does not split into that many", preview.Reason, StringComparison.Ordinal);
        }
    }
}
