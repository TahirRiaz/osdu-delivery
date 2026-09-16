using System.Collections.Concurrent;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The pinned OSDU contracts (<c>osdu/specs</c>, linked into the test output), loaded once each, and the check every
/// route test runs over the requests its route sent: each request must be an operation of the contract of the service
/// it went to, and conform to it.
/// </summary>
internal static class OsduContracts
{
    private static readonly ConcurrentDictionary<string, Lazy<ApiContract>> Loaded = new(StringComparer.Ordinal);

    public static string Root => Path.Combine(AppContext.BaseDirectory, "specs");

    public static ApiContract Storage => Get("core/storage", "core", "storage", "openapi.yaml");

    public static ApiContract File => Get("core/file", "core", "file", "openapi.yaml");

    public static ApiContract Dataset => Get("core/dataset", "core", "dataset", "openapi.yaml");

    public static ApiContract Workflow => Get("core/workflow", "core", "workflow", "openapi.yaml");

    public static ApiContract Search => Get("core/search", "core", "search", "openapi.yaml");

    public static ApiContract Legal => Get("core/legal", "core", "legal", "openapi.yaml");

    public static ApiContract Schema => Get("core/schema", "core", "schema_service", "openapi.yaml");

    public static ApiContract Register => Get("core/register", "core", "register", "openapi.yaml");

    public static ApiContract Entitlements => Get("core/entitlements", "core", "entitlements", "openapi.yaml");

    public static ApiContract WellboreDdms => Get("wellbore-ddms", "wellbore-ddms", "openapi.json");

    public static ApiContract SeismicDdms => Get("seismic-ddms", "seismic-ddms", "openapi.yaml");

    public static ApiContract RafsDdms => Get("rafs-ddms", "rafs-ddms", "openapi.yaml");

    public static ApiContract WellDeliveryDdms => Get("well-delivery-ddms", "well-delivery-ddms", "swagger.yaml");

    public static ApiContract ProductionDspdm => Get("production-dspdm", "production-dspdm", "swagger-api.json");

    public static ApiContract ProductionTimeSeriesIngestion => Get("production-timeseries/ingestion", "production-timeseries", "ingestion.openapi.yaml");

    public static ApiContract ProductionTimeSeries => Get("production-timeseries/query", "production-timeseries", "timeseries.openapi.yaml");

    public static ApiContract ExternalDataServices => Get("eds-dms", "eds-dms", "openapi.yaml");

    /// <summary>Every pinned OpenAPI and Swagger contract.</summary>
    public static IReadOnlyList<ApiContract> All =>
    [
        Storage, File, Dataset, Workflow, Search, Legal, Schema, Register, Entitlements,
        WellboreDdms, SeismicDdms, RafsDdms, WellDeliveryDdms, ProductionDspdm, ProductionTimeSeriesIngestion, ProductionTimeSeries,
        ExternalDataServices,
    ];

    /// <summary>
    /// Asserts that every request conforms to the contract of the service it went to, and returns the operations the
    /// requests exercised (<c>core/storage PUT /records</c>) so a test can also assert what it covered. The contract whose
    /// operation matches the request most strongly checks it (a match under the service's own base path beats a bare
    /// suffix; on a tie the contract listed first wins), and a request no contract declares fails the test.
    /// <paramref name="outside"/> names the requests that do not go to an OSDU service (a signed upload URL, a token
    /// endpoint), which are not checked.
    /// </summary>
    public static IReadOnlySet<string> AssertConform(IEnumerable<FakeHttpHandler.Request> requests, Func<FakeHttpHandler.Request, bool>? outside, params ApiContract[] contracts)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(contracts);
        var failures = new List<string>();
        var exercised = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (outside?.Invoke(request) == true)
            {
                continue;
            }

            var path = request.Uri.AbsolutePath;
            var matched = contracts
                .Select(c => (Contract: c, Hit: c.Find(request.Method.Method, path)))
                .Where(m => m.Hit is not null)
                .OrderByDescending(m => m.Hit!.Value.Literals)
                .FirstOrDefault();
            if (matched.Contract is null)
            {
                failures.Add($"{request.Method} {path}: no contract of {string.Join(", ", contracts.Select(c => c.Name))} declares it");
                continue;
            }

            var operation = matched.Hit!.Value.Operation;
            exercised.Add($"{matched.Contract.Name} {operation.Method} {operation.Template}");
            failures.AddRange(matched.Contract.Check(request).Select(v => $"[{matched.Contract.Name}] {v}"));
        }

        Assert.True(exercised.Count > 0 || failures.Count > 0, "No request was checked against a contract; the test sent nothing to an OSDU service.");
        Assert.True(failures.Count == 0, "Requests that break their OSDU contract:\n" + string.Join("\n", failures));
        return exercised;
    }

    private static ApiContract Get(string name, params string[] path)
        => Loaded.GetOrAdd(name, key => new Lazy<ApiContract>(() => ApiContract.Load(key, Path.Combine([Root, .. path])))).Value;
}
