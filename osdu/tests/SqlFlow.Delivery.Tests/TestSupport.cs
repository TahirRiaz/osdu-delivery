using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Sources;

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

/// <summary>
/// An in-memory SQLite copy of the module's own database (schema <c>osdu</c>), created from the model and shared across
/// contexts on one open connection: the ledger under test is the real <see cref="OsduLedger"/> over the real model.
/// </summary>
public sealed class SqliteOsdu : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OsduDbContext> _options;

    public SqliteOsdu()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OsduDbContext>().UseSqlite(_connection).Options;
        using var db = new OsduDbContext(_options);
        db.Database.EnsureCreated();
    }

    public OsduDbContext CreateDbContext() => new(_options);

    public OsduLedger Ledger(TimeProvider? time = null) => new(CreateDbContext, time);

    public OsduTemplateStore Templates(TimeProvider? time = null) => new(CreateDbContext, time);

    public OsduCacheStore Caches() => new(CreateDbContext);

    /// <summary>
    /// Declares what <paramref name="flowName"/> caches for <paramref name="scope"/> exactly as the repository sync leaves it:
    /// one <c>osdu.CacheDefinition</c> row per type, replacing every row the flow had. No types declares nothing for the flow.
    /// </summary>
    public async Task DeclareCacheAsync(string scope, string flowName, params ReferenceTypeSpec[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        await using var db = CreateDbContext();
        await db.DeliveryCacheDefinitions.Where(d => d.FlowName == flowName).ExecuteDeleteAsync();
        var repoId = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var type in types)
        {
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = flowName,
                Scope = scope,
                Origin = CacheOrigins.Text(type.Origin),
                Endpoint = type.Origin == CacheOrigin.Osdu ? "https://osdu.example.test" : null,
                Connection = type.Origin == CacheOrigin.Table ? "${env:INGESTION_DB}" : null,
                SourceObject = type.Table,
                KeyField = type.Key,
                DictionaryPath = type.DictionaryPath,
                RelativePath = "cache/" + flowName + ".yaml",
                Name = type.Name,
                EntityType = type.EntityType,
                Kind = type.Kind,
                Query = type.Origin == CacheOrigin.Osdu ? type.Query : null,
                FieldsJson = new JsonArray(type.Fields.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["as"] = f.Name }).ToArray()).ToJsonString(),
                OnChange = type.OnChange == CacheChangeMode.Approve ? "approve" : "auto",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// An in-memory SQLite copy of the platform's catalog, for the one seam the module shares with it: the catalog sync
/// extension, which joins the catalog's own transaction.
/// </summary>
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

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// One version of one partition's cache held in memory, as a render reads it. The engine suites share the sample cache
/// through it, so no suite reads a database another suite is writing on the same SQLite connection; the store itself is
/// covered by its own suite.
/// </summary>
public sealed class FixedCacheStore : ICacheStore
{
    private readonly string _scope;
    private readonly string _flowName;
    private readonly ReferenceSnapshot _snapshot;
    private readonly CacheDeclaration _declaration;

    /// <param name="scope">The partition whose cache the store holds.</param>
    /// <param name="flowName">The cache flow that wrote the one version.</param>
    /// <param name="snapshot">The one version.</param>
    /// <param name="declaration">What the partition's cache flows declare; none when null.</param>
    public FixedCacheStore(string scope, string flowName, ReferenceSnapshot snapshot, CacheDeclaration? declaration = null)
    {
        _scope = scope;
        _flowName = flowName;
        _snapshot = snapshot;
        _declaration = declaration ?? CacheDeclaration.None(scope);
    }

    public Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default)
        => Task.FromResult(scope == _scope ? _snapshot.Version : null);

    public Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default)
        => Task.FromResult(scope == _scope && version == _snapshot.Version ? _snapshot : null);

    public Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CacheVersionInfo>>(scope == _scope ? [Info()] : []);

    public Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default)
        => Task.FromResult(scope == _scope && (version is null || version == _snapshot.Version) ? Info() : null);

    private CacheVersionInfo Info()
        => new(
            _scope, _snapshot.Version, 1, _snapshot.CapturedUtc.UtcDateTime, true, null, null, "tests", "sample files", _flowName,
            _snapshot.Types.Sum(t => (long)t.Items.Count), _snapshot.Types.Select(t => new CacheVersionType(t.Name, t.EntityType, t.Items.Count)).ToList(),
            _snapshot.SystemProperties);

    public Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default)
        => Task.FromResult(scope == _scope ? _declaration : CacheDeclaration.None(scope));

    public Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default)
        => throw new InvalidOperationException("The fixed sample cache is read-only; write versions through the module's cache store.");
}

/// <summary>
/// Records every delivery and replays configured outcomes. Safe to call from many workers at once: the recordings are
/// written under a lock, and the store keeps how many deliveries were in flight at once, overall and per node, where a
/// node is whatever <see cref="CurrentNode"/> names on the calling flow of control (a fan-out member, say).
/// </summary>
public sealed class FakeProtocol : IDeliveryProtocol
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _inFlightByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _maxInFlightByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deliveriesByNode = new(StringComparer.Ordinal);
    private long _version = 1000;
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>The node a delivery made on this flow of control is counted under; unset is <c>local</c>.</summary>
    public static AsyncLocal<string?> CurrentNode { get; } = new();

    public List<DeliveryWork> Deliveries { get; } = [];

    /// <summary>The most deliveries that were in flight at the same moment.</summary>
    public int MaxInFlight
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    /// <summary>Per node, the most deliveries it had in flight at the same moment.</summary>
    public IReadOnlyDictionary<string, int> MaxInFlightByNode
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_maxInFlightByNode, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Per node, how many deliveries it made.</summary>
    public IReadOnlyDictionary<string, int> DeliveriesByNode
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_deliveriesByNode, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The correlation id in effect when each delivery was made, in the order of <see cref="Deliveries"/>.</summary>
    public List<string?> Correlations { get; } = [];

    public List<(string TargetId, long? Expected)> Verifies { get; } = [];

    public Func<DeliveryWork, Exception?>? FailWith { get; set; }

    public Func<string, VerifyResult>? VerifyWith { get; set; }

    /// <summary>Runs before each delivery; lets a test hold a delivery open (for example until it is cancelled).</summary>
    public Func<DeliveryWork, CancellationToken, Task>? Before { get; set; }

    public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var node = CurrentNode.Value ?? "local";
        lock (_gate)
        {
            Deliveries.Add(work);
            Correlations.Add(OsduCorrelation.Current);
            _deliveriesByNode[node] = _deliveriesByNode.GetValueOrDefault(node) + 1;
            _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
            var here = _inFlightByNode[node] = _inFlightByNode.GetValueOrDefault(node) + 1;
            _maxInFlightByNode[node] = Math.Max(_maxInFlightByNode.GetValueOrDefault(node), here);
        }

        try
        {
            return await DeliverInFlightAsync(work, ct);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                _inFlightByNode[node]--;
            }
        }
    }

    private async Task<DeliveryOutcome> DeliverInFlightAsync(DeliveryWork work, CancellationToken ct)
    {
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
            foreach (var file in await work.Payload.ListChunksAsync(ct))
            {
                await using var stream = await work.Payload.OpenAsync(file, ct);
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
        lock (_gate)
        {
            Verifies.Add((targetId, expectedVersion));
        }

        return Task.FromResult(VerifyWith?.Invoke(targetId) ?? new VerifyResult(VerifyOutcome.Match, expectedVersion, null));
    }

    public List<(string TargetId, RemovalScope Scope)> Deletes { get; } = [];

    /// <summary>Target ids the fake target no longer holds, so a removal of them reports them already gone.</summary>
    public HashSet<string> Gone { get; } = new(StringComparer.Ordinal);

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Deletes.Add((targetId, scope));
        }

        return Task.FromResult(Gone.Contains(targetId)
            ? new DeleteOutcome(false, true, "record not found in OSDU")
            : new DeleteOutcome(true, false, scope.ToString().ToLowerInvariant()));
    }

    /// <summary>The records the fake target holds, by target id, for read-backs.</summary>
    public Dictionary<string, JsonObject> Held { get; } = new(StringComparer.Ordinal);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => Task.FromResult(Held.TryGetValue(targetId, out var record) ? (JsonObject?)record.DeepClone().AsObject() : null);

    public bool Reachable { get; set; } = true;

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(new ProbeOutcome(Reachable, Reachable ? 200 : 503, Reachable ? "the service answered" : "service unavailable", "/about"));
}

/// <summary>A listener that keeps every event it is given, for tests that follow what a worker reported.</summary>
public sealed class RecordingListener : IDeliveryListener
{
    private readonly object _gate = new();
    private readonly List<DeliveryEvent> _events = [];

    public IReadOnlyList<DeliveryEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(evt);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands the engine a ready-made protocol instead of building one over HTTP.</summary>
public sealed class FakeProtocolFactory : IProtocolFactory
{
    private readonly IDeliveryProtocol _protocol;

    public FakeProtocolFactory(IDeliveryProtocol protocol)
    {
        _protocol = protocol;
    }

    /// <summary>Stands in for a protocol that cannot be built at all: an unresolved secret, an identity out of reach.</summary>
    public Func<Exception?>? FailWith { get; set; }

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default)
        => FailWith?.Invoke() is { } failure ? Task.FromException<IDeliveryProtocol>(failure) : Task.FromResult(_protocol);
}

/// <summary>
/// Builds the real protocols over one fake OSDU (<paramref name="handler"/>), the way the node's factory builds them over
/// the network, each over an HTTP runtime of its own that the factory disposes.
/// </summary>
public sealed class FakeOsduProtocols(HttpMessageHandler handler) : IProtocolFactory, IDisposable
{
    private readonly List<HttpRuntime> _runtimes = [];
    private readonly object _gate = new();

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var runtime = new HttpRuntime(flow.Reliability, new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        lock (_gate)
        {
            _runtimes.Add(runtime);
        }

        return ProtocolFactory.CreateAsync(flow, runtime, new SecretResolver([new EnvSecretProvider()]), NullLoggerFactory.Instance, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var runtime in _runtimes)
            {
                runtime.Dispose();
            }

            _runtimes.Clear();
        }
    }
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

    /// <summary>A rule over the whole request, for URLs the test cannot know in advance (a run id the protocol chose).</summary>
    public FakeHttpHandler OnMatch(Func<HttpRequestMessage, bool> match, Func<int, HttpResponseMessage> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

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
        ArgumentNullException.ThrowIfNull(request);
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
    /// <summary>
    /// The sample estate declares where it delivers the way a real estate does, as ${env:...} references a node holds
    /// (osdu/docs/environment-variables.md), so the suites supply them exactly as a node would. The values are the ones
    /// every mapping fixture pins, which is what keeps a rendered document byte-identical to the fixture's expected
    /// record. Set for the whole assembly before any test reads a sample document, and never overwriting a value the
    /// process was started with, so a run against a real estate keeps its own.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSampleEstateReferences()
    {
        Reference("OSDU_DATA_PARTITION", "dev");
        Reference("OSDU_ACL_OWNER", "data.default.owners@dev.dataservices.energy");
        Reference("OSDU_ACL_VIEWER", "data.default.viewers@dev.dataservices.energy");
        Reference("OSDU_LEGAL_TAG", "dev-reference-data-default");

        static void Reference(string name, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    public const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    public const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";

    /// <summary>The kind the sample trajectory mapping renders.</summary>
    public const string WellboreTrajectoryKind = "osdu:wks:work-product-component--WellboreTrajectory:1.3.0";

    /// <summary>The connection reference the suites give a flow whose source is the in-memory ingestion tables.</summary>
    public const string MemoryConnection = "mem://ingestion";

    /// <summary>
    /// The samples directory as the repository lays it out: a folder per source, and beside them the bundled schemas
    /// that are not repository content at all (see <see cref="TemplateFiles"/>).
    /// </summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "samples");

    /// <summary>
    /// The wells source: one folder holding its flows, the mappings they pin, the cache they resolve against and the
    /// drop-off folder the pre-ingestion flows read. A repository is laid out per source, so this is what a sync sees
    /// as one project.
    /// </summary>
    public static string Source => Path.Combine(Root, "wells");

    public static string Mappings => Path.Combine(Source, "mappings");

    /// <summary>
    /// The bundled OSDU schemas the sample mappings pin, as a template import reads them. They sit beside the source
    /// folders rather than inside one, because a template is a catalog object captured from OSDU's schema service, not
    /// a file a repository sync reads; these copies exist so a suite can save templates with no OSDU to capture from.
    /// </summary>
    public static string TemplateFiles => Path.Combine(Root, "templates");

    public static string Flow => Path.Combine(Source, "flows", "wells-welllog-03-header-delivery.yaml");

    /// <summary>The wellbore master-data flow of the sample estate.</summary>
    public static string WellboreFlowFile => Path.Combine(Source, "flows", "wells-wellbore-03-header-delivery.yaml");

    /// <summary>The sample cache flow: what the sample cache holds.</summary>
    public static string CacheFlow => Path.Combine(Source, "cache", "wells-osdu-00-reference-cache.yaml");

    /// <summary>The sample lookups cache flow: the lookup tables the sample mappings translate source values through.</summary>
    public static string LookupsCacheFlow => Path.Combine(Source, "cache", "wells-lookups-00-cache.yaml");

    /// <summary>
    /// The sample cache records, one file per cached type. They sit beside the source folders rather than inside one,
    /// for the same reason <see cref="TemplateFiles"/> does: a cache lives in the module's database, captured there by
    /// a run of the flow that defines it, so a repository holds that flow document and nothing else about the cache.
    /// These files exist so a suite can fill a cache with no OSDU platform to capture from.
    /// </summary>
    public static string CacheRecords => Path.Combine(Root, "cache-records");

    /// <summary>The source's drop-off folder: the files the pre-ingestion flows read and the payloads the delivery streams.</summary>
    public static string Data => Path.Combine(Source, "data");

    /// <summary>
    /// The partition the sample flows search and deliver to, whose cache the sample delivery flow reads. A cache is keyed
    /// by the partition a flow actually reaches, so this is the resolved partition even though the estate's documents name
    /// it as a reference: capture and read both resolve, so both agree.
    /// </summary>
    public const string SampleCacheScope = SamplePartition;

    /// <summary>
    /// What that reference resolves to on a node, and so what the ids the sample estate mints carry: the render
    /// parameter a mapping composes an id from is resolved before the document is rendered, while the cache a flow
    /// reads is scoped by the partition as the document writes it.
    /// </summary>
    public const string SamplePartition = "dev";

    /// <summary>The name of the sample cache flow, which fills the cache of <see cref="SampleCacheScope"/>.</summary>
    public const string SampleCacheFlowName = "wells-osdu-00-reference-cache";

    /// <summary>The name of the sample lookups cache flow, which holds the lookup tables in the same cache.</summary>
    public const string SampleLookupsFlowName = "wells-lookups-00-cache";

    /// <summary>When the sample cache records were captured: the version label the sample cache is imported under.</summary>
    public static readonly DateTimeOffset SampleCacheCaptured = new(2026, 9, 8, 21, 27, 27, TimeSpan.Zero);

    // One database holding the sample templates and the sample cache for the whole run: templates and cache versions are
    // immutable, and after the warm-up a render never reaches the database behind them.
    private static readonly Lazy<(SqliteOsdu Database, OsduTemplateStore Store, ICacheStore Cache)> SampleDatabase = new(() =>
    {
        var database = new SqliteOsdu();
        var store = database.Templates();
        ImportSampleTemplatesAsync(store).GetAwaiter().GetResult();
        var version = ImportSampleCacheAsync(database.Caches()).GetAwaiter().GetResult();
        return (database, store, new FixedCacheStore(SampleCacheScope, SampleCacheFlowName, version, SampleCacheDeclaration()));
    });

    /// <summary>
    /// What the sample cache flows declare for their partition, as the module's database holds it after a sync: the
    /// reference data of the reference cache flow, and the lookup tables of the lookups flow with the key and fields a sync
    /// reads out of each dictionary.
    /// </summary>
    public static CacheDeclaration SampleCacheDeclaration()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.LoadCache(CacheFlow);
        var lookups = loader.LoadCache(LookupsCacheFlow);
        var declared = flow.Types.Select(t => new CacheTypeDeclaration(flow.Name, t.Name, t.EntityType, t.Kind, t.Query, t.Fields, t.OnChange)).ToList();
        foreach (var type in lookups.Types)
        {
            var (key, fields) = type.Origin == CacheOrigin.Dictionary
                ? (SampleDictionary(type.Dictionary!).Key, SampleDictionary(type.Dictionary!).FieldSpecs())
                : (type.Key, type.Fields);
            declared.Add(new CacheTypeDeclaration(lookups.Name, type.Name, type.EntityType, null, "*", fields, type.OnChange, type.Origin, key));
        }

        return new CacheDeclaration(SampleCacheScope, declared);
    }

    /// <summary>A dictionary of the sample estate, read from its file under the estate's dictionaries folder.</summary>
    public static DictionaryDefinition SampleDictionary(string name)
    {
        var path = Path.Combine(Source, DictionaryCatalog.DirectoryName, name + ".yaml");
        return new DeliveryDocumentLoader().LoadDictionary(path, $"{DictionaryCatalog.DirectoryName}/{name}.yaml");
    }

    /// <summary>
    /// The lookup tables the sample lookups cache flow captures, built from the sample estate's own files as a refresh
    /// builds them: each dictionary type from its dictionary, and the curve dictionary's table type from the file its
    /// ingestion flow loads into that table, keyed and trimmed as a capture of the table keys and trims it.
    /// </summary>
    public static IReadOnlyList<ReferenceType> SampleLookups()
    {
        var flow = new DeliveryDocumentLoader().LoadCache(LookupsCacheFlow);
        var types = new List<ReferenceType>();
        foreach (var type in flow.Types)
        {
            if (type.Origin == CacheOrigin.Dictionary)
            {
                types.Add(SampleDictionary(type.Dictionary!).ToLookup(type.Name));
                continue;
            }

            var file = Directory.GetFiles(Path.Combine(Data, "curve-dictionary"), "*.csv").Single();
            var lines = File.ReadAllLines(file).Where(line => line.Trim().Length > 0).ToList();
            var header = lines[0].Split(',').Select(column => column.Trim()).ToList();
            var rows = new List<ReferenceItem>();
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                var key = cells[header.IndexOf(type.Key!)].Trim();
                var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [type.Key!] = ReferenceValue.Of(key) };
                foreach (var field in type.Fields)
                {
                    var value = cells[header.IndexOf(field.Path)];
                    if (value.Length > 0)
                    {
                        fields[field.Name] = ReferenceValue.Of(value);
                    }
                }

                rows.Add(new ReferenceItem(key, fields));
            }

            types.Add(new ReferenceType(type.Name, type.EntityType, rows.OrderBy(r => r.Id, StringComparer.Ordinal), type.Key));
        }

        return types;
    }

    /// <summary>A template store holding the sample templates, shared by the engine tests.</summary>
    public static ITemplateStore SampleTemplates => SampleDatabase.Value.Store;

    /// <summary>The sample cache at its one version, shared by the engine tests.</summary>
    public static ICacheStore SampleCache => SampleDatabase.Value.Cache;

    /// <summary>
    /// Imports the sample cache records into <paramref name="store"/> as a version of the sample partition's cache, checked
    /// against what the sample cache flow declares, exactly as 'sqlflow cache import' writes them, over the sample lookup
    /// tables written a minute before as the lookups flow's refresh writes them; returns the version as loaded back: the
    /// current one, labelled <see cref="SampleCacheCaptured"/>, which holds both.
    /// </summary>
    public static async Task<ReferenceSnapshot> ImportSampleCacheAsync(ICacheStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var flow = new DeliveryDocumentLoader().LoadCache(CacheFlow);
        // The estate names its partition as a reference; a cache is keyed by what that resolves to, as a capture and a
        // render both key it.
        var lookups = new SnapshotBuilder(
            store, SampleCacheScope, SampleLookupsFlowName, new TestClock(SampleCacheCaptured.AddMinutes(-1)), Logger<SnapshotBuilder>());
        await lookups.WriteAsync(SampleLookups(), new CacheCapture(null, "tests", "sample dictionaries and curve dictionary"), []);
        var builder = new SnapshotBuilder(store, SampleCacheScope, flow.Name, new TestClock(SampleCacheCaptured), Logger<SnapshotBuilder>());
        var write = await builder.ImportDirectoryAsync(CacheRecords, flow.Types, new CacheCapture(null, "tests", "sample files"));
        return (await store.LoadAsync(SampleCacheScope, write.Snapshot.Version))!;
    }

    /// <summary>Saves the sample templates into <paramref name="store"/> and loads each once, returning what was saved.</summary>
    public static async Task<IReadOnlyList<TemplateSaved>> ImportSampleTemplatesAsync(ITemplateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var saved = new List<TemplateSaved>();
        foreach (var kind in new[] { WellLogKind, WellboreKind, WellboreTrajectoryKind })
        {
            var schema = SampleTemplate(kind);
            saved.Add(await store.SaveAsync(schema, "sample file", "tests"));
            await store.LoadAsync(new TemplateReference(schema.Kind, schema.Version));
        }

        return saved;
    }

    /// <summary>
    /// Renders <paramref name="record"/> to the end, as the plan does: whatever a render asks of the renderer's search is
    /// answered between renders until one finishes.
    /// </summary>
    public static async Task<RenderResult> RenderSettledAsync(MappingRenderer renderer, SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        for (var round = 0; round < 10; round++)
        {
            var result = renderer.Render(record);
            if (!result.IsIncomplete)
            {
                return result;
            }

            await renderer.Search.AnswerAsync(result.Unanswered);
        }

        throw new InvalidOperationException("The render still asked the search something after ten rounds.");
    }

    /// <summary>The sample schema of a kind, read from its bundled file.</summary>
    public static SchemaSnapshot SampleTemplate(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var file = Path.Combine(TemplateFiles, kind.Replace(':', '_') + ".json");
        return TemplateSources.FromBundledJson(File.ReadAllText(file), kind, new DateTimeOffset(2026, 9, 7, 22, 37, 2, TimeSpan.Zero), file);
    }

    public static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osdu-delivery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The platform file stores plus the delivery writers, exactly as the hosts register them.</summary>
    public static FileStoreRegistry Stores() => new([new LocalFileStore()], [new LocalFileWriter()], [new LocalFileReader()]);

    /// <summary>The payload files over the local stores, as a node lists and opens them.</summary>
    public static IPayloadFiles Payloads() => new StoragePayloadFiles(Stores());

    /// <summary>
    /// The engine as a host composes it, over the in-memory ingestion tables: everything a run needs except a target,
    /// which the suites supply as a fake protocol.
    /// </summary>
    public static EngineContext Engine(
        ILedger? ledger,
        TimeProvider? time = null,
        IProtocolFactory? protocols = null,
        ITemplateStore? templates = null,
        ICacheStore? cache = null,
        IIngestionSourceFactory? sources = null,
        IPayloadFiles? payloads = null,
        IRecordSearchFactory? searches = null)
    {
        var stores = Stores();
        var loader = new DeliveryDocumentLoader();
        return new EngineContext(
            loader,
            sources ?? new MemoryIngestionTables(time),
            payloads ?? new StoragePayloadFiles(stores),
            stores,
            new SecretResolver([new EnvSecretProvider()]),
            ledger,
            time ?? TimeProvider.System,
            NullLoggerFactory.Instance,
            protocols ?? new DefaultProtocolFactory(new SecretResolver([new EnvSecretProvider()]), NullLoggerFactory.Instance),
            CompositeDeliveryListener.Empty,
            Templates: templates ?? SampleTemplates,
            Cache: cache ?? SampleCache,
            Searches: searches ?? FixedRecordSearchFactory.SampleWellbores());
    }

    /// <summary>
    /// The sample well log flow with the network target replaced by a local placeholder (tests never call OSDU), its
    /// source pointed at the in-memory ingestion tables, and its work and payload locations under
    /// <paramref name="root"/>, a directory the test owns.
    /// </summary>
    public static FlowDefinition LocalFlow(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var flow = new DeliveryDocumentLoader().LoadFlow(Flow);
        return Localize(flow, root);
    }

    /// <summary>
    /// <paramref name="flow"/> as if its document sat in <paramref name="root"/>, still rendering with the sample mappings. A
    /// run through the executor writes its history next to the flow's document, and the sample estate's folder is shared
    /// by every test of the process (the lineage tests copy it whole while others run), so a test that runs the executor
    /// runs a flow whose document is in a folder of its own.
    /// </summary>
    public static FlowDefinition InFolder(FlowDefinition flow, string root)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var folder = Path.Combine(root, "flows");
        Directory.CreateDirectory(folder);
        return flow with
        {
            SourcePath = Path.Combine(folder, Path.GetFileName(flow.SourcePath ?? Flow)),
            Render = flow.Render with { MappingsDirectory = Mappings },
        };
    }

    /// <summary>The sample wellbore flow, localized the same way; it streams no payload.</summary>
    public static FlowDefinition LocalWellboreFlow(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var flow = new DeliveryDocumentLoader().LoadFlow(WellboreFlowFile);
        return Localize(flow, root);
    }

    private static FlowDefinition Localize(FlowDefinition flow, string root)
    {
        var payloads = flow.Source.Payloads.ToDictionary(
            p => p.Key,
            p => p.Value with { Root = Path.Combine(root, "curves") },
            StringComparer.Ordinal);
        return flow with
        {
            Source = flow.Source with
            {
                Connection = MemoryConnection,
                Work = Path.Combine(root, "work"),
                Payloads = payloads,
            },
            Target = flow.Target with
            {
                Endpoint = "http://localhost:9/petrodb",
                Auth = new TargetAuth { Type = TargetAuthType.None },
                // The partition stays: it is what names the cache the render reads.
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = SampleCacheScope },
            },
            // SQLite in-memory shares one connection, so the test worker runs one record at a time and one renderer.
            Reliability = flow.Reliability with { Concurrency = 1, RenderParallelism = 1, Retry = flow.Reliability.Retry with { Attempts = 3, RecordBaseDelayMinutes = 1 } },
        };
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    /// <summary>A flow read for its target alone: its source and render name tables and a mapping no test opens.</summary>
    public static FlowDefinition Targeting(FlowTarget target, string? interfaceName = null) => new()
    {
        Name = "targeting",
        Interface = interfaceName,
        Source = new FlowSource
        {
            Connection = MemoryConnection,
            Record = new FlowSourceTable { Object = "ing.Record", Key = ["record_id"] },
            Work = "work",
        },
        Render = new FlowRender { Mapping = "Targeting@1.0.0" },
        Target = target,
    };
}

/// <summary>A compact OSDU-shaped schema for unit tests: allOf, a definitions ref, an array of objects, tags.</summary>
public static class TestSchema
{
    public const string Kind = "test:wks:work-product-component--Thing:1.0.0";

    /// <summary>The entries every test mapping starts from: the envelope, the key's own property and the one property the schema requires.</summary>
    public const string BaseEntries = """
          - { target: osdu.acl.owners, static: [owners@x] }
          - { target: osdu.acl.viewers, static: [viewers@x] }
          - { target: osdu.legal.legaltags, static: [tag] }
          - { target: osdu.legal.otherRelevantDataCountries, static: [NO] }
          - { target: osdu.data.Name, source: dataset.name }
          - { target: osdu.data.Depth, source: dataset.depth }
        """;

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
                    "Day": { "type": "string", "format": "date" },
                    "Clock": { "type": "string", "format": "time" },
                    "Weight": { "type": "number" },
                    "Small": { "type": "integer", "format": "int32" },
                    "Big": { "type": "integer", "format": "int64" },
                    "Days": { "type": "array", "items": { "type": "string", "format": "date" } },
                    "Curves": { "type": "array", "items": { "type": "object", "properties": { "CurveID": { "type": "string" }, "TopDepth": { "type": "number" } } } },
                    "Nested": { "type": "object", "properties": { "Inner": { "type": "string" } } },
                    "Aliases": { "type": "array", "items": { "type": "string" } },
                    "Symbol": { "type": "string" }
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

    /// <summary>The template version of <see cref="Build"/>, which every test mapping pins.</summary>
    public static TemplateReference Template => new(Kind, Build().Version);

    public static ReferenceSnapshot References() => new("refs-1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "m", ["Name"] = "metre" }),
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "ft", ["Name"] = "foot" }),
        ]),
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            ReferenceItem.FromText("dev:master-data--Wellbore:abc", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FacilityName"] = "NO 1/1-A" }),
        ]),
    ]);

    public static RenderContext Context(string mapping = "Thing@1.0.0") => new()
    {
        MappingReference = mapping,
        CacheScope = "dev",
        CacheVersion = "refs-1",
        SchemaSnapshotVersion = Build().Version,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
    };

    /// <summary>
    /// A mapping document over the test template: the header, <paramref name="baseEntries"/>, then <paramref name="entries"/>
    /// (YAML list items indented by two spaces), then <paramref name="fixtures"/> (a whole top-level block).
    /// </summary>
    public static string MappingDocument(string entries = "", string fixtures = "", string baseEntries = BaseEntries) =>
        $"""
        documentType: mapping
        name: Thing
        version: 1.0.0
        template:
          kind: {Kind}
          version: {Build().Version}
        dataset:
          system: test
          key: [dataset.name]
        parameters:
          dataPartition: {"{"} required: true {"}"}
        mappings:

        """ + baseEntries + "\n" + entries + "\n" + fixtures + "\n";

    /// <summary>A valid mapping over the test template, with <paramref name="entries"/> appended to the base entries.</summary>
    public static MappingDefinition Mapping(string entries = "", string fixtures = "", string baseEntries = BaseEntries)
        => new DeliveryDocumentLoader().ParseMapping(MappingDocument(entries, fixtures, baseEntries), "thing.yaml");

    /// <summary>A mapping document with a cache entry and a repeater, for the loader tests.</summary>
    public static string MappingYaml => MappingDocument("""
          - target: osdu.data.Unit
            source: cache.UnitOfMeasure.id
            findBy: cache.UnitOfMeasure.Code = dataset.unit
          - target: osdu.data.Curves
            source: dataset.curves
          - target: osdu.data.Curves[].CurveID
            source: dataset.curves.curve_id
          - target: osdu.data.Curves[].TopDepth
            source: dataset.curves.top
        """);

    public static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;
}

/// <summary>
/// A search that answers from records it was given instead of a platform, keeping to the protocol a platform search keeps:
/// a question is unknown until it has been asked, and anything it was not given is found nowhere.
/// </summary>
public sealed class FixedRecordSearch : IRecordSearch
{
    private readonly IReadOnlyList<(string Field, string Value, string Id)> _known;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SearchQuestion, SearchAnswer> _answers = new();

    public FixedRecordSearch(IReadOnlyList<(string Field, string Value, string Id)> known) => _known = known;

    /// <summary>Every question asked of this search, in order, so a test can count the round trips.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<SearchQuestion> Asked { get; } = new();

    public bool TryAnswer(SearchQuestion question, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchAnswer? answer)
        => _answers.TryGetValue(question, out answer);

    public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
    {
        foreach (var question in questions.Where(q => !_answers.ContainsKey(q)))
        {
            Asked.Enqueue(question);
            var ids = _known
                .Where(k => string.Equals(k.Field, question.Field, StringComparison.Ordinal) && string.Equals(k.Value, question.Value, StringComparison.Ordinal))
                .Select(k => k.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            _answers[question] = ids.Count switch
            {
                0 => SearchAnswer.None,
                1 => SearchAnswer.Found(ids[0]),
                _ => SearchAnswer.Ambiguous(ids.Count, ids),
            };
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes a <see cref="FixedRecordSearch"/> for every runtime, over the same records, and keeps each one it made. An id
/// written with <see cref="Partition"/> in it is a record of the partition the flow delivers to, as a platform search
/// finds only the records of the partition it is asked in.
/// </summary>
public sealed class FixedRecordSearchFactory(params (string Field, string Value, string Id)[] known) : IRecordSearchFactory
{
    /// <summary>Stands for the partition of the flow the search is made for.</summary>
    public const string Partition = "{partition}";

    /// <summary>
    /// The sample wellbores, found by name in whichever partition the flow delivers to: what the sample estate's mappings
    /// find when they look up the wellbores the sample drops name, the ids the sample cache held before wellbores were
    /// searched for.
    /// </summary>
    public static FixedRecordSearchFactory SampleWellbores() => new(
        ("data.FacilityName", "OSDU-DEV-1-A", Partition + ":master-data--Wellbore:OSDU-DEV-1-A"),
        ("data.FacilityName", "OSDU-DEV-1-B", Partition + ":master-data--Wellbore:OSDU-DEV-1-B"));

    public System.Collections.Concurrent.ConcurrentQueue<FixedRecordSearch> Created { get; } = new();

    public IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target)
    {
        var declared = flow.Target.Headers.FirstOrDefault(h => h.Key.Equals("data-partition-id", StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
        var partition = declared.StartsWith("${env:", StringComparison.Ordinal) && declared.EndsWith('}')
            ? Environment.GetEnvironmentVariable(declared[6..^1]) ?? declared
            : declared;
        var search = new FixedRecordSearch(known.Select(k => (k.Field, k.Value, k.Id.Replace(Partition, partition, StringComparison.Ordinal))).ToList());
        Created.Enqueue(search);
        return search;
    }
}
