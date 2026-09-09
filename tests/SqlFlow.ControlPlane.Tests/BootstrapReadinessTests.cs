using Microsoft.Extensions.Diagnostics.HealthChecks;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Readiness gates traffic (the k8s readinessProbe and the Container Apps health probe both call
/// <c>/health/ready</c>), so it has to assert more than "the database answers". A control plane whose catalog could
/// not be provisioned connects perfectly well and then fails every request that touches a table it does not have;
/// reporting Ready in that state puts a broken replica behind the load balancer and lets a rollout succeed.
/// </summary>
public sealed class BootstrapReadinessTests
{
    private static async Task<HealthStatus> CheckAsync(BootstrapState state)
    {
        var result = await new BootstrapHealthCheck(state).CheckHealthAsync(
            new HealthCheckContext { Registration = new HealthCheckRegistration("bootstrap", new BootstrapHealthCheck(state), null, null) });
        return result.Status;
    }

    [Fact]
    public async Task Readiness_is_red_until_provisioning_completes()
    {
        var state = new BootstrapState();

        // Startup, before the first attempt has finished: not ready.
        Assert.False(state.Completed);
        Assert.Equal(HealthStatus.Unhealthy, await CheckAsync(state));

        state.MarkCompleted();
        Assert.True(state.Completed);
        Assert.Equal(HealthStatus.Healthy, await CheckAsync(state));
    }

    [Fact]
    public async Task A_refusal_keeps_readiness_red_and_carries_the_reason()
    {
        var state = new BootstrapState();
        state.MarkRefused("The catalog database is missing 1 table(s) this build declares: delivery.UpdateTag.");

        Assert.False(state.Completed);
        Assert.Contains("delivery.UpdateTag", state.Failure, StringComparison.Ordinal);

        var result = await new BootstrapHealthCheck(state).CheckHealthAsync(
            new HealthCheckContext { Registration = new HealthCheckRegistration("bootstrap", new BootstrapHealthCheck(state), null, null) });
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("delivery.UpdateTag", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_failure_keeps_readiness_red_until_a_later_attempt_succeeds()
    {
        var state = new BootstrapState();
        state.MarkRetrying("A network-related or instance-specific error occurred.");
        Assert.Equal(HealthStatus.Unhealthy, await CheckAsync(state));

        // The service retries with backoff; readiness turns green only when an attempt actually completes.
        state.MarkCompleted();
        Assert.Equal(HealthStatus.Healthy, await CheckAsync(state));
        Assert.Null(state.Failure);
    }
}
