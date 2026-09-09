using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Whether first-run provisioning has finished, published so the readiness probe can reflect it.
///
/// Readiness gates traffic (the k8s readinessProbe and the Container Apps health probe both call
/// <c>/health/ready</c>), and connectivity alone is not enough to answer it: a control plane whose catalog could
/// not be provisioned reaches its database perfectly well and then fails every request that touches a table it
/// does not have. Without this, such a replica reports Ready, joins the load balancer, and serves 500s while the
/// rollout counts it as healthy.
/// </summary>
public sealed class BootstrapState
{
    private volatile string? _failure;
    private volatile bool _completed;

    /// <summary>True once provisioning has finished successfully. Readiness gates on this.</summary>
    public bool Completed => _completed;

    /// <summary>Why provisioning has not completed, for the probe's response body; null while it is still trying.</summary>
    public string? Failure => _failure;

    public void MarkCompleted()
    {
        _failure = null;
        _completed = true;
    }

    /// <summary>A deterministic refusal (a missing database, a populated non-catalog one, a stale schema): terminal.</summary>
    public void MarkRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _failure = reason;
        _completed = false;
    }

    /// <summary>A transient failure the service will retry; readiness stays red until an attempt succeeds.</summary>
    public void MarkRetrying(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _failure = reason;
        _completed = false;
    }
}

/// <summary>
/// The readiness half of <see cref="BootstrapState"/>: unhealthy until first-run provisioning has completed, so a
/// replica that cannot provision its catalog is never routed to.
/// </summary>
public sealed class BootstrapHealthCheck : IHealthCheck
{
    private readonly BootstrapState _state;

    public BootstrapHealthCheck(BootstrapState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(_state.Completed
            ? HealthCheckResult.Healthy("Catalog provisioning completed.")
            : HealthCheckResult.Unhealthy(_state.Failure ?? "Catalog provisioning has not completed yet."));
}
