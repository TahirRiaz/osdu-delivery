using Microsoft.AspNetCore.Http;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The OSDU partition the workbench works in, which every OSDU page is read in (docs/partitions-design.md section 7): the
/// module's GUI sends the title bar's partition with every request, under <see cref="Header"/>, so a read across flows (the
/// Records page, the audit trail, the search) reads that partition alone. A request that names a partition itself reads
/// that one instead; one that names none, from a client that sends no header, reads every partition.
/// </summary>
public static class WorkbenchPartition
{
    /// <summary>The request header the module's GUI carries the workbench's partition in.</summary>
    public const string Header = "X-Osdu-Partition";

    /// <summary>The partition the workbench works in, as the request's header names it, or null when it names none that is a partition id.</summary>
    public static string? Of(HttpRequest? request)
    {
        if (request is null || !request.Headers.TryGetValue(Header, out var values))
        {
            return null;
        }

        var named = values.ToString().Trim();
        return named.Length > 0 && CacheScope.IsPartitionId(named) ? named : null;
    }

    /// <summary>The partition a read covers: <paramref name="asked"/> when the request names one, else the workbench's.</summary>
    public static string? Named(string? asked, HttpRequest? request)
        => string.IsNullOrWhiteSpace(asked) ? Of(request) : asked.Trim();
}
