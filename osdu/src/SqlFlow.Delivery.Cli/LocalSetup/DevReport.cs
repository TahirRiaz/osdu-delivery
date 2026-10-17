using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Cli.LocalSetup;

/// <summary>What a finding says about the run.</summary>
internal enum FindingStatus
{
    /// <summary>In place.</summary>
    Ok,

    /// <summary>A start needs it and it is missing or wrong: the launchers stop.</summary>
    Error,

    /// <summary>A run that needs it fails; the launchers start.</summary>
    Warning,

    /// <summary>Optional, and not set or not needed.</summary>
    Off,

    /// <summary>Said for the reader's information.</summary>
    Info,
}

/// <summary>One line of the check: a status, what it is about, what was found, and how to fix it.</summary>
internal sealed record Finding(FindingStatus Status, string Label, string Detail, IReadOnlyList<string> Fixes)
{
    public static Finding Ok(string label, string detail) => new(FindingStatus.Ok, label, detail, []);

    public static Finding Error(string label, string detail, params string[] fixes) => new(FindingStatus.Error, label, detail, fixes);

    public static Finding Warning(string label, string detail, params string[] fixes) => new(FindingStatus.Warning, label, detail, fixes);

    public static Finding Off(string label, string detail) => new(FindingStatus.Off, label, detail, []);

    public static Finding Info(string label, string detail, params string[] fixes) => new(FindingStatus.Info, label, detail, fixes);
}

/// <summary>A titled group of findings.</summary>
internal sealed record ReportSection(string Title, IReadOnlyList<Finding> Findings);

/// <summary>The check's findings, by section.</summary>
internal sealed record DevReport(IReadOnlyList<ReportSection> Sections, string Launcher)
{
    /// <summary>How many findings stop a start.</summary>
    public int Errors => Sections.Sum(s => s.Findings.Count(f => f.Status == FindingStatus.Error));

    /// <summary>How many findings a run will miss.</summary>
    public int Warnings => Sections.Sum(s => s.Findings.Count(f => f.Status == FindingStatus.Warning));

    /// <summary>True when nothing stops a start.</summary>
    public bool Ready => Errors == 0;

    /// <summary>The closing line.</summary>
    public string Verdict => Ready
        ? Warnings == 0 ? "Ready to start." : $"Ready to start. The {Warnings} WARN line(s) above are what a flow run will miss."
        : $"Not ready to start: {Errors} ERROR line(s) above. Fix them ({Launcher} setup asks the setup questions again), then start {Launcher} again.";

    /// <summary>Writes the report as text: a heading per section, then a line per finding with its fixes under it.</summary>
    public void Write(IPrompter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Say(string.Empty);
        output.Say("OSDU Delivery: what this machine has for a local run, and what it is missing", Tone.Heading);
        foreach (var section in Sections.Where(s => s.Findings.Count > 0))
        {
            output.Say(string.Empty);
            output.Say(section.Title, Tone.Heading);
            WriteFindings(output, section.Findings);
        }

        output.Say(string.Empty);
        output.Say(Verdict, Ready ? Warnings == 0 ? Tone.Good : Tone.Caution : Tone.Bad);
    }

    /// <summary>Writes findings, a line each with its fixes under it.</summary>
    public static void WriteFindings(IPrompter output, IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(findings);
        foreach (var finding in findings)
        {
            var (tag, tone) = finding.Status switch
            {
                FindingStatus.Ok => ("  ok     ", Tone.Good),
                FindingStatus.Error => ("  ERROR  ", Tone.Bad),
                FindingStatus.Warning => ("  WARN   ", Tone.Caution),
                FindingStatus.Off => ("  -      ", Tone.Note),
                _ => ("         ", Tone.Plain),
            };
            var label = finding.Label.Length < 46 ? finding.Label.PadRight(46) : finding.Label + " ";
            output.Say($"{tag}{label}{finding.Detail}", tone);
            foreach (var fix in finding.Fixes)
            {
                output.Say($"           -> {fix}");
            }
        }
    }

    /// <summary>The report as one JSON document.</summary>
    public JsonObject ToJson() => new()
    {
        ["ready"] = Ready,
        ["errors"] = Errors,
        ["warnings"] = Warnings,
        ["sections"] = new JsonArray([.. Sections.Select(s => (JsonNode)new JsonObject
        {
            ["title"] = s.Title,
            ["findings"] = new JsonArray([.. s.Findings.Select(f => (JsonNode)new JsonObject
            {
                ["status"] = f.Status.ToString().ToLowerInvariant(),
                ["label"] = f.Label,
                ["detail"] = f.Detail,
                ["fixes"] = new JsonArray([.. f.Fixes.Select(x => (JsonNode)JsonValue.Create(x)!)]),
            })]),
        })]),
    };
}
