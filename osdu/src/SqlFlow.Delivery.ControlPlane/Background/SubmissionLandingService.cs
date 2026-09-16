using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Submissions;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Finishes the API submissions whose own request could not (docs/stage4-design.md section 4.2 step 6). A submission is
/// accepted in one write and then landed and queued by the request that accepted it; a request that dies in between
/// leaves records the ledger holds and nothing running. This sweep takes such a submission over once its grace period
/// has passed: it lands the files again (writing the same bytes to the same names, which is a no-op for a file already
/// there) and queues the chain, both idempotent, so a submission is delivered once however often this runs. It also
/// settles a queued submission whose chain ended without delivering it, with the member run that stopped it.
/// </summary>
/// <remarks>
/// Hosted on every replica. Two replicas sweeping at once cost a little duplicated work and never a wrong result: the
/// landing write refuses to replace a file whose content differs, and the enqueue commits the submission's group inside
/// the run group's own transaction, so the second one is declined and answers with the first one's chain.
/// </remarks>
public sealed partial class SubmissionLandingService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly SubmissionResumeOptions _options;
    private readonly ILogger<SubmissionLandingService> _logger;

    public SubmissionLandingService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<SubmissionResumeOptions> options,
        ILogger<SubmissionLandingService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this tick; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogPassError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One sweep: the submissions still waiting to be landed or queued, then the queued ones whose chain has ended.</summary>
    private async Task PassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var osdu = provider.GetRequiredService<OsduDbContext>();
        var now = _clock.GetUtcNow().UtcDateTime;
        var due = now.AddSeconds(-Math.Max(1, _options.GraceSeconds));

        var waiting = await osdu.DeliveryInlineSubmissions.AsNoTracking()
            .Where(s => s.GroupId == null && (s.Status == InlineStatuses.Accepted || s.Status == InlineStatuses.Landed) && s.ReceivedUtc <= due)
            .OrderBy(s => s.ReceivedUtc)
            .Take(_options.BatchSize)
            .Select(s => s.SubmissionId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var submissionId in waiting)
        {
            ct.ThrowIfCancellationRequested();
            await ResumeAsync(provider, submissionId, ct).ConfigureAwait(false);
        }

        var queued = await osdu.DeliveryInlineSubmissions.AsNoTracking()
            .Where(s => s.Status == InlineStatuses.Queued && s.GroupId != null)
            .OrderBy(s => s.ReceivedUtc)
            .Take(_options.BatchSize)
            .Select(s => new { s.SubmissionId, s.GroupId, s.OsduRunId })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var submission in queued)
        {
            ct.ThrowIfCancellationRequested();
            await SettleAsync(provider, submission.SubmissionId, submission.GroupId!.Value, submission.OsduRunId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Lands a submission's files and queues its chain, exactly as the request that accepted it would have.</summary>
    private async Task ResumeAsync(IServiceProvider provider, Guid submissionId, CancellationToken ct)
    {
        var ledger = provider.GetRequiredService<ILedger>();
        var submission = await ledger.GetInlineSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null || submission.GroupId is not null)
        {
            return;
        }

        var db = provider.GetRequiredService<CatalogDbContext>();
        var osdu = provider.GetRequiredService<OsduDbContext>();
        var documents = provider.GetRequiredService<DeliveryDocumentLoader>();
        var engine = provider.GetRequiredService<EngineContext>();
        var dispatcher = provider.GetRequiredService<IRunDispatcher>();

        try
        {
            var (flow, problem) = await DeliveryEndpoints.ResolveSubmissionFlowAsync(db, documents, submission, ct).ConfigureAwait(false);
            if (flow is null)
            {
                await FailAsync(ledger, submissionId, problem!, ct).ConfigureAwait(false);
                return;
            }

            var (mapping, mappingProblem) = await DeliveryEndpoints.ResolveMappingAsync(osdu, documents, flow, ct).ConfigureAwait(false);
            if (mapping is null)
            {
                await FailAsync(ledger, submissionId, mappingProblem!, ct).ConfigureAwait(false);
                return;
            }

            var values = submission.Parameters();
            var files = SubmissionLanding.Plan(submission, flow.Flow, mapping, values);
            var contents = new List<LandingContent>(files.Count);
            foreach (var file in files)
            {
                contents.Add(await LandingFileWriter.RenderAsync(file, ct).ConfigureAwait(false));
            }

            await DeliveryEndpoints.LandAsync(engine, ledger, files, contents, _clock, submissionId, ct).ConfigureAwait(false);
            var chain = await DeliveryEndpoints.EnqueueChainAsync(db, dispatcher, ledger, flow, submission, files, pool: null, user: null, ct).ConfigureAwait(false);
            if (chain.Problem is null)
            {
                LogResumed(submissionId, chain.GroupId, submission.FlowName);
            }
        }
        catch (Exception ex) when (ex is DeliveryException or FlowValidationException or SqlFlowException or IOException or UnauthorizedAccessException)
        {
            // The submission keeps its records and its accepted state: the next sweep tries again, and the failure is
            // recorded on it so an operator can see why it is not moving.
            var error = SecretHygiene.RedactedMessage(ex);
            LogResumeFailed(submissionId, error);
            await ledger.MarkInlineStatusAsync(submissionId, InlineStatuses.Accepted, null, null, error, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles a submission whose chain has ended: completed when the OSDU member succeeded, failed when a member
    /// stopped the chain, with that member's run, flow and redacted error. A chain still running is left alone, and so
    /// is one whose run rows retention has removed, which the resume path picks up as a submission to queue again.
    /// </summary>
    private async Task SettleAsync(IServiceProvider provider, Guid submissionId, Guid groupId, Guid? osduRunId, CancellationToken ct)
    {
        var db = provider.GetRequiredService<CatalogDbContext>();
        var ledger = provider.GetRequiredService<ILedger>();
        var members = await db.Runs.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .Select(r => new { r.RunId, r.FlowName, r.Status, r.Error })
            .ToListAsync(ct).ConfigureAwait(false);
        if (members.Count == 0 || members.Exists(m => !RunStatuses.IsTerminal(m.Status)))
        {
            return;
        }

        var osdu = members.Find(m => osduRunId is { } id && m.RunId == id);
        if (osdu is not null && osdu.Status == RunStatuses.Succeeded)
        {
            await ledger.MarkInlineStatusAsync(submissionId, InlineStatuses.Completed, null, null, null, ct).ConfigureAwait(false);
            LogSettled(submissionId, InlineStatuses.Completed, groupId);
            return;
        }

        var stopped = members.Find(m => m.Status is RunStatuses.Failed or RunStatuses.Cancelled) ?? members.Find(m => m.Status == RunStatuses.Skipped);
        var error = stopped is null
            ? $"The chain of submission {submissionId:D} ended without delivering its records, and no member says why."
            : $"{stopped.FlowName} ({stopped.Status}, run {stopped.RunId:D}): {stopped.Error ?? "no error recorded"}";
        await ledger.MarkInlineStatusAsync(submissionId, InlineStatuses.Failed, null, null, error, ct).ConfigureAwait(false);
        LogSettled(submissionId, InlineStatuses.Failed, groupId);
    }

    private async Task FailAsync(ILedger ledger, Guid submissionId, string reason, CancellationToken ct)
    {
        LogResumeFailed(submissionId, reason);
        await ledger.MarkInlineStatusAsync(submissionId, InlineStatuses.Failed, null, null, reason, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Submission {SubmissionId} was landed and queued as group {GroupId} of flow '{Flow}' by the resume sweep.")]
    private partial void LogResumed(Guid submissionId, Guid groupId, string flow);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Submission {SubmissionId} could not be finished: {Error}")]
    private partial void LogResumeFailed(Guid submissionId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Submission {SubmissionId} is {Status}: its chain (group {GroupId}) has ended.")]
    private partial void LogSettled(Guid submissionId, string status, Guid groupId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Submission sweep error: {Error}")]
    private partial void LogPassError(string error);
}
