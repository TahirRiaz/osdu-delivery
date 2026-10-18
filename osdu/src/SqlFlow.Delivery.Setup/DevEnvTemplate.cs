using System.Text.RegularExpressions;
using SqlFlow.Core;

namespace SqlFlow.Delivery.Setup;

/// <summary>What a setting of the template is needed for, which decides what its absence means.</summary>
internal enum SettingNeed
{
    /// <summary>A start needs it: missing, the check reports an error and the launchers stop.</summary>
    Start,

    /// <summary>A run that reads it needs it: missing, the check warns and the launchers start.</summary>
    Flows,

    /// <summary>It has a default: reported when set.</summary>
    Optional,
}

/// <summary>A setting the template names.</summary>
/// <param name="Name">The variable's name; one holding <c>&lt;name&gt;</c> stands for a family (a repository's settings).</param>
/// <param name="Value">The template's value: a default, a placeholder, or <see cref="DevValues.Generated"/>.</param>
/// <param name="Need">The section it is in.</param>
/// <param name="Commented">True when the template writes it commented out: optional, and unset unless the user sets it.</param>
internal sealed record TemplateSetting(string Name, string Value, SettingNeed Need, bool Commented)
{
    /// <summary>True for a family's pattern rather than one setting.</summary>
    public bool IsFamily => Name.Contains(DevEnvTemplate.FamilyToken, StringComparison.Ordinal);
}

/// <summary>
/// The template a local run's env file is written from, <c>dev.env.example</c>: a header, then sections whose titles start
/// "Needed to start", "Needed to run flows" or "Optional", each holding settings (commented out when optional and unset)
/// and the comments explaining them.
/// </summary>
internal sealed partial class DevEnvTemplate
{
    /// <summary>The token a family's names hold in place of the member's name.</summary>
    public const string FamilyToken = "<name>";

    private DevEnvTemplate(IReadOnlyList<string> lines, int bodyStart, IReadOnlyList<TemplateSetting> settings)
    {
        Lines = lines;
        BodyStart = bodyStart;
        Settings = settings;
    }

    /// <summary>Every line of the template.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The index of the first section's title: everything before it is the template's own header.</summary>
    public int BodyStart { get; }

    /// <summary>The settings, in template order.</summary>
    public IReadOnlyList<TemplateSetting> Settings { get; }

    /// <summary>The setting named <paramref name="name"/>, or null.</summary>
    public TemplateSetting? Find(string name) => Settings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>Reads the template at <paramref name="path"/>.</summary>
    /// <exception cref="FlowValidationException">The file is missing or breaks the template's form; the message names the line.</exception>
    public static async Task<DevEnvTemplate> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"The template {path} is missing; it ships with the repository. Restore it with: git checkout -- {DevCheckout.TemplateName}");
        }

        return Parse(await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false), path);
    }

    /// <summary>Reads template <paramref name="lines"/>; <paramref name="source"/> names them in an error.</summary>
    /// <exception cref="FlowValidationException">A section's title is none of the three, or no section names a setting.</exception>
    public static DevEnvTemplate Parse(IReadOnlyList<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var settings = new List<TemplateSetting>();
        SettingNeed? section = null;
        var bodyStart = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (SectionTitle().Match(line) is { Success: true } title)
            {
                var text = title.Groups["title"].Value;
                section = text.StartsWith("Needed to start", StringComparison.Ordinal) ? SettingNeed.Start
                    : text.StartsWith("Needed to run flows", StringComparison.Ordinal) ? SettingNeed.Flows
                    : text.StartsWith("Optional", StringComparison.Ordinal) ? SettingNeed.Optional
                    : throw new FlowValidationException(
                        $"{source}({i + 1}): the section '{text}' is none of 'Needed to start', 'Needed to run flows' and 'Optional'.");
                if (bodyStart < 0)
                {
                    bodyStart = i;
                }

                continue;
            }

            if (section is { } need && SettingLine().Match(line) is { Success: true } setting)
            {
                settings.Add(new TemplateSetting(setting.Groups["name"].Value, setting.Groups["value"].Value.Trim(), need, line.StartsWith('#')));
            }
        }

        if (settings.Count == 0)
        {
            throw new FlowValidationException($"{source} names no setting under a section; its sections start '# ===== Needed to start', '# ===== Needed to run flows' or '# ===== Optional'.");
        }

        return new DevEnvTemplate([.. lines], bodyStart, settings);
    }

    /// <summary>
    /// A line that sets a variable, commented out or not: <c>NAME=value</c> or <c># NAME=value</c>, the name letters, digits and
    /// _, or a family's pattern holding <see cref="FamilyToken"/>.
    /// </summary>
    public static Match MatchSetting(string line) => SettingLine().Match(line.Trim());

    [GeneratedRegex(@"^#\s*=====\s*(?<title>.+?)\s*=====\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionTitle();

    [GeneratedRegex(@"^(#\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*(<name>[A-Za-z0-9_]*)?)=(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingLine();
}
