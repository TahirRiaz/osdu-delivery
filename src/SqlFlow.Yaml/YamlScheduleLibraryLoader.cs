using SqlFlow.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>The named schedules one library file declares, plus any warnings raised parsing it.</summary>
public sealed record ScheduleLibrary(IReadOnlyList<NamedSchedule> Schedules, IReadOnlyList<string> Warnings);

/// <summary>One shared schedule a library file (or a named inline block) publishes for reuse by name.</summary>
public sealed record NamedSchedule(string Name, ScheduleSpec Spec);

/// <summary>
/// Loads a shared-schedule library file: a <c>schedules.yaml</c> (or <c>*.schedules.yaml</c>) whose top-level
/// <c>schedules:</c> key maps a name to a cadence, so many flows can reference one definition by name instead of
/// repeating the block. The cadence fields are exactly a flow's inline <c>schedule:</c> block (cron or
/// intervalSeconds, timezone, enabled, catchup, maxConcurrency); the map key is the reference name. Cron/interval syntax is not
/// validated here (the catalog owns the cron library, as for inline schedules); an entry that declares neither a
/// cron, an interval, nor an <c>after</c> is dropped with a warning so an empty placeholder never becomes a broken
/// schedule.
/// <para>
/// An entry may instead declare <c>after: &lt;other-schedule&gt;</c>, which makes it a CHAINED (shadow) schedule: it
/// carries no cadence and never becomes due on the clock, firing once each time the named parent's fire completes.
/// That is how a strict serial chain is expressed (<c>a</c> on a cron, <c>b: {after: a}</c>, <c>c: {after: b}</c>),
/// which a stagger of independent crons can only approximate. Declaring both a cron and an <c>after</c> is
/// contradictory, so the cron is dropped with a warning; chaining a schedule to itself is dropped entirely. Longer
/// cycles cannot be seen from one file and are broken at fire time by the scheduler.
/// </para>
/// </summary>
public sealed class YamlScheduleLibraryLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class LibraryYaml
    {
        public Dictionary<string, ScheduleEntryYaml?>? Schedules { get; set; }
    }

    private sealed class ScheduleEntryYaml
    {
        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? After { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>Parses a library file's YAML. <paramref name="source"/> only labels warnings.</summary>
    public ScheduleLibrary Parse(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var warnings = new List<string>();

        LibraryYaml? parsed;
        try
        {
            parsed = _deserializer.Deserialize<LibraryYaml>(yaml);
        }
        catch (YamlException ex)
        {
            warnings.Add($"{source}: invalid schedule library - {ex.Message}");
            return new ScheduleLibrary([], warnings);
        }

        if (parsed?.Schedules is not { Count: > 0 } entries)
        {
            warnings.Add($"{source}: no 'schedules:' entries; nothing to publish.");
            return new ScheduleLibrary([], warnings);
        }

        var schedules = new List<NamedSchedule>(entries.Count);
        foreach (var (rawName, entry) in entries)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add($"{source}: a schedule with a blank name is ignored.");
                continue;
            }

            var after = string.IsNullOrWhiteSpace(entry?.After) ? null : entry.After.Trim();
            var hasClock = entry is not null && (!string.IsNullOrWhiteSpace(entry.Cron) || entry.IntervalSeconds is not null);

            if (entry is null || (!hasClock && after is null))
            {
                warnings.Add(
                    $"{source}: schedule '{name}' declares neither a cron, an intervalSeconds, nor an after; ignored.");
                continue;
            }

            // A schedule is driven by the clock or by a parent, never both: honouring a cron on a chained schedule
            // would fire it twice per cycle, once on the clock and once behind its parent.
            if (hasClock && after is not null)
            {
                warnings.Add(
                    $"{source}: schedule '{name}' sets 'after: {after}' together with a cron/intervalSeconds; "
                    + "a chained schedule has no cadence of its own, so the cron is ignored.");
            }

            if (after is not null && string.Equals(after, name, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{source}: schedule '{name}' chains after itself; ignored.");
                continue;
            }

            schedules.Add(new NamedSchedule(name, new ScheduleSpec
            {
                Name = name,
                Cron = after is not null || string.IsNullOrWhiteSpace(entry.Cron) ? null : entry.Cron.Trim(),
                IntervalSeconds = after is not null ? null : entry.IntervalSeconds,
                After = after,
                Timezone = string.IsNullOrWhiteSpace(entry.Timezone) ? "UTC" : entry.Timezone.Trim(),
                Enabled = entry.Enabled ?? true,
                Catchup = entry.Catchup ?? false,
                MaxConcurrency = NormalizeMaxConcurrency(entry.MaxConcurrency, name, source, warnings),
            }));
        }

        return new ScheduleLibrary(schedules, warnings);
    }

    /// <summary>Resolves the declared member-concurrency bound through the shared
    /// <see cref="ScheduleDefaults.Resolve"/> (omitted takes the product default, <c>0</c> is the explicit unbounded
    /// opt-out, negative is meaningless), warning on a value that had to be corrected.</summary>
    internal static int? NormalizeMaxConcurrency(int? value, string name, string source, List<string> warnings)
    {
        var resolved = ScheduleDefaults.Resolve(value, out var invalid);
        if (invalid)
        {
            warnings.Add(
                $"{source}: schedule '{name}' sets maxConcurrency to {value}, which is not a usable bound; "
                + $"using the default of {ScheduleDefaults.MaxConcurrency} (use 0 for unbounded).");
        }

        return resolved;
    }
}
