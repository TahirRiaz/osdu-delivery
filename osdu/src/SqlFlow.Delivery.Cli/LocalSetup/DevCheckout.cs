using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Cli.LocalSetup;

/// <summary>
/// The checkout a local run starts from: the folder holding the template <c>dev.env.example</c> (beside dev.bat and dev.sh),
/// the env file the setup writes and the check reads (its <c>.sqlflow/env</c>, or the one <c>--env-file</c> names), and the
/// command line's own project there.
/// </summary>
/// <param name="Root">The checkout's root folder.</param>
/// <param name="EnvFile">The env file, as a full path.</param>
internal sealed record DevCheckout(string Root, string EnvFile)
{
    /// <summary>The template's file name, at the checkout's root.</summary>
    public const string TemplateName = "dev.env.example";

    /// <summary>The template.</summary>
    public string TemplatePath => Path.Combine(Root, TemplateName);

    /// <summary>
    /// The checkout at <paramref name="startDirectory"/> or above it: the nearest folder holding <see cref="TemplateName"/>.
    /// </summary>
    /// <exception cref="FlowValidationException">No folder there or above holds the template.</exception>
    public static DevCheckout Find(string startDirectory, string? envFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, TemplateName)))
            {
                var file = string.IsNullOrWhiteSpace(envFile)
                    ? Path.Combine(directory.FullName, LocalEnvFile.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                    : Path.GetFullPath(envFile);
                return new DevCheckout(directory.FullName, file);
            }
        }

        throw new FlowValidationException(
            $"No {TemplateName} was found in {Path.GetFullPath(startDirectory)} or above it. Run 'sqlflow dev' from an OSDU Delivery checkout "
            + "(dev.bat and dev.sh do), where the template sits beside them.");
    }

    /// <summary>A path as the user reads it: relative to the checkout when it is inside it, else in full.</summary>
    public string Show(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Root, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }
}
