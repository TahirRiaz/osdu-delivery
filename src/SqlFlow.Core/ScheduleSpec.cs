namespace SqlFlow.Core;

/// <summary>
/// A flow's declared schedule, as written in the <c>schedule:</c> block of its YAML and carried through the
/// engine. It says WHEN a flow should run (a cron expression in a time zone, or a fixed interval), not how; the
/// control plane turns it into actual fires by enqueuing runs. A resolved schedule has exactly one of
/// <see cref="Cron"/> / <see cref="IntervalSeconds"/> set; a schedule that only references a shared definition by
/// name carries just <see cref="Ref"/> until the repo-wide scan resolves it (see <see cref="Ref"/>). Purely
/// declarative data: parsing it has no scheduling-library dependency, so it travels freely from the YAML loaders
/// through lineage collection into the catalog.
/// </summary>
public sealed record ScheduleSpec
{
    /// <summary>
    /// The name of a shared schedule this flow reuses, when the flow wrote <c>schedule: &lt;name&gt;</c> as a bare
    /// scalar instead of an inline block. The named definition lives either in a <c>schedules.yaml</c> library file
    /// or as a <see cref="Name"/>d inline block on another flow in the same repo; the estate scan resolves the
    /// reference to a concrete cadence (clearing this) before the schedule reaches the catalog. Null for an inline
    /// or already-resolved schedule.
    /// </summary>
    public string? Ref { get; init; }

    /// <summary>The name this inline schedule publishes for reuse, when the flow's <c>schedule:</c> block carries a
    /// <c>name:</c> key. Other flows in the repo can then reference it with <c>schedule: &lt;name&gt;</c>. Metadata
    /// only (the cadence still applies to this flow); null for an unnamed inline schedule or a reference.</summary>
    public string? Name { get; init; }

    /// <summary>A standard cron expression (5 fields, or 6 with a leading seconds field), evaluated in
    /// <see cref="Timezone"/>. Null when the schedule is interval-based or an unresolved <see cref="Ref"/>.</summary>
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
