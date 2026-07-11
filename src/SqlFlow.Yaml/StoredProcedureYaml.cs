namespace SqlFlow.Yaml;

// Binding-only DTOs for the stored-procedure document (flowType: sp). Every property is nullable; all defaults
// and validation live in YamlStoredProcedureFlowLoader, which maps these into the immutable
// SqlFlow.Core.StoredProcedures.StoredProcedureFlow.

internal sealed class StoredProcedureYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference, the back-compatible
    /// form) or a map with 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }
    public StoredProcedureTargetYaml? Procedure { get; set; }
    public string? PostInvoke { get; set; }
    public Dictionary<string, InvokeBlockYaml>? Invokes { get; set; }
    public Dictionary<string, ServicePrincipalYaml>? ServicePrincipals { get; set; }
    public bool? OnErrorResume { get; set; }
}

internal sealed class StoredProcedureTargetYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c>; SQL Server when omitted. The procedure executes
    /// with T-SQL, so anything but mssql/azdb is rejected at parse time.</summary>
    public string? Provider { get; set; }

    public string? Object { get; set; }
}
