using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Writing an inline submission out as a drop (design.md section 3.4): where it goes, what it declares, how the records
/// are keyed and joined, and everything it refuses. What the reader gets back is checked with the real
/// <see cref="DropReader"/>, because the whole point is that the run reads an ordinary drop.
/// </summary>
public sealed class InlineDropTests : IDisposable
{
    private readonly WellboreEstate _estate = new();
    private readonly SqliteCatalog _db = new();
    private readonly EngineContext _engine;
    private readonly CatalogLedger _ledger;

    public InlineDropTests()
    {
        _ledger = _db.Ledger();
        _engine = Samples.Engine(_ledger);
    }

    [Fact]
    public async Task The_drop_is_keyed_sorted_and_joined_back_to_each_record()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-INLINE-2", "second", "2026-09-12T10:00:00Z", "TWO-A", "TWO-B"),
            WellboreEstate.Wellbore("WB-INLINE-1", "first", "2026-09-12T10:00:00Z"),
            WellboreEstate.WellboreWithoutAliases("WB-INLINE-3", "third", "2026-09-12T10:00:00Z")));
        var written = await WriteAsync(submission);

        Assert.True(written.Written);
        Assert.True(written.Manifest.Partitioned);
        Assert.Equal(WellboreEstate.MappingReference, written.Manifest.Mapping);
        Assert.Equal(WellboreEstate.FlowName, written.Manifest.Flow);
        Assert.Equal("north", written.Manifest.Parameters["site"]);
        Assert.Equal(3, written.Manifest.RecordCount);
        Assert.Empty(written.Manifest.Payloads);
        // No source versions: an inline submission is never skipped by the tier-0 gate and never moves a watermark.
        Assert.Empty(written.Manifest.SourceVersions);
        Assert.Equal(InlineDrop.RecordFile, Assert.Single(written.Manifest.Root.Files));
        Assert.Equal(DropReader.DeliveryKeyColumn, written.Manifest.Scopes["aliases"].ParentKey);

        var records = await ReadAsync(written.Location);
        Assert.Equal(3, records.Count);
        // The root file is sorted by the key's text, which is what lets the intake merge-join the child rows.
        Assert.Equal(records.Select(r => r.DeclaredDeliveryKey!.Value.ToString("D")).OrderBy(k => k, StringComparer.Ordinal), records.Select(r => r.DeclaredDeliveryKey!.Value.ToString("D")));
        foreach (var name in new[] { "WB-INLINE-1", "WB-INLINE-2", "WB-INLINE-3" })
        {
            var record = records.Single(r => r.Row.GetString("facility_name") == name);
            Assert.Equal(WellboreEstate.Key(name).Value, record.DeclaredDeliveryKey);
        }

        Assert.Equal(["TWO-A", "TWO-B"], records.Single(r => r.Row.GetString("facility_name") == "WB-INLINE-2").ScopeRows("aliases").Select(a => a.GetString("alias_name")));
        Assert.Empty(records.Single(r => r.Row.GetString("facility_name") == "WB-INLINE-1").ScopeRows("aliases"));
        Assert.Empty(records.Single(r => r.Row.GetString("facility_name") == "WB-INLINE-3").ScopeRows("aliases"));
    }

    [Fact]
    public async Task Every_column_the_mapping_or_the_flow_reads_is_declared_and_a_column_nobody_reads_is_reported()
    {
        var sparse = WellboreEstate.Row("WB-SPARSE", "no id", "2026-09-12T10:00:00Z");
        sparse.Remove("facility_id");
        sparse["source_system_note"] = "not read by the mapping";
        var submission = await AcceptAsync(WellboreEstate.Records(new { record = sparse }));
        var written = await WriteAsync(submission);

        var columns = written.Manifest.Root.Columns;
        var names = columns.Select(c => c.Name).ToList();
        Assert.Equal(DropReader.DeliveryKeyColumn, names[0]);
        Assert.Contains("facility_id", names);
        Assert.Contains("update_date", names);
        Assert.Contains("source_system_note", names);
        Assert.Contains(written.Warnings, w => w.Contains("source_system_note", StringComparison.Ordinal) && w.Contains("does not read", StringComparison.Ordinal));
        Assert.Contains(written.Warnings, w => w.Contains("facility_id", StringComparison.Ordinal) && w.Contains("null in every row", StringComparison.Ordinal));

        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Null(record.Row.Get("facility_id"));
        Assert.Equal("not read by the mapping", record.Row.GetString("source_system_note"));
        // An alias scope nobody sent is still declared, so the mapping's collection has a scope to iterate.
        Assert.True(written.Manifest.Scopes.ContainsKey("aliases"));
        Assert.Empty(record.ScopeRows("aliases"));
    }

    [Fact]
    public async Task Values_keep_their_type_and_their_text_through_the_drop()
    {
        var row = WellboreEstate.Row("WB-TYPES", "Brønnøysund Å \U0001F6E2", "2026-09-12T10:00:00Z");
        row["depth"] = 1234.5;
        row["count"] = 42;
        row["active"] = true;
        row["absent"] = null;
        var submission = await AcceptAsync(WellboreEstate.Records(new { record = row }));
        var written = await WriteAsync(submission);

        var declared = written.Manifest.Root.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
        Assert.Equal(InlineColumnTypes.Real, declared["depth"]);
        Assert.Equal(InlineColumnTypes.Whole, declared["count"]);
        Assert.Equal(InlineColumnTypes.Flag, declared["active"]);
        Assert.Equal(InlineColumnTypes.Text, declared["absent"]);

        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Equal(1234.5, record.Row.Get("depth"));
        Assert.Equal(42L, record.Row.Get("count"));
        Assert.Equal(true, record.Row.Get("active"));
        Assert.Null(record.Row.Get("absent"));
        Assert.Equal("Brønnøysund Å \U0001F6E2", record.Row.GetString("facility_description"));
    }

    [Fact]
    public async Task A_record_that_sends_its_own_delivery_key_is_written_under_it()
    {
        var mine = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var row = WellboreEstate.Row("WB-DECLARED", "declared key", "2026-09-12T10:00:00Z");
        row[DropReader.DeliveryKeyColumn] = mine.ToString("D");
        var written = await WriteAsync(await AcceptAsync(WellboreEstate.Records(new { record = row })));

        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Equal(mine, record.DeclaredDeliveryKey);
        // The column the record sent is where it was declared, not moved to the front.
        Assert.Equal(DropReader.DeliveryKeyColumn, written.Manifest.Root.Columns.Single(c => c.Name == DropReader.DeliveryKeyColumn).Name);
    }

    [Fact]
    public async Task A_record_whose_natural_key_is_incomplete_gets_a_join_key_of_its_own()
    {
        var written = await WriteAsync(await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-KEYED", "has a key", "2026-09-12T10:00:00Z", "A"),
            WellboreEstate.Wellbore(null, "no key at all", "2026-09-12T10:00:00Z", "B"))));

        var records = await ReadAsync(written.Location);
        Assert.Equal(2, records.Count);
        var untracked = records.Single(r => r.Row.GetString("facility_name") is null);
        Assert.NotNull(untracked.DeclaredDeliveryKey);
        Assert.NotEqual(WellboreEstate.Key("WB-KEYED").Value, untracked.DeclaredDeliveryKey);
        // Its own child rows still reach it, so nothing is silently attached to another record.
        Assert.Equal(["B"], untracked.ScopeRows("aliases").Select(a => a.GetString("alias_name")));
    }

    [Fact]
    public async Task The_same_record_twice_in_one_submission_is_refused_naming_both()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-TWICE", "first copy", "2026-09-12T10:00:00Z"),
            WellboreEstate.Wellbore("WB-OTHER", "other", "2026-09-12T10:00:00Z"),
            WellboreEstate.Wellbore("WB-TWICE", "second copy", "2026-09-12T11:00:00Z")));

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => WriteAsync(submission));
        Assert.Contains("records[0] and records[2] are the same record", ex.Message, StringComparison.Ordinal);
        Assert.Contains(WellboreEstate.Key("WB-TWICE").ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_again_reuses_the_drop_a_run_already_wrote_and_refuses_another_submission_at_the_same_place()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-ONCE", "once", "2026-09-12T10:00:00Z")));
        var first = await WriteAsync(submission);
        var manifestPath = Path.Combine(first.Location, "manifest.json");
        var written = File.GetLastWriteTimeUtc(manifestPath);

        var again = await WriteAsync(submission);
        Assert.False(again.Written);
        Assert.Empty(again.Warnings);
        Assert.Equal(submission.SubmissionId, again.Manifest.SubmissionId);
        Assert.Equal(written, File.GetLastWriteTimeUtc(manifestPath));

        // A different submission pointed at the same folder is a defect, not something to overwrite.
        var other = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-ELSE", "else", "2026-09-12T10:00:00Z")));
        var mapping = await MappingAsync();
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => InlineDrop.WriteAsync(
            _engine.Drops, _engine.Stores, first.Location, _estate.Definition, mapping, other));
        Assert.Contains(submission.SubmissionId.ToString("D"), ex.Message, StringComparison.Ordinal);
        Assert.Contains(other.SubmissionId.ToString("D"), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_drop_of_a_submission_goes_under_the_flow_work_location()
    {
        var flow = _estate.Definition;
        var id = Guid.NewGuid();
        var byDefault = InlineDrop.Location(flow, WellboreEstate.Values, id);
        Assert.Contains(Path.Combine(".work", InlineDrop.Folder, id.ToString("D")), byDefault, StringComparison.Ordinal);
        Assert.StartsWith(_estate.Root.Replace('\\', '/') + "/drops/north", byDefault.Replace('\\', '/'), StringComparison.Ordinal);

        // A flow that declares where its work goes puts the drop there instead, so the drop container need not be writable.
        var work = Path.Combine(_estate.Root, "work").Replace('\\', '/');
        var declared = WellboreEstate.Load(_estate.WriteFlow("wellbore-work", _estate.Flow("wellbore-work", WellboreEstate.MappingReference, $"  work: {work}/{{site}}")));
        Assert.Equal($"{work}/north", FlowParameters.WorkLocation(declared, WellboreEstate.Values, "ignored").Replace('\\', '/'));
        Assert.StartsWith($"{work}/north/{InlineDrop.Folder}/{id:D}", InlineDrop.Location(declared, WellboreEstate.Values, id).Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flow_takes_records_only_when_its_document_offers_manual_submission()
    {
        Assert.Null(InlineDrop.Refusal(_estate.Definition));

        // A flow that simply does not offer it says so, naming the key that turns it on.
        var notOffered = WellboreEstate.Load(_estate.WriteFlow(
            "wellbore-no-manual",
            _estate.Flow("wellbore-no-manual", WellboreEstate.MappingReference, manualSubmission: false)));
        var refusal = InlineDrop.Refusal(notOffered);
        Assert.Contains("source.manualSubmission", refusal, StringComparison.Ordinal);

        // A flow that streams payload files offers it on the same terms: what its records carry is where the files are,
        // never the bytes, so the protocol it delivers through is no reason to refuse the submission.
        var payloadFlow = WellboreEstate.Load(_estate.WriteFlow(WellboreEstate.PayloadFlowName, _estate.PayloadFlow()));
        Assert.Null(InlineDrop.Refusal(payloadFlow));
        var payloadNotOffered = WellboreEstate.Load(_estate.WriteFlow("wellbore-files-off", _estate.PayloadFlow("wellbore-files-off", manualSubmission: false)));
        Assert.Contains("source.manualSubmission", InlineDrop.Refusal(payloadNotOffered), StringComparison.Ordinal);

        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-FILES", "files", "2026-09-12T10:00:00Z")));
        var mapping = await MappingAsync();
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => InlineDrop.WriteAsync(
            _engine.Drops, _engine.Stores, Path.Combine(_estate.Root, "nowhere"), notOffered, mapping, submission));
        Assert.Equal(refusal, ex.Message);
    }

    [Fact]
    public async Task A_submission_of_another_flow_is_refused()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-OTHERFLOW", "x", "2026-09-12T10:00:00Z")));
        var otherFlow = WellboreEstate.Load(_estate.WriteFlow("wellbore-other", _estate.Flow("wellbore-other", WellboreEstate.MappingReference)));
        var mapping = await MappingAsync();

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => InlineDrop.WriteAsync(
            _engine.Drops, _engine.Stores, Path.Combine(_estate.Root, "nowhere"), otherFlow, mapping, submission));
        Assert.Contains("belongs to flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_submission_of_many_records_is_written_as_one_sorted_file()
    {
        const int Count = 300;
        var many = Enumerable.Range(0, Count)
            .Select(i => WellboreEstate.Wellbore($"WB-BULK-{i:D4}", $"bulk {i}", "2026-09-12T10:00:00Z", $"ALIAS-{i:D4}"))
            .ToArray();
        var written = await WriteAsync(await AcceptAsync(WellboreEstate.Records(many)));

        Assert.Equal(Count, written.Manifest.RecordCount);
        var records = await ReadAsync(written.Location);
        Assert.Equal(Count, records.Count);
        Assert.Equal(Count, records.Count(r => r.ScopeRows("aliases").Count == 1));
        Assert.Equal(
            Enumerable.Range(0, Count).Select(i => $"WB-BULK-{i:D4}").OrderBy(n => WellboreEstate.Key(n).ToString(), StringComparer.Ordinal),
            records.Select(r => r.Row.GetString("facility_name")));
    }

    [Fact]
    public async Task A_record_points_at_its_payload_files_and_the_drop_declares_where_to_read_them()
    {
        var flow = PayloadFlow();
        var location = _estate.WritePayloadFiles("WB-FILES-1");
        var written = await WriteAsync(await AcceptAsync(RecordsWithFiles("WB-FILES-1", location), flow), flow);

        var payload = Assert.Single(written.Manifest.Payloads);
        Assert.Equal(WellboreEstate.PayloadName, payload.Key);
        // The location travels with the record, so the drop's own folders say nothing about where the files are.
        Assert.Equal(InlineDrop.LocationColumnPrefix + WellboreEstate.PayloadName, payload.Value.LocationColumn);
        Assert.Null(payload.Value.PathTemplate);
        Assert.Null(payload.Value.HashColumn);
        Assert.Equal("text/csv", payload.Value.ContentType);

        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Equal(location, record.Row.GetString(InlineDrop.LocationColumnPrefix + WellboreEstate.PayloadName));
    }

    [Fact]
    public async Task A_flow_that_decides_payload_changes_by_hash_carries_the_hash_of_every_record()
    {
        var flow = PayloadFlow("wellbore-files-hash", hashDetect: true);
        var location = _estate.WritePayloadFiles("WB-FILES-HASH");
        Assert.Contains(
            "content hash",
            InlineDrop.FilesRefusal(flow, InlineRecords.Parse(RecordsWithFiles("WB-FILES-HASH", location))),
            StringComparison.Ordinal);

        var written = await WriteAsync(await AcceptAsync(RecordsWithFiles("WB-FILES-HASH", location, "sha256:abc"), flow), flow);
        Assert.Equal(InlineDrop.HashColumnPrefix + WellboreEstate.PayloadName, written.Manifest.Payloads[WellboreEstate.PayloadName].HashColumn);
        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Equal("sha256:abc", record.Row.GetString(InlineDrop.HashColumnPrefix + WellboreEstate.PayloadName));
    }

    [Fact]
    public void Where_a_submission_may_point_at_files_is_what_the_flow_allows()
    {
        var flow = PayloadFlow();
        var inside = InlineRecords.Parse(RecordsWithFiles("WB-INSIDE", _estate.Lake + "/WB-INSIDE"));
        Assert.Null(InlineDrop.FilesRefusal(flow, inside));

        foreach (var (records, fragment) in new[]
        {
            (RecordsWithFiles("WB-OUT", "C:/somewhere/else"), "outside what flow"),
            (RecordsWithFiles("WB-DOTS", _estate.Lake + "/../escape"), "must not contain '..'"),
            (WellboreEstate.Records(WellboreEstate.Wellbore("WB-NONE", "no files", "2026-09-12T10:00:00Z")), "points at no files"),
        })
        {
            Assert.Contains(fragment, InlineDrop.FilesRefusal(flow, InlineRecords.Parse(records)), StringComparison.Ordinal);
        }

        // A flow that streams nothing has nowhere to read files from, and a payload it does not stream is named as such.
        Assert.Contains("streams no payload files", InlineDrop.FilesRefusal(_estate.Definition, inside), StringComparison.Ordinal);
        var other = InlineRecords.Parse(WellboreEstate.Records(new
        {
            record = WellboreEstate.Row("WB-OTHERSET", "x", "2026-09-12T10:00:00Z"),
            files = new Dictionary<string, object> { ["curves"] = _estate.Lake + "/x" },
        }));
        Assert.Contains("which it does not stream", InlineDrop.FilesRefusal(flow, other), StringComparison.Ordinal);
    }

    /// <summary>
    /// A flow that streams files whose mapping iterates no child scope at all: the drop still has to be keyed, because
    /// the payload is joined to its record by the delivery key and the reader refuses a payload drop whose root rows
    /// carry none. The live estate's document mapping has exactly this shape, and this is what it found.
    /// </summary>
    [Fact]
    public async Task A_payload_drop_is_keyed_even_when_the_mapping_iterates_no_child_scope()
    {
        _estate.WriteMapping(FlatMappingReference, FlatMapping);
        var flow = PayloadFlow("wellbore-files-flat", mapping: FlatMappingReference);
        var location = _estate.WritePayloadFiles("WB-FLAT");
        var written = await WriteAsync(await AcceptAsync(RecordsWithFiles("WB-FLAT", location), flow), flow);

        Assert.True(written.Manifest.Partitioned);
        Assert.Single(written.Manifest.Scopes);
        Assert.Contains(written.Manifest.Root.Columns, c => c.Name == DropReader.DeliveryKeyColumn);

        var record = Assert.Single(await ReadAsync(written.Location));
        Assert.Equal(WellboreEstate.Key("WB-FLAT").Value, record.DeclaredDeliveryKey);
        Assert.Equal(location, record.Row.GetString(InlineDrop.LocationColumnPrefix + WellboreEstate.PayloadName));
    }

    [Fact]
    public async Task A_record_may_not_carry_the_column_reserved_for_where_its_files_are()
    {
        var flow = PayloadFlow();
        var row = WellboreEstate.Row("WB-RESERVED", "x", "2026-09-12T10:00:00Z");
        row[InlineDrop.LocationColumnPrefix + WellboreEstate.PayloadName] = "C:/elsewhere";
        var submission = await AcceptAsync(
            WellboreEstate.Records(new { record = row, files = new Dictionary<string, object> { [WellboreEstate.PayloadName] = _estate.Lake + "/WB-RESERVED" } }),
            flow);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => WriteAsync(submission, flow));
        Assert.Contains("is reserved for where the payload", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _db.Dispose();
        _estate.Dispose();
    }

    private const string FlatMappingReference = "WellboreFlat@1.0.0";

    /// <summary>The wellbore mapping with no child scope at all, which is the shape a document mapping has.</summary>
    private const string FlatMapping = """
        documentType: mapping
        name: WellboreFlat
        version: 1.0.0
        kind: osdu:wks:master-data--Wellbore:1.3.0
        source: { system: recall }
        identity: { naturalKey: [data.FacilityName], label: "{facility_name}" }
        envelope:
          legalTags: [opendes-reference-data-default]
          otherRelevantDataCountries: [NO]
          acl:
            owners: [data.default.owners@opendes.dataservices.energy]
            viewers: [data.default.viewers@opendes.dataservices.energy]
        parameters:
          dataPartition: { required: true }
        properties:
          - { target: data.FacilityName, source: facility_name, transform: trim }
          - { target: data.FacilityDescription, source: facility_description }
        """;

    private static string RecordsWithFiles(string name, string location, string? hash = null)
        => WellboreEstate.Records(WellboreEstate.WellboreWithFiles(name, "with files", "2026-09-12T10:00:00Z", location, hash));

    private FlowDefinition PayloadFlow(string name = WellboreEstate.PayloadFlowName, bool hashDetect = false, string? mapping = null)
        => WellboreEstate.Load(_estate.WriteFlow(name, _estate.PayloadFlow(name, hashDetect: hashDetect, mapping: mapping)));

    private async Task<InlineSubmissionState> AcceptAsync(string records, FlowDefinition? definition = null)
    {
        var flow = definition ?? _estate.Definition;
        var submission = InlineSubmissionState.Accept(
            Guid.CreateVersion7(), flow, "deliver", false, FlowParameters.Resolve(flow, WellboreEstate.Values),
            InlineRecords.Parse(records), DateTime.UtcNow, "api:test-source");
        await using var db = _db.CreateDbContext();
        db.DeliveryInlineSubmissions.Add(InlineSubmissionRows.ToEntity(submission));
        await db.SaveChangesAsync();
        return submission;
    }

    private async Task<ResolvedMapping> MappingAsync()
    {
        using var runtime = await FlowRuntime.CreateAsync(_engine, _estate.Definition, WellboreEstate.Values, Path.Combine(_estate.Root, "drops", "north"));
        return runtime.Mapping;
    }

    private async Task<InlineDropResult> WriteAsync(InlineSubmissionState submission, FlowDefinition? definition = null)
    {
        var flow = definition ?? _estate.Definition;
        var location = InlineDrop.Location(flow, FlowParameters.Resolve(flow, WellboreEstate.Values), submission.SubmissionId);
        using var runtime = await FlowRuntime.CreateAsync(_engine, flow, WellboreEstate.Values, location);
        return await runtime.WriteInlineDropAsync(submission);
    }

    private async Task<List<SourceRecord>> ReadAsync(string location)
    {
        var drop = await _engine.Drops.OpenAsync(location, "manifest.json");
        var records = new List<SourceRecord>();
        await foreach (var record in _engine.Drops.ReadRecordsAsync(drop))
        {
            records.Add(record);
        }

        return records;
    }
}
