using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Hosts the shared <see cref="RunWorker"/> drain loop as a control-plane background service. The loop itself (claim
/// a queued run, execute it through the shared engine, record the outcome) is the node runtime that a standalone
/// <c>sqlflow worker</c> also runs; here it idles on the in-process <see cref="RunQueueSignal"/> so a triggered run
/// starts within milliseconds, with the runtime's poll interval as the fallback for schedule- and other-node-enqueued
/// work. It serves the pools configured under <c>ControlPlane:Worker:Pools</c> (empty = untargeted runs only, the
/// single-node default).
/// </summary>
public sealed class RunExecutionWorker : BackgroundService
{
    // The poll fallback bound: a nudge starts a triggered run at once; this only governs schedule-/other-node-enqueued
    // runs and recovery, where a couple of seconds of latency is immaterial.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly RunWorker _worker;
    private readonly RunQueueSignal _signal;
    private readonly IReadOnlyList<string> _pools;

    public RunExecutionWorker(RunWorker worker, RunQueueSignal signal, IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(options);
        _worker = worker;
        _signal = signal;
        _pools = options.Value.Worker.Pools;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _worker.RunAsync(PollInterval, _pools, (timeout, ct) => _signal.WaitAsync(timeout, ct), stoppingToken);
}
