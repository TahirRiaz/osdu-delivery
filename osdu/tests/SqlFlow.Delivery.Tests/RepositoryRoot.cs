using System.Reflection;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The repository the suite was built from, for the tests that read files the build does not copy next to the binaries
/// (the vendored specifications, the MCP server's sources). The build bakes the path in (the test project's
/// <c>RepositoryRoot</c> assembly metadata), because a suite built into an artifacts folder outside the repository, as
/// <c>--artifacts-path</c> builds it, cannot find the repository by walking up from its own binaries; a suite whose
/// metadata names no repository on this machine (built elsewhere and copied) falls back to that walk.
/// </summary>
public static class RepositoryRoot
{
    /// <summary>The file only the repository's root folder holds.</summary>
    public const string Marker = "OsduDelivery.sln";

    /// <summary>The metadata key the test projects bake the repository's path under.</summary>
    public const string MetadataKey = "RepositoryRoot";

    private static readonly Lazy<string> Found = new(Find);

    /// <summary>The repository's root folder, the one holding <see cref="Marker"/>.</summary>
    public static string Path => Found.Value;

    /// <summary>A path inside the repository, from its parts relative to the root.</summary>
    public static string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return System.IO.Path.Combine([Path, .. parts]);
    }

    private static string Find()
    {
        var baked = typeof(RepositoryRoot).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(baked) && File.Exists(System.IO.Path.Combine(baked, Marker)))
        {
            return System.IO.Path.GetFullPath(baked);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, Marker)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"The suite cannot find the repository it was built from: its build recorded '{baked ?? "(nothing)"}' as the repository, " +
            $"which holds no {Marker}, and no folder above {AppContext.BaseDirectory} holds one either. Run the suite from a build of the repository's test project.");
    }
}
