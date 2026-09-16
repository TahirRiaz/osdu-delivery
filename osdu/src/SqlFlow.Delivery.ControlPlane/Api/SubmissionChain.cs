using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One landing file of a submission, as the chain reads it: which dataset it carries, which pre-ingestion flow
/// reads it, and the file name that flow is bounded to.</summary>
public sealed record SubmissionLandingRef(string Dataset, string PreFlow, string FileName);

/// <summary>
/// The chain of runs one API submission is delivered by (docs/stage4-design.md section 4.2 step 4): the pre-ingestion
/// flows its files land for, every ingestion flow between them and the OSDU flow, and the OSDU flow itself, in wave
/// order. The set is computed from the catalog's own lineage (<see cref="CatalogFlowDependency"/>) and pipeline waves, so
/// a submission runs exactly the flows a scheduled load of the same files would, and a declared pre flow that does not
/// reach the OSDU flow refuses the submission instead of landing files nothing would read.
/// </summary>
public sealed record SubmissionChain(
    string Anchor,
    IReadOnlyList<RunScopeMember> Members,
    IReadOnlyDictionary<string, RunParameters> MemberParameters,
    int OsduMemberIndex,
    IReadOnlyDictionary<string, string> PreFlowByDataset)
{
    /// <summary>The run id of the OSDU flow's member in an enqueued group.</summary>
    public Guid OsduRunId(IReadOnlyList<Guid> runIds)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        return OsduMemberIndex >= 0 && OsduMemberIndex < runIds.Count
            ? runIds[OsduMemberIndex]
            : throw new DeliveryException($"The enqueued chain of flow '{Anchor}' has {runIds.Count} member run(s), so its OSDU member cannot be named.");
    }

    /// <summary>The run id of the member that reads <paramref name="dataset"/>'s landing file, or null when that flow is not a member.</summary>
    public Guid? PreRunId(string dataset, IReadOnlyList<Guid> runIds)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        if (!PreFlowByDataset.TryGetValue(dataset, out var flowName))
        {
            return null;
        }

        var index = -1;
        for (var i = 0; i < Members.Count; i++)
        {
            if (string.Equals(Members[i].FlowName, flowName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index < runIds.Count ? runIds[index] : null;
    }

    /// <summary>
    /// The chain that delivers <paramref name="submission"/>, or the refusal to answer the request with. Every member is an
    /// active pipeline of the flow's repository; the pre flows read exactly the landed files (a full load of one file
    /// pattern, so a bounded run never resets the landing table), the ingestion flows run as they are defined, and the OSDU
    /// flow runs the submission's own operation over the records it sent.
    /// </summary>
    public static async Task<(SubmissionChain? Chain, string? Refusal)> PlanAsync(
        CatalogDbContext db, Guid repoId, string osduFlowName, InlineSubmissionState submission, IReadOnlyList<SubmissionLandingRef> landings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(osduFlowName);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(landings);

        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active)
            .Select(p => new { p.Id, p.Name, p.Kind, p.Batch, p.Wave })
            .ToListAsync(ct).ConfigureAwait(false);
        var byName = pipelines.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (!byName.TryGetValue(osduFlowName, out var osduPipeline))
        {
            return (null, $"Flow '{osduFlowName}' is not an active pipeline of its repository, so no chain can be queued for submission {submission.SubmissionId:D}.");
        }

        var edges = await db.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => new { d.FromFlow, d.ToFlow })
            .ToListAsync(ct).ConfigureAwait(false);
        var descendants = Edges(edges.Select(e => (e.FromFlow, e.ToFlow)));
        var ancestors = Edges(edges.Select(e => (e.ToFlow, e.FromFlow)));

        var upstreamOfOsdu = Reachable(ancestors, osduFlowName);
        var members = new List<string>();
        var preFlowByDataset = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var landing in landings)
        {
            if (!byName.TryGetValue(landing.PreFlow, out var pre))
            {
                return (null, $"Submission {submission.SubmissionId:D} lands its '{landing.Dataset}' rows for pre-ingestion flow '{landing.PreFlow}', which is not an active pipeline of the flow's repository.");
            }

            var downstream = Reachable(descendants, pre.Name);
            if (!downstream.Contains(osduFlowName, StringComparer.OrdinalIgnoreCase))
            {
                return (null,
                    $"Pre-ingestion flow '{pre.Name}' does not reach delivery flow '{osduFlowName}' through the catalog's lineage, so landing '{landing.Dataset}' for it would never deliver anything. "
                    + "Sync the repository so the chain's lineage is current, or point source.submissions at the pre flow that loads the tables this flow reads.");
            }

            preFlowByDataset[landing.Dataset] = pre.Name;
            Add(members, pre.Name);
            foreach (var between in downstream)
            {
                if (byName.TryGetValue(between, out var middle) && upstreamOfOsdu.Contains(between, StringComparer.OrdinalIgnoreCase)
                    && !string.Equals(between, osduFlowName, StringComparison.OrdinalIgnoreCase))
                {
                    Add(members, middle.Name);
                }
            }
        }

        Add(members, osduPipeline.Name);

        // Wave order is the catalog's own topological order, which is what the dispatcher gates the group by; the OSDU
        // flow is last whatever its wave, because it reads what every other member loads.
        var ordered = members
            .Select(name => byName[name])
            .OrderBy(p => string.Equals(p.Name, osduFlowName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p.Wave < 0 ? 0 : p.Wave)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var scope = ordered
            .Select(p => new RunScopeMember(p.Name, p.Kind, p.Wave < 0 ? 0 : p.Wave, p.Batch ?? CatalogPipeline.DefaultBatch, p.Id))
            .ToList();

        var parameters = new Dictionary<string, RunParameters>(StringComparer.Ordinal);
        foreach (var (dataset, preFlow) in preFlowByDataset)
        {
            var file = landings.First(l => string.Equals(l.Dataset, dataset, StringComparison.Ordinal)).FileName;
            // The pre flow reads exactly the file this submission landed, whatever its own watermark says, and a bounded
            // run never resets the landing table under the consumers of its typed view.
            parameters[preFlow] = new RunParameters { FullLoad = true, FilePattern = file };
        }

        parameters[osduFlowName] = new RunParameters
        {
            Operation = submission.Operation,
            Values = submission.Parameters(),
            Payload = new DeliveryRunPayload { SubmissionId = submission.SubmissionId, Force = submission.Force }.ToJson(),
        };

        foreach (var member in parameters.Values)
        {
            member.Validate();
        }

        var osduIndex = scope.FindIndex(m => string.Equals(m.FlowName, osduFlowName, StringComparison.OrdinalIgnoreCase));
        return (new SubmissionChain(osduFlowName, scope, parameters, osduIndex, preFlowByDataset), null);
    }

    private static void Add(List<string> members, string flowName)
    {
        if (!members.Contains(flowName, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(flowName);
        }
    }

    private static Dictionary<string, List<string>> Edges(IEnumerable<(string From, string To)> edges)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in edges)
        {
            if (!map.TryGetValue(from, out var next))
            {
                next = [];
                map[from] = next;
            }

            if (!next.Contains(to, StringComparer.OrdinalIgnoreCase))
            {
                next.Add(to);
            }
        }

        return map;
    }

    /// <summary>Every flow reachable from <paramref name="start"/> along <paramref name="edges"/>, excluding itself.</summary>
    private static HashSet<string> Reachable(Dictionary<string, List<string>> edges, string start)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.Count > 0)
        {
            foreach (var next in edges.GetValueOrDefault(pending.Dequeue(), []))
            {
                if (seen.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return seen;
    }
}
