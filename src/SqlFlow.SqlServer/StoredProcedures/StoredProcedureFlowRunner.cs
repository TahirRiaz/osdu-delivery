using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Runs;
using SqlFlow.Core.StoredProcedures;

namespace SqlFlow.SqlServer.StoredProcedures;

/// <summary>
/// Executes a stored-procedure flow: resolve the target server alias through the connection registry, run the
/// three-part procedure on it (no parameters bound, the legacy contract), and record the run. It shares the
/// run-log seam with ingestion (FlowType 'sp', zero row counts), so without-database mode logs nothing and
/// with-database mode writes one flw.SysLog row. A set PostInvokeAlias runs the named flw.Invoke flow after the
/// procedure; without an invoke runner wired (without-database mode) a set alias is surfaced as a clear error by
/// NullInvokeRunner rather than silently skipped. The catch keeps the original error and never throws.
/// </summary>
public sealed class StoredProcedureFlowRunner
{
    private readonly IConnectionResolver _resolver;
    private readonly IIngestionRunLog _runLog;
    private readonly IInvokeRunner _invoke;

    public StoredProcedureFlowRunner(IConnectionResolver resolver, IIngestionRunLog? runLog = null, IInvokeRunner? invoke = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
        _invoke = invoke ?? NullInvokeRunner.Instance;
    }

    public async Task<StoredProcedureRunResult> RunAsync(StoredProcedureFlow flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;

        // The executed SQL is captured unconditionally (the result carries it on success AND failure) and
        // woven into the event log at Trace level, the same dual feed as every other runner.
        var trace = new List<SqlTraceEntry>();
        void Trace(string step, string? sql)
        {
            if (!string.IsNullOrWhiteSpace(sql))
            {
                var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql };
                trace.Add(entry);
                events.Log(RunLogLevel.Trace, step, sql);
                statements.Report(entry);
            }
        }

        try
        {
            events.Log(RunLogLevel.Info, "run.start",
                $"stored procedure '{flow.SysAlias}' (flow {flow.FlowId}): EXEC {flow.Procedure.QualifiedName} on '{flow.Server}'");
            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);
            Trace("procedure.exec", $"EXEC {flow.Procedure.QualifiedName};");
            await ExecuteProcedureAsync(resolved.CanonicalString, flow.Procedure, ct).ConfigureAwait(false);

            // PostInvokeAlias: run the named flw.Invoke flow after the procedure. A failure it does not tolerate
            // is raised and fails the flow; without an invoke runner wired, NullInvokeRunner raises a clear error.
            if (!string.IsNullOrWhiteSpace(flow.PostInvokeAlias))
            {
                events.Log(RunLogLevel.Info, "invoke.post", $"running post-invoke '{flow.PostInvokeAlias.Trim()}'");
                await _invoke.RunByAliasAsync(flow.PostInvokeAlias!.Trim(), ct).ConfigureAwait(false);
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"SUCCESS in {duration}s");
            await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, success: true, error: null, trace), ct).ConfigureAwait(false);

            return new StoredProcedureRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SqlTrace.MarkLastFailed(trace, statements, ex.Message);
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"FAILED after {duration}s: {ex.Message}");
            try
            {
                await _runLog.WriteAsync(BuildRecord(flow, options, runId, startUtc, endUtc, duration, success: false, error: ex.Message, trace), ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error, which is returned below.
            }

            return new StoredProcedureRunResult
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

    private static async Task ExecuteProcedureAsync(string connectionString, RelationalObject procedure, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(procedure.QualifiedName, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static IngestionRunRecord BuildRecord(
        StoredProcedureFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        bool success, string? error, IReadOnlyList<SqlTraceEntry> trace)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"-->{flow.Server}.{flow.Procedure.QualifiedName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            Success = success,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);
}
