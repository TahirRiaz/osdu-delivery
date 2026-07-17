namespace SqlFlow.Core;

/// <summary>
/// A flow's schedule declaration, as written in the <c>schedule:</c> key of its YAML and carried through the
/// engine. It is written one of two ways, and the two mean different things:
/// <list type="bullet">
/// <item><description>A reference (<c>schedule: nightly</c>, or <c>schedule: [nightly, hourly]</c>) declares
/// MEMBERSHIP: this flow joins those named schedules. It carries only <see cref="Refs"/>; the cadence lives with
/// the named definition and is never copied onto the flow. A named schedule fires ONCE and runs every flow that
/// joined it as one wave-ordered group.</description></item>
/// <item><description>An inline block (<c>schedule: { cron: ... }</c>) declares a cadence on this flow, which
/// joins it implicitly. A <see cref="Name"/> publishes that cadence so other flows can join it too, making the
/// declaring flow and every referencing flow members of the one schedule.</description></item>
/// </list>
/// Purely declarative data: parsing it has no scheduling-library dependency, so it travels freely from the YAML
/// loaders through lineage collection into the catalog. What a fire RUNS is the schedule's member set; there is no
/// per-flow scope, because membership is the only selector.
/// </summary>
public sealed record ScheduleSpec
{
    /// <summary>
    /// The names of the shared schedules this flow joins, when the flow wrote its <c>schedule:</c> as a bare scalar
    /// or a sequence of names instead of an inline block. The named definitions live in a <c>schedules.yaml</c>
    /// library file or as a <see cref="Name"/>d inline block on another flow in the same repo; the repo-wide estate
    /// scan binds each reference to its definition. Empty for an inline schedule.
    /// </summary>
    public IReadOnlyList<string> Refs { get; init; } = [];

    /// <summary>The name this inline schedule publishes, when the flow's <c>schedule:</c> block carries a
    /// <c>name:</c> key. Other flows in the repo then join it with <c>schedule: &lt;name&gt;</c>, and the fire runs
    /// the declaring flow together with every flow that joined. Null for an unnamed inline block or a
    /// reference.</summary>
    public string? Name { get; init; }

    /// <summary>A standard cron expression (5 fields, or 6 with a leading seconds field), evaluated in
    /// <see cref="Timezone"/>. Null when the schedule is interval-based or is a <see cref="Refs"/> membership
    /// declaration.</summary>
    public string? Cron { get; init; }

    /// <summary>A fixed interval, in seconds, between runs. Null when the schedule is cron-based.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>The IANA time zone the cron is evaluated in (for example <c>Europe/Oslo</c>); <c>UTC</c> by default.</summary>
    public string Timezone { get; init; } = "UTC";

    /// <summary>Whether the schedule is active. A disabled schedule is recorded but never fires, which pauses every
    /// member at once.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether missed occurrences are backfilled. When false (the default) a fire that was missed because the
    /// host was down is skipped and the schedule resumes at the next occurrence after now. When true the schedule
    /// catches up, firing one missed occurrence per scheduler tick until it is current again.</summary>
    public bool Catchup { get; init; }

    /// <summary>Whether this declaration is a membership reference rather than a cadence of its own.</summary>
    public bool IsReference => Refs.Count > 0;
}
