using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.ManagedWorkers;
using SqlFlow.Delivery.Tests;
using SqlFlow.Dispatch.ManagedWorkers;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The OSDU module's launchers for SQLFlow's managed workers: none while the feature is off, both offered while it is on
/// (so a misnamed launcher is refused naming them), and the selected one's settings checked when the host starts. None of
/// this needs a database: the host starts against an unreachable catalog, so dispatch never activates and no launcher is
/// ever called.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ManagedWorkersModuleTests
{
    [Fact]
    public async Task Off_TheModuleOffersNoLauncher()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        Assert.Empty(factory.Services.GetServices<IWorkerLauncher>());
        Assert.Null(factory.Services.GetService<ManagedFleet>());
    }

    [Fact]
    public async Task OnWithRadix_TheFleetStartsWorkersAsRadixJobs()
    {
        await using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");
        using var client = factory.CreateClient();

        Assert.Equal(
            ["process", "radix"],
            factory.Services.GetServices<IWorkerLauncher>().Select(l => l.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(RadixWorkerLauncher.LauncherName, factory.Services.GetRequiredService<ManagedFleet>().LauncherName);
    }

    [Fact]
    public void OnWithRadix_WithoutTheSchedulerUrl_TheHostRefusesToStart()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl is required", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithRadix_AJobTimeLimitTheWorkersOutlive_IsRefused()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:MaxLifetimeHours", "24")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("greater than ControlPlane:ManagedWorkers:MaxLifetimeHours (24)", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithTheProcessLauncher_WithoutAWorkerHost_TheHostRefusesToStart()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "process");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:Process:Command is required", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithAMisnamedLauncher_TheHostRefusesToStart_NamingTheOnesItHas()
    {
        using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "kubernetes");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = Flatten(ex);
        Assert.Contains("Launcher is 'kubernetes'", message, StringComparison.Ordinal);
        Assert.Contains("'radix'", message, StringComparison.Ordinal);
        Assert.Contains("'process'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnselectedLaunchersSettings_AreNotChecked()
    {
        // The process launcher has no command here, and does not need one while Radix is the launcher in use.
        await using var factory = Host()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", "radix")
            .WithSetting("ControlPlane:ManagedWorkers:Radix:JobSchedulerUrl", "http://worker:8080");
        using var client = factory.CreateClient();

        Assert.NotNull(factory.Services.GetRequiredService<ManagedFleet>());
    }

    private static ControlPlaneAppFactory Host()
        => new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }
}
