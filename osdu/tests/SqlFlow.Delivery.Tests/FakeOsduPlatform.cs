using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A stateful stand-in for the OSDU services the Stage 6 routes call, built from their pinned contracts: Storage, Search,
/// Legal, the Dataset service with the staging locations each provider signs, the Workflow service with a scripted
/// engine behind it (what each workflow writes, the status it ends in and the XCom entries it pushes), and the Airflow
/// REST API. Every request is recorded as <see cref="FakeHttpHandler"/> records them, so the contract harness checks them.
/// </summary>
public sealed class FakeOsduPlatform : HttpMessageHandler
{
    public const string Endpoint = "http://localhost";

    public const string AirflowEndpoint = "http://localhost/airflow";

    /// <summary>Where the Wellbore DDMS sits under the platform, as the flows name it with ddmsRoot.</summary>
    public const string DdmsRoot = "/api/os-wellbore-ddms";

    /// <summary>How the Dataset service signs a location, per provider (osdu/specs/core/INTEGRATION.md section 2.5.1).</summary>
    public enum Staging
    {
        /// <summary>Azure: a blob SAS for a file, a Data Lake directory SAS for a collection.</summary>
        Azure,

        /// <summary>Core-plus on MinIO: a presigned PUT for a file, a POST policy without a key for a collection.</summary>
        Minio,

        /// <summary>Core-plus on S3: a POST policy naming the directory as its key.</summary>
        S3,

        /// <summary>Core-plus on Google Cloud Storage: a folder token for a collection.</summary>
        Google,

        /// <summary>IBM: temporary credentials for an endpoint the location does not name.</summary>
        Ibm,
    }

    /// <summary>What one workflow does when it runs: what it writes, how it ends, and what its tasks push.</summary>
    public sealed class Script
    {
        /// <summary>The status each poll after the first answers; the first answers <see cref="Pending"/>.</summary>
        public string Terminal { get; init; } = "finished";

        public string Pending { get; init; } = string.Empty;

        /// <summary>What the run does to the platform once it is triggered: the records it writes, the files it stores.</summary>
        public Action<FakeOsduPlatform, Run>? Effect { get; init; }

        /// <summary>The XCom entries its tasks push, by task and key.</summary>
        public Func<Run, IReadOnlyDictionary<(string Task, string Key), JsonNode?>>? XCom { get; init; }

        /// <summary>The task the Workflow service's latestInfo names as the run's latest.</summary>
        public string LatestTask { get; init; } = "update_status_finished_task";
    }

    public sealed class Run(string workflow, string runId, JsonObject context)
    {
        public string Workflow { get; } = workflow;

        public string RunId { get; } = runId;

        public JsonObject Context { get; } = context;

        public int Polls { get; set; }

        public IReadOnlyDictionary<(string Task, string Key), JsonNode?> XCom { get; set; } = new Dictionary<(string, string), JsonNode?>();
    }

    private readonly object _gate = new();
    private int _locations;

    public List<FakeHttpHandler.Request> Calls { get; } = [];

    public Dictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Purged { get; } = new(StringComparer.Ordinal);

    /// <summary>The workflows the partition registers, with what each does.</summary>
    public Dictionary<string, Script> Workflows { get; } = new(StringComparer.Ordinal);

    public List<Run> Runs { get; } = [];

    public Staging Provider { get; set; } = Staging.Azure;

    /// <summary>The bytes each staged file was uploaded with, by its path in the staging area.</summary>
    public Dictionary<string, byte[]> Staged { get; } = new(StringComparer.Ordinal);

    /// <summary>The bytes a registered dataset's retrieval URL serves, by dataset id.</summary>
    public Dictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);

    /// <summary>What a search finds, from its kind and query.</summary>
    public Func<string, string?, IReadOnlyList<string>>? Search { get; set; }

    /// <summary>The ids retrieval instructions leave out, as the service leaves out an id it cannot read.</summary>
    public HashSet<string> Unretrievable { get; } = new(StringComparer.Ordinal);

    /// <summary>The Airflow REST API version the fake answers on.</summary>
    public string AirflowVersion { get; set; } = "v1";

    /// <summary>The bulk data the DDMS holds for each record, as the bytes it was sent.</summary>
    public Dictionary<string, byte[]> Bulk { get; } = new(StringComparer.Ordinal);

    /// <summary>The DDMS refuses a record write whose bulk link differs from the one it holds (wellbore-ddms brief section 4).</summary>
    public int RefusedLinks { get; private set; }

    public void Register(string workflow, Script? script = null) => Workflows[workflow] = script ?? new Script();

    /// <summary>Stores a record as storage would, with the next version.</summary>
    public long Put(JsonObject record)
    {
        var id = record["id"]!.GetValue<string>();
        lock (_gate)
        {
            var version = Records.TryGetValue(id, out var existing) ? existing["version"]!.GetValue<long>() + 1 : 1_700_000_000_000_000L + Records.Count;
            var stored = (JsonObject)record.DeepClone();
            stored["version"] = version;
            Records[id] = stored;
            Removed.Remove(id);
            return version;
        }
    }

    public static JsonObject Record(string id, string kind, JsonObject? data = null) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["acl"] = new JsonObject { ["viewers"] = new JsonArray("data.default.viewers@opendes.example.com"), ["owners"] = new JsonArray("data.default.owners@opendes.example.com") },
        ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("opendes-public"), ["otherRelevantDataCountries"] = new JsonArray("NO") },
        ["data"] = data ?? new JsonObject { ["Name"] = id },
    };

    /// <summary>The requests that go to a location a service signed, which no OSDU contract describes.</summary>
    public static bool ToSignedLocation(FakeHttpHandler.Request request)
        => request.Uri.AbsolutePath.StartsWith("/staging/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/upload/storage/", StringComparison.Ordinal);

    public IEnumerable<FakeHttpHandler.Request> Triggers(string workflow)
        => Calls.Where(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith($"/workflow/{workflow}/workflowRun", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? body = null;
        byte[] bytes = [];
        if (request.Content is not null)
        {
            bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            body = request.Content.Headers.ContentType?.MediaType is { } media && (media.Contains("json", StringComparison.Ordinal) || media.StartsWith("text/", StringComparison.Ordinal) || media.StartsWith("multipart/", StringComparison.Ordinal))
                ? Encoding.UTF8.GetString(bytes)
                : (bytes.Length == 0 ? null : $"({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)");
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            Calls.Add(new FakeHttpHandler.Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
        }

        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        lock (_gate)
        {
            return Route(method, path, request.RequestUri, body, bytes, request);
        }
    }

    private HttpResponseMessage Route(string method, string path, Uri uri, string? body, byte[] bytes, HttpRequestMessage request)
    {
        const string workflowRoot = "/api/workflow/v1";
        if (path == workflowRoot + "/info" || path == "/api/dataset/v1/info" || path == "/api/storage/v2/info" || path == "/api/file/v2/info")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["version"] = "1.0.0" });
        }

        if (path.StartsWith(workflowRoot + "/workflow/", StringComparison.Ordinal))
        {
            return WorkflowService(method, path[(workflowRoot + "/workflow/").Length..].Split('/'), body);
        }

        if (path.StartsWith("/api/dataset/v1/", StringComparison.Ordinal))
        {
            return DatasetService(method, path["/api/dataset/v1/".Length..], uri, body);
        }

        if (path.StartsWith("/api/storage/v2/", StringComparison.Ordinal))
        {
            return StorageService(method, Uri.UnescapeDataString(path["/api/storage/v2/".Length..]), body);
        }

        if (path.StartsWith("/api/file/v2/files/", StringComparison.Ordinal))
        {
            return FileService(method, path["/api/file/v2/files/".Length..], body);
        }

        if (path.StartsWith(DdmsRoot + "/", StringComparison.Ordinal))
        {
            return Ddms(method, path[DdmsRoot.Length..], bytes, body);
        }

        if (path == "/api/search/v2/query_with_cursor" || path == "/api/search/v2/query")
        {
            var query = JsonNode.Parse(body!)!;
            var ids = Search?.Invoke(query["kind"]!.GetValue<string>(), query["query"]?.GetValue<string>()) ?? [];
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
                ["totalCount"] = ids.Count,
            });
        }

        if (path == "/api/legal/v1/legaltags:validate")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["invalidLegalTags"] = new JsonArray() });
        }

        if (path.StartsWith("/staging/", StringComparison.Ordinal))
        {
            return StagingArea(method, path, uri, bytes, request);
        }

        if (path.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal))
        {
            var name = HttpUtility.ParseQueryString(uri.Query)["name"]!;
            Staged["/staging/gcs/" + name] = bytes;
            return Json(HttpStatusCode.OK, new JsonObject { ["name"] = name });
        }

        if (path.StartsWith("/download/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/download/".Length..]);
            return Content.TryGetValue(id, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (path.StartsWith("/airflow/", StringComparison.Ordinal))
        {
            return Airflow(method, path["/airflow".Length..], request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route for " + method + " " + path) };
    }

    private HttpResponseMessage WorkflowService(string method, string[] parts, string? body)
    {
        var workflow = Uri.UnescapeDataString(parts[0]);
        if (!Workflows.TryGetValue(workflow, out var script))
        {
            return Error(HttpStatusCode.NotFound, $"Workflow: {workflow} doesn't exist");
        }

        if (parts.Length == 1 && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["workflowName"] = workflow });
        }

        if (parts.Length == 2 && parts[1] == "workflowRun" && method == "POST")
        {
            var trigger = JsonNode.Parse(body!)!.AsObject();
            var runId = trigger["runId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("D");
            if (Runs.Any(r => r.RunId == runId))
            {
                return Error(HttpStatusCode.Conflict, "A Workflow with the given name already exists.");
            }

            var run = new Run(workflow, runId, (JsonObject)(trigger["executionContext"]?.DeepClone() ?? new JsonObject()));
            Runs.Add(run);
            script.Effect?.Invoke(this, run);
            run.XCom = script.XCom?.Invoke(run) ?? new Dictionary<(string, string), JsonNode?>();
            return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["runId"] = runId, ["status"] = "submitted" });
        }

        var found = parts.Length >= 3 ? Runs.FirstOrDefault(r => r.Workflow == workflow && r.RunId == Uri.UnescapeDataString(parts[2])) : null;
        if (found is null)
        {
            return Error(HttpStatusCode.NotFound, "no such run");
        }

        if (parts.Length == 4 && parts[3] == "latestInfo")
        {
            var xcom = new JsonObject();
            foreach (var ((task, key), value) in found.XCom.Where(x => x.Key.Task == script.LatestTask))
            {
                // The Workflow service gives each entry as the text Airflow 2 renders.
                xcom[key] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : PythonRepr(value);
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["dag_id"] = workflow, ["task_id"] = script.LatestTask, ["state"] = "success", ["xcom"] = xcom });
        }

        found.Polls++;
        var status = found.Polls == 1 && script.Pending.Length > 0 ? script.Pending : script.Terminal;
        return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["runId"] = found.RunId, ["status"] = status });
    }

    private HttpResponseMessage DatasetService(string method, string operation, Uri uri, string? body)
    {
        if (operation == "storageInstructions" && method == "POST")
        {
            var type = HttpUtility.ParseQueryString(uri.Query)["kindSubType"]!;
            var n = (++_locations).ToString(CultureInfo.InvariantCulture);
            var collection = type.StartsWith("dataset--FileCollection.", StringComparison.Ordinal);
            var location = (Provider, collection) switch
            {
                (Staging.Azure, false) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/blob/f-{n}?sv=2021-06-08&sr=b&sp=cw&sig=secret", ["fileSource"] = $"/staging/blob/f-{n}", ["createdBy"] = "delivery" },
                (Staging.Azure, true) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/dfs/d-{n}?sv=2021-06-08&sr=d&sp=racwl&sdd=1&sig=secret", ["fileCollectionSource"] = $"/d-{n}", ["createdBy"] = "delivery" },
                (Staging.Minio or Staging.S3 or Staging.Google, false) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/obm/u-{n}/f-{n}?X-Amz-Signature=secret", ["fileSource"] = $"/u-{n}/f-{n}", ["createdBy"] = "delivery" },
                (Staging.Minio, true) => new JsonObject
                {
                    ["url"] = $"{Endpoint}/staging/minio/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["x-amz-algorithm"] = "AWS4-HMAC-SHA256", ["x-amz-credential"] = "cred", ["x-amz-date"] = "20260917T000000Z", ["policy"] = "cG9saWN5", ["x-amz-signature"] = "secret" },
                },
                (Staging.S3, true) => new JsonObject
                {
                    ["url"] = $"{Endpoint}/staging/s3/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["policy"] = "cG9saWN5", ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256", ["X-Amz-Credential"] = "cred", ["X-Amz-Date"] = "20260917T000000Z", ["X-Amz-Signature"] = "secret", ["key"] = $"c{n}/" },
                },
                (Staging.Google, true) => new JsonObject
                {
                    ["url"] = "https://storage.googleapis.com/staging-bucket/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["bucket"] = "staging-bucket", ["filepath"] = $"c{n}/", ["connectionString"] = "folder-token" },
                },
                // IBM: temporary credentials, twice, and the object's unsigned address, with no endpoint for either.
                _ => new JsonObject
                {
                    ["connectionString"] = "AccessKeyId=a;SecretAccessKey=b;SessionToken=c;Expiration=2026-09-17T01:00:00Z",
                    ["credentials"] = new JsonObject { ["accessKeyId"] = "a", ["secretAccessKey"] = "b", ["sessionToken"] = "c", ["expiration"] = "2026-09-17T01:00:00Z" },
                    ["unsignedUrl"] = $"s3://bucket/c{n}",
                },
            };
            var provider = Provider switch { Staging.Azure => "AZURE", Staging.Minio => "ANTHOS", Staging.S3 => "S3", Staging.Google => "GCP", _ => "IBM" };
            return Json(HttpStatusCode.OK, new JsonObject { ["storageLocation"] = location, ["providerKey"] = provider });
        }

        if (operation == "registerDataset" && method == "PUT")
        {
            var registered = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!["datasetRegistries"]!.AsArray())
            {
                var record = node!.AsObject();
                var id = record["id"]!.GetValue<string>();
                var kind = record["kind"]!.GetValue<string>();
                if (!string.Equals(id.Split(':')[1], kind.Split(':')[2], StringComparison.OrdinalIgnoreCase))
                {
                    return Error(HttpStatusCode.BadRequest, "Invalid record id");
                }

                Put(record);
                registered.Add(Records[id].DeepClone());
            }

            return Json(HttpStatusCode.Created, new JsonObject { ["datasetRegistries"] = registered });
        }

        if (operation == "retrievalInstructions" && method == "POST")
        {
            var datasets = new JsonArray();
            foreach (var id in JsonNode.Parse(body!)!["datasetRegistryIds"]!.AsArray().Select(i => i!.GetValue<string>()))
            {
                if (Records.ContainsKey(id) && !Removed.Contains(id) && !Unretrievable.Contains(id))
                {
                    datasets.Add(new JsonObject
                    {
                        ["datasetRegistryId"] = id,
                        ["providerKey"] = "AZURE",
                        ["retrievalProperties"] = new JsonObject { ["signedUrl"] = $"{Endpoint}/download/{Uri.EscapeDataString(id)}?sig=secret", ["createdBy"] = "delivery" },
                    });
                }
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["datasets"] = datasets });
        }

        if (operation.StartsWith("metadataRecord/", StringComparison.Ordinal) && operation.EndsWith("/softDelete", StringComparison.Ordinal) && method == "POST")
        {
            var id = Uri.UnescapeDataString(operation["metadataRecord/".Length..^"/softDelete".Length]);
            if (!Records.ContainsKey(id) || Removed.Contains(id))
            {
                return Error(HttpStatusCode.NotFound, "Record not found");
            }

            Removed.Add(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.NotFound, "no dataset route " + operation);
    }

    private HttpResponseMessage StorageService(string method, string operation, string? body)
    {
        if (operation == "records" && method == "PUT")
        {
            var ids = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!.AsArray())
            {
                var version = Put(node!.AsObject());
                ids.Add(node["id"]!.GetValue<string>() + ":" + version.ToString(CultureInfo.InvariantCulture));
            }

            return Json(HttpStatusCode.Created, new JsonObject { ["recordCount"] = ids.Count, ["recordIdVersions"] = ids });
        }

        if (operation == "query/records" && method == "POST")
        {
            var found = new JsonArray();
            var invalid = new JsonArray();
            foreach (var id in JsonNode.Parse(body!)!["records"]!.AsArray().Select(i => i!.GetValue<string>()))
            {
                if (Records.TryGetValue(id, out var record) && !Removed.Contains(id))
                {
                    found.Add(record.DeepClone());
                }
                else
                {
                    invalid.Add(id);
                }
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["records"] = found, ["invalidRecords"] = invalid, ["retryRecords"] = new JsonArray() });
        }

        if (operation == "records/delete" && method == "POST")
        {
            foreach (var id in JsonNode.Parse(body!)!.AsArray().Select(i => i!.GetValue<string>()))
            {
                Removed.Add(id);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (operation.StartsWith("records/", StringComparison.Ordinal))
        {
            var rest = operation["records/".Length..];
            if (rest.EndsWith(":delete", StringComparison.Ordinal) && method == "POST")
            {
                var id = rest[..^":delete".Length];
                if (!Records.ContainsKey(id) || Removed.Contains(id))
                {
                    return Error(HttpStatusCode.NotFound, "Record not found");
                }

                Removed.Add(id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (rest.EndsWith("/versions", StringComparison.Ordinal) && method == "DELETE")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == "DELETE")
            {
                if (!Records.Remove(rest))
                {
                    return Error(HttpStatusCode.NotFound, "Record not found");
                }

                Purged.Add(rest);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == "GET")
            {
                return Records.TryGetValue(rest, out var record) && !Removed.Contains(rest)
                    ? Json(HttpStatusCode.OK, record.DeepClone())
                    : Error(HttpStatusCode.NotFound, "Record not found");
            }
        }

        return Error(HttpStatusCode.NotFound, "no storage route " + operation);
    }

    private HttpResponseMessage StagingArea(string method, string path, Uri uri, byte[] bytes, HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (path.StartsWith("/staging/dfs/", StringComparison.Ordinal))
        {
            // The Data Lake create, append and flush of a file under a directory SAS.
            if (request.Headers.TryGetValues("x-ms-version", out var versions) is false || versions.Single() != query["sv"])
            {
                return Error(HttpStatusCode.BadRequest, "x-ms-version must state the SAS version");
            }

            switch (method, query["resource"], query["action"])
            {
                case ("PUT", "file", null):
                    Staged[path] = [];
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case ("PATCH", null, "append"):
                    var position = long.Parse(query["position"]!, CultureInfo.InvariantCulture);
                    if (!Staged.TryGetValue(path, out var sofar) || sofar.LongLength != position)
                    {
                        return Error(HttpStatusCode.BadRequest, "InvalidFlushPosition");
                    }

                    Staged[path] = [.. sofar, .. bytes];
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                case ("PATCH", null, "flush"):
                    return long.Parse(query["position"]!, CultureInfo.InvariantCulture) == Staged[path].LongLength
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        : Error(HttpStatusCode.BadRequest, "InvalidFlushPosition");
                default:
                    return Error(HttpStatusCode.BadRequest, "not a Data Lake path operation");
            }
        }

        if ((path == "/staging/minio/" || path == "/staging/s3/") && method == "POST")
        {
            var form = Encoding.UTF8.GetString(bytes);
            var key = FormField(form, "key") ?? string.Empty;
            if (FormField(form, "policy") is null || FormField(form, "file") is null)
            {
                return Error(HttpStatusCode.BadRequest, "a POST policy upload names its policy and its file");
            }

            Staged[path + key] = bytes;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == "PUT")
        {
            if (path.StartsWith("/staging/blob/", StringComparison.Ordinal) && !request.Headers.Contains("x-ms-blob-type"))
            {
                return Error(HttpStatusCode.BadRequest, "MissingRequiredHeader");
            }

            Staged[path] = bytes;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        return Error(HttpStatusCode.BadRequest, "not a staging operation");
    }

    /// <summary>
    /// The File service calls of the file routes: a landing-zone location, a dataset record registered with an id the
    /// service mints, and its removal with its file.
    /// </summary>
    private HttpResponseMessage FileService(string method, string operation, string? body)
    {
        if (operation == "uploadURL" && method == "GET")
        {
            var n = (++_locations).ToString(CultureInfo.InvariantCulture);
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["FileID"] = "file-" + n,
                ["Location"] = new JsonObject { ["SignedURL"] = $"{Endpoint}/staging/landing/l-{n}?sig=secret", ["FileSource"] = $"/staging/landing/l-{n}" },
            });
        }

        if (operation == "metadata" && method == "POST")
        {
            var record = JsonNode.Parse(body!)!.AsObject();
            var id = $"opendes:dataset--File.Generic:minted-{(++_locations).ToString(CultureInfo.InvariantCulture)}";
            record["id"] = id;
            Put(record);
            return Json(HttpStatusCode.Created, new JsonObject { ["id"] = id });
        }

        if (operation.EndsWith("/metadata", StringComparison.Ordinal) && method == "DELETE")
        {
            var id = Uri.UnescapeDataString(operation[..^"/metadata".Length]);
            if (!Records.ContainsKey(id) || Removed.Contains(id))
            {
                return Error(HttpStatusCode.NotFound, "Record Not Found");
            }

            Removed.Add(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.NotFound, "no file route " + operation);
    }

    /// <summary>
    /// The Wellbore DDMS v3 calls the routes make: records written and read through a collection, bulk data written in one
    /// request (which sets the record's bulk link and gives it a new version), its description read back, and deletes.
    /// </summary>
    private HttpResponseMessage Ddms(string method, string path, byte[] bytes, string? body)
    {
        if (path == "/about" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["service"] = "Wellbore DDMS", ["version"] = "0.2" });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "ddms" || parts[1] != "v3")
        {
            return Error(HttpStatusCode.NotFound, "no DDMS route " + path);
        }

        if (parts.Length == 3 && method == "POST")
        {
            var versions = new JsonArray();
            var ids = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!.AsArray())
            {
                var record = node!.AsObject();
                var id = record["id"]!.GetValue<string>();
                var held = Records.TryGetValue(id, out var existing) ? existing["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>() : null;
                var sent = record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();
                if (held != sent)
                {
                    RefusedLinks++;
                    return Error(HttpStatusCode.BadRequest, "bulkURI differs from the latest version");
                }

                var version = Put(record);
                ids.Add(id);
                versions.Add(id + ":" + version.ToString(CultureInfo.InvariantCulture));
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["recordCount"] = ids.Count, ["recordIds"] = ids, ["recordIdVersions"] = versions, ["skippedRecordIds"] = new JsonArray() });
        }

        var recordId = Uri.UnescapeDataString(parts[3]);
        if (parts.Length == 4)
        {
            switch (method)
            {
                case "GET":
                    return Records.TryGetValue(recordId, out var record) && !Removed.Contains(recordId)
                        ? Json(HttpStatusCode.OK, record.DeepClone())
                        : Error(HttpStatusCode.NotFound, "record not found");
                case "DELETE":
                    if (!Records.ContainsKey(recordId))
                    {
                        return Error(HttpStatusCode.NotFound, "record not found");
                    }

                    Removed.Add(recordId);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts.Length == 5 && parts[4] == "data")
        {
            if (method == "POST")
            {
                if (!Records.TryGetValue(recordId, out var record))
                {
                    return Error(HttpStatusCode.NotFound, "record not found");
                }

                Bulk[recordId] = bytes;
                var linked = (JsonObject)record.DeepClone();
                linked["data"]!["ExtensionProperties"] = new JsonObject { ["wdms"] = new JsonObject { ["bulkURI"] = "urn:wdms-1:uuid:" + Guid.NewGuid().ToString("D") } };
                Put(linked);
                return Json(HttpStatusCode.OK, new JsonObject());
            }

            if (method == "GET")
            {
                return Bulk.ContainsKey(recordId)
                    ? Json(HttpStatusCode.OK, new JsonObject { ["numberOfRows"] = 1, ["columns"] = new JsonArray("MD", "GR") })
                    : Error(HttpStatusCode.NotFound, "no bulk data");
            }
        }

        return Error(HttpStatusCode.NotFound, "no DDMS route " + path);
    }

    private HttpResponseMessage Airflow(string method, string path, HttpRequestMessage request)
    {
        if (path == "/auth/token" && method == "POST")
        {
            return Json(HttpStatusCode.Created, new JsonObject { ["access_token"] = "eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9.sig" });
        }

        var prefix = $"/api/{AirflowVersion}/dags/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || method != "GET")
        {
            return Error(HttpStatusCode.NotFound, "no airflow route");
        }

        var expected = AirflowVersion == "v2" ? "Bearer eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9.sig" : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("airflow-user:airflow-password"));
        if (request.Headers.Authorization?.ToString() != expected)
        {
            return Error(HttpStatusCode.Unauthorized, "unauthenticated");
        }

        // dags/{dag}/dagRuns/{run}/taskInstances/{task}/xcomEntries/{key}
        var parts = path[prefix.Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        var run = Runs.FirstOrDefault(r => r.Workflow == parts[0] && r.RunId == parts[2]);
        if (run is null || !run.XCom.TryGetValue((parts[4], parts[6]), out var value))
        {
            return Error(HttpStatusCode.NotFound, "no xcom entry");
        }

        var rendered = AirflowVersion == "v1"
            ? (JsonNode?)(value is JsonValue text && text.TryGetValue<string>(out var s) ? s : PythonRepr(value))
            : value?.DeepClone();
        return Json(HttpStatusCode.OK, new JsonObject { ["key"] = parts[6], ["task_id"] = parts[4], ["dag_id"] = parts[0], ["value"] = rendered });
    }

    /// <summary>The text Airflow 2 gives for a stored Python value: <c>str()</c> of it, with single quotes.</summary>
    public static string PythonRepr(JsonNode? value) => value switch
    {
        null => "None",
        JsonArray array => "[" + string.Join(", ", array.Select(PythonRepr)) + "]",
        JsonObject obj => "{" + string.Join(", ", obj.Select(kv => "'" + kv.Key + "': " + PythonRepr(kv.Value))) + "}",
        JsonValue text when text.TryGetValue<string>(out var s) => "'" + s + "'",
        _ => value.ToJsonString(),
    };

    private static string? FormField(string form, string name)
    {
        var at = -1;
        foreach (var marker in new[] { $"name={name}\r\n", $"name=\"{name}\"", $"name={name};" })
        {
            at = form.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                break;
            }
        }

        if (at < 0)
        {
            return null;
        }

        var start = form.IndexOf("\r\n\r\n", at, StringComparison.Ordinal) + 4;
        var end = form.IndexOf("\r\n", start, StringComparison.Ordinal);
        return form[start..end];
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode node)
        => new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message)
        => Json(status, new JsonObject { ["code"] = (int)status, ["reason"] = status.ToString(), ["message"] = message });
}

/// <summary>A record's files held in memory, one payload set, each opened fresh for every request.</summary>
internal sealed class MemoryFiles(params (string Name, string Text)[] files) : IPayloadSource
{
    public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PayloadFile>>(files.Select((f, i) => new PayloadFile(i, "mem://files/" + f.Name, Encoding.UTF8.GetByteCount(f.Text))).ToList());

    public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(files[file.Index].Text), writable: false));
    }
}
