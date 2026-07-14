using SqlFlow.Core;
using SqlFlow.Core.Copy;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads a file-copy flow (flowType: cpy) from YAML into a validated <see cref="CopyFlow"/>. YamlDotNet handles the
/// grammar; this class maps the parsed document, normalizes the operation enum, and enforces the source/target
/// requirements. Secret material (storage keys, SAS, SFTP passwords/keys) is always a <c>${...}</c> reference, never
/// inline, matching every other flow kind.
/// </summary>
public sealed class YamlCopyFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public CopyFlow LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public CopyFlow Parse(string yaml, string source = "<inline>")
    {
        CopyDocumentYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<CopyDocumentYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static CopyFlow Map(CopyDocumentYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a cpy flow", source);
        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        return new CopyFlow
        {
            Name = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Operation = ParseEnum(y.Operation, CopyOperation.Copy, "operation", source),
            Source = MapEndpoint(sourceYaml, "source", source),
            Target = MapEndpoint(targetYaml, "target", source),
            Options = MapOptions(y.Options),
        };
    }

    private static CopyEndpoint MapEndpoint(CopyEndpointYaml y, string field, string source)
        => new()
        {
            Location = Require(y.Location, $"{field}.location", source),
            Pattern = string.IsNullOrWhiteSpace(y.Pattern) ? "*" : y.Pattern!.Trim(),
            Recursive = y.Recursive ?? true,
            ModifiedWithinDays = y.ModifiedWithinDays ?? 0,
            ConnectionStringRef = YamlDocumentParts.NullIfBlank(y.ConnectionStringRef),
            SasTokenRef = YamlDocumentParts.NullIfBlank(y.SasTokenRef),
            AccountKeyRef = YamlDocumentParts.NullIfBlank(y.AccountKeyRef),
            Username = YamlDocumentParts.NullIfBlank(y.Username),
            PasswordRef = YamlDocumentParts.NullIfBlank(y.PasswordRef),
            PrivateKeyRef = YamlDocumentParts.NullIfBlank(y.PrivateKeyRef),
            PassphraseRef = YamlDocumentParts.NullIfBlank(y.PassphraseRef),
        };

    private static CopyOptions MapOptions(CopyOptionsYaml? y)
    {
        if (y is null)
        {
            return new CopyOptions();
        }

        return new CopyOptions
        {
            Overwrite = y.Overwrite ?? true,
            PreserveStructure = y.PreserveStructure ?? true,
            ZipName = YamlDocumentParts.NullIfBlank(y.ZipName),
        };
    }

    private static string Require(string? value, string field, string source)
        => string.IsNullOrWhiteSpace(value) ? throw new FlowValidationException($"{source}: '{field}' is required.") : value.Trim();

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetNames<TEnum>())
        {
            if (candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(candidate);
            }
        }

        throw new FlowValidationException(
            $"{source}: '{value}' is not a valid value for '{field}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Binding DTOs (mutable, nullable; YamlDotNet only).
// ---------------------------------------------------------------------------------------------------------------

internal sealed class CopyDocumentYaml
{
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Operation { get; set; }
    public CopyEndpointYaml? Source { get; set; }
    public CopyEndpointYaml? Target { get; set; }
    public CopyOptionsYaml? Options { get; set; }
}

internal sealed class CopyEndpointYaml
{
    public string? Location { get; set; }
    public string? Pattern { get; set; }
    public bool? Recursive { get; set; }
    public int? ModifiedWithinDays { get; set; }
    public string? ConnectionStringRef { get; set; }
    public string? SasTokenRef { get; set; }
    public string? AccountKeyRef { get; set; }
    public string? Username { get; set; }
    public string? PasswordRef { get; set; }
    public string? PrivateKeyRef { get; set; }
    public string? PassphraseRef { get; set; }
}

internal sealed class CopyOptionsYaml
{
    public bool? Overwrite { get; set; }
    public bool? PreserveStructure { get; set; }
    public string? ZipName { get; set; }
}
