using Microsoft.Extensions.DependencyInjection;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Wraps the run dispatcher the host registered, keeping its lifetime and whatever it was registered as. A module's
/// <c>ConfigureServices</c> runs after SQLFlow's own registrations (<c>IControlPlaneModule</c>), so the dispatcher is
/// always there to wrap by the time this is called; a host that somehow has none is a composition error and says so
/// rather than silently running without the wrapper.
/// </summary>
internal static class RunDispatcherDecoration
{
    /// <summary>Replaces the registered dispatcher with what <paramref name="decorate"/> makes of it.</summary>
    public static void Decorate(IServiceCollection services, Func<IRunDispatcher, IServiceProvider, IRunDispatcher> decorate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var registered = services.LastOrDefault(d => d.ServiceType == typeof(IRunDispatcher))
            ?? throw new InvalidOperationException(
                $"No {nameof(IRunDispatcher)} is registered, so the delivery module cannot attach its configuration to the runs the control plane queues. The module is being configured outside a SQLFlow control plane host.");

        services.Remove(registered);
        services.Add(ServiceDescriptor.Describe(
            typeof(IRunDispatcher),
            provider => decorate(Inner(provider, registered), provider),
            registered.Lifetime));
    }

    /// <summary>The dispatcher the host had registered, however it registered it.</summary>
    private static IRunDispatcher Inner(IServiceProvider provider, ServiceDescriptor registered)
    {
        if (registered.ImplementationInstance is IRunDispatcher instance)
        {
            return instance;
        }

        if (registered.ImplementationFactory is { } factory)
        {
            return (IRunDispatcher)factory(provider);
        }

        return (IRunDispatcher)ActivatorUtilities.CreateInstance(
            provider,
            registered.ImplementationType
                ?? throw new InvalidOperationException($"The registered {nameof(IRunDispatcher)} names no implementation to wrap."));
    }
}
