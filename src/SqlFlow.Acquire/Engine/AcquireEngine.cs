using System.Diagnostics;
using System.Text.Json;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Orchestrates one acquisition run: it builds the reliability primitives, resolves authentication (once per run, or
/// per iteration when the issuer scopes tokens that way), expands the fan-out iterations into concrete request
/// contexts (date windows, static lists, and id lists discovered from a prior request), runs the selected transport
/// for each context, and assembles the run result and the advanced watermark. Transports do the fetching and land
/// the raw payloads; the engine owns everything around them.
/// </summary>
public sealed class AcquireEngine
{
    private readonly IRawLandingStore _landing;
    private readonly AuthResolver _auth;
    private readonly ISecretResolver _secrets;
    private readonly IReadOnlyList<IAcquireTransport> _transports;
    private readonly TimeProvider _time;
    private readonly Func<AcquireReliability, HttpClient> _httpClientFactory;

    public AcquireEngine(
        IRawLandingStore landing,
        AuthResolver auth,
        ISecretResolver secrets,
        IEnumerable<IAcquireTransport> transports,
        TimeProvider time,
        Func<AcquireReliability, HttpClient>? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(landing);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(time);
        _landing = landing;
        _auth = auth;
        _secrets = secrets;
        _transports = transports.ToList();
        _time = time;
        _httpClientFactory = httpClientFactory ?? HttpClientBuilder.Build;
    }

    public async Task<AcquireRunResult> RunAsync(
        AcquireFlow flow, Guid runId, IRunEventSink log, string? priorWatermark,
        CancellationToken ct = default, AcquireRunOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var run = overrides ?? AcquireRunOverrides.None;

        var stopwatch = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var watermark = new WatermarkState(flow.Incremental, priorWatermark);

        // Everything from parameter binding onward runs inside the failure envelope: a configuration error
        // (missing param, bad transport, unresolvable secret, auth failure) surfaces as a FAILED RUN with the
        // cause in the result, exactly like a mid-run fetch failure, so a scheduled run records its failure
        // rather than throwing out of the runner.
        var pages = 0;
        var iterations = 0;
        var success = true;
        string? error = null;
        string? resolvedBase = null;
        LandingPipeline? pipeline = null;
        HttpClient? client = null;
        try
        {
            var transport = SelectTransport(flow.Source.Transport);

            var baseVars = new TemplateContext(now).WithDate("now", now);
            BindParams(flow, run.Params, baseVars, log);
            if (flow.Incremental is { BindVariable: { } bindVar } && watermark.Before is { } before)
            {
                baseVars.WithString(bindVar, before);
            }

            resolvedBase = await _secrets.ResolveAsync(TemplateEngine.Render(flow.Landing.Target, baseVars), ct).ConfigureAwait(false);
            pipeline = new LandingPipeline(flow.Landing, _landing, resolvedBase, runId, log, run.DryRun);

            client = _httpClientFactory(flow.Source.Reliability);
            var dataHttp = HttpExecutorFor(client, flow.Source.Reliability, flow.Source.Reliability.UrlAllowlist);
            // Token and OIDC-discovery calls may target a host outside the data allowlist; they are trusted
            // config, so the auth executor keeps the SSRF IP guard but not the host allowlist.
            var authHttp = HttpExecutorFor(client, flow.Source.Reliability, []);

            var refreshPerIteration = flow.Source.Auth.Token?.RefreshPerIteration == true;
            var discoveryAuth = await _auth.ResolveAsync(flow.Source.Auth, authHttp, baseVars, ct).ConfigureAwait(false);

            var contexts = await ExpandAsync(flow.Source, baseVars, flow.Source.Iterations, 0, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false);
            foreach (var vars in contexts)
            {
                ct.ThrowIfCancellationRequested();
                var iterationAuth = refreshPerIteration ? await _auth.ResolveAsync(flow.Source.Auth, authHttp, vars, ct).ConfigureAwait(false) : discoveryAuth;
                var fetch = new AcquireFetch
                {
                    Source = flow.Source,
                    Vars = vars,
                    Auth = iterationAuth,
                    Landing = pipeline,
                    Watermark = watermark,
                    Log = log,
                    Secrets = _secrets,
                    Http = transport is HttpTransport ? dataHttp : null,
                    Iteration = iterations,
                    Probe = run.Probe,
                    MaxPagesOverride = run.MaxPagesOverride,
                };

                await transport.FetchAsync(fetch, ct).ConfigureAwait(false);
                pages += fetch.Pages;
                iterations++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure surfaces as a failed (or partial) run: files landed before the failure are kept, the
            // counters reflect what completed, and the watermark is reported but not persisted (the history reader
            // only advances from a successful run), so the next run re-fetches the un-landed remainder.
            success = false;
            error = ex.Message;
            log.Log(RunLogLevel.Info, "acquire.error", ex.Message);
        }
        finally
        {
            client?.Dispose();
        }

        stopwatch.Stop();
        return new AcquireRunResult
        {
            RunId = runId,
            Success = success,
            Error = error,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            Iterations = iterations,
            PagesFetched = pages,
            FilesWritten = pipeline?.FilesWritten ?? 0,
            Skipped = pipeline?.Skipped ?? 0,
            BytesWritten = pipeline?.BytesWritten ?? 0,
            LandedBase = resolvedBase,
            WatermarkBefore = watermark.Before,
            WatermarkAfter = success ? watermark.Current : watermark.Before,
            Files = pipeline?.Files ?? [],
        };
    }

    private IAcquireTransport SelectTransport(AcquireTransport transport)
        => _transports.FirstOrDefault(t => t.CanHandle(transport))
           ?? throw new SqlFlowException($"No transport is registered for '{transport}'.");

    /// <summary>
    /// Binds the flow's declared parameters into the run's template context: the declared default first, overlaid
    /// by any runtime value the caller supplied. A runtime value for an undeclared name is rejected (a typo'd
    /// override must fail loudly, not silently fetch the default window), and a parameter declared without a
    /// default that receives no runtime value is a hard error before the first request is sent.
    /// </summary>
    private static void BindParams(
        AcquireFlow flow, IReadOnlyDictionary<string, string>? runtimeParams, TemplateContext vars, IRunEventSink log)
    {
        if (runtimeParams is { Count: > 0 })
        {
            var undeclared = runtimeParams.Keys
                .Where(name => !flow.Params.ContainsKey(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (undeclared.Count > 0)
            {
                throw new SqlFlowException(
                    $"Runtime parameter(s) {string.Join(", ", undeclared.Select(p => $"'{p}'"))} are not declared by " +
                    $"flow '{flow.Name}'. Declare them under 'params:' (name: default) so overrides are typo-safe. " +
                    $"Declared: {(flow.Params.Count == 0 ? "(none)" : string.Join(", ", flow.Params.Keys.OrderBy(k => k, StringComparer.Ordinal)))}.");
            }
        }

        var missing = new List<string>();
        foreach (var (name, fallback) in flow.Params)
        {
            var supplied = runtimeParams is not null && runtimeParams.TryGetValue(name, out var value) ? value : fallback;
            if (supplied is null)
            {
                missing.Add(name);
                continue;
            }

            vars.WithString(name, supplied);
            if (runtimeParams?.ContainsKey(name) == true)
            {
                log.Log(RunLogLevel.Info, "params", $"parameter '{name}' overridden for this run.");
            }
        }

        if (missing.Count > 0)
        {
            throw new SqlFlowException(
                $"Parameter(s) {string.Join(", ", missing.OrderBy(m => m, StringComparer.Ordinal).Select(m => $"'{m}'"))} " +
                $"declare no default and were not supplied for this run of flow '{flow.Name}'.");
        }
    }

    private HttpExecutor HttpExecutorFor(HttpClient client, AcquireReliability reliability, IReadOnlyList<string> allowlist)
        => new(client, new RetryPolicy(reliability.Retry, _time), new RateLimiter(reliability.RateLimitRps, _time), new UrlGuard(allowlist), reliability.MaxResponseBytes, _time);

    private async Task<List<TemplateContext>> ExpandAsync(
        AcquireSource source,
        TemplateContext ctx,
        IReadOnlyList<AcquireIteration> iterations,
        int index,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        DateTimeOffset now,
        WatermarkState watermark,
        AcquireRunOverrides run,
        CancellationToken ct)
    {
        if (index >= iterations.Count)
        {
            return [ctx];
        }

        var iteration = iterations[index];
        var binders = await BindersAsync(source, iteration, ctx, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false);

        var results = new List<TemplateContext>();
        foreach (var binder in binders)
        {
            var child = ctx.Clone();
            binder(child);
            results.AddRange(await ExpandAsync(source, child, iterations, index + 1, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<Action<TemplateContext>>> BindersAsync(
        AcquireSource source,
        AcquireIteration iteration,
        TemplateContext ctx,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        DateTimeOffset now,
        WatermarkState watermark,
        AcquireRunOverrides run,
        CancellationToken ct)
    {
        switch (iteration.Kind)
        {
            case AcquireIterationKind.DateWindow:
                return DateWindowBinders(iteration, now, watermark, run);

            case AcquireIterationKind.List:
            {
                var variable = iteration.Variable ?? throw new SqlFlowException("A list iteration requires 'variable'.");
                return iteration.Values.Select(value => (Action<TemplateContext>)(c => c.WithString(variable, value))).ToList();
            }

            case AcquireIterationKind.IdsFrom:
                return await IdsFromBindersAsync(source, iteration, ctx, discoveryAuth, dataHttp, ct).ConfigureAwait(false);

            default:
                throw new SqlFlowException($"Unsupported iteration kind '{iteration.Kind}'.");
        }
    }

    private static IReadOnlyList<Action<TemplateContext>> DateWindowBinders(
        AcquireIteration iteration, DateTimeOffset now, WatermarkState watermark, AcquireRunOverrides run)
    {
        // An externally-bounded backfill window (the typed per-run contract) replaces the iteration's own
        // bounds for this run; otherwise the authored from/to (or the watermark, or the 1-day default) apply.
        var from = run.WindowFrom
            ?? (iteration.From is { } f ? RelativeTime.Resolve(f, now)
                : watermark.Before is { } wm && DateTimeOffset.TryParse(wm, out var parsed) ? parsed
                : now.AddDays(-1));
        var to = run.WindowTo ?? (iteration.To is { } t ? RelativeTime.Resolve(t, now) : now);

        if (from > to)
        {
            throw new SqlFlowException($"Date window 'from' ({from:o}) is after 'to' ({to:o}).");
        }

        var binders = new List<Action<TemplateContext>>();
        var cursor = from;
        var guard = 0;
        while (cursor < to && guard++ < 100_000)
        {
            var stepEnd = Step(cursor, iteration.Granularity);
            if (stepEnd > to)
            {
                stepEnd = to;
            }

            var stepStart = cursor;
            binders.Add(c => c
                .WithDate(iteration.FromVariable, stepStart)
                .WithDate(iteration.ToVariable, stepEnd)
                .WithReferenceDate(stepStart));
            cursor = stepEnd;
        }

        return binders;
    }

    private static DateTimeOffset Step(DateTimeOffset from, AcquireWindowGranularity granularity) => granularity switch
    {
        AcquireWindowGranularity.Hour => from.AddHours(1),
        AcquireWindowGranularity.Day => from.AddDays(1),
        AcquireWindowGranularity.Month => from.AddMonths(1),
        _ => from.AddDays(1),
    };

    private async Task<IReadOnlyList<Action<TemplateContext>>> IdsFromBindersAsync(
        AcquireSource source,
        AcquireIteration iteration,
        TemplateContext ctx,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        CancellationToken ct)
    {
        var variable = iteration.Variable ?? throw new SqlFlowException("An idsFrom iteration requires 'variable'.");
        var idRequest = iteration.IdRequest ?? throw new SqlFlowException("An idsFrom iteration requires an 'idRequest'.");
        var idPath = iteration.IdPath ?? throw new SqlFlowException("An idsFrom iteration requires an 'idPath'.");

        var result = await dataHttp.SendAsync(() => HttpRequestBuilder.Build(source.BaseUrl, idRequest, ctx, discoveryAuth, new Dictionary<string, string>(StringComparer.Ordinal)), allowStatuses: null, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.Body);
        var ids = JsonPathReader.SelectValues(document.RootElement, idPath);

        var batchSize = Math.Max(1, iteration.BatchSize);
        var binders = new List<Action<TemplateContext>>();
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var batch = string.Join(iteration.BatchSeparator, ids.Skip(i).Take(batchSize));
            binders.Add(c => c.WithString(variable, batch));
        }

        return binders;
    }
}
