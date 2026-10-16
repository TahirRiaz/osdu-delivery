using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Puts a run's calls to OSDU on its trace: each call at debug with the status and how long it took, which the run's gate
/// keeps for the records the trace describes and for the first calls made outside any record; each retry, until the
/// run's allowance of them runs out, with why it is repeated and how long the stack waits first. That is what says where a
/// slow record spends its time: throttled by the service, timing out, or streaming a large payload.
/// </summary>
internal sealed class RunHttpTrace : IHttpObserver
{
    private readonly RunTrace _trace;
    private readonly ILogger _logger;

    public RunHttpTrace(RunTrace trace, ILogger<RunHttpTrace> logger)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(logger);
        _trace = trace;
        _logger = logger;
    }

    public void Ended(HttpAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var again = attempt.Attempt > 1 ? string.Create(CultureInfo.InvariantCulture, $" (attempt {attempt.Attempt})") : string.Empty;
        var took = RunTrace.Elapsed(attempt.Elapsed);
        if (attempt.Status is { } status)
        {
            _logger.LogDebug("{Method} {Url} answered HTTP {Status} in {Elapsed}{Again}.", attempt.Method, attempt.Url, status, took, again);
        }
        else
        {
            _logger.LogDebug("{Method} {Url} ended without an answer after {Elapsed}{Again}: {Failure}.", attempt.Method, attempt.Url, took, again, attempt.Failure ?? "no reason given");
        }
    }

    public void Retrying(HttpRetry retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (!_trace.DescribesRetry(out var firstLeftOut))
        {
            if (firstLeftOut)
            {
                _logger.LogInformation(
                    RunTrace.Bounded,
                    "{Count} retries of a call are on the trace; later ones are not, and a record whose call gave up says why in its history.",
                    _trace.RetryAllowance);
            }

            return;
        }

        _logger.LogInformation(
            RunTrace.Bounded,
            "{Method} {Url}: {Cause} on attempt {Attempt} of {Max}; trying again in {Wait}.",
            retry.Method, retry.Url, retry.Cause, retry.Attempt, retry.MaxAttempts, RunTrace.Elapsed(retry.Wait));
    }
}
