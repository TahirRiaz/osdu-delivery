using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Core.Storage;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>A controllable clock for lease, backoff and cache tests.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;
    private long _timestamp;

    public TestClock(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        _timestamp = 0;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }
}

/// <summary>An in-memory SQLite catalog with the schema created from the model, shared across contexts on one open
/// connection: the ledger under test is the real <see cref="CatalogLedger"/> over the real catalog model.</summary>
public sealed class SqliteCatalog : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CatalogDbContext> _options;

    public SqliteCatalog()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<CatalogDbContext>().UseSqlite(_connection).Options;
        using var db = new CatalogDbContext(_options);
        db.Database.EnsureCreated();
    }

    public CatalogDbContext CreateDbContext() => new(_options);

    public CatalogLedger Ledger(TimeProvider? time = null) => new(CreateDbContext, time);

    public void Dispose() => _connection.Dispose();
}

/// <summary>Records every delivery and replays configured outcomes.</summary>
public sealed class FakeProtocol : IDeliveryProtocol
{
    private long _version = 1000;

    public List<DeliveryWork> Deliveries { get; } = [];

    public List<(string TargetId, long? Expected)> Verifies { get; } = [];

    public Func<DeliveryWork, Exception?>? FailWith { get; set; }

    public Func<string, VerifyResult>? VerifyWith { get; set; }

    /// <summary>Runs before each delivery; lets a test hold a delivery open (for example until it is cancelled).</summary>
    public Func<DeliveryWork, CancellationToken, Task>? Before { get; set; }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduWellLog;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        Deliveries.Add(work);
        if (Before is { } before)
        {
            await before(work, ct);
        }

        if (FailWith?.Invoke(work) is { } failure)
        {
            throw failure;
        }

        var chunks = 0;
        if (work.DeliverPayload && work.Payload is not null)
        {
            foreach (var chunk in await work.Payload.ListChunksAsync(ct))
            {
                await using var stream = await work.Payload.OpenAsync(chunk, ct);
                using var sink = new MemoryStream();
                await stream.CopyToAsync(sink, ct);
                chunks++;
            }
        }

        var version = work.DeliverMetadata ? Interlocked.Increment(ref _version) : work.ExistingVersion;
        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        if (version is { } v)
        {
            returned["version"] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (work.DeliverPayload)
        {
            returned["chunks"] = chunks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var now = DateTime.UtcNow;
        return new DeliveryOutcome
        {
            MetadataDelivered = work.DeliverMetadata,
            PayloadDelivered = work.DeliverPayload,
            TargetVersion = version,
            ChunksSent = chunks,
            Returned = returned,
            Steps = [new DeliveryStep("fake", now, now, 200, returned)],
        };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
    {
        Verifies.Add((targetId, expectedVersion));
        return Task.FromResult(VerifyWith?.Invoke(targetId) ?? new VerifyResult(VerifyOutcome.Match, expectedVersion, null));
    }

    public List<(string TargetId, bool Purge)> Deletes { get; } = [];

    public Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        Deletes.Add((targetId, purge));
        return Task.FromResult(new DeleteOutcome(true, false, purge ? "purged" : "logically deleted"));
    }

    /// <summary>The records the fake target holds, by target id, for read-backs.</summary>
    public Dictionary<string, JsonObject> Held { get; } = new(StringComparer.Ordinal);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => Task.FromResult(Held.TryGetValue(targetId, out var record) ? (JsonObject?)record.DeepClone().AsObject() : null);

    public bool Reachable { get; set; } = true;

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(new ProbeOutcome(Reachable, Reachable ? 200 : 503, Reachable ? "the service answered" : "service unavailable", "/about"));
}

/// <summary>Hands the engine a ready-made protocol instead of building one over HTTP.</summary>
public sealed class FakeProtocolFactory : IProtocolFactory
{
    private readonly IDeliveryProtocol _protocol;

    public FakeProtocolFactory(IDeliveryProtocol protocol)
    {
        _protocol = protocol;
    }

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default) => Task.FromResult(_protocol);
}

/// <summary>A scripted HTTP handler: matches requests by method and path, records bodies, returns canned responses.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri Uri, string? Body, string? ContentType, IReadOnlyDictionary<string, string> Headers);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<int, HttpResponseMessage> Respond)> _rules = [];
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);

    public List<Request> Calls { get; } = [];

    public FakeHttpHandler On(HttpMethod method, string pathSuffix, Func<int, HttpResponseMessage> respond)
    {
        _rules.Add((r => r.Method == method && r.RequestUri!.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeHttpHandler On(HttpMethod method, string pathSuffix, HttpStatusCode status, string? json = null)
        => On(method, pathSuffix, _ => Json(status, json));

    public static HttpResponseMessage Json(HttpStatusCode status, string? json)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Calls.Add(new Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
        foreach (var (match, respond) in _rules)
        {
            if (match(request))
            {
                var key = request.Method + " " + request.RequestUri!.AbsolutePath;
                var hit = _hits.GetValueOrDefault(key);
                _hits[key] = hit + 1;
                return respond(hit);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no rule for " + request.RequestUri) };
    }
}

/// <summary>Paths to the sample documents linked into the test output, and a ready-made engine context over them.</summary>
public static class Samples
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "samples");

    public static string Mappings => Path.Combine(Root, "mappings");

    public static string Snapshots => Path.Combine(Root, "snapshots");

    public static string Flow => Path.Combine(Root, "flows", "recall-welllog.yaml");

    public static string References => Path.Combine(Root, "references");

    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osdu-delivery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The platform file stores plus the delivery writers, exactly as the hosts register them.</summary>
    public static FileStoreRegistry Stores() => new([new LocalFileStore()], [new LocalFileWriter()], [new LocalFileReader()]);

    public static EngineContext Engine(ILedger? ledger, TimeProvider? time = null)
    {
        var stores = Stores();
        var loader = new DeliveryDocumentLoader();
        return new EngineContext(
            loader,
            new DropReader(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            ledger,
            time ?? TimeProvider.System,
            NullLoggerFactory.Instance,
            new DefaultProtocolFactory(new SecretResolver([new EnvSecretProvider()]), NullLoggerFactory.Instance),
            CompositeDeliveryListener.Empty);
    }

    /// <summary>The sample flow with the network target replaced by a local placeholder (tests never call OSDU).</summary>
    public static FlowDefinition LocalFlow(string dropLocation)
    {
        var flow = new DeliveryDocumentLoader().LoadFlow(Flow);
        return flow with
        {
            Source = flow.Source with { Location = dropLocation },
            Target = flow.Target with
            {
                Endpoint = "http://localhost:9/petrodb",
                Auth = new TargetAuth { Type = TargetAuthType.None },
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            },
            // SQLite in-memory shares one connection, so the test worker runs one record at a time and one renderer.
            Reliability = flow.Reliability with { Concurrency = 1, RenderParallelism = 1, Retry = flow.Reliability.Retry with { Attempts = 3, RecordBaseDelayMinutes = 1 } },
        };
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;
}

/// <summary>A compact OSDU-shaped schema for unit tests: allOf, a definitions ref, an array of objects, tags.</summary>
public static class TestSchema
{
    public const string Kind = "test:wks:work-product-component--Thing:1.0.0";

    public static SchemaSnapshot Build() => SchemaSnapshot.Parse(Kind, """
        {
          "$id": "https://example.org/Thing.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "acl": { "$ref": "#/definitions/AbstractAccessControlList.1.0.0" },
            "legal": { "type": "object", "properties": { "legaltags": { "type": "array", "items": { "type": "string" } }, "otherRelevantDataCountries": { "type": "array", "items": { "type": "string" } } }, "required": ["legaltags", "otherRelevantDataCountries"] },
            "tags": { "type": "object", "additionalProperties": { "type": "string" } },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/AbstractCommon.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "WellboreID": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Wellbore:[\\w\\-\\.\\:\\%]+:[0-9]*$", "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Wellbore" } ] },
                    "Depth": { "type": "number" },
                    "Count": { "type": "integer" },
                    "IsRegular": { "type": "boolean" },
                    "Unit": { "type": "string", "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "UnitOfMeasure" } ] },
                    "When": { "type": "string", "format": "date-time" },
                    "Curves": { "type": "array", "items": { "type": "object", "properties": { "CurveID": { "type": "string" }, "TopDepth": { "type": "number" } } } },
                    "Nested": { "type": "object", "properties": { "Inner": { "type": "string" } } }
                  },
                  "required": ["Depth"]
                }
              ]
            }
          },
          "required": ["kind", "acl", "legal"],
          "definitions": {
            "AbstractAccessControlList.1.0.0": { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } }, "viewers": { "type": "array", "items": { "type": "string" } } }, "required": ["owners", "viewers"] },
            "AbstractCommon.1.0.0": { "type": "object", "properties": { "Name": { "type": "string" }, "Description": { "type": "string" } } }
          }
        }
        """, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public static ReferenceSnapshot References() => new("refs-1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "m", ["Name"] = "metre" }),
            new ReferenceItem("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "ft", ["Name"] = "foot" }),
        ]),
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:abc", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FacilityName"] = "NO 1/1-A" }),
        ]),
    ]);

    public static RenderContext Context(string mapping = "Thing@1.0.0") => new()
    {
        MappingReference = mapping,
        ReferenceSnapshotVersion = "refs-1",
        SchemaSnapshotVersion = Build().Version,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
    };

    /// <summary>A minimal valid mapping over the test schema.</summary>
    public static MappingDefinition Mapping(params MappingProperty[] extra) => new()
    {
        Name = "Thing",
        Version = "1.0.0",
        Kind = Kind,
        Source = new MappingSource { System = "test", Scopes = ["curves"] },
        Identity = new MappingIdentity { NaturalKey = ["data.Name"] },
        Envelope = new MappingEnvelope
        {
            LegalTags = ["tag"],
            OtherRelevantDataCountries = ["NO"],
            Acl = new MappingAcl { Owners = ["owners@x"], Viewers = ["viewers@x"] },
        },
        Parameters = new Dictionary<string, MappingParameter>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = new() { Required = true } },
        Properties =
        [
            new MappingProperty { Target = "data.Name", Source = "name" },
            new MappingProperty { Target = "data.Depth", Source = "depth" },
            .. extra,
        ],
    };

    public static string MappingYaml => """
        documentType: mapping
        name: Thing
        version: 1.0.0
        kind: test:wks:work-product-component--Thing:1.0.0
        source: { system: test, scopes: [curves] }
        identity: { naturalKey: [data.Name] }
        envelope:
          legalTags: [tag]
          otherRelevantDataCountries: [NO]
          acl: { owners: [owners@x], viewers: [viewers@x] }
        parameters:
          dataPartition: { required: true }
        properties:
          - { target: data.Name, source: name }
          - { target: data.Depth, source: depth }
          - target: data.Unit
            source: unit
            transform: reference
            config: { type: UnitOfMeasure, matchBy: [Code] }
          - target: data.Curves
            collection: true
            scope: curves
            properties:
              - { target: CurveID, source: curve_id }
              - { target: TopDepth, source: top }
        """;

    public static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;
}
