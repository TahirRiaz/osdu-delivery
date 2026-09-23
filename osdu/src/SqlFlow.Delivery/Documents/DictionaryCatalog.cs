using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Finds dictionary documents in a repository. A dictionary <c>RecallUnits</c> is the file <c>RecallUnits.yaml</c> (or
/// <c>.yml</c>) in the nearest <c>dictionaries/</c> directory walking up from the cache flow that declares it, the way a
/// delivery flow finds its <c>mappings/</c>, so a cache flow three folders deep still finds the repository's shared
/// dictionaries. The file must declare the name it is filed under, so a name always resolves to exactly the file that was
/// reviewed, and one file holds one dictionary.
/// </summary>
public sealed class DictionaryCatalog
{
    public const string DirectoryName = "dictionaries";

    private const int MaxAscent = 16;

    private readonly DeliveryDocumentLoader _loader;

    public DictionaryCatalog(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    /// <summary>
    /// The dictionaries directory for a cache flow whose file sits in <paramref name="flowFolder"/>: the nearest one walking
    /// up, looked for only where <paramref name="within"/> allows (null allows anywhere), or null when there is none.
    /// </summary>
    public static string? Locate(string flowFolder, Func<string, bool>? within = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFolder);
        var current = Path.GetFullPath(flowFolder);
        for (var i = 0; i < MaxAscent && current is not null && (within is null || within(current)); i++)
        {
            var candidate = Path.Combine(current, DirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Loads dictionary <paramref name="name"/> for a cache flow in <paramref name="flowFolder"/>, naming every file as
    /// <paramref name="shown"/> gives it (relative to the repository, say). The name names a file and nothing else: one
    /// carrying a path is refused before any file is looked at.
    /// </summary>
    /// <exception cref="FlowValidationException">No such dictionary, or a file that does not declare the name it is filed under.</exception>
    public DictionaryFile Load(string name, string flowFolder, Func<string, string> shown, Func<string, bool>? within = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shown);
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(name))
        {
            throw new FlowValidationException($"Dictionary '{name}' names a path; a dictionary is named by its name alone, without '/', '\\' or '..'.");
        }

        var directory = Locate(flowFolder, within)
            ?? throw new FlowValidationException(
                $"Dictionary '{name}' was not found: there is no {DirectoryName}/ directory in '{shown(Path.GetFullPath(flowFolder))}' or any folder above it. Create {DirectoryName}/{name}.yaml beside the repository's flows.");
        var candidates = new[] { Path.Combine(directory, name + ".yaml"), Path.Combine(directory, name + ".yml") };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FlowValidationException(
                $"Dictionary '{name}' was not found under '{shown(directory)}'. Expected {name}.yaml or {name}.yml.");

        var dictionary = _loader.LoadDictionary(path, shown(path));
        if (!string.Equals(dictionary.Name, name, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{shown(path)}: declares dictionary '{dictionary.Name}' but is filed as '{name}'. The file name and the document must agree.");
        }

        return new DictionaryFile(dictionary, path, shown(path));
    }
}

/// <summary>A dictionary found for a cache flow: the document, its full path, and the path messages and the catalog name it by.</summary>
public sealed record DictionaryFile(DictionaryDefinition Dictionary, string FullPath, string ShownPath);
