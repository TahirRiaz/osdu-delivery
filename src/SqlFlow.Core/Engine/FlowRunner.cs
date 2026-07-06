using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Diagnostics;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Orchestrates a flow: infer source schema, introspect target, diff, generate DDL, then
/// (plan: stop and report / run: execute + bulk load). Stateless and reentrant: all per-run state
/// lives in a <see cref="RunContext"/> created per call, so any number of flows can run concurrently
/// without interfering. Every run has a unique RunId stamped on each event, log scope, and result,
/// so concurrent runs stay individually attributable.
/// </summary>
public sealed class FlowRunner
{
    private readonly IReadOnlyList<ISourceReader> _sources;
    private readonly ISqlTypeMapper _typeMapper;
    private readonly ISchemaProvider _schema;
    private readonly IDdlGenerator _ddl;
    private readonly IBulkLoader _loader;
    private readonly IIndexManager _indexManager;
    private readonly IDesiredIndexManager _desiredIndexManager;
    private readonly IIncrementalProbe _incrementalProbe;
    private readonly IStateStore _state;
    private readonly IFlowEventSink _events;
    private readonly ISecretResolver _secrets;
    private readonly IInferenceService _inference;
    private readonly ILogger<FlowRunner> _logger;

    public FlowRunner(
        IEnumerable<ISourceReader> sources,
        ISqlTypeMapper typeMapper,
        ISchemaProvider schema,
        IDdlGenerator ddl,
        IBulkLoader loader,
        IIndexManager indexManager,
        IDesiredIndexManager desiredIndexManager,
        IIncrementalProbe incrementalProbe,
        IStateStore state,
        IFlowEventSink events,
        ISecretResolver secrets,
        IInferenceService inference,
        ILogger<FlowRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
        _typeMapper = typeMapper;
        _schema = schema;
        _ddl = ddl;
        _loader = loader;
        _indexManager = indexManager;
        _desiredIndexManager = desiredIndexManager;
        _incrementalProbe = incrementalProbe;
        _state = state;
        _events = events;
        _secrets = secrets;
        _inference = inference;
        _logger = logger;
    }

    public async Task<FlowPlan> PlanAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var context = new RunContext(Guid.CreateVersion7(), flow.FlowId, flow.Name);
        using var scope = _logger.BeginScope("Flow {FlowName} ({RunId})", flow.Name, context.RunId);
        return await PlanCoreAsync(flow, context, ct).ConfigureAwait(false);
    }

    public Task<FlowResult> RunAsync(FlowDefinition flow, CancellationToken ct = default)
        => RunAsync(flow, null, ct);

    /// <param name="flow">The validated flow to run.</param>
    /// <param name="runId">An orchestrator-assigned run id stamped on the run instead of minting one; the
    /// control-plane trigger supplies the id it already handed the caller so the recorded run resolves under it.
    /// Null mints a fresh time-ordered id, which is what every direct CLI run does.</param>
    /// <param name="ct">Cancellation for the run.</param>
    public async Task<FlowResult> RunAsync(FlowDefinition flow, Guid? runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        var context = new RunContext(runId ?? Guid.CreateVersion7(), flow.FlowId, flow.Name);
        var startedAt = Stopwatch.GetTimestamp();
        string? connectionString = null;
        IReadOnlyList<string> disabledIndexes = [];

        using var scope = _logger.BeginScope("Flow {FlowName} {FlowId} ({RunId})", flow.Name, context.FlowId, context.RunId);
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity("flow.run");
        activity?.SetTag("flow.name", flow.Name);
        activity?.SetTag("flow.id", context.FlowId);
        activity?.SetTag("flow.run_id", context.RunId);
        activity?.SetTag("flow.type", flow.Source.Type);
        Emit(context, $"Flow '{flow.Name}' started -> {flow.Target.QualifiedName}");

        try
        {
            connectionString = await _secrets.ResolveAsync(flow.Target.Connection, ct).ConfigureAwait(false);

            // Incremental: probe the target for the watermark and bound the source read to new files
            // only. Returns the original flow unchanged when there is no incremental spec or no watermark.
            var effectiveFlow = await ApplyIncrementalAsync(flow, connectionString, context, ct).ConfigureAwait(false);

            var plan = await PlanCoreAsync(effectiveFlow, context, ct, connectionString).ConfigureAwait(false);

            // A table that did not exist before this run is created by the schema DDL below; that
            // distinction drives index handling: a new table gets its declared (desired) indexes built
            // after load, while an existing table has its current indexes disabled for the load and
            // rebuilt after. The two paths are mutually exclusive, so no index work is duplicated.
            var tableIsNew = plan.Actual is null;

            if (plan.DdlStatements.Count > 0)
            {
                await StageAsync("schema.apply-ddl", context, () => _schema.ExecuteDdlAsync(connectionString, plan.DdlStatements, ct)).ConfigureAwait(false);
            }

            if (flow.PreProcess.Count > 0)
            {
                await StageAsync("target.preprocess", context, () => _schema.ExecuteDdlAsync(connectionString, flow.PreProcess, ct)).ConfigureAwait(false);
            }

            // Disabling and rebuilding every nonclustered index costs O(table size), which pays off when the
            // load replaces or dwarfs the existing data but dominates wall-clock when a watermark-bounded
            // incremental run appends a small delta. An incremental bound (effectiveFlow differs from flow)
            // means only new data is read, so per-row index maintenance during the insert is the cheaper path
            // and the disable/rebuild pair is skipped for that run.
            var incrementallyBounded = !ReferenceEquals(effectiveFlow, flow);
            if (flow.Load.ManageIndexes && !tableIsNew && !incrementallyBounded)
            {
                disabledIndexes = await StageAsync("indexes.disable", context,
                    () => _indexManager.DisableNonClusteredAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct)).ConfigureAwait(false);
            }
            else if (flow.Load.ManageIndexes && !tableIsNew)
            {
                Emit(context, "indexes: incremental run loads only the delta; keeping indexes online instead of disable/rebuild", stage: "indexes.disable");
            }

            if (flow.Load.Mode == LoadMode.TruncateLoad)
            {
                await StageAsync("target.truncate", context, () => _loader.TruncateAsync(connectionString, flow.Target, ct)).ConfigureAwait(false);
            }

            var reader = ResolveReader(effectiveFlow.Source.Type);
            var read = await StageAsync("source.open", context, () => reader.OpenAsync(effectiveFlow.Source, plan.SourceColumns, ct)).ConfigureAwait(false);

            // Row-level incremental: drop any row not past the watermark before it reaches the bulk loader.
            // This is reader-agnostic (DuckDB also pushes the same bound into its scan; here it is a no-op
            // pass-through), so every source kind gets the same row-level filter from one place.
            await using var data = WrapWatermarkFilter(effectiveFlow.Source, read.Reader);
            var rows = await StageAsync("target.load", context, () => _loader.LoadAsync(connectionString, flow.Target, data, flow.Load, ct), r => r).ConfigureAwait(false);

            // The manifest is populated as the reader streams, so per-file row counts are known only
            // once the load has drained it.
            foreach (var file in read.ProcessedFiles)
            {
                Emit(context, $"read '{file.Name}' ({file.Rows} row(s))", stage: "source.open");
            }

            if (disabledIndexes.Count > 0)
            {
                var toRebuild = disabledIndexes;
                disabledIndexes = [];
                await StageAsync("indexes.rebuild", context,
                    () => _indexManager.RebuildAsync(connectionString, flow.Target.Schema, flow.Target.Table, toRebuild, ct)).ConfigureAwait(false);
            }

            if (tableIsNew && !string.IsNullOrWhiteSpace(flow.DesiredIndexes))
            {
                var script = flow.DesiredIndexes;
                var actions = await StageAsync("indexes.desired", context,
                    () => _desiredIndexManager.ApplyAsync(connectionString, script, ct), a => a.Count).ConfigureAwait(false);
                foreach (var action in actions)
                {
                    if (action.Kind == IndexActionKind.Created)
                    {
                        Emit(context, $"created index [{action.IndexName}] on {action.Table}", stage: "indexes.desired");
                    }
                    else
                    {
                        Emit(context, $"index [{action.IndexName}] not created: {action.Detail}", FlowEventLevel.Warning, "indexes.desired");
                    }
                }
            }

            if (flow.PostProcess.Count > 0)
            {
                await StageAsync("target.postprocess", context, () => _schema.ExecuteDdlAsync(connectionString, flow.PostProcess, ct)).ConfigureAwait(false);
            }

            await StageAsync("source.complete", context, () => reader.CompleteAsync(effectiveFlow.Source, ct)).ConfigureAwait(false);

            // Pre-ingestion transform view: refresh the typed view over the just-loaded table (the V2 post-process
            // that downstream chained flows read for correct data types). Runs after source.complete so a view
            // failure never leaves the files un-finalized: the load stands, the files are marked ingested, and a
            // re-run regenerates the view (CREATE OR ALTER is idempotent) without re-reading anything. A failure
            // here still fails the run - downstream flows read this view, so a stale one must be loud.
            TransformViewResult? transformView = null;
            if (flow.Inference.GeneratesView)
            {
                transformView = await StageAsync("transform.view", context,
                    () => GenerateTransformViewAsync(flow, connectionString, ct), r => r.Columns.Count).ConfigureAwait(false);
                Emit(context,
                    $"transformation view [{flow.Target.Schema}].[{transformView.ViewName}] refreshed "
                    + $"({transformView.Columns.Count} column(s), {transformView.Columns.Count(c => c.Converted)} typed)",
                    stage: "transform.view");
            }

            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            activity?.SetTag("rows.loaded", rows);

            _logger.LogInformation(
                "Flow '{Flow}' succeeded: {Rows} row(s) into {Target} in {ElapsedMs:F0} ms.",
                flow.Name, rows, flow.Target.QualifiedName, totalMs);
            Emit(context, $"Flow '{flow.Name}' succeeded: {rows} row(s) in {totalMs:F0} ms");

            await _state.SaveAsync(
                new FlowState { FlowName = flow.Name, LastRunUtc = DateTimeOffset.UtcNow, LastStatus = "Success" },
                ct).ConfigureAwait(false);

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Success,
                RowsLoaded = rows,
                DdlExecuted = plan.DdlStatements,
                ProcessedFiles = read.ProcessedFiles,
                Trace = context.Trace,
                TotalMs = totalMs,
                TransformView = transformView,
            };
        }
        catch (NoSourceFilesException ex) when (flow.Incremental is { FullLoad: false })
        {
            // Incremental runs commonly find nothing new; that is a clean no-op, not a failure. The
            // probe runs before any target mutation, so there is nothing to roll back here.
            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogInformation("Flow '{Flow}': no new files to load ({Reason}).", flow.Name, ex.Message);
            Emit(context, $"Flow '{flow.Name}': no new files to load");

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Success,
                RowsLoaded = 0,
                Trace = context.Trace,
                TotalMs = totalMs,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: rebuild any indexes we disabled, so a failed load doesn't leave the table
            // with its indexes disabled.
            if (disabledIndexes.Count > 0 && connectionString is not null)
            {
                try
                {
                    await _indexManager.RebuildAsync(connectionString, flow.Target.Schema, flow.Target.Table, disabledIndexes, ct).ConfigureAwait(false);
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to rebuild disabled indexes after a failed load on {Target}.", flow.Target.QualifiedName);
                }
            }

            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Flow '{Flow}' failed after {ElapsedMs:F0} ms.", flow.Name, totalMs);
            Emit(context, $"Flow '{flow.Name}' failed: {ex.Message}", FlowEventLevel.Error);

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Failed,
                Trace = context.Trace,
                TotalMs = totalMs,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Probes the target for the incremental watermark and returns a flow whose source is bounded to new data.
    /// In file-date mode the source is bounded to files newer than the watermark; in row-level mode the bound
    /// is injected as reader options that DuckDB pushes into its scan and the engine enforces row by row.
    /// Returns the flow unchanged when there is no incremental spec, a forced full load, or no prior watermark
    /// (empty/missing target).
    /// </summary>
    private async Task<FlowDefinition> ApplyIncrementalAsync(FlowDefinition flow, string connectionString, RunContext context, CancellationToken ct)
    {
        if (flow.Incremental is not { FullLoad: false } incremental)
        {
            return flow;
        }

        var probeTable = string.IsNullOrWhiteSpace(incremental.Table) ? flow.Target.QualifiedName : incremental.Table!;

        if (!string.IsNullOrWhiteSpace(incremental.WatermarkColumn))
        {
            return await ApplyRowWatermarkAsync(flow, connectionString, probeTable, incremental, context, ct).ConfigureAwait(false);
        }

        var watermark = await StageAsync("incremental.probe", context,
            () => _incrementalProbe.GetWatermarkAsync(connectionString, probeTable, incremental.DateColumn, incremental.OverlapDays, ct))
            .ConfigureAwait(false);

        if (watermark is not { } mark)
        {
            Emit(context, "incremental: no prior watermark on the target; loading all available files");
            return flow;
        }

        Emit(context, $"incremental: target watermark {mark:u}; reading files newer than it (overlap {incremental.OverlapDays}d)");

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
        {
            ["incrementalAfterDate"] = mark.UtcDateTime.ToString("o"),
        };

        return flow with { Source = flow.Source with { Options = options } };
    }

    /// <summary>
    /// Row-level incremental: probe <c>MAX(WatermarkColumn)</c> on the target, then inject the typed bound as
    /// source options. DuckDB renders them into a pushdown predicate (so it reads almost nothing past the bound
    /// on a large dataset); every reader is then filtered row by row against the same bound by the engine.
    /// </summary>
    private async Task<FlowDefinition> ApplyRowWatermarkAsync(
        FlowDefinition flow, string connectionString, string probeTable, IncrementalSpec incremental, RunContext context, CancellationToken ct)
    {
        var column = incremental.WatermarkColumn!;
        var bound = await StageAsync("incremental.probe", context,
            () => _incrementalProbe.GetRowWatermarkAsync(connectionString, probeTable, column, incremental.WatermarkOverlap, ct))
            .ConfigureAwait(false);

        if (bound is null)
        {
            Emit(context, $"incremental: no prior watermark on the target for column '{column}'; loading all available rows");
            return flow;
        }

        var canonical = WatermarkPredicate.Format(bound);
        Emit(context, $"incremental: target watermark {column} > {canonical}; reading only rows past it");

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
        {
            [WatermarkPredicate.ColumnOption] = column,
            [WatermarkPredicate.ValueOption] = canonical,
            [WatermarkPredicate.KindOption] = bound.Kind.ToString(),
        };

        return flow with { Source = flow.Source with { Options = options } };
    }

    /// <summary>
    /// Wraps the source reader with the row-level watermark filter when the engine injected a row-level bound
    /// (see <see cref="ApplyRowWatermarkAsync"/>); otherwise returns the reader unchanged.
    /// </summary>
    private static DbDataReader WrapWatermarkFilter(SourceSpec source, DbDataReader reader)
    {
        if (!source.Options.TryGetValue(WatermarkPredicate.ColumnOption, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return reader;
        }

        var value = source.Options.TryGetValue(WatermarkPredicate.ValueOption, out var v) ? v ?? string.Empty : string.Empty;
        var kindName = source.Options.TryGetValue(WatermarkPredicate.KindOption, out var k) ? k : null;
        var kind = WatermarkPredicate.ParseKind(kindName!);
        return new WatermarkFilteringDataReader(reader, column, value, kind);
    }

    /// <summary>
    /// The pre-ingestion transform post-process: introspects the just-loaded table (fresh, so evolved columns are
    /// included), profiles it when inference is on, merges the authored transforms over the inferred columns, and
    /// refreshes <c>[schema].[v&lt;Table&gt;]</c> with the resolved projection. The view is what the downstream
    /// chained flow reads, so the resolved columns ride back on the result for the run log and the catalog.
    /// </summary>
    private async Task<TransformViewResult> GenerateTransformViewAsync(FlowDefinition flow, string connectionString, CancellationToken ct)
    {
        var table = await _schema.GetTableSchemaAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(
                $"The transformation view cannot be generated: {flow.Target.QualifiedName} was not found after the load.");
        var tableColumns = table.Columns.Select(c => c.Name).ToList();

        IReadOnlyList<InferredColumn> inferred = [];
        if (flow.Inference.Enabled)
        {
            // The inference service resolves the connection reference itself (same secret path as the load).
            var report = await _inference.InferAsync(new InferenceRequest
            {
                Connection = flow.Target.Connection,
                Schema = flow.Target.Schema,
                Table = flow.Target.Table,
                Policy = flow.Inference,
            }, ct).ConfigureAwait(false);

            inferred = report.Columns.Select(c => new InferredColumn
            {
                ColumnName = c.ColumnName,
                DataType = c.DataType,
                SelectExpression = c.SelectExpression,
                Converted = c.Converted,
                Style = c.Style,
                NumericFormat = Enum.TryParse<NumericFormat>(c.NumericFormat, out var format) ? format : null,
            }).ToList();
        }

        var resolved = ColumnTransformResolver.Resolve(tableColumns, flow.Inference, inferred);
        var viewName = $"v_{flow.Target.Table}";
        var ddl = TransformViewBuilder.Build(flow.Target.Schema, viewName, flow.Target.Schema, flow.Target.Table, resolved);
        await _schema.ExecuteDdlAsync(connectionString, [ddl], ct).ConfigureAwait(false);

        return new TransformViewResult { ViewName = viewName, Ddl = ddl, Columns = resolved };
    }

    /// <param name="flow">The flow to plan.</param>
    /// <param name="context">Per-run state for events and tracing.</param>
    /// <param name="ct">Cancellation for the plan.</param>
    /// <param name="resolvedConnection">The already-resolved target connection string when the caller resolved
    /// it (the run path does, so one run never resolves the same secret twice); null resolves it here.</param>
    private async Task<FlowPlan> PlanCoreAsync(FlowDefinition flow, RunContext context, CancellationToken ct, string? resolvedConnection = null)
    {
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity("flow.plan");
        activity?.SetTag("flow.name", flow.Name);
        activity?.SetTag("flow.id", context.FlowId);
        activity?.SetTag("flow.run_id", context.RunId);

        var connectionString = resolvedConnection ?? await _secrets.ResolveAsync(flow.Target.Connection, ct).ConfigureAwait(false);
        var reader = ResolveReader(flow.Source.Type);

        var sourceColumns = await StageAsync("source.columns", context, () => reader.GetColumnsAsync(flow.Source, ct)).ConfigureAwait(false);
        var desired = DesiredSchemaBuilder.Build(flow.Target, sourceColumns, flow.Schema, _typeMapper);
        var actual = await StageAsync("target.introspect", context, () => _schema.GetTableSchemaAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct)).ConfigureAwait(false);
        var delta = SchemaDiffer.Diff(desired, actual, flow.Schema.Evolve);
        var statements = _ddl.Generate(flow.Target, delta);

        _logger.LogInformation(
            "Planned '{Flow}': {Action}; {Adds} column(s) to add; {Ddl} DDL statement(s).",
            flow.Name, actual is null ? "create table" : "table exists", delta.ColumnsToAdd.Count, statements.Count);

        return new FlowPlan
        {
            Flow = flow,
            SourceColumns = sourceColumns,
            Desired = desired,
            Actual = actual,
            Delta = delta,
            DdlStatements = statements,
            Trace = context.Trace,
        };
    }

    private async Task<T> StageAsync<T>(string operation, RunContext context, Func<Task<T>> action, Func<T, long?>? rows = null)
    {
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity(operation);
        Emit(context, $"{operation} started", FlowEventLevel.Trace, operation);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await action().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var rowCount = rows?.Invoke(result);
            activity?.SetTag("rows", rowCount);
            context.Trace.Add(new TraceEntry { Operation = operation, ElapsedMs = elapsedMs, Succeeded = true, Rows = rowCount });
            _logger.LogDebug("Stage '{Stage}' ok in {ElapsedMs:F1} ms.", operation, elapsedMs);
            _events.Publish(new FlowEvent
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = context.FlowName,
                Stage = operation,
                Rows = rowCount,
                ElapsedMs = elapsedMs,
                Message = $"{operation} ({elapsedMs:F0} ms{(rowCount is { } r ? $", {r} rows" : string.Empty)})",
            });
            return result;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            context.Trace.Add(new TraceEntry { Operation = operation, ElapsedMs = elapsedMs, Succeeded = false, Detail = ex.Message });
            _events.Publish(new FlowEvent
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = context.FlowName,
                Level = FlowEventLevel.Error,
                Stage = operation,
                ElapsedMs = elapsedMs,
                Message = $"{operation} failed: {ex.Message}",
            });
            throw;
        }
    }

    private Task StageAsync(string operation, RunContext context, Func<Task> action)
        => StageAsync(operation, context, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });

    private void Emit(RunContext context, string message, FlowEventLevel level = FlowEventLevel.Info, string? stage = null)
        => _events.Publish(new FlowEvent
        {
            RunId = context.RunId,
            FlowId = context.FlowId,
            FlowName = context.FlowName,
            Level = level,
            Stage = stage,
            Message = message,
        });

    private ISourceReader ResolveReader(string sourceType)
        => _sources.FirstOrDefault(s => s.CanHandle(sourceType))
           ?? throw new SqlFlowException($"No source reader is registered for source type '{sourceType}'.");

    /// <summary>Per-run state. A fresh instance per run/plan call keeps concurrent runs isolated.</summary>
    private sealed class RunContext(Guid runId, Guid flowId, string flowName)
    {
        public Guid RunId { get; } = runId;
        public Guid FlowId { get; } = flowId;
        public string FlowName { get; } = flowName;
        public List<TraceEntry> Trace { get; } = [];
    }
}
