using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Manifest ingestion (design.md section 8.1): the batch's files go to the landing zone first; then one manifest
/// carrying the batch's records, and one dataset entry per uploaded file, is handed to the ingestion workflow
/// (openapi workflow v1, POST workflow/{name}/workflowRun); the run is polled until it finishes; and the records
/// are read back from storage (openapi storage v2, POST query/records) so each settles on its own evidence: present
/// with a version, delivered; absent, failed with the run named. Dataset ids derive from the record id, so a
/// redelivery overwrites its datasets instead of leaking new ones, and the record's dataset list is complete before
/// the workflow registers them. The manifest step records the run id before polling starts (section 16.3), so a
/// retry after a poll timeout resumes the same run; a run that failed, or that finished without writing the record,
/// is triggered again on the next try, and the failed run stays on the attempt.
/// </summary>
public sealed class OsduManifestProtocol : IDeliveryProtocol
{
    public const string DefaultWorkflowRunPath = "/api/workflow/v1/workflow/{workflow}/workflowRun";
    public const string DefaultWorkflowStatusPath = "/api/workflow/v1/workflow/{workflow}/workflowRun/{runId}";
    public const string DefaultProbePath = "/api/workflow/v1/info";
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";

    public const string ManifestStep = "manifest";
    public const string WorkflowStep = "workflow";
    public const string RecordsStep = "records";

    /// <summary>Records per storage read-back request (openapi storage v2, MultiRecordIds takes at most 100).</summary>
    public const int QueryBatch = 100;

    private static readonly HashSet<string> Finished = new(StringComparer.Ordinal) { "SUCCESS", "PARTIAL_SUCCESS", "FAILED" };
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal) { "SUBMITTED", "INPROGRESS", "RUNNING", "QUEUED" };

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;

    public OsduManifestProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduManifest;

    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, ProtocolOptions.MaxBatchSize);

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
        var fresh = new List<Staged>();
        var resumed = new Dictionary<string, List<Staged>>(StringComparer.Ordinal);
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
                var datasets = new List<JsonObject>();
                var ids = new List<string>();
                var files = 0;
                if (work.DeliverPayload)
                {
                    var chunks = await FileUploads.ListChunksAsync(work, _requestBodyCeiling, ct).ConfigureAwait(false);
                    var uploaded = await FileUploads.UploadAsync(_client, _options, work, chunks, steps, ct).ConfigureAwait(false);
                    foreach (var file in uploaded)
                    {
                        var id = DatasetId(work.TargetId, _options.DatasetKind, file.Index);
                        ids.Add(id);
                        datasets.Add(FileUploads.DatasetRecord(_options, work.Document, file, id));
                    }

                    files = uploaded.Count;
                }
                else
                {
                    ids.AddRange(FileUploads.DatasetIds(work.TargetState));
                }

                var document = (JsonObject)work.Document.DeepClone();
                if (ids.Count > 0)
                {
                    FileUploads.SetDatasets(document, _options.DatasetsProperty, ids);
                }

                var section = _options.ManifestSection ?? SectionOf(document);
                var staged = new Staged(i, work, document, section, datasets, ids, steps, files);
                if (work.Completed(ManifestStep) is { } earlier && earlier.TryGetValue("runId", out var runId) && !string.IsNullOrEmpty(runId))
                {
                    steps.Resumed(ManifestStep, earlier);
                    if (!resumed.TryGetValue(runId, out var group))
                    {
                        group = [];
                        resumed[runId] = group;
                    }

                    group.Add(staged);
                }
                else
                {
                    fresh.Add(staged);
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex, steps.Steps);
            }
        }

        // Runs an earlier try started: a finished one settles the records it wrote here; the records it failed or
        // left out go into the new manifest with everything else.
        foreach (var (runId, group) in resumed)
        {
            try
            {
                var run = await PollAsync(runId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                if (run.Status == "FAILED")
                {
                    _logger.LogWarning("Workflow run {RunId} of {Workflow} failed; {Count} record(s) go into a new run.", runId, _options.WorkflowName, group.Count);
                    foreach (var staged in group)
                    {
                        staged.Steps.Add(WorkflowStep, run.Started, 200, run.Values(), "the run failed; a new run is triggered");
                    }

                    fresh.AddRange(group);
                    continue;
                }

                var missing = await SettleAsync(group, run, outcomes, ct).ConfigureAwait(false);
                if (missing.Count > 0)
                {
                    _logger.LogWarning("Workflow run {RunId} of {Workflow} finished {Status} without {Count} of its {Total} record(s); they go into a new run.", runId, _options.WorkflowName, run.Status, missing.Count, group.Count);
                    fresh.AddRange(missing);
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var staged in group)
                {
                    outcomes[staged.Index] = DeliveryOutcome.Failed(ex, staged.Steps.Steps);
                }
            }
        }

        if (fresh.Count > 0)
        {
            try
            {
                var triggered = await TriggerAsync(fresh, ct).ConfigureAwait(false);
                var run = await PollAsync(triggered.RunId, triggered.Started, ct).ConfigureAwait(false);
                if (run.Status == "FAILED")
                {
                    var failure = new DeliveryException($"workflow run {run.RunId} of {_options.WorkflowName} failed; the next try triggers a new run");
                    foreach (var staged in fresh)
                    {
                        staged.Steps.Add(WorkflowStep, run.Started, 200, run.Values(), failure.Message);
                        outcomes[staged.Index] = DeliveryOutcome.Failed(failure, staged.Steps.Steps);
                    }
                }
                else
                {
                    foreach (var staged in await SettleAsync(fresh, run, outcomes, ct).ConfigureAwait(false))
                    {
                        var reason = $"workflow run {run.RunId} of {_options.WorkflowName} finished {run.Status} but {staged.Work.TargetId} is not in storage; the run's log names what it rejected, and the next try triggers a new run";
                        staged.Steps.Add(RecordsStep, run.Started, null, null, reason);
                        outcomes[staged.Index] = DeliveryOutcome.Failed(new DeliveryException(reason), staged.Steps.Steps);
                    }
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var staged in fresh)
                {
                    outcomes[staged.Index] = DeliveryOutcome.Failed(ex, staged.Steps.Steps);
                }
            }
        }

        return outcomes;
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return FileUploads.DeleteRecordAndDatasetsAsync(_client, _options, targetId, scope, targetState, ct);
    }

    /// <summary>
    /// The dataset id of one file of a record, derived from the record id and the dataset kind so that it is the
    /// same on every delivery: <c>dev:work-product-component--WellLog:abc</c> with
    /// <c>osdu:wks:dataset--File.Generic:1.0.0</c> gives <c>dev:dataset--File.Generic:abc-0</c>.
    /// </summary>
    internal static string DatasetId(string targetId, string datasetKind, int index)
    {
        var first = targetId.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : targetId.IndexOf(':', first + 1);
        if (first <= 0 || second < 0 || second == targetId.Length - 1)
        {
            throw new DeliveryException($"record id '{targetId}' is not partition:type:name, so no dataset id can be derived from it");
        }

        var kind = datasetKind.Split(':');
        if (kind.Length != 4 || string.IsNullOrEmpty(kind[2]))
        {
            throw new DeliveryException($"target.protocolOptions.datasetKind '{datasetKind}' is not authority:source:type:version");
        }

        return $"{targetId[..first]}:{kind[2]}:{targetId[(second + 1)..]}-{index.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The manifest section a record belongs in, from the group type of its kind.</summary>
    internal static string SectionOf(JsonObject document)
    {
        var kind = document["kind"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
        var parts = kind.Split(':');
        var type = parts.Length == 4 ? parts[2] : string.Empty;
        var cut = type.IndexOf("--", StringComparison.Ordinal);
        var group = cut > 0 ? type[..cut] : type;
        return group switch
        {
            "work-product-component" => "WorkProductComponents",
            "work-product" => "WorkProduct",
            "dataset" => "Datasets",
            "master-data" => "MasterData",
            "reference-data" => "ReferenceData",
            _ => throw new DeliveryException($"kind '{kind}' names no manifest section (its group type is not work-product-component, work-product, dataset, master-data or reference-data); set target.protocolOptions.manifestSection"),
        };
    }

    /// <summary>One manifest for the batch (osdu:wks:Manifest:1.0.0): the records in their sections, the batch's datasets under Data.</summary>
    private JsonObject BuildManifest(List<Staged> group)
    {
        var manifest = new JsonObject { ["kind"] = _options.ManifestKind };
        var reference = new JsonArray();
        var master = new JsonArray();
        var components = new JsonArray();
        var datasets = new JsonArray();
        JsonObject? workProduct = null;
        foreach (var staged in group)
        {
            var document = (JsonObject)staged.Document.DeepClone();
            switch (staged.Section)
            {
                case "ReferenceData":
                    reference.Add(document);
                    break;
                case "MasterData":
                    master.Add(document);
                    break;
                case "WorkProductComponents":
                    components.Add(document);
                    break;
                case "Datasets":
                    datasets.Add(document);
                    break;
                case "WorkProduct":
                    if (workProduct is not null)
                    {
                        throw new DeliveryException("a manifest carries one WorkProduct and the batch holds more than one; set target.protocolOptions.batchSize to 1");
                    }

                    workProduct = document;
                    break;
                default:
                    throw new DeliveryException($"'{staged.Section}' is not a manifest section");
            }

            foreach (var dataset in staged.Datasets)
            {
                datasets.Add(dataset.DeepClone());
            }
        }

        if (reference.Count > 0)
        {
            manifest["ReferenceData"] = reference;
        }

        if (master.Count > 0)
        {
            manifest["MasterData"] = master;
        }

        if (workProduct is not null || components.Count > 0 || datasets.Count > 0)
        {
            var data = new JsonObject();
            if (workProduct is not null)
            {
                data["WorkProduct"] = workProduct;
            }

            if (components.Count > 0)
            {
                data["WorkProductComponents"] = components;
            }

            if (datasets.Count > 0)
            {
                data["Datasets"] = datasets;
            }

            manifest["Data"] = data;
        }

        return manifest;
    }

    /// <summary>
    /// Triggers one run for the group and reports the manifest step, with the run id, on every record before the
    /// poll starts. The run id is chosen here, so a request the service accepted before a retry resent it answers
    /// 409 and is polled, not run twice.
    /// </summary>
    private async Task<WorkflowRun> TriggerAsync(List<Staged> group, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("D");
        var manifest = BuildManifest(group);
        var payload = new JsonObject { ["AppKey"] = _options.WorkflowAppKey };
        if (_client.Header("data-partition-id") is { } partition)
        {
            payload["data-partition-id"] = partition;
        }

        foreach (var (name, value) in _options.WorkflowPayload)
        {
            payload[name] = value;
        }

        var body = new JsonObject
        {
            ["runId"] = runId,
            ["executionContext"] = new JsonObject { ["Payload"] = payload, ["manifest"] = manifest },
        };
        var url = _client.Url(_options.WorkflowRunPath ?? DefaultWorkflowRunPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = _options.WorkflowName });
        var started = _time.GetUtcNow().UtcDateTime;
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 409 }, ct).ConfigureAwait(false);
        string? workflowId = null;
        var status = "SUBMITTED";
        if ((int)result.Status != 409 && result.Body.Length > 0)
        {
            var root = OsduHttpClient.ParseJson(result, url);
            workflowId = JsonPathReader.SelectValue(root, "workflowId");
            status = JsonPathReader.SelectValue(root, "status") ?? status;
            runId = JsonPathReader.SelectValue(root, "runId") ?? runId;
        }

        var run = new WorkflowRun(runId, workflowId, status.ToUpperInvariant(), null, null, started);
        var values = run.Values();
        values["workflow"] = _options.WorkflowName;
        values["records"] = group.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var staged in group)
        {
            staged.Steps.Add(ManifestStep, started, (int)result.Status, values);
            await staged.Work.ReportStepAsync(ManifestStep, values, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Workflow run {RunId} of {Workflow} triggered for {Count} record(s) ({Status}).", runId, _options.WorkflowName, group.Count, run.Status);
        return run;
    }

    /// <summary>Polls the run until it finishes, or the flow's timeout passes (the next try resumes the same run).</summary>
    private async Task<WorkflowRun> PollAsync(string runId, DateTime started, CancellationToken ct)
    {
        var url = _client.Url(_options.WorkflowStatusPath ?? DefaultWorkflowStatusPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow"] = _options.WorkflowName, ["runId"] = runId });
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.WorkflowPollSeconds));
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.WorkflowTimeoutMinutes));
        var deadline = _time.GetUtcNow() + timeout;
        var polls = 0;
        while (true)
        {
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            var root = OsduHttpClient.ParseJson(result, url);
            polls++;
            var status = (JsonPathReader.SelectValue(root, "status") ?? throw new DeliveryException($"{url.AbsolutePath} did not report the run's status.")).ToUpperInvariant();
            if (Finished.Contains(status))
            {
                _logger.LogInformation("Workflow run {RunId} of {Workflow} finished {Status} after {Polls} poll(s).", runId, _options.WorkflowName, status, polls);
                return new WorkflowRun(runId, JsonPathReader.SelectValue(root, "workflowId"), status, JsonPathReader.SelectValue(root, "startTimeStamp"), JsonPathReader.SelectValue(root, "endTimeStamp"), started);
            }

            if (!Pending.Contains(status))
            {
                throw new DeliveryException($"workflow run {runId} of {_options.WorkflowName} reported an unknown status '{status}'");
            }

            if (_time.GetUtcNow() + interval > deadline)
            {
                throw new DeliveryException($"workflow run {runId} of {_options.WorkflowName} is still {status} after {timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minute(s); the next try resumes polling it");
            }

            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the group's records back from storage after the run finished. The ones storage holds settle as
    /// delivered with their version; the ones storage asked to retry fail for this try; the rest are returned to
    /// the caller, which decides whether they go into a new run now or on the next try.
    /// </summary>
    private async Task<List<Staged>> SettleAsync(List<Staged> group, WorkflowRun run, DeliveryOutcome[] outcomes, CancellationToken ct)
    {
        var url = _client.Url(_options.RecordQueryPath ?? DefaultRecordQueryPath);
        var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
        var retry = new HashSet<string>(StringComparer.Ordinal);
        var status = 0;
        foreach (var chunk in group.Chunk(QueryBatch))
        {
            var body = new JsonObject
            {
                ["records"] = new JsonArray(chunk.Select(s => (JsonNode?)JsonValue.Create(s.Work.TargetId)).ToArray()),
                ["attributes"] = new JsonArray(JsonValue.Create("data." + _options.DatasetsProperty)),
            };
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct).ConfigureAwait(false);
            status = (int)result.Status;
            var root = OsduHttpClient.ParseJson(result, url);
            foreach (var record in JsonPathReader.SelectElements(root, "records[*]"))
            {
                if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                {
                    versions[idNode.GetString()!] = record.TryGetProperty("version", out var versionNode) && versionNode.ValueKind == JsonValueKind.Number && versionNode.TryGetInt64(out var version)
                        ? version
                        : null;
                }
            }

            foreach (var id in JsonPathReader.SelectValues(root, "retryRecords[*]"))
            {
                retry.Add(id);
            }
        }

        var runValues = run.Values();
        var completed = _time.GetUtcNow().UtcDateTime;
        var missing = new List<Staged>();
        foreach (var staged in group)
        {
            var id = staged.Work.TargetId;
            staged.Steps.Add(WorkflowStep, run.Started, 200, runValues);
            if (versions.TryGetValue(id, out var version))
            {
                var recordValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = id };
                if (version is { } v)
                {
                    recordValues["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                staged.Steps.Add(RecordsStep, completed, status, recordValues);
                var returned = new Dictionary<string, string>(runValues, StringComparer.Ordinal);
                foreach (var (name, value) in recordValues)
                {
                    returned[name] = value;
                }

                if (staged.Ids.Count > 0)
                {
                    returned[FileUploads.DatasetIdsValue] = string.Join(",", staged.Ids);
                }

                if (staged.Files > 0)
                {
                    returned["files"] = staged.Files.ToString(CultureInfo.InvariantCulture);
                }

                outcomes[staged.Index] = new DeliveryOutcome
                {
                    MetadataDelivered = true,
                    PayloadDelivered = staged.Work.DeliverPayload,
                    TargetVersion = version ?? staged.Work.ExistingVersion,
                    ChunksSent = staged.Files,
                    Detail = $"workflow run {run.RunId} {run.Status}",
                    Returned = returned,
                    Steps = staged.Steps.Steps,
                };
            }
            else if (retry.Contains(id))
            {
                var reason = $"storage asked for a retry when {id} was read back after workflow run {run.RunId}";
                staged.Steps.Add(RecordsStep, completed, status, null, reason);
                outcomes[staged.Index] = DeliveryOutcome.Failed(new DeliveryException(reason), staged.Steps.Steps);
            }
            else
            {
                missing.Add(staged);
            }
        }

        return missing;
    }

    private sealed record Staged(int Index, DeliveryWork Work, JsonObject Document, string Section, List<JsonObject> Datasets, List<string> Ids, DeliverySteps Steps, int Files);

    private sealed record WorkflowRun(string RunId, string? WorkflowId, string Status, string? StartTimeStamp, string? EndTimeStamp, DateTime Started)
    {
        public Dictionary<string, string> Values()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = RunId, ["status"] = Status };
            if (WorkflowId is not null)
            {
                values["workflowId"] = WorkflowId;
            }

            if (StartTimeStamp is not null)
            {
                values["startTimeStamp"] = StartTimeStamp;
            }

            if (EndTimeStamp is not null)
            {
                values["endTimeStamp"] = EndTimeStamp;
            }

            return values;
        }
    }
}
