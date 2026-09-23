using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What one render asked of the platform: the questions the run could not answer yet, and the answers the render used.
/// It belongs to one render, never to the renderer, which the plan's parallel workers share.
/// </summary>
internal sealed class SearchTrail
{
    private List<SearchQuestion>? _unanswered;
    private List<SearchUsage>? _used;

    /// <summary>The questions to ask before the row is rendered again, each once, in the order the render met them.</summary>
    public IReadOnlyList<SearchQuestion> Unanswered => _unanswered ?? (IReadOnlyList<SearchQuestion>)[];

    /// <summary>The answers the render consulted, each once, in the order it consulted them.</summary>
    public IReadOnlyList<SearchUsage> Used => _used ?? (IReadOnlyList<SearchUsage>)[];

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
}
