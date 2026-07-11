using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.StoredProcedures;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed stored-procedure document (flowType: sp): the flow itself plus the document-local connection
/// registry it declares. The connections become an in-memory data-source store, so the flow runs through the
/// exact same resolver and runner as full mode, with no control database anywhere.
/// </summary>
public sealed record StoredProcedureDocument
{
    public required StoredProcedureFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on the procedure endpoint).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }

    /// <summary>The document's named invokes (the <c>invokes:</c> block), referenced by postInvoke.</summary>
    public IReadOnlyList<Core.Invoke.InvokeDefinition> Invokes { get; init; } = [];

    /// <summary>The document's named Azure service principals (the <c>servicePrincipals:</c> block).</summary>
    public IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; } = [];
}

/// <summary>
/// Loads a stored-procedure flow (one existing procedure executed on a resolved SQL Server, no parameters
/// bound, the legacy contract) from YAML. YamlDotNet handles the grammar; this class is the mapping/validation
/// layer that turns the parsed document into a validated <see cref="StoredProcedureDocument"/>.
/// </summary>
public sealed class YamlStoredProcedureFlowLoader
{
    private const string TargetConnectionName = "target";

    // The unquoted-scalar option types the invoke parameters block (a plain true/42 stays a boolean/number,
    // a quoted scalar a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public StoredProcedureDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public StoredProcedureDocument Parse(string yaml, string source = "<inline>")
    {
        StoredProcedureYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<StoredProcedureYaml>(yaml);
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

    private static StoredProcedureDocument Map(StoredProcedureYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a stored-procedure flow", source);
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var procedureYaml = y.Procedure ?? throw new FlowValidationException($"{source}: 'procedure' is required.");

        var server = YamlDocumentParts.ResolveEndpointConnection(
            procedureYaml.Server, procedureYaml.Connection, procedureYaml.Provider,
            "procedure", TargetConnectionName, connections, source);

        // The procedure executes with T-SQL on the resolved server, so it is SQL Server by design; a foreign
        // provider is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "procedure", "a stored-procedure flow's server", source);

        var raw = YamlDocumentParts.NullIfBlank(procedureYaml.Object)
            ?? throw new FlowValidationException(
                $"{source}: 'procedure.object' is required (a three-part name like Database.Schema.Procedure).");

        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);
        var invokes = YamlInvokeParts.MapInvokes(y.Invokes, servicePrincipals, source);

        var flow = new StoredProcedureFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Procedure = YamlDocumentParts.ParseQualifiedObject(raw, "procedure.object", source),
            OnErrorResume = y.OnErrorResume ?? true,
            PostInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PostInvoke, "postInvoke", invokes, source),
            Description = YamlDocumentParts.NullIfBlank(y.Description),
        };

        return new StoredProcedureDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
            Invokes = invokes.Values.ToList(),
            ServicePrincipals = servicePrincipals.Values.ToList(),
        };
    }
}
