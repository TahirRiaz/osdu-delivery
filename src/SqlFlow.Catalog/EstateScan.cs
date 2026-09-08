using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

/// <summary>One flow document the estate scan found, projected to what every catalog consumer needs.</summary>
public sealed record CollectedFlow
{
    public required string Name { get; init; }

    /// <summary>The document kind (its <c>flowType</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The flow document path, relative to the scanned folder, forward-slashed.</summary>
    public required string File { get; init; }

    public string? Batch { get; init; }

    public ExecutionMode Mode { get; init; } = ExecutionMode.Auto;

    public FlowLifecycle Lifecycle { get; init; } = FlowLifecycle.Production;

    /// <summary>The flow's <c>schedule:</c> declaration, or null when it declares none: either an inline cadence or
    /// a membership reference to named schedules. Carried from the document so <see cref="EstateScanResult.Schedules"/>
    /// can be resolved once the whole estate is in hand.</summary>
    public ScheduleSpec? Schedule { get; init; }

    /// <summary>What the flow reads, as the pipeline row shows it (no credential in it).</summary>
    public string? SourceReference { get; init; }

    /// <summary>What the flow writes to, as the pipeline row shows it.</summary>
    public string? TargetReference { get; init; }

    public required DateTime FileWriteUtc { get; init; }
}

/// <summary>One named schedule the estate declares, with the flows that joined it.</summary>
public sealed record CollectedSchedule
{
    /// <summary>The schedule's name: its reference target and its identity in the catalog.</summary>
    public required string Name { get; init; }

    /// <summary>The cadence (cron or interval, time zone, enabled, catchup), or the parents it chains after.</summary>
    public required ScheduleSpec Spec { get; init; }

    /// <summary>The repo-relative path of the file the cadence is written in: a <c>schedules.yaml</c> library file,
    /// or the flow document carrying the inline block. This is what the GUI shows as "where is this defined".</summary>
    public required string OriginFile { get; init; }

    /// <summary>The flow whose inline <c>schedule:</c> block declares this schedule; null when a library file does.
    /// With it the catalog can point at the declaring flow's stored (secret-redacted) YAML instead of keeping a
    /// second copy of the document.</summary>
    public string? OriginFlow { get; init; }

    /// <summary>The library file's text, carried only for a library-declared schedule: a flow document is already
    /// stored (redacted) on its pipeline row, but nothing else in the catalog holds a <c>schedules.yaml</c>.</summary>
    public string? LibraryYaml { get; init; }

    /// <summary>Where the definition came from, for warnings (a library file's relative path, or a flow and file).</summary>
    public string Origin => OriginFlow is null ? OriginFile : $"'{OriginFlow}' ({OriginFile})";

    /// <summary>The flow names that joined this schedule: the flow that declared it inline, plus every flow whose
    /// <c>schedule:</c> references it by name. A fire runs exactly this set. Empty when a library entry nothing
    /// references.</summary>
    public List<string> Members { get; } = [];
}

/// <summary>What one scan of a repository found: its flows, its named schedules with their member sets, and the
/// warnings raised on the way.</summary>
public sealed class EstateScanResult
{
    public List<CollectedFlow> Flows { get; } = [];

    /// <summary>Every named schedule the estate declares, with the flows that joined it, resolved once the whole
    /// repo is in hand (a reference can point at a definition in any file). This is the authority on WHAT a fire
    /// runs: a schedule fires once and runs its member set as one group. Ordered by name.</summary>
    public List<CollectedSchedule> Schedules { get; } = [];

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Scans a folder of flow documents: every <c>*.yaml</c> that parses as a flow becomes a
/// <see cref="CollectedFlow"/>, the shared-schedule library files and inline <c>schedule:</c> blocks are resolved
/// into named schedules with member sets, and anything that is not a flow (a library, a config, an unrelated
/// yaml) is silently ignored. A document that fails to parse becomes a warning and the scan continues: one
/// broken file must not blind the whole estate. Adapted from SQLFlow's lineage flow-set collector with the
/// lineage facts, servers and subscribers removed.
/// </summary>
public sealed class EstateScanner
{
    private readonly YamlDocumentLoader _documents;
    private readonly YamlScheduleLibraryLoader _scheduleLibraries = new();

    public EstateScanner(YamlDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
    }

    public EstateScanResult Collect(string flowDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowDirectory);
        var root = Path.GetFullPath(flowDirectory);
        if (!Directory.Exists(root))
        {
            throw new SqlFlowException($"Flow directory not found: '{root}'.");
        }

        var result = new EstateScanResult();
        var files = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(f => !IsScheduleLibraryFile(f))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Normalize(Path.GetRelativePath(root, file));
            FlowDocument document;
            try
            {
                document = _documents.LoadFile(file);
            }
            catch (SqlFlowException ex)
            {
                // A .yaml that names a known kind but fails to parse is a broken flow worth surfacing; a .yaml with
                // no flowType at all (a library, config, or unrelated yaml) is simply not a flow document.
                if (LooksLikeFlowDocument(file))
                {
                    result.Warnings.Add($"{relative}: {ex.Message}");
                }

                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file matched but could not be read: a real problem worth surfacing, distinct from a non-flow file.
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            result.Flows.Add(new CollectedFlow
            {
                Name = document.Name,
                Kind = document.Kind,
                File = relative,
                Batch = document.Batch,
                Mode = document.Mode,
                Lifecycle = document.Lifecycle,
                Schedule = document.Schedule,
                SourceReference = document.SourceReference,
                TargetReference = document.TargetReference,
                FileWriteUtc = System.IO.File.GetLastWriteTimeUtc(file),
            });
        }

        // Shared schedules: build the repo-wide library (dedicated schedules.yaml files plus named inline blocks),
        // then resolve every `schedule: <name>` reference to a concrete cadence. Done after the whole estate is
        // collected because a reference can point at a definition in any file.
        ResolveSchedules(result, root);

        var duplicates = result.Flows
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in duplicates)
        {
            result.Warnings.Add(
                $"flow name '{group.Key}' is declared by {group.Count()} documents ({string.Join(", ", group.Select(f => f.File))}); " +
                "the first wins in the catalog, which is almost never intended.");
        }

        return result;
    }

    /// <summary>Whether a file is a shared-schedule library: named <c>schedules.yaml</c> or ending in
    /// <c>.schedules.yaml</c>. These are not flow documents (they are excluded from the flow parse and handled by
    /// <see cref="ResolveSchedules"/>) and never become pipelines; they only publish named schedules for flows to
    /// reference. Public because the proposal preflight must classify a proposed file exactly as this scan will
    /// classify it once the proposal merges.</summary>
    public static bool IsScheduleLibraryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("schedules.yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".schedules.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A cheap textual probe: does the file carry a top-level <c>flowType:</c> key? Used only to decide
    /// whether a parse failure is a broken flow (warn) or a non-flow yaml (ignore).</summary>
    private static bool LooksLikeFlowDocument(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith("flowType:", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Builds the repo's named schedules and their MEMBER SETS. A schedule is defined once (a <c>schedules.yaml</c>
    /// library entry, or an inline block on a flow) and flows join it by name with <c>schedule: &lt;name&gt;</c>; a
    /// flow may join several. Joining is membership, never a cadence copy: the schedule fires once and runs every
    /// member as one group. An unnamed inline block takes its declaring flow's name, so every schedule is named and
    /// every fire has a member set. Library entries and inline names share one namespace and the first definition of
    /// a name wins (a redefinition is warned). A reference to an unknown name leaves that flow unscheduled with a
    /// warning, never a broken schedule.
    /// </summary>
    private void ResolveSchedules(EstateScanResult result, string root)
    {
        var library = new Dictionary<string, CollectedSchedule>(StringComparer.OrdinalIgnoreCase);

        void Register(string name, ScheduleSpec spec, string originFile, string? originFlow, string? libraryYaml)
        {
            var schedule = new CollectedSchedule
            {
                Name = name,
                Spec = spec with { Name = name, Refs = [] },
                OriginFile = originFile,
                OriginFlow = originFlow,
                LibraryYaml = libraryYaml,
            };
            if (!library.TryAdd(name, schedule))
            {
                result.Warnings.Add(
                    $"schedule name '{name}' is declared more than once ({schedule.Origin} redefines {library[name].Origin}); the first wins.");
            }
        }

        // 1) Dedicated library files.
        var libraryFiles = Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(IsScheduleLibraryFile)
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in libraryFiles)
        {
            var relative = Normalize(Path.GetRelativePath(root, file));
            string yaml;
            try
            {
                yaml = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"{relative}: skipped: {ex.Message}");
                continue;
            }

            var parsed = _scheduleLibraries.Parse(yaml, relative);
            result.Warnings.AddRange(parsed.Warnings);
            foreach (var named in parsed.Schedules)
            {
                Register(named.Name, named.Spec, relative, originFlow: null, libraryYaml: yaml);
            }
        }

        // 2) Inline blocks. A name: publishes the cadence for other flows to join; an unnamed block is still a
        //    schedule, named after its flow. Either way the declaring flow is a member: writing a cadence on a flow
        //    schedules that flow.
        foreach (var flow in result.Flows)
        {
            if (flow.Schedule is { IsReference: false } inline)
            {
                Register(
                    string.IsNullOrWhiteSpace(inline.Name) ? flow.Name : inline.Name,
                    inline,
                    flow.File,
                    flow.Name,
                    libraryYaml: null);
            }
        }

        // 3) Bind membership. The declaring flow of an inline block joins its own schedule; a referencing flow joins
        //    each name it lists. A flow can appear once per schedule at most, so a repeated reference is idempotent.
        void Join(string scheduleName, string flowName)
        {
            var members = library[scheduleName].Members;
            if (!members.Contains(flowName, StringComparer.OrdinalIgnoreCase))
            {
                members.Add(flowName);
            }
        }

        foreach (var flow in result.Flows)
        {
            switch (flow.Schedule)
            {
                case { IsReference: false } inline:
                {
                    var name = string.IsNullOrWhiteSpace(inline.Name) ? flow.Name : inline.Name;
                    // A losing redefinition (warned above) still joins the winning schedule of that name: the author
                    // asked for this cadence under this name, and the first definition is the one that survives.
                    Join(name, flow.Name);
                    break;
                }

                case { IsReference: true } reference:
                {
                    foreach (var name in reference.Refs)
                    {
                        if (library.ContainsKey(name))
                        {
                            Join(name, flow.Name);
                        }
                        else
                        {
                            result.Warnings.Add(
                                $"'{flow.Name}' ({flow.File}) joins schedule '{name}', which no schedules.yaml or " +
                                "named inline block defines; the flow is left unscheduled.");
                        }
                    }

                    break;
                }
            }
        }

        // 4) A library entry nothing joined never fires. That is a real authoring mistake (a renamed source, a typo
        //    on the referencing side), so it is surfaced rather than sitting in the catalog as a schedule with an
        //    empty set.
        foreach (var schedule in library.Values)
        {
            if (schedule.Members.Count == 0)
            {
                result.Warnings.Add(
                    $"schedule '{schedule.Name}' ({schedule.Origin}) has no members: no flow joins it with " +
                    $"'schedule: {schedule.Name}', so it would fire nothing.");
            }
        }

        result.Schedules.AddRange(library.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));

        // 5) Every flow that automatic dispatch could run should be attached to a schedule. A 'mode: manual' or
        //    'mode: disabled' flow opted out deliberately, so it is exempt; anything else that joined nothing will
        //    simply never run on its own. A delivery flow is normally driven by notifications as well, so this is
        //    a warning, never an error.
        var attached = new HashSet<string>(
            library.Values.SelectMany(s => s.Members), StringComparer.OrdinalIgnoreCase);
        foreach (var flow in result.Flows)
        {
            if (!attached.Contains(flow.Name) && flow.Mode == ExecutionMode.Auto)
            {
                result.Warnings.Add(
                    $"'{flow.Name}' ({flow.File}) is attached to no schedule and is not 'mode: manual' " +
                    "or 'mode: disabled'; it runs only when triggered directly.");
            }
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
