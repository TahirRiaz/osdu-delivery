namespace SqlFlow.Core;

/// <summary>The product defaults a schedule declaration falls back to when its YAML leaves a key out.</summary>
public static class ScheduleDefaults
{
    /// <summary>
    /// How many members of one fire execute at once when the schedule does not say. Bounded rather than unbounded
    /// because the members of a wave almost always share one upstream, and a wide source (23 flows off a single
    /// modest SQL Server, say) exhausts that server's connections long before the estate runs out of workers. Four
    /// keeps a fire meaningfully parallel while leaving a small source room to breathe; a source that can take more
    /// raises it explicitly, and <c>maxConcurrency: 0</c> opts out entirely.
    /// </summary>
    public const int MaxConcurrency = 4;

    /// <summary>
    /// Resolves a declared <c>maxConcurrency</c> to the effective bound, where null means UNBOUNDED. The one
    /// implementation both YAML loaders and the API create path use, so a schedule means the same thing however it
    /// was declared:
    /// <list type="bullet">
    /// <item>omitted (<paramref name="declared"/> null) takes <see cref="MaxConcurrency"/>;</item>
    /// <item><c>0</c> is the explicit opt-out and returns null (unbounded);</item>
    /// <item>a positive value is taken verbatim;</item>
    /// <item>a negative value is meaningless, so <paramref name="invalid"/> is signalled and the default applies;
    /// it is never stored, because a bound below zero would leave every member of the fire unclaimable.</item>
    /// </list>
    /// </summary>
    public static int? Resolve(int? declared, out bool invalid)
    {
        invalid = false;
        switch (declared)
        {
            case null:
                return MaxConcurrency;
            case 0:
                return null;
            case > 0:
                return declared;
            default:
                invalid = true;
                return MaxConcurrency;
        }
    }
}

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

    /// <summary>
    /// How many of this schedule's members may EXECUTE at the same time, already resolved through
    /// <see cref="ScheduleDefaults.Resolve"/>: a positive bound, or null for UNBOUNDED (which the YAML asks for with
    /// <c>maxConcurrency: 0</c>). A schedule that says nothing gets <see cref="ScheduleDefaults.MaxConcurrency"/>;
    /// a value of 1 makes the fire strictly serial, one member after another.
    /// <para>
    /// The bound is per FIRE, and because a group's waves are gated (no member is claimable until every lower wave
    /// is terminal), only one wave is ever eligible at a time; this is therefore the width of the running wave.
    /// It exists because the members of a wave usually share one upstream: 23 flows reading a single modest source
    /// server can exhaust its connections even while the estate has capacity to spare. Bounding the estate's whole
    /// worker concurrency to protect one source would throttle every other source too, so the knob belongs to the
    /// schedule that fans out.
    /// </para>
    /// </summary>
    public int? MaxConcurrency { get; init; } = ScheduleDefaults.MaxConcurrency;

    /// <summary>Whether this declaration is a membership reference rather than a cadence of its own.</summary>
    public bool IsReference => Refs.Count > 0;
}
