using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The estate's environments as the title bar's environment menu lists them: <see cref="Current"/> is the one this control
/// plane is (null when none is configured), and <see cref="Environments"/> every one of them in the configured order,
/// empty when only the current one is named.
/// </summary>
public sealed record EnvironmentsDto(string? Current, IReadOnlyList<EnvironmentDto> Environments);

/// <summary>One environment: its name, what it is for, the address of its GUI, and whether this control plane is it.</summary>
public sealed record EnvironmentDto(string Name, string? Description, string Url, bool Current);

/// <summary>
/// <c>GET /api/v1/environments</c>: the environments the estate runs as separate deployments
/// (<c>ControlPlane:Environments</c>), so the GUI's title bar names the one a person is in and opens another. It answers a
/// signed-in caller, since the list names the estate's other addresses.
/// </summary>
public static class EnvironmentEndpoints
{
    public static RouteGroupBuilder MapEnvironmentEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/environments", Get).WithTags("Environments").WithName("GetEnvironments");
        return group;
    }

    private static Ok<EnvironmentsDto> Get(IOptions<ControlPlaneOptions> options)
    {
        var environments = options.Value.Environments;
        var current = environments.CurrentName();
        return TypedResults.Ok(new EnvironmentsDto(
            current,
            [.. environments.Links.OfType<EnvironmentLinkOptions>().Select(link =>
            {
                var name = link.Name!.Trim();
                var description = link.Description?.Trim();
                return new EnvironmentDto(
                    name,
                    string.IsNullOrEmpty(description) ? null : description,
                    link.Url!.Trim(),
                    string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
            })]));
    }
}
