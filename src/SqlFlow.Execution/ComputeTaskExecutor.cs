using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Maintenance;
using SqlFlow.Core.Profiling;
using SqlFlow.SqlServer.Comparison;
using SqlFlow.SqlServer.Health;
using SqlFlow.SqlServer.Maintenance;
using SqlFlow.SqlServer.Profiling;

namespace SqlFlow.Execution;

/// <summary>
/// Executes one validated <see cref="ComputeTaskPayload"/> against the live datasource and returns the result
/// as JSON for the queue row. This is the compute-task counterpart of <see cref="DocumentExecutor"/>: it runs
/// on whatever node claimed the task, resolves the connection reference through the SAME resolver, factory,
/// and provider catalog readers the engine ingests with (one code path for every provider: SQL Server, Azure
/// SQL, MySQL, PostgreSQL, Oracle), and never sees or stores a secret. Interactive operations run under a
/// bounded timeout so a wedged source can never pin a worker slot; unique-key detection is unbounded but
/// cancellable (the operator cancel path) and backstopped by the queue's running-task expiry.
/// </summary>
public sealed class ComputeTaskExecutor
{
    /// <summary>Results are stored on the queue row and served to the GUI verbatim; compact camelCase with enum
    /// names, matching the product's other JSON surfaces.</summary>
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The connection-open budget for testConnection: long enough for a cold Azure SQL resume, short
    /// enough that a black-holed host answers the operator in bounded time.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The budget for browse/introspect operations (catalog queries are metadata reads; two minutes is
    /// generous even for a database with tens of thousands of objects).</summary>
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The largest result a task may record. Listing limits already bound normal results; this is the
    /// backstop against a pathological introspection (thousands of columns/indexes) bloating the catalog.</summary>
    private const int MaxResultChars = 8_000_000;

    private readonly CatalogService _catalog;
    private readonly IConnectionResolver _resolver;
    private readonly IConnectionFactory _factory;

    public ComputeTaskExecutor(CatalogService catalog, IConnectionResolver resolver, IConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(factory);
        _catalog = catalog;
        _resolver = resolver;
        _factory = factory;
    }

    /// <summary>
    /// Runs the task and returns its result JSON. Throws <see cref="SqlFlowException"/> (or the provider's
    /// connection/query exception) on failure; the caller records the redacted message on the task row. Honors
    /// <paramref name="ct"/> throughout, so an operator cancel aborts the in-flight query.
    /// </summary>
    public async Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var timeout = payload.Operation switch
        {
            ComputeOperations.TestConnection => ConnectTimeout,
            // Measurement and comparison are bounded by the work, not by a clock: a fragmentation scan over a
            // large warehouse and an anti-join over a billion rows both legitimately outrun any deadline that
            // would be safe for an interactive browse. They stay cancellable (the operator cancel path) and
            // are backstopped by the queue's running-task expiry, exactly like detectUniqueKey.
            ComputeOperations.DetectUniqueKey or ComputeOperations.DwhMaintenance or ComputeOperations.CompareBaseline
                => Timeout.InfiniteTimeSpan,
            _ => BrowseTimeout,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            var result = await ExecuteCoreAsync(payload, timeoutCts.Token).ConfigureAwait(false);
            if (result.Length > MaxResultChars)
            {
                throw new SqlFlowException(
                    $"The operation's result is {result.Length} characters, over the {MaxResultChars}-character " +
                    "limit for a compute task. Narrow the scope (a schema filter, a smaller limit) and retry.");
            }

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SqlFlowException(
                $"The {payload.Operation} operation timed out after {timeout.TotalSeconds:0} seconds. The source " +
                "may be unreachable from this node, or the query may need a narrower scope.");
        }
    }

    private async Task<string> ExecuteCoreAsync(ComputeTaskPayload payload, CancellationToken ct)
    {
        var reference = payload.SourceRef.Trim();
        var kind = payload.ProviderKind;
        var query = new CatalogQuery
        {
            NameLike = string.IsNullOrWhiteSpace(payload.NameLike) ? null : payload.NameLike.Trim(),
            IncludeTables = payload.IncludeTables,
            IncludeViews = payload.IncludeViews,
            IncludeSystem = payload.IncludeSystem,
            Offset = payload.Offset,
            Limit = payload.Limit,
        };

        switch (payload.Operation)
        {
            case ComputeOperations.TestConnection:
                return await TestConnectionAsync(reference, kind, ct).ConfigureAwait(false);

            case ComputeOperations.ListDatabases:
            {
                var databases = await _catalog.DatabasesAsync(reference, query, kind, ct).ConfigureAwait(false);
                return ToJson(new { databases });
            }

            case ComputeOperations.ListSchemas:
            {
                var schemas = await _catalog.SchemasAsync(reference, NullIfBlank(payload.Database), query, kind, ct).ConfigureAwait(false);
                return ToJson(new { schemas });
            }

            case ComputeOperations.ListObjects:
            {
                var scope = new ObjectScope { Database = NullIfBlank(payload.Database), Schema = NullIfBlank(payload.Schema) };
                var page = await _catalog.ObjectsAsync(reference, scope, query, kind, ct).ConfigureAwait(false);
                return ToJson(page);
            }

            case ComputeOperations.SearchObjects:
            {
                var matches = await _catalog
                    .SearchAsync(reference, NullIfBlank(payload.Database), payload.SearchTerm!.Trim(), kind, ct)
                    .ConfigureAwait(false);
                return ToJson(new { matches });
            }

            case ComputeOperations.IntrospectObject:
            {
                var obj = await _catalog.IntrospectAsync(reference, RequiredName(payload), kind, ct).ConfigureAwait(false);
                // Not-found is a legitimate answer (the object may have been dropped since it was listed), not an
                // infrastructure failure, so it succeeds with an explicit marker instead of failing the task.
                return obj is null ? ToJson(new { found = false }) : ToJson(new { found = true, @object = obj });
            }

            case ComputeOperations.DetectUniqueKey:
                return await DetectUniqueKeyAsync(payload, reference, kind, ct).ConfigureAwait(false);

            case ComputeOperations.MissingIndexes:
            case ComputeOperations.StatisticsHealth:
            case ComputeOperations.IndexUsage:
            case ComputeOperations.TopQueries:
                return await WarehouseHealthAsync(payload, reference, kind, ct).ConfigureAwait(false);

            case ComputeOperations.DwhMaintenance:
                return await MaintenanceAsync(payload, reference, kind, ct).ConfigureAwait(false);

            case ComputeOperations.CompareBaseline:
                return await CompareBaselineAsync(payload, reference, kind, ct).ConfigureAwait(false);

            default:
                // Validate() has already refused unknown operations at both boundaries; reaching this arm means a
                // new operation was added to the contract without an executor arm, which must fail loudly.
                throw new SqlFlowException($"No executor is implemented for compute operation '{payload.Operation}'.");
        }
    }

    private async Task<string> TestConnectionAsync(string reference, DataSourceKind? kind, CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, kind, ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _factory.OpenAsync(resolved, ct).ConfigureAwait(false);
        stopwatch.Stop();

        // ServerVersion is provider-implemented and can throw on an exotic driver state; the connection is
        // already proven open, so a missing version must not fail the test itself.
        string? serverVersion;
        try
        {
            serverVersion = connection.ServerVersion;
        }
        catch (InvalidOperationException)
        {
            serverVersion = null;
        }

        return ToJson(new
        {
            ok = true,
            kind = resolved.Kind.ToString(),
            serverVersion,
            database = NullIfBlank(connection.Database),
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
        });
    }

    private async Task<string> DetectUniqueKeyAsync(
        ComputeTaskPayload payload, string reference, DataSourceKind? kind, CancellationToken ct)
    {
        // The kind gate ran at enqueue for explicit kinds; an @alias resolves its kind here on the node, so the
        // resolved kind is re-checked before any T-SQL profiling is attempted against a foreign engine.
        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, kind, ct).ConfigureAwait(false);
        if (resolved.Kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB))
        {
            throw new SqlFlowException(
                $"detectUniqueKey profiles with T-SQL; the source resolved to kind '{resolved.Kind}'. Only SQL " +
                "Server and Azure SQL sources are supported.");
        }

        var name = RequiredName(payload);

        // The columns come from the same catalog introspection every browse operation uses, so both sides see
        // one source of truth (and a dropped/renamed object fails here with a precise message).
        var obj = await _catalog.IntrospectAsync(reference, name, kind, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"Object {name.QualifiedName} was not found on the source.");
        if (obj.Columns.Count == 0)
        {
            throw new SqlFlowException($"Object {name.QualifiedName} has no columns to profile.");
        }

        var options = new UniqueKeyOptions
        {
            MaxKeyColumns = payload.MaxKeyColumns,
            MaxCandidates = payload.MaxCandidates,
            Verify = payload.VerifyCandidates,
        };

        await using var probe = await SqlServerUniquenessProbe
            .CreateAsync(
                resolved.CanonicalString, name.QualifiedName, obj.Columns.Select(c => c.Name).ToList(),
                payload.SampleSize, payload.TrustDeclaredKeys, ct)
            .ConfigureAwait(false);
        var report = (await UniqueKeyDetector.DetectAsync(probe, probe.EligibleColumns, options, ct).ConfigureAwait(false))
            with { ObjectName = name.QualifiedName, ExcludedColumns = probe.ExcludedColumns };
        return ToJson(report);
    }

    /// <summary>
    /// Runs one of the four warehouse-health probes. They ARE maintenance actions of the same name, so this
    /// goes through the maintenance registry rather than re-querying the DMVs: one code path, one set of
    /// thresholds, one place a probe's SQL lives. Only the SHAPE differs, and deliberately so: this operation
    /// answers with the report's native Detail payload, which is the result shape the insights recommendations
    /// have always parsed. A caller wanting the ranked findings asks for dwhMaintenance instead.
    /// </summary>
    private async Task<string> WarehouseHealthAsync(
        ComputeTaskPayload payload, string reference, DataSourceKind? kind, CancellationToken ct)
    {
        var request = new MaintenanceRequest
        {
            Action = payload.Operation,
            Scope = new MaintenanceScope { Database = NullIfBlank(payload.Database) },
            Limit = payload.Limit,
        };

        var report = await RunMaintenanceAsync(request, reference, kind, ct).ConfigureAwait(false);
        return ToJson(report.Detail);
    }

    /// <summary>Runs one standard warehouse maintenance action and returns the full ranked report.</summary>
    private async Task<string> MaintenanceAsync(
        ComputeTaskPayload payload, string reference, DataSourceKind? kind, CancellationToken ct)
    {
        // Re-validating on the node is not belt-and-braces: the queue row is data from the database, and the
        // resolved kind of an @alias is only knowable here.
        var request = payload.ToMaintenanceRequest();
        var report = await RunMaintenanceAsync(request, reference, kind, ct).ConfigureAwait(false);
        return ToJson(report);
    }

    private async Task<MaintenanceReport> RunMaintenanceAsync(
        MaintenanceRequest request, string reference, DataSourceKind? kind, CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, kind, ct).ConfigureAwait(false);
        MaintenanceActions.Validate(request, resolved.Kind);

        return await SqlServerMaintenanceActions.RunAsync(
            new MaintenanceExecutionContext
            {
                ConnectionString = resolved.CanonicalString,
                Kind = resolved.Kind,
                Request = request,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares the current estate against the OLD production baseline over a linked server. The allowlist
    /// decision was made and recorded at enqueue, so the node re-runs every shape and fragment check against
    /// the linked server the payload names rather than re-deciding a policy it cannot see.
    /// </summary>
    private async Task<string> CompareBaselineAsync(
        ComputeTaskPayload payload, string reference, DataSourceKind? kind, CancellationToken ct)
    {
        var request = payload.ToComparisonRequest(
            payload.LinkedServer is null ? [] : [payload.LinkedServer]);

        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, kind, ct).ConfigureAwait(false);
        if (resolved.Kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB))
        {
            throw new SqlFlowException(
                "Baseline comparison runs its aggregation and anti-join in T-SQL over a linked server; the " +
                $"source resolved to kind '{resolved.Kind}'. Only SQL Server and Azure SQL sources are supported.");
        }

        return request.Mode switch
        {
            BaselineCompareMode.Inventory => ToJson(await SqlServerBaselineComparer
                .InventoryAsync(resolved.CanonicalString, request, ct).ConfigureAwait(false)),
            BaselineCompareMode.Schema => ToJson(await SqlServerBaselineComparer
                .SchemaAsync(resolved.CanonicalString, request, ct).ConfigureAwait(false)),
            _ => ToJson(await SqlServerBaselineComparer
                .DataAsync(resolved.CanonicalString, request, ct).ConfigureAwait(false)),
        };
    }

    private static ThreePartName RequiredName(ComputeTaskPayload payload) => new()
    {
        Database = NullIfBlank(payload.Database),
        Schema = payload.Schema!.Trim(),
        Name = payload.ObjectName!.Trim(),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, ResultJsonOptions);
}
