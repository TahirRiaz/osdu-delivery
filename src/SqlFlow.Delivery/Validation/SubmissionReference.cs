namespace SqlFlow.Delivery.Validation;

/// <summary>
/// The caller's own name for a submission: what the sending system calls this piece of work in its own records (a
/// filename, a ticket, a job id). The delivery side never interprets it, and nothing about a delivery depends on it;
/// it exists so an operator holding the source's name for the work can find the submission that carried it, and so a
/// source can reconcile what it sent against what the ledger holds without keeping this system's ids.
/// <para>
/// One normalisation and one refusal serve every door it can arrive through (the submission API, a prepared drop's
/// manifest, and the ledger row each becomes), so a reference the API accepts can always be stored and always reads
/// back as it was sent.
/// </para>
/// </summary>
public static class SubmissionReference
{
    /// <summary>The longest reference taken, matching the catalog column so an accepted reference always stores.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The stored form of <paramref name="reference"/>: trimmed, with a blank one being no reference at all. A caller
    /// that sends spaces and a caller that sends nothing mean the same thing, and storing them differently would make
    /// two submissions look unlike for the idempotency check.
    /// </summary>
    public static string? Normalize(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        return reference.Trim();
    }

    /// <summary>
    /// Why <paramref name="reference"/> cannot be taken, or null when it can. Checked against the normalised form, so
    /// surrounding space never counts towards the ceiling. <paramref name="field"/> names the key in the message, which
    /// differs by door (<c>reference</c> in a request, <c>manifest: reference</c> in a drop).
    /// </summary>
    public static string? Refusal(string? reference, string field = "reference")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var normalized = Normalize(reference);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length > MaxLength)
        {
            return $"{field} is at most {MaxLength} characters; this one is {normalized.Length}. It is what the sending system calls this work, so a name is what belongs here, not the content.";
        }

        // Control characters would break every surface that shows a reference back (a listing, a log line, a CSV export)
        // and carry no meaning in a name, so they are refused at the door rather than escaped at each reader.
        return normalized.Any(char.IsControl)
            ? $"{field} carries a control character. It is a plain one-line name: a filename, a ticket or a job id."
            : null;
    }
}
