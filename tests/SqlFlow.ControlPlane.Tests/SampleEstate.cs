namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The sample delivery estate (<c>samples/recall-welllog</c>) copied to a temp repository, for the platform tests
/// that need a run to genuinely execute. The one production flow kind needs its mapping, its snapshots and a drop
/// to render from, and the sample holds all three, so a test copies it rather than inventing a second estate that
/// would drift from the real one. This mirrors what the GUI e2e fixture does, for the same reason.
/// </summary>
internal static class SampleEstate
{
    /// <summary>The log source the sample's drop was generated for; the flow's one required parameter.</summary>
    public const string LogSource = "STAT_COMP";

    /// <summary>
    /// The flow's name, which the copy keeps: the drop's manifest names the flow it was prepared for, and the
    /// intake refuses a drop prepared for a different one. A test scopes itself by repository instead, which is
    /// what makes the pipeline id unique anyway.
    /// </summary>
    public const string FlowName = "recall-welllog";

    /// <summary>The parts of the estate a run needs: the documents, what they render with, and the drop itself.</summary>
    private static readonly string[] Parts = ["flows", "mappings", "snapshots", "references", "out"];

    /// <summary>
    /// Copies the estate into <paramref name="destination"/> and points the flow at the copied drop. The declared
    /// drop location is relative to the repository root in git, which is not where a temp copy sits; nothing else
    /// about the flow is touched, so what executes is the sample flow as shipped.
    /// </summary>
    public static string CopyTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var source = Locate();
        foreach (var part in Parts)
        {
            CopyDirectory(Path.Combine(source, part), Path.Combine(destination, part));
        }

        var flowFile = Path.Combine(destination, "flows", "recall-welllog.yaml");
        var drop = Path.Combine(destination, "out", "{logSource}").Replace('\\', '/');
        File.WriteAllText(flowFile, File.ReadAllText(flowFile).Replace("samples/recall-welllog/out/{logSource}", drop, StringComparison.Ordinal));
        return destination;
    }

    /// <summary>
    /// The estate as the build copied it next to the test binaries. It is copied rather than read out of the
    /// repository so the suite works wherever the build output lands, which walking up from the binaries does not.
    /// </summary>
    private static string Locate()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "samples");
        if (!Directory.Exists(copied))
        {
            throw new DirectoryNotFoundException(
                $"The sample estate was not copied next to the test binaries ({copied}). It is a Content item of this test project; rebuild the suite.");
        }

        return copied;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }
}
