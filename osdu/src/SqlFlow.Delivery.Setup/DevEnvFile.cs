using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Setup;

/// <summary>
/// Writes a local run's env file: the template's sections with the setup's values in place, every other name the replaced
/// file set kept at the end, and the settings it kept commented out carried over as they were. A write replaces the file in
/// one move, keeps the file it replaces beside it, and leaves the file readable by its owner alone where the file system says
/// who may read (the file holds secrets).
/// </summary>
internal static class DevEnvFile
{
    /// <summary>The section title the kept names go under; an "Optional" one, so the file reads as the template does.</summary>
    public const string KeptTitle = "# ===== Optional: kept from the file this setup replaced (the template does not name them) =====";

    /// <summary>The env file's lines for <paramref name="values"/>.</summary>
    /// <param name="template">The template.</param>
    /// <param name="values">Every value the file sets, by name, in the order they were given.</param>
    /// <param name="parked">Commented-out settings of the replaced file, as written there, carried over as they are.</param>
    /// <param name="written">When the file is written, for its header.</param>
    public static IReadOnlyList<string> Render(DevEnvTemplate template, IReadOnlyList<KeyValuePair<string, string>> values, IReadOnlyList<string> parked, DateTimeOffset written)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(parked);

        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            byName.TryAdd(name, value);
        }

        var output = new List<string>
        {
            "# OSDU Delivery: the settings of a local run on this machine. NEVER COMMITTED: .sqlflow/ is git-ignored.",
            string.Create(CultureInfo.InvariantCulture, $"# Written by osdu-delivery-setup on {written:yyyy-MM-dd HH:mm} from {DevCheckout.TemplateName}, which explains each"),
            "# setting. 'dev.bat setup' (or './dev.sh setup') asks the questions again; 'dev.bat check' reports what is set and missing.",
            "# It holds this machine's values, secrets included: never copy one into a flow, a mapping, a commit or a chat.",
            string.Empty,
        };

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var familyWritten = false;
        for (var i = Math.Max(template.BodyStart, 0); i < template.Lines.Count; i++)
        {
            var line = template.Lines[i].TrimEnd();
            var setting = DevEnvTemplate.MatchSetting(line);
            if (setting.Success)
            {
                var name = setting.Groups["name"].Value;
                if (name.Contains(DevEnvTemplate.FamilyToken, StringComparison.Ordinal))
                {
                    // A family's members go before its commented-out pattern, once, in the order they were given.
                    if (!familyWritten)
                    {
                        var prefix = name[..name.IndexOf(DevEnvTemplate.FamilyToken, StringComparison.Ordinal)];
                        foreach (var (member, value) in values.Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal) && placed.Add(v.Key)))
                        {
                            output.Add($"{member}={value}");
                        }

                        familyWritten = true;
                    }

                    output.Add(line);
                    continue;
                }

                if (byName.TryGetValue(name, out var given) && placed.Add(name))
                {
                    output.Add($"{name}={given}");
                    continue;
                }

                if (!line.StartsWith('#') && string.Equals(setting.Groups["value"].Value.Trim(), DevValues.Generated, StringComparison.Ordinal) && placed.Add(name))
                {
                    output.Add($"{name}={DevValues.GenerateFor(name)}");
                    continue;
                }
            }

            output.Add(line);
        }

        var kept = values.Where(v => !placed.Contains(v.Key)).ToList();
        var templateLines = new HashSet<string>(template.Lines.Select(l => l.Trim()), StringComparer.Ordinal);
        var carried = parked.Where(p => !templateLines.Contains(p.Trim())).Distinct(StringComparer.Ordinal).ToList();
        if (kept.Count > 0 || carried.Count > 0)
        {
            output.Add(string.Empty);
            output.Add(KeptTitle);
            foreach (var (name, value) in kept)
            {
                if (placed.Add(name))
                {
                    output.Add($"{name}={value}");
                }
            }

            if (carried.Count > 0)
            {
                output.Add("# Commented out in the replaced file, and kept that way:");
                output.AddRange(carried.Select(c => c.Trim()));
            }
        }

        return output;
    }

    /// <summary>The settings <paramref name="lines"/> keep commented out (<c># NAME=value</c>), as written.</summary>
    public static IReadOnlyList<string> CommentedSettings(IEnumerable<string> lines)
        => [.. lines.Select(l => l.Trim()).Where(l => l.StartsWith('#') && DevEnvTemplate.MatchSetting(l) is { Success: true } m
            && !m.Groups["name"].Value.Contains(DevEnvTemplate.FamilyToken, StringComparison.Ordinal))];

    /// <summary>
    /// Writes <paramref name="lines"/> to <paramref name="path"/>: into a file beside it, then moved over it, so a reader
    /// never sees half a file. With <paramref name="backupStamp"/>, the file it replaces is kept as
    /// <c>&lt;name&gt;.&lt;time&gt;.bak</c>, whose path this returns (null when nothing was kept). On a file system with Unix
    /// modes, the file (and its backup) is readable by its owner only.
    /// </summary>
    public static async Task<string?> WriteAsync(string path, IReadOnlyList<string> lines, DateTimeOffset? backupStamp, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException($"{path} has no folder.");
        Directory.CreateDirectory(directory);

        string? backup = null;
        if (backupStamp is { } now && File.Exists(path))
        {
            backup = string.Create(CultureInfo.InvariantCulture, $"{path}.{now:yyyyMMdd-HHmmss}.bak");
            for (var n = 2; File.Exists(backup); n++)
            {
                backup = string.Create(CultureInfo.InvariantCulture, $"{path}.{now:yyyyMMdd-HHmmss}-{n}.bak");
            }

            File.Copy(path, backup);
            OwnerOnly(backup);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.writing";
        try
        {
            var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            OwnerOnly(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return backup;
    }

    /// <summary>
    /// Replaces the value of each <c>NAME=&lt;generated&gt;</c> line of the file at <paramref name="path"/> with a generated
    /// one (a template copied by hand), leaving every other line as it is. Returns the names it generated.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CompleteGeneratedAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var generated = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(line[(separator + 1)..].Trim(), DevValues.Generated, StringComparison.Ordinal))
            {
                var name = line[..separator].Trim();
                lines[i] = $"{name}={DevValues.GenerateFor(name)}";
                generated.Add(name);
            }
        }

        if (generated.Count > 0)
        {
            // Only placeholders change, so the file it replaces holds nothing worth keeping beside it.
            await WriteAsync(path, lines, backupStamp: null, ct).ConfigureAwait(false);
        }

        return generated;
    }

    private static void OwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
