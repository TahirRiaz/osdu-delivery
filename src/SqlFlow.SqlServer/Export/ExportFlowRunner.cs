using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Runs;

namespace SqlFlow.SqlServer.Export;

/// <summary>
/// Runs an export flow: resolve the source server, probe the source columns, plan the chunk SELECTs and file
/// names, then stream each chunk to a file (CSV or Parquet) on the selected destination, fanned out under a
/// thread cap. An empty chunk's file is deleted. The run is recorded to the run log (FlowType 'exp'). A set
/// PostInvokeAlias runs the named flw.Invoke flow after the files are written; without an invoke runner wired
/// (without-database mode) a set alias is surfaced as a clear error by NullInvokeRunner. Log-only catch: the
/// runner never throws, returning a failed result with the original error.
/// </summary>
public sealed class ExportFlowRunner
{
    private readonly IConnectionResolver _resolver;
    private readonly IReadOnlyList<IExportDestination> _destinations;
    private readonly IIngestionRunLog _runLog;
    private readonly IInvokeRunner _invoke;

    public ExportFlowRunner(IConnectionResolver resolver, IEnumerable<IExportDestination>? destinations = null, IIngestionRunLog? runLog = null, IInvokeRunner? invoke = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _destinations = destinations?.ToList() ?? [new LocalExportDestination()];
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _invoke = invoke ?? NullInvokeRunner.Instance;
    }

    public async Task<ExportRunResult> RunAsync(ExportFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;

        // The generated-SQL trace is captured unconditionally (the result carries it on success AND failure;
        // a failed export is exactly when the generated SQL matters) and woven into the event log at Trace
        // level, so a trace-level run.log shows each statement in its execution context.
        var trace = new List<SqlTraceEntry>();
        void Trace(string step, string? sql)
        {
            if (!string.IsNullOrWhiteSpace(sql))
            {
                trace.Add(new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql });
                events.Log(RunLogLevel.Trace, step, sql);
            }
        }

        try
        {
            events.Log(RunLogLevel.Info, "run.start",
                $"export '{flow.SysAlias}' (flow {flow.FlowId}): {flow.Source.QualifiedName} -> {flow.TrgPath} ({flow.TrgFiletype})");

            if (string.IsNullOrWhiteSpace(flow.TrgPath))
            {
                throw new SqlFlowException($"Export flow {flow.FlowId} has no trgPath.");
            }

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false);
            var sourceConnectionString = resolved.CanonicalString;

            // The full three-part name: the declared database is honored even when the connection's default
            // catalog differs, matching the segment planner and the stored-procedure runner.
            var from = flow.Source.QualifiedName;

            var probeSql = $"SELECT * FROM {from}";
            Trace("source.probe", probeSql);
            var columns = await ProbeColumnsAsync(sourceConnectionString, probeSql, from, ct).ConfigureAwait(false);
            events.Log(RunLogLevel.Debug, "source.probe", $"{columns.Count} column(s): {string.Join(", ", columns)}");

            var keyMax = 0;
            if (string.Equals(flow.ExportBy.Trim(), "K", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(flow.IncrementalColumn))
            {
                // Read the key-max under the same source hint (for example NOLOCK) as the data SELECTs.
                var keyMaxSql = $"SELECT MAX([{Escape(flow.IncrementalColumn!)}]) FROM {from}{ExportSegmentPlanner.TableHint(flow.SrcWithHint)}";
                Trace("source.keymax", keyMaxSql);
                keyMax = await ProbeKeyMaxAsync(sourceConnectionString, keyMaxSql, ct).ConfigureAwait(false);
            }

            var segments = ExportSegmentPlanner.Plan(flow, columns, keyMax, startUtc);
            events.Log(RunLogLevel.Info, "export.plan",
                $"{segments.Count} segment(s), chunked by '{flow.ExportBy.Trim().ToUpperInvariant()}', {Math.Max(1, flow.NoOfThreads)} concurrent");
            foreach (var segment in segments)
            {
                Trace("export.segment", $"-- {segment.FileName}\n{segment.Sql}");
            }

            var destination = _destinations.FirstOrDefault(d => d.CanHandle(flow.TrgPath!))
                ?? throw new SqlFlowException($"No export destination handles the path '{flow.TrgPath}'.");

            var maxDegree = Math.Min(segments.Count, Math.Max(1, flow.NoOfThreads));
            using var gate = new SemaphoreSlim(maxDegree, maxDegree);
            var tasks = segments.Select(async segment =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var file = await WriteSegmentAsync(flow, segment, sourceConnectionString, destination, ct).ConfigureAwait(false);
                    events.Log(RunLogLevel.Info, "export.file", file is null
                        ? $"{segment.FileName}: empty segment, no file"
                        : $"{file.Path}: {file.Rows} row(s), {file.Bytes} byte(s)");
                    return file;
                }
                finally
                {
                    gate.Release();
                }
            });

            var written = await Task.WhenAll(tasks).ConfigureAwait(false);
            var files = written.Where(f => f is not null).Select(f => f!).ToList();
            var totalRows = files.Sum(f => f.Rows);

            // PostInvokeAlias: run the named flw.Invoke flow after the files are written. A failure it does not
            // tolerate is raised and fails the run; without an invoke runner wired, NullInvokeRunner raises a
            // clear error rather than silently skipping the requested hook.
            if (!string.IsNullOrWhiteSpace(flow.PostInvokeAlias))
            {
                events.Log(RunLogLevel.Info, "invoke.post", $"running post-invoke '{flow.PostInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.PostInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"SUCCESS: {files.Count} file(s), {totalRows} row(s) in {duration}s");
            await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, totalRows, success: true, error: null, segments, trace), ct).ConfigureAwait(false);

            return new ExportRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                TotalRows = totalRows,
                Files = files,
                SqlTrace = trace,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"FAILED after {duration}s: {ex.Message}");
            try
            {
                await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, 0, success: false, error: ex.Message, [], trace), ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error.
            }

            return new ExportRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = false,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                Error = ex.Message,
            };
        }
    }

    private static async Task<ExportedFile?> WriteSegmentAsync(ExportFlow flow, ExportSegment segment, string sourceConnectionString, IExportDestination destination, CancellationToken ct)
    {
        var fullPath = ComposePath(flow, segment);

        await using var connection = new SqlConnection(sourceConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(segment.Sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var writer = ExportFileWriterFactory.Create(flow);
        long rows;
        var stream = await destination.OpenWriteAsync(fullPath, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            rows = await writer.WriteAsync(reader, stream, ct).ConfigureAwait(false);
        }

        // An empty chunk leaves no file (legacy parity).
        if (rows == 0)
        {
            await destination.DeleteIfExistsAsync(fullPath, ct).ConfigureAwait(false);
            return null;
        }

        // ZipTrg: replace the written file with a single-entry .zip (legacy ZipTrg). A destination that cannot
        // compress throws a clear error via the default ZipAsync, so a requested zip is never silently skipped.
        if (flow.ZipTrg)
        {
            fullPath = await destination.ZipAsync(fullPath, ct).ConfigureAwait(false);
        }

        var bytes = await destination.GetSizeAsync(fullPath, ct).ConfigureAwait(false);
        return new ExportedFile(fullPath, rows, bytes);
    }

    private static string ComposePath(ExportFlow flow, ExportSegment segment)
    {
        var basePath = flow.TrgPath!.TrimEnd('/', '\\');
        return $"{basePath}/{segment.SubFolder}{segment.FileName}.{flow.TrgFiletype}";
    }

    private static async Task<IReadOnlyList<string>> ProbeColumnsAsync(string connectionString, string probeSql, string from, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(probeSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);

        var columns = new List<string>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        if (columns.Count == 0)
        {
            throw new SqlFlowException($"Export source {from} exposes no columns.");
        }

        return columns;
    }

    private static async Task<int> ProbeKeyMaxAsync(string connectionString, string keyMaxSql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(keyMaxSql, connection) { CommandTimeout = 0 };
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static IngestionRunRecord BuildRecord(
        ExportFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        long totalRows, bool success, string? error, IReadOnlyList<ExportSegment> segments, IReadOnlyList<SqlTraceEntry> trace)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"{flow.SrcServer}.{flow.Source.QualifiedName}-->{flow.TrgPath}.{flow.TrgFileName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = totalRows,
            Success = success,
            Threads = flow.NoOfThreads > 0 ? flow.NoOfThreads : null,
            SelectCmd = segments.Count > 0 ? segments[0].Sql : null,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
