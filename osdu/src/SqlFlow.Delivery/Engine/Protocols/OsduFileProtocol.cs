using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Record plus files (design.md section 8.1): the files go first, then the record that references them. Per chunk
/// of the record's payload, a signed landing-zone location is fetched, the chunk streamed to it, and its dataset
/// record registered through the file service; then the record itself is written through the storage array
/// endpoint with its dataset list pointing at the registered ids, batched with the other records of the batch
/// (section 16.2). Every upload and registration is a resumable step (section 16.3), and a metadata-only change
/// rewrites the record with the datasets its earlier delivery registered. A purge removes the dataset records and
/// their files with the record.
/// </summary>
public sealed class OsduFileProtocol : IDeliveryProtocol
{
    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly OsduRecordProtocol _records;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;

    public OsduFileProtocol(OsduHttpClient client, ProtocolOptions options, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.ForFiles(besideBulk: false);
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
        _records = new OsduRecordProtocol(client, options, _time);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.File;

    public int MaxBatch => _records.MaxBatch;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome[works.Count];
        var staged = new List<Staged>();
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            if (!work.DeliverMetadata && !work.DeliverPayload)
            {
                outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
                continue;
            }

            var steps = new DeliverySteps(_time);
            try
            {
                IReadOnlyList<string> datasets;
                var files = 0;
                if (work.DeliverPayload)
                {
                    var chunks = await FileUploads.ListChunksAsync(work, _requestBodyCeiling, ct).ConfigureAwait(false);
                    var uploaded = await FileUploads.UploadAsync(_client, _options, work, chunks, steps, ct).ConfigureAwait(false);
                    var ids = new List<string>(uploaded.Count);
                    foreach (var file in uploaded)
                    {
                        ids.Add(await FileUploads.RegisterAsync(_client, _options, work, file, steps, _time, ct).ConfigureAwait(false));
                    }

                    datasets = ids;
                    files = uploaded.Count;
                }
                else
                {
                    datasets = FileUploads.DatasetIds(work.TargetState);
                }

                var document = (JsonObject)work.Document.DeepClone();
                if (datasets.Count > 0)
                {
                    FileUploads.SetDatasets(document, _options.DatasetsProperty, datasets);
                }

                // The record is always written: a payload change moves its dataset list even when its own document did not change.
                var record = work with { Document = document, DeliverMetadata = true, DeliverPayload = false, Payload = null };
                staged.Add(new Staged(i, work, record, steps, files, datasets));
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex, steps.Steps);
            }
        }

        if (staged.Count == 0)
        {
            return outcomes;
        }

        var written = await _records.DeliverBatchAsync(staged.Select(s => s.Record).ToList(), ct).ConfigureAwait(false);
        for (var j = 0; j < staged.Count; j++)
        {
            var (index, original, _, steps, files, datasets) = staged[j];
            var result = written[j];
            var allSteps = steps.Steps.Concat(result.Steps).ToList();
            if (result.Failure is { } failure)
            {
                outcomes[index] = DeliveryOutcome.Failed(failure, allSteps);
                continue;
            }

            var returned = new Dictionary<string, string>(result.Returned, StringComparer.Ordinal);
            if (datasets.Count > 0)
            {
                returned[FileUploads.DatasetIdsValue] = string.Join(",", datasets);
            }

            if (files > 0)
            {
                returned["files"] = files.ToString(CultureInfo.InvariantCulture);
            }

            outcomes[index] = new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = original.DeliverPayload,
                TargetVersion = result.TargetVersion,
                ChunksSent = files,
                Detail = files > 0 ? $"{files.ToString(CultureInfo.InvariantCulture)} file(s) uploaded and registered" : result.Detail,
                Returned = returned,
                Steps = allSteps,
            };
        }

        return outcomes;
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, expectedVersion, ct);

    int IDeliveryProtocol.MaxVerifyBatch => OsduRecordProtocol.MaxVerifyBatch;

    /// <summary>The record lives in the storage service like any other, so it verifies in batched reads like any other.</summary>
    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => RecordWriter.VerifyBatchAsync(_client, _options.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath, requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.VersionsAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => RecordWriter.ReadVersionAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, version, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? FileUploads.DefaultFileProbePath, ct);

    private LegalTagValidator? _legal;

    /// <summary>Asks the legal service under this target, when the flow's target reaches it (see <see cref="LegalTagValidator.PathFor"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (LegalTagValidator.PathFor(_options, platformEndpoint: true) is not { } path)
        {
            return null;
        }

        _legal ??= new LegalTagValidator(_client, path, _time);
        return await _legal.InvalidAsync(tags, ct).ConfigureAwait(false);
    }

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return FileUploads.DeleteRecordAndDatasetsAsync(_client, _options, targetId, scope, targetState, ct);
    }

    /// <summary>
    /// The reversible removal leaves the record's datasets in place so OSDU can restore it whole, which is exactly
    /// what the storage service's bulk soft delete does, so that scope goes through it. The purges have to visit
    /// each record's datasets and stay one at a time.
    /// </summary>
    public Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
        => scope == RemovalScope.Record
            ? _records.DeleteBatchAsync(removals, scope, ct)
            : ((IDeliveryProtocol)this).DeleteOneByOneAsync(removals, scope, ct);

    private sealed record Staged(int Index, DeliveryWork Original, DeliveryWork Record, DeliverySteps Steps, int Files, IReadOnlyList<string> Datasets);
}
