using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What one render met on its way: the questions it asked of the platform that the run could not answer yet, the answers
/// it used, and which alternative each <c>$coalesce</c> node took its value from. It belongs to one render, never to the
/// renderer, which the plan's parallel workers share.
/// </summary>
internal sealed class RenderTrail
{
    private List<SearchQuestion>? _unanswered;
    private List<SearchUsage>? _used;
    private Dictionary<(string Target, int Alternative), CoalesceChoice>? _chosen;

    /// <summary>The questions to ask before the row is rendered again, each once, in the order the render met them.</summary>
    public IReadOnlyList<SearchQuestion> Unanswered => _unanswered ?? (IReadOnlyList<SearchQuestion>)[];

    /// <summary>The answers the render consulted, each once, in the order it consulted them.</summary>
    public IReadOnlyList<SearchUsage> Used => _used ?? (IReadOnlyList<SearchUsage>)[];

    /// <summary>
    /// The alternative each <c>$coalesce</c> node took its value from, once per node and alternative, in the order the
    /// render met them, with how many values it gave: an item of a repeated array counts once each.
    /// </summary>
    public IReadOnlyList<CoalesceChoice> Chosen => _chosen is null ? [] : [.. _chosen.Values];

    public void Ask(SearchQuestion question)
    {
        _unanswered ??= [];
        if (!_unanswered.Contains(question))
        {
            _unanswered.Add(question);
        }
    }

    public void Use(SearchQuestion question, SearchAnswer answer)
    {
        var usage = new SearchUsage(question.Kind, question.Field, question.Value, question.Query, answer.Outcome, answer.Id);
        _used ??= [];
        if (!_used.Contains(usage))
        {
            _used.Add(usage);
        }
    }

    /// <summary>Notes that the node filling <paramref name="target"/> took its value from its alternative number <paramref name="alternative"/>.</summary>
    public void Choose(string target, int alternative, int of, string origin, bool unverified)
    {
        _chosen ??= [];
        var key = (target, alternative);
        _chosen[key] = _chosen.TryGetValue(key, out var seen)
            ? seen with { Values = seen.Values + 1, Unverified = seen.Unverified || unverified }
            : new CoalesceChoice(target, alternative, of, origin, 1, unverified);
    }
}
