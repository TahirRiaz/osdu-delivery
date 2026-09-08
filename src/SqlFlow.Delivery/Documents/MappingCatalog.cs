using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Locates pinned mappings on disk. A mapping <c>WellLog@1.4.0</c> is the file <c>WellLog@1.4.0.yaml</c> (or
/// <c>WellLog/1.4.0.yaml</c>) under the mappings directory. The file must declare the same name and version it is
/// filed under, so a reference always resolves to exactly the artifact that was reviewed.
/// </summary>
public sealed class MappingCatalog
{
    private readonly string _directory;
    private readonly DeliveryDocumentLoader _loader;

    public MappingCatalog(string directory, DeliveryDocumentLoader loader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(loader);
        _directory = directory;
        _loader = loader;
    }

    public string Directory => _directory;

    public MappingDefinition Load(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == reference.Length - 1)
        {
            throw new FlowValidationException($"Mapping reference '{reference}' must be pinned as 'Name@version'.");
        }

        var name = reference[..at];
        var version = reference[(at + 1)..];
        var candidates = new[]
        {
            Path.Combine(_directory, $"{name}@{version}.yaml"),
            Path.Combine(_directory, $"{name}@{version}.yml"),
            Path.Combine(_directory, name, $"{version}.yaml"),
            Path.Combine(_directory, name, $"{version}.yml"),
        };

        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FlowValidationException(
                $"Mapping '{reference}' was not found under '{_directory}'. Expected one of: {string.Join(", ", candidates.Select(Path.GetFileName))}.");

        var mapping = _loader.LoadMapping(path);
        if (!mapping.Name.Equals(name, StringComparison.Ordinal) || !mapping.Version.Equals(version, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{path}: declares '{mapping.Reference}' but is filed as '{reference}'. The file name and the document must agree.");
        }

        return mapping;
    }

    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(_directory, "*.y*ml", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n.Contains('@', StringComparison.Ordinal))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Resolves a flow's declared parameters against supplied values and substitutes tokens.</summary>
public static class FlowParameters
{
    /// <summary>
    /// Returns the effective parameter values: supplied values, then defaults; throws when a required parameter is
    /// missing or an undeclared one is supplied.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(FlowDefinition flow, IReadOnlyDictionary<string, string>? supplied)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);
    }

    /// <summary>The same resolution over any declared parameter set (a retrieval flow's, say); <paramref name="where"/> names the document in errors.</summary>
    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, FlowParameter> parameters, string where, IReadOnlyDictionary<string, string>? supplied)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, parameter) in parameters)
        {
            if (supplied is not null && supplied.TryGetValue(name, out var v))
            {
                values[name] = v;
            }
            else if (parameter.Default is not null)
            {
                values[name] = parameter.Default;
            }
            else if (parameter.Required)
            {
                throw new FlowValidationException($"{where}: parameter '{name}' is required. Supply it with --set {name}=value or through the manifest.");
            }
        }

        if (supplied is not null)
        {
            foreach (var name in supplied.Keys)
            {
                if (!parameters.ContainsKey(name))
                {
                    throw new FlowValidationException($"{where}: parameter '{name}' is not declared under parameters.");
                }
            }
        }

        return values;
    }

    /// <summary>Substitutes <c>{name}</c> tokens in a string from the resolved values.</summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{(?<name>[A-Za-z0-9_]+)\}", m =>
            values.TryGetValue(m.Groups["name"].Value, out var v) ? v : m.Value);
    }

    /// <summary>The drop location with parameter tokens substituted.</summary>
    public static string DropLocation(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return Substitute(flow.Source.Location, values);
    }

    /// <summary>The work root the intake writes batches under: the declared <c>source.work</c> (tokens substituted) or the drop's <c>.work</c> folder.</summary>
    public static string WorkLocation(FlowDefinition flow, IReadOnlyDictionary<string, string> values, string dropLocation)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return FlowSource.WorkRoot(flow.Source.Work is null ? null : Substitute(flow.Source.Work, values), dropLocation);
    }

    /// <summary>The flow's declared known-state location with parameter tokens substituted, or null when it declares none.</summary>
    public static string? KnownStateLocation(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Source.KnownState is null ? null : Substitute(flow.Source.KnownState, values);
    }
}
