using System.Diagnostics.CodeAnalysis;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a run knows about records it must find on the platform, and how it learns the rest.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of resolving a relationship, beside the partition's cache. A cache holds a closed set that a
/// capture can hold whole: the units, the type codes, the vocabularies a partition defines once and changes rarely. The
/// records a delivery refers to are not that. A partition's wellbores are business data: they grow without bound, they
/// change constantly, and capturing them to answer one lookup costs more every day it runs. Those are searched for, one
/// value at a time, with a query that can match only the record that holds exactly that value.
/// </para>
/// <para>
/// A render asks its questions while it runs, not before, so a mapping may search on a value the render itself
/// produced: a column after its modifiers, anything the row makes available. Resolving before the render would allow
/// only what is known before it starts.
/// </para>
/// <para>
/// The render itself stays synchronous and free of I/O. It asks <see cref="TryAnswer"/> for what the run already knows;
/// a question nobody has asked yet leaves the render incomplete, naming what it needs. The caller answers those with
/// <see cref="AnswerAsync"/>, a batch of records' questions in one round, and renders again. Answers are kept for the
/// run, so only a row that asks something new pays for another pass, and the thousandth record referring to one
/// wellbore asks nothing.
/// </para>
/// <para>
/// An implementation is shared by the plan's parallel renderers: both members are safe to call concurrently, and a
/// question asked by two of them at once is sent once.
/// </para>
/// </remarks>
public interface IRecordSearch
{
    /// <summary>
    /// The answer this run already has for <paramref name="question"/>, or false when it has never been asked. Finding
    /// nothing is an answer too, and is not asked again.
    /// </summary>
    bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer);

    /// <summary>
    /// Answers every question in <paramref name="questions"/> this run has not answered yet, so the renders that asked
    /// them can run again and finish. Asking for none does nothing.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// The platform could not be asked: it was unreachable, refused the run's credentials, or failed after its retries.
    /// An answer that did not come is never taken for finding nothing, which would hold records for the wrong reason.
    /// </exception>
    Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default);
}

/// <summary>What asking the platform one question came to.</summary>
public enum SearchOutcome
{
    /// <summary>Exactly one record matches.</summary>
    Found,

    /// <summary>No record matches.</summary>
    NotFound,

    /// <summary>More than one record matches, so the value names none of them.</summary>
    Ambiguous,

    /// <summary>The platform refused the query itself, and said why.</summary>
    Refused,
}

/// <summary>The answer to one <see cref="SearchQuestion"/>.</summary>
public sealed record SearchAnswer
{
    /// <summary>How many of the matching records an ambiguous answer names; the rest are counted.</summary>
    public const int NamedCandidates = 5;

    private SearchAnswer(SearchOutcome outcome, string? id, long total, IReadOnlyList<string> candidates, string? reason)
    {
        Outcome = outcome;
        Id = id;
        Total = total;
        Candidates = candidates;
        Reason = reason;
    }

    public SearchOutcome Outcome { get; }

    /// <summary>The record found, as OSDU names it without a version.</summary>
    public string? Id { get; }

    /// <summary>How many records the platform says match.</summary>
    public long Total { get; }

    /// <summary>For an ambiguous answer, the ids of the first matches the platform returned, at most <see cref="NamedCandidates"/>.</summary>
    public IReadOnlyList<string> Candidates { get; }

    /// <summary>For a refused query, what the platform said.</summary>
    public string? Reason { get; }

    /// <summary>No record matches.</summary>
    public static SearchAnswer None { get; } = new(SearchOutcome.NotFound, null, 0, [], null);

    /// <summary>Exactly one record matches: <paramref name="id"/>.</summary>
    public static SearchAnswer Found(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new SearchAnswer(SearchOutcome.Found, id, 1, [], null);
    }

    /// <summary><paramref name="total"/> records match, of which <paramref name="candidates"/> are the first returned.</summary>
    public static SearchAnswer Ambiguous(long total, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, 2);
        return new SearchAnswer(SearchOutcome.Ambiguous, null, total, candidates.Take(NamedCandidates).ToList(), null);
    }

    /// <summary>The platform refused the query, saying <paramref name="reason"/>.</summary>
    public static SearchAnswer Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SearchAnswer(SearchOutcome.Refused, null, 0, [], reason);
    }

    /// <summary>What the answer says, as a hold reason quotes it.</summary>
    public string Describe() => Outcome switch
    {
        SearchOutcome.Found => $"found {Id}",
        SearchOutcome.NotFound => "found no record",
        SearchOutcome.Ambiguous => $"found {Total} records ({string.Join(", ", Candidates)}{(Total > Candidates.Count ? $" and {Total - Candidates.Count} more" : string.Empty)})",
        _ => $"was refused by the search service: {Reason}",
    };
}

/// <summary>
/// One question a render consulted and the answer it got, kept so a record says how every reference it carries was
/// resolved, the way a cache usage does.
/// </summary>
/// <param name="Kind">The kind searched.</param>
/// <param name="Field">The property compared.</param>
/// <param name="Value">What it was compared with.</param>
/// <param name="Query">The query sent.</param>
/// <param name="Outcome">What the platform answered.</param>
/// <param name="Id">The record found, when exactly one was.</param>
public sealed record SearchUsage(string Kind, string Field, string Value, string Query, SearchOutcome Outcome, string? Id);

/// <summary>
/// A search for a render that must not reach the platform: every question is answered as found-nothing, and nothing is
/// ever asked. For a render of a mapping's shape rather than of a record, where no platform is in play and no reference
/// is expected to resolve. A delivery is never given this.
/// </summary>
public sealed class NoRecordSearch : IRecordSearch
{
    public static NoRecordSearch Instance { get; } = new();

    public bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer)
    {
        answer = SearchAnswer.None;
        return true;
    }

    public Task AnswerAsync(IReadOnlyCollection<SearchQuestion> questions, CancellationToken ct = default)
        => Task.CompletedTask;
}
