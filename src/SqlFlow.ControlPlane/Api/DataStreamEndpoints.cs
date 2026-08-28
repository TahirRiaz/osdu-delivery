using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Model;
using SqlFlow.HealthCheck;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One detector's verdict on a stream: whether it fired, its confidence in [0, 1], which way the
/// deviation went (<c>below</c> / <c>above</c> / <c>none</c>), whether it may raise a finding on its own, and
/// the evidence sentence. Detectors that stayed quiet are reported too, so a healthy verdict is auditable
/// rather than merely asserted.</summary>
public sealed record StreamSignalDto(
    string Detector, bool Fired, double Score, string Direction, bool Primary, string Detail);

/// <summary>What one table's traffic normally looks like, learned from its own history after reprocessing was
/// excluded. <c>shape</c> is <c>daily</c>, <c>weekdays</c>, <c>weekly</c>, <c>several-days-a-week</c>,
/// <c>periodic</c>, or <c>sporadic</c>; <c>loadDays</c> names the weekdays it reliably loads on;
/// <c>reliability</c> is the share of expected days it actually delivered on.</summary>
public sealed record StreamPatternDto(
    string Shape, IReadOnlyList<string> LoadDays, double TypicalRows, double LowRows, double HighRows,
    double Reliability, string Description);

/// <summary>A stream's measured normal: how much it writes, how often, and where it is trending. These are the
/// averages an operator checks a verdict against, and what every detector is calibrated on.</summary>
public sealed record StreamProfileDto(
    StreamPatternDto Pattern,
    string Cadence, double ExpectedGapDays, string CadenceSource, double MaxObservedGapDays,
    DateTime? LastLoadUtc, DateTime? LastRunUtc, double? DaysSinceLastLoad, double? DaysSinceLastRun,
    int RunDays, int LoadedDays, long Runs, long Failures,
    long TotalRowsInserted, long TotalRowsUpdated, long TotalRowsDeleted,
    double AvgRowsInsertedPerRun, double AvgRowsUpdatedPerRun, double AvgRowsDeletedPerRun,
    double AvgRowsWrittenPerLoadedDay, double MedianRowsWrittenPerLoadedDay, double TrendRowsPerDay,
    int UnexpectedNullDays, int EmptyRunDays, int NoRunDays, double PredictedNullDays,
    int TrimmedLoadDays, double TrimFence);

/// <summary>One analysed day: what arrived, what was expected, and how the point was judged.</summary>
public sealed record StreamPointDto(
    DateTime Date, long RowsWritten, long RowsInserted, long RowsUpdated, long RowsDeleted,
    int Runs, int Failures, int ExcludedBackfillRuns,
    double Expected, double Severity, bool Anomaly, string? Reason, bool Imputed, bool Immature,
    double ExpectedLoadRate, bool UnexpectedNull, bool Trimmed);

/// <summary>
/// One data stream: a flow, the table it writes, and the ensemble's verdict on whether data is still arriving
/// the way it should. <see cref="Series"/> is null on the board (a hundred streams times sixty days is a
/// payload nobody reads) and populated on the single-stream endpoint, which is what the chart draws.
/// </summary>
public sealed record DataStreamDto(
    Guid PipelineId, string FlowName, string FlowKind, string? Batch, bool Active, string? TargetObject,
    string? ScheduleName, string? Cron, string? Timezone,
    string Status, string Category, string Severity, double Confidence, int AgreeingDetectors, string Summary,
    StreamProfileDto Profile, IReadOnlyList<StreamSignalDto> Signals, IReadOnlyList<StreamPointDto>? Series);

/// <summary>
/// The data-stream board: every monitored stream with its verdict, ranked most urgent first, plus the counts
/// that say how much of the estate is in each state. The counts cover every ANALYSED stream, so a truncated
/// list is visibly truncated.
/// </summary>
public sealed record DataStreamsDto(
    int WindowDays, DateTime FromUtc, DateTime AsOfUtc, bool IncludeBackfills,
    int TotalStreams, int AnalyzedStreams, long ExcludedBackfillRuns,
    int StalledCount, int DegradedCount, int WatchCount, int HealthyCount, int InsufficientHistoryCount,
    IReadOnlyList<DataStreamDto> Streams);

/// <summary>
/// DataStream anomaly detection. Three questions about every loaded table, ranked because they are not
/// equally urgent: <b>zero data</b> (an outage, and the only one that can be critical), <b>less data than
/// normal</b> (a degradation, capped at warning), and <b>more data than normal</b> (information).
///
/// <para>
/// The estate already records how many rows every run inserted, updated, and deleted. That is a heartbeat for
/// every table the platform writes, collected with no configuration at all, which is what makes this the
/// complement of the <c>hc</c> flow rather than a competitor to it. A health-check flow reads the monitored
/// table itself along a declared date column and sees the shape of the data; it costs a flow per table, so in
/// practice a few tables get one. This surface covers every stream the moment it has run once, and hands the
/// deep instrument the shortlist worth pointing at.
/// </para>
///
/// <para>
/// Reprocessing is removed in two passes, and everything above depends on it. This endpoint drops the runs
/// the log FLAGS as backfills (<c>includeBackfills</c> keeps them), and the detector then trims the outsized
/// days the flags missed: a catch-up after an outage, a re-run kicked off without backfill parameters, a
/// source that delivered a year in one file. Either kind left in redefines the stream's normal and leaves
/// every ordinary day afterwards reading as a collapse, which is what makes a volume test worthless. And
/// where a stream declares a schedule, the cadence it is held to comes from the CRON rather than its own
/// recent behaviour, because a stream broken for three weeks otherwise teaches the detector that three-week
/// gaps are what it does.
/// </para>
///
/// The analysis itself is <see cref="StreamAnomalyDetector"/>, which shares its estimators with the
/// health-check engine so the two surfaces cannot disagree about what abnormal means.
/// </summary>
public static class DataStreamEndpoints
{
    /// <summary>The widest window the board accepts. Past this the per-stream change-point search stops being
    /// an interactive read, and the older history is a different regime anyway.</summary>
    public const int MaxWindowDays = 180;

    /// <summary>The default window: long enough for a weekly stream to show a cadence and for the rate test to
    /// have a baseline behind its recent slice.</summary>
    public const int DefaultWindowDays = 60;

    /// <summary>Streams analysed at most in one request, newest activity first. Ranking requires analysing
    /// every candidate, so this is the real bound on the work a single call can do; the response reports both
    /// numbers, so a capped sweep is visible rather than silently partial.</summary>
    public const int MaxStreams = 1000;

    public const int MaxResults = 500;

    public static RouteGroupBuilder MapDataStreamEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var streams = group.MapGroup("/datastreams").WithTags("DataStreams");
        streams.MapGet(string.Empty, GetDataStreamsAsync).WithName("GetDataStreams");
        streams.MapGet("/{pipelineId:guid}", GetDataStreamAsync).WithName("GetDataStream");

        return group;
    }

    /// <summary>
    /// The board. Analyses every stream that ran in the window and returns them ranked most urgent first.
    /// </summary>
    /// <param name="db">The shadow catalog.</param>
    /// <param name="clock">The clock the window and drought arithmetic are anchored at.</param>
    /// <param name="days">The analysis window (1 to <see cref="MaxWindowDays"/>).</param>
    /// <param name="repoId">Restrict to one repository.</param>
    /// <param name="batch">Restrict to one batch (data source).</param>
    /// <param name="status">Restrict to one verdict: <c>stalled</c>, <c>degraded</c>, <c>watch</c>,
    /// <c>healthy</c>, or <c>insufficient-history</c>.</param>
    /// <param name="includeBackfills">Count backfills and other operator-driven reprocessing as normal
    /// traffic. False by default, which is what keeps a history replay from redefining a stream's normal.</param>
    /// <param name="limit">How many streams to return (the counts still cover every analysed stream).</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<Results<Ok<DataStreamsDto>, ProblemHttpResult>> GetDataStreamsAsync(
        CatalogDbContext db, TimeProvider clock, int? days, Guid? repoId, string? batch, string? status,
        bool? includeBackfills, int? limit, CancellationToken ct)
    {
        if (Validate(days, limit) is { } problem)
        {
            return problem;
        }

        var report = await ComputeAsync(
            db, clock, days ?? DefaultWindowDays, repoId, batch, includeBackfills == true,
            pipelineId: null, ct).ConfigureAwait(false);

        var filtered = string.IsNullOrWhiteSpace(status)
            ? report.Streams
            : report.Streams.Where(s => s.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        return TypedResults.Ok(report with { Streams = filtered.Take(limit ?? 100).ToList() });
    }

    /// <summary>One stream in full, including the day-by-day series the chart draws and every detector's
    /// reasoning. Same analysis as the board, so the detail can never disagree with the row that led to it.</summary>
    private static async Task<Results<Ok<DataStreamDto>, NotFound, ProblemHttpResult>> GetDataStreamAsync(
        Guid pipelineId, CatalogDbContext db, TimeProvider clock, int? days, bool? includeBackfills,
        CancellationToken ct)
    {
        if (Validate(days, limit: null) is { } problem)
        {
            return problem;
        }

        var report = await ComputeAsync(
            db, clock, days ?? DefaultWindowDays, repoId: null, batch: null, includeBackfills == true,
            pipelineId, ct).ConfigureAwait(false);

        var stream = report.Streams.FirstOrDefault(s => s.PipelineId == pipelineId);
        return stream is null ? TypedResults.NotFound() : TypedResults.Ok(stream);
    }

    /// <summary>
    /// The shared computation both endpoints read, in four passes over the catalog and one analysis per
    /// stream: the per-day write statistics of the runs that count, the per-day count of the backfill runs
    /// that do not, the schedule each stream is held to, and the pipeline metadata. The target table is
    /// resolved last, for the returned page only, because a lineage read for a thousand streams to display a
    /// hundred is work nobody asked for.
    /// </summary>
    private static async Task<DataStreamsDto> ComputeAsync(
        CatalogDbContext db, TimeProvider clock, int windowDays, Guid? repoId, string? batch,
        bool includeBackfills, Guid? pipelineId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var fromUtc = now.Date.AddDays(-(windowDays - 1));

        var window = db.Runs.AsNoTracking()
            .Where(r => r.WrittenUtc >= fromUtc
                && (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed)
                && (repoId == null || r.RepoId == repoId)
                && (pipelineId == null || r.PipelineId == pipelineId));

        // Ordinary traffic only, unless the caller asked otherwise: see Reprocessing for what that excludes.
        var counted = includeBackfills ? window : window.Where(NotReprocessing);

        var activity = await counted
            .GroupBy(r => new { r.PipelineId, Day = r.WrittenUtc.Date })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.Day,
                Runs = g.Count(),
                Failures = g.Count(r => r.Status == RunStatuses.Failed),
                // A run that reports no insert/update/delete breakdown still reports what it loaded; attributing
                // that to inserts keeps the stream's total volume honest for the flow kinds (file, export, copy)
                // whose results carry only a row count.
                Inserted = g.Sum(r => r.RowsInserted != null || r.RowsUpdated != null || r.RowsDeleted != null
                    ? r.RowsInserted ?? 0
                    : r.RowsLoaded ?? 0),
                Updated = g.Sum(r => r.RowsUpdated ?? 0),
                Deleted = g.Sum(r => r.RowsDeleted ?? 0),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var excluded = includeBackfills
            ? []
            : await window.Where(Reprocessing)
                .GroupBy(r => new { r.PipelineId, Day = r.WrittenUtc.Date })
                .Select(g => new ExcludedDay(g.Key.PipelineId, g.Key.Day, g.Count()))
                .ToListAsync(ct).ConfigureAwait(false);
        var excludedByStream = excluded
            .GroupBy(e => e.PipelineId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Day.Date, e => e.Runs));

        var schedules = await LoadSchedulesAsync(db, now, repoId, ct).ConfigureAwait(false);

        var candidateIds = activity.Select(a => a.PipelineId)
            .Concat(excludedByStream.Keys)
            .Distinct()
            .ToList();
        var meta = await db.Pipelines.AsNoTracking()
            .Where(p => candidateIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Kind, p.Batch, p.Active })
            .ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);

        var batchFilter = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim();
        var byStream = activity.GroupBy(a => a.PipelineId).ToDictionary(g => g.Key, g => g.ToList());

        // A stream that loaded NOTHING inside the window may simply be new, or may have been dead longer than
        // we looked. Those are opposite findings, and the window cannot tell them apart, so the last load
        // before it is read from the full history. Scoped to the streams that need it, which is normally a
        // handful, rather than scanning the run table for every stream on the board.
        var silentIds = candidateIds.Where(id => !byStream.ContainsKey(id)
            || byStream[id].All(r => r.Inserted + r.Updated + r.Deleted == 0)).ToList();
        var lastLoadBeforeWindow = silentIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking()
                .Where(r => silentIds.Contains(r.PipelineId)
                    && r.WrittenUtc < fromUtc
                    && r.Status == RunStatuses.Succeeded
                    && ((r.RowsInserted ?? 0) + (r.RowsUpdated ?? 0) + (r.RowsDeleted ?? 0) > 0
                        || (r.RowsInserted == null && r.RowsUpdated == null && r.RowsDeleted == null
                            && (r.RowsLoaded ?? 0) > 0)))
                .GroupBy(r => r.PipelineId)
                .Select(g => new { PipelineId = g.Key, LastLoadUtc = g.Max(r => r.WrittenUtc) })
                .ToDictionaryAsync(x => x.PipelineId, x => x.LastLoadUtc, ct).ConfigureAwait(false);

        // The candidates, newest activity first, so a capped sweep keeps the streams whose state is current.
        var ordered = candidateIds
            .Where(id => batchFilter is null
                || string.Equals(meta.GetValueOrDefault(id)?.Batch, batchFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => byStream.TryGetValue(id, out var rows) ? rows.Max(r => r.Day) : DateTime.MinValue)
            .ToList();
        var analyzed = ordered.Take(MaxStreams).ToList();

        var results = new List<DataStreamDto>(analyzed.Count);
        foreach (var id in analyzed)
        {
            ct.ThrowIfCancellationRequested();

            var excludedDays = excludedByStream.GetValueOrDefault(id) ?? [];
            var rows = byStream.GetValueOrDefault(id) ?? [];
            var days = rows.Select(r => r.Day.Date)
                .Concat(excludedDays.Keys)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            var buckets = days.Select(day =>
            {
                var row = rows.FirstOrDefault(r => r.Day.Date == day);
                return new StreamBucket
                {
                    Date = day,
                    Runs = row?.Runs ?? 0,
                    Failures = row?.Failures ?? 0,
                    ExcludedBackfillRuns = excludedDays.GetValueOrDefault(day),
                    RowsInserted = row?.Inserted ?? 0,
                    RowsUpdated = row?.Updated ?? 0,
                    RowsDeleted = row?.Deleted ?? 0,
                };
            }).ToList();

            var schedule = schedules.GetValueOrDefault(id);
            var analysis = StreamAnomalyDetector.Analyze(
                buckets, fromUtc, now,
                new StreamAnomalyOptions
                {
                    ExpectedGapDaysOverride = schedule?.ExpectedGapDays,
                    LastKnownLoadUtc = lastLoadBeforeWindow.TryGetValue(id, out var seen) ? seen : null,
                });

            var pipeline = meta.GetValueOrDefault(id);
            results.Add(new DataStreamDto(
                id, pipeline?.Name ?? id.ToString(), pipeline?.Kind ?? "?", pipeline?.Batch,
                pipeline?.Active ?? false, TargetObject: null,
                schedule?.Name, schedule?.Cron, schedule?.Timezone,
                StatusName(analysis.Status), analysis.Category, analysis.Severity, analysis.Confidence,
                analysis.AgreeingDetectors, analysis.Summary,
                ToDto(analysis.Profile),
                analysis.Signals.Select(s => new StreamSignalDto(
                    DetectorName(s.Detector), s.Fired, s.Score, DirectionName(s.Direction), s.Primary,
                    s.Detail)).ToList(),
                pipelineId is null ? null : analysis.Series.Select(ToDto).ToList()));
        }

        var ranked = results
            .OrderBy(s => SeverityRank(s.Severity))
            .ThenBy(s => StatusRank(s.Status))
            .ThenBy(s => CategoryRank(s.Category))
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => s.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await ResolveTargetsAsync(db, ranked, ct).ConfigureAwait(false);

        // Scoped to the streams that were analysed, so the number squares with the board rather than counting
        // reprocessing on streams a filter removed.
        var analyzedSet = analyzed.ToHashSet();
        return new DataStreamsDto(
            windowDays, fromUtc, now, includeBackfills,
            ordered.Count, ranked.Count,
            excluded.Where(e => analyzedSet.Contains(e.PipelineId)).Sum(e => (long)e.Runs),
            ranked.Count(s => s.Status == "stalled"),
            ranked.Count(s => s.Status == "degraded"),
            ranked.Count(s => s.Status == "watch"),
            ranked.Count(s => s.Status == "healthy"),
            ranked.Count(s => s.Status == "insufficient-history"),
            ranked);
    }

    /// <summary>Backfill runs on one day of one stream, counted but never analysed.</summary>
    private sealed record ExcludedDay(Guid PipelineId, DateTime Day, int Runs);

    /// <summary>
    /// The run shapes that are reprocessing rather than the stream's ordinary traffic: a forced full load, a
    /// bounded backfill window, an ad-hoc source predicate or file pattern, a reprocess from the source
    /// minimum, an assertions-only evaluation (which loads nothing by design), or an engine-derived scope of
    /// backfill / init-load. Every one is already flagged on the run row, so excluding them is a fact read
    /// from the log rather than a guess from the row counts.
    /// </summary>
    private static readonly Expression<Func<CatalogRun, bool>> Reprocessing = run =>
        run.FullLoad
            || run.BackfillFrom != null
            || run.BackfillTo != null
            || run.SourceFilter != null
            || run.FilePattern != null
            || run.ReprocessFromSourceMin
            || run.AssertionsOnly
            || run.IncrementalMode == IncrementalModes.Backfill
            || run.IncrementalMode == IncrementalModes.InitLoad;

    /// <summary>The exact complement of <see cref="Reprocessing"/>, negated from the same expression tree so
    /// the counted and excluded queries cannot drift into double-counting a run or dropping one.</summary>
    private static readonly Expression<Func<CatalogRun, bool>> NotReprocessing =
        Expression.Lambda<Func<CatalogRun, bool>>(
            Expression.Not(Reprocessing.Body), Reprocessing.Parameters);

    /// <summary>The declared cadence each stream is held to. A flow on several enabled schedules is held to the
    /// most frequent of them (the shortest gap), because that is the one whose miss is noticeable first. Gaps
    /// are computed once per distinct timing rather than once per member, since a source's whole wave usually
    /// shares one schedule.</summary>
    private static async Task<Dictionary<Guid, ScheduleCadence>> LoadSchedulesAsync(
        CatalogDbContext db, DateTime now, Guid? repoId, CancellationToken ct)
    {
        var members = await db.ScheduleMembers.AsNoTracking()
            .Join(db.Schedules.AsNoTracking(), m => m.ScheduleId, s => s.Id, (m, s) => new
            {
                m.PipelineId, s.Name, s.Cron, s.IntervalSeconds, s.Timezone, s.Enabled, s.RepoId,
            })
            .Where(x => x.Enabled && (repoId == null || x.RepoId == repoId))
            .ToListAsync(ct).ConfigureAwait(false);

        var gapCache = new Dictionary<(string? Cron, int? Interval, string Timezone), double?>();
        var byPipeline = new Dictionary<Guid, ScheduleCadence>();
        foreach (var member in members)
        {
            var key = (member.Cron, member.IntervalSeconds, member.Timezone);
            if (!gapCache.TryGetValue(key, out var gap))
            {
                gap = ScheduleClock.ExpectedGapDays(member.Cron, member.IntervalSeconds, member.Timezone, now);
                gapCache[key] = gap;
            }

            if (gap is not { } days)
            {
                continue; // a chained or malformed schedule declares no cadence; the detector infers one
            }

            var cadence = new ScheduleCadence(member.Name, member.Cron, member.Timezone, days);
            if (!byPipeline.TryGetValue(member.PipelineId, out var existing) || days < existing.ExpectedGapDays)
            {
                byPipeline[member.PipelineId] = cadence;
            }
        }

        return byPipeline;
    }

    /// <summary>Fills in the table each returned stream writes, from the lineage edges. Done for the ranked
    /// page only and in one query; a stream whose lineage has not been computed simply reports no target,
    /// which is a display gap rather than an analysis one.</summary>
    private static async Task ResolveTargetsAsync(
        CatalogDbContext db, List<DataStreamDto> streams, CancellationToken ct)
    {
        if (streams.Count == 0)
        {
            return;
        }

        var ids = streams.Select(s => s.PipelineId).ToList();
        var writes = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId != null && e.Relation == "Writes" && ids.Contains(e.PipelineId!.Value))
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectName, e.Tier })
            .ToListAsync(ct).ConfigureAwait(false);

        // A flow can write several objects (a staging table and its target). The DECLARED edge is the flow's
        // own target; anything else is derived from a module body, so it is named only when nothing declared
        // exists.
        var targets = writes
            .GroupBy(w => w.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (g.FirstOrDefault(w => w.Tier == "Declared") ?? g.First()).ObjectName);

        for (var i = 0; i < streams.Count; i++)
        {
            if (targets.TryGetValue(streams[i].PipelineId, out var target))
            {
                streams[i] = streams[i] with { TargetObject = target };
            }
        }
    }

    /// <summary>One stream's declared cadence: which schedule holds it, and how many days that schedule
    /// normally leaves between fires.</summary>
    private sealed record ScheduleCadence(string Name, string? Cron, string Timezone, double ExpectedGapDays);

    private static StreamProfileDto ToDto(StreamProfile profile) => new(
        new StreamPatternDto(
            profile.Pattern.Shape,
            profile.Pattern.LoadDays.Select(d => d.ToString()).ToList(),
            profile.Pattern.TypicalRows, profile.Pattern.LowRows, profile.Pattern.HighRows,
            profile.Pattern.Reliability, profile.Pattern.Description),
        profile.Cadence.ToString().ToLowerInvariant(), profile.ExpectedGapDays, profile.CadenceSource,
        profile.MaxObservedGapDays, profile.LastLoadUtc, profile.LastRunUtc,
        Finite(profile.DaysSinceLastLoad), Finite(profile.DaysSinceLastRun),
        profile.RunDays, profile.LoadedDays, profile.Runs, profile.Failures,
        profile.TotalRowsInserted, profile.TotalRowsUpdated, profile.TotalRowsDeleted,
        profile.AvgRowsInsertedPerRun, profile.AvgRowsUpdatedPerRun, profile.AvgRowsDeletedPerRun,
        profile.AvgRowsWrittenPerLoadedDay, profile.MedianRowsWrittenPerLoadedDay, profile.TrendRowsPerDay,
        profile.UnexpectedNullDays, profile.EmptyRunDays, profile.NoRunDays, profile.PredictedNullDays,
        profile.TrimmedLoadDays, profile.TrimFence);

    private static StreamPointDto ToDto(StreamPoint point) => new(
        point.Date, point.RowsWritten, point.RowsInserted, point.RowsUpdated, point.RowsDeleted,
        point.Runs, point.Failures, point.ExcludedBackfillRuns, point.Expected, point.Severity,
        point.Anomaly, point.Reason, point.Imputed, point.Immature,
        point.ExpectedLoadRate, point.UnexpectedNull, point.Trimmed);

    /// <summary>"Never" is infinity to the detector and null over the wire: JSON has no infinity, and a client
    /// that reads one as a number would render a stream that has never loaded as loading moments ago.</summary>
    private static double? Finite(double value) => double.IsFinite(value) ? Math.Round(value, 2) : null;

    private static string StatusName(StreamStatus status) => status switch
    {
        StreamStatus.Stalled => "stalled",
        StreamStatus.Degraded => "degraded",
        StreamStatus.Watch => "watch",
        StreamStatus.Healthy => "healthy",
        _ => "insufficient-history",
    };

    private static string DetectorName(StreamDetector detector) => detector switch
    {
        StreamDetector.Silence => "silence",
        StreamDetector.NullDays => "nullDays",
        StreamDetector.Cadence => "cadence",
        StreamDetector.VolumeOutlier => "volumeOutlier",
        StreamDetector.LevelShift => "levelShift",
        _ => "rateChange",
    };

    private static string DirectionName(StreamDirection direction) => direction switch
    {
        StreamDirection.Below => "below",
        StreamDirection.Above => "above",
        _ => "none",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2,
    };

    /// <summary>Within one severity, a stopped stream outranks a wobbling one, and anything analysed outranks
    /// a stream there was not enough history to judge.</summary>
    private static int StatusRank(string status) => status switch
    {
        "stalled" => 0,
        "degraded" => 1,
        "watch" => 2,
        "healthy" => 3,
        _ => 4,
    };

    /// <summary>Within one severity and status, the three questions in the order an operator must answer
    /// them: no data at all, then less than normal, then more than normal.</summary>
    private static int CategoryRank(string category) => category switch
    {
        "stalled" => 0,
        "failing" => 1,
        "gap-days" => 2,
        "not-running" => 3,
        "less-than-normal" => 4,
        "more-than-normal" => 5,
        _ => 6,
    };

    private static ProblemHttpResult? Validate(int? days, int? limit)
    {
        if (days is < 1 or > MaxWindowDays)
        {
            return TypedResults.Problem(
                detail: $"days must be between 1 and {MaxWindowDays}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (limit is < 1 or > MaxResults)
        {
            return TypedResults.Problem(
                detail: $"limit must be between 1 and {MaxResults}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        return null;
    }
}
