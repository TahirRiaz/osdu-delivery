namespace SqlFlow.Core;

/// <summary>
/// A flow's declared schedule, as written in the <c>schedule:</c> block of its YAML and carried through the
/// engine. It says WHEN a flow should run (a cron expression in a time zone, or a fixed interval), not how; the
/// control plane turns it into actual fires by enqueuing runs. Exactly one of <see cref="Cron"/> /
/// <see cref="IntervalSeconds"/> is set. Purely declarative data: parsing it has no scheduling-library dependency,
/// so it travels freely from the YAML loaders through lineage collection into the catalog.
/// </summary>
public sealed record ScheduleSpec
{
    /// <summary>A standard cron expression (5 fields, or 6 with a leading seconds field), evaluated in
    /// <see cref="Timezone"/>. Null when the schedule is interval-based.</summary>
    public string? Cron { get; init; }

    /// <summary>A fixed interval, in seconds, between runs. Null when the schedule is cron-based.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>The IANA time zone the cron is evaluated in (for example <c>Europe/Oslo</c>); <c>UTC</c> by default.</summary>
    public string Timezone { get; init; } = "UTC";

    /// <summary>Whether the schedule is active. A disabled schedule is recorded but never fires.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether missed occurrences are backfilled. When false (the default) a fire that was missed because the
    /// host was down is skipped and the schedule resumes at the next occurrence after now. When true the schedule
    /// catches up, firing one missed occurrence per scheduler tick until it is current again.</summary>
    public bool Catchup { get; init; }
}
