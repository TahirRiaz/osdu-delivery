using SqlFlow.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// The single entry point for loading any flow document: it sniffs the root <c>flowType</c> key with a cheap
/// probe pass (which also reads the envelope: schedule, mode, lifecycle), then delegates the body to the
/// registered <see cref="IFlowDocumentKind"/> whose <c>flowType</c> matches. A missing or unknown kind is a
/// clear error naming the kinds this host knows, rather than a confusing downstream validation failure.
/// </summary>
public sealed class YamlDocumentLoader
{
    private sealed class DocumentKindYaml
    {
        public string? FlowType { get; set; }

        public ScheduleYaml? Schedule { get; set; }

        public string? Mode { get; set; }

        public string? Lifecycle { get; set; }
    }

    private sealed class ScheduleYaml
    {
        /// <summary>Set when the key was a bare scalar (<c>schedule: nightly</c>) or a sequence
        /// (<c>schedule: [nightly, hourly]</c>): the named schedules this flow joins.</summary>
        public List<string> Refs { get; set; } = [];

        public string? Name { get; set; }

        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>The mapping shape the converter delegates an inline <c>schedule:</c> block to: the same inline fields
    /// as <see cref="ScheduleYaml"/> minus the reference-only <c>Refs</c>. It is deliberately NOT accepted by
    /// <see cref="ScheduleYamlConverter"/>, so the normal object deserializer binds it (camelCase, coercion,
    /// unknown-key tolerance) without recursing back into the converter.</summary>
    private sealed class InlineScheduleYaml
    {
        public string? Name { get; set; }

        public string? Cron { get; set; }

        public int? IntervalSeconds { get; set; }

        public string? Timezone { get; set; }

        public bool? Enabled { get; set; }

        public bool? Catchup { get; set; }

        public int? MaxConcurrency { get; set; }
    }

    /// <summary>
    /// The <c>schedule:</c> key is written three ways: a bare scalar (joining one shared schedule by name, e.g.
    /// <c>schedule: nightly</c>), a sequence (joining several, e.g. <c>schedule: [nightly, hourly]</c>, so one flow
    /// can sit in a nightly full refresh and an hourly subset), or a mapping (an inline cadence, optionally carrying
    /// a <c>name:</c> to publish it for others to join). YamlDotNet cannot bind all three shapes to one type, so this
    /// converter reads the scalar and sequence forms itself and hands the mapping form to the normal deserializer.
    /// </summary>
    private sealed class ScheduleYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ScheduleYaml);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            // schedule: nightly
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return string.IsNullOrWhiteSpace(scalar.Value)
                    ? null
                    : new ScheduleYaml { Refs = [scalar.Value.Trim()] };
            }

            // schedule: [nightly, hourly]
            if (parser.Current is SequenceStart)
            {
                var names = rootDeserializer(typeof(List<string>)) as List<string> ?? [];
                var refs = names
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return refs.Count == 0 ? null : new ScheduleYaml { Refs = refs };
            }

            if (rootDeserializer(typeof(InlineScheduleYaml)) is not InlineScheduleYaml inline)
            {
                return null;
            }

            return new ScheduleYaml
            {
                Name = inline.Name,
                Cron = inline.Cron,
                IntervalSeconds = inline.IntervalSeconds,
                Timezone = inline.Timezone,
                Enabled = inline.Enabled,
                Catchup = inline.Catchup,
                MaxConcurrency = inline.MaxConcurrency,
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => throw new NotSupportedException("The schedule probe is read-only.");
    }

    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ScheduleYamlConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, IFlowDocumentKind> _kinds;

    public YamlDocumentLoader(IEnumerable<IFlowDocumentKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _kinds = new Dictionary<string, IFlowDocumentKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind.FlowType);
            if (!_kinds.TryAdd(kind.FlowType.Trim(), kind))
            {
                throw new InvalidOperationException(
                    $"Two document kinds claim flowType '{kind.FlowType}' ({_kinds[kind.FlowType.Trim()].GetType().Name} and {kind.GetType().Name}).");
            }
        }
    }

    /// <summary>The kinds this loader knows, in registration order by flowType.</summary>
    public IReadOnlyCollection<IFlowDocumentKind> Kinds => _kinds.Values;

    public FlowDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public FlowDocument Parse(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        DocumentKindYaml? probe;
        try
        {
            probe = _probe.Deserialize<DocumentKindYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        var flowType = probe?.FlowType?.Trim();
        var envelope = new FlowDocumentEnvelope(
            MapSchedule(probe?.Schedule),
            YamlDocumentParts.ParseExecutionMode(probe?.Mode, "mode", source),
            YamlDocumentParts.ParseLifecycle(probe?.Lifecycle, source));

        if (string.IsNullOrEmpty(flowType))
        {
            throw new FlowValidationException($"{source}: 'flowType' is required. {KnownKinds()}");
        }

        if (!_kinds.TryGetValue(flowType, out var kind))
        {
            throw new FlowValidationException($"{source}: unknown flowType '{flowType}'. {KnownKinds()}");
        }

        return kind.Parse(yaml, source, envelope);
    }

    /// <summary>Whether a document's <c>flowType</c> names a kind this loader knows, without parsing the body.</summary>
    public bool IsKnownKind(string? flowType) => !string.IsNullOrWhiteSpace(flowType) && _kinds.ContainsKey(flowType.Trim());

    /// <summary>
    /// Recognises a companion document (one a registered kind owns beside its flows, keyed by a top-level
    /// <c>documentType</c>) and parses it through its kind, returning the type and the display name. Null when the
    /// text declares no <c>documentType</c>; a <see cref="FlowValidationException"/> when it declares one no kind
    /// owns, or when the kind rejects it.
    /// </summary>
    public (string DocumentType, string Name)? ParseCompanion(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var documentType = TopLevelValue(yaml, "documentType");
        if (documentType is null)
        {
            return null;
        }

        var kind = _kinds.Values.OfType<ICompanionDocumentKind>()
            .FirstOrDefault(k => k.DocumentType.Equals(documentType, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlowValidationException($"{source}: no registered kind owns documentType '{documentType}'.");
        return (kind.DocumentType, kind.ParseCompanion(yaml, source));
    }

    /// <summary>The scalar value of a top-level key, read textually: the cheap probe that decides which family a
    /// document belongs to before anything is parsed.</summary>
    private static string? TopLevelValue(string yaml, string key)
    {
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] is ' ' or '\t' or '#')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(colon + 1)..].Trim();
                var comment = value.IndexOf('#', StringComparison.Ordinal);
                if (comment >= 0)
                {
                    value = value[..comment].Trim();
                }

                return string.IsNullOrWhiteSpace(value) ? null : value.Trim('"', '\'');
            }
        }

        return null;
    }

    private string KnownKinds()
    {
        if (_kinds.Count == 0)
        {
            return "No document kinds are registered in this host.";
        }

        var known = _kinds.Values
            .OrderBy(k => k.FlowType, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"'{k.FlowType}' ({k.Description})");
        return "Known kinds: " + string.Join("; ", known) + ".";
    }

    private static ScheduleSpec? MapSchedule(ScheduleYaml? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        // A membership declaration (schedule: nightly, or schedule: [nightly, hourly]): carry the names for the
        // repo-wide scan to bind. The cadence is deliberately NOT copied onto the flow (a single-file parse has no
        // view of the shared library anyway): the flow JOINS those schedules, and each one fires once for all of
        // its members.
        if (schedule.Refs.Count > 0)
        {
            return new ScheduleSpec { Refs = schedule.Refs };
        }

        // An inline block carrying neither a cron nor an interval declares nothing to fire; treat it as absent so
        // an empty/placeholder block is not stored as a broken schedule. The cron syntax itself is validated where
        // the schedule is armed (the catalog/control plane owns the cron library).
        if (string.IsNullOrWhiteSpace(schedule.Cron) && schedule.IntervalSeconds is null)
        {
            return null;
        }

        return new ScheduleSpec
        {
            Name = string.IsNullOrWhiteSpace(schedule.Name) ? null : schedule.Name.Trim(),
            Cron = string.IsNullOrWhiteSpace(schedule.Cron) ? null : schedule.Cron.Trim(),
            IntervalSeconds = schedule.IntervalSeconds,
            Timezone = string.IsNullOrWhiteSpace(schedule.Timezone) ? "UTC" : schedule.Timezone.Trim(),
            Enabled = schedule.Enabled ?? true,
            Catchup = schedule.Catchup ?? false,
            // Omitted takes the product default; 0 is the explicit unbounded opt-out. The probe has nowhere to
            // surface a warning, so a meaningless negative simply falls back to the default (the schedule-library
            // loader, which does have a warning channel, reports it).
            MaxConcurrency = ScheduleDefaults.Resolve(schedule.MaxConcurrency, out _),
        };
    }
}
