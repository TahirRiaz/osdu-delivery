using SqlFlow.Core.Model;

namespace SqlFlow.Core;

/// <summary>Base type for all SQLFlow errors.</summary>
public class SqlFlowException : Exception
{
    public SqlFlowException(string message)
        : base(message)
    {
    }

    public SqlFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a pipeline definition is invalid.</summary>
public sealed class FlowValidationException : SqlFlowException
{
    public FlowValidationException(string message)
        : base(message)
    {
    }

    public FlowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a source has no files to read (none matched, or none left after the filters/date
/// window). The engine treats this as a clean no-op for an incremental run and as a failure otherwise.
/// </summary>
public sealed class NoSourceFilesException : SqlFlowException
{
    public NoSourceFilesException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when the target schema diverges from the desired schema under a 'strict' policy.</summary>
public sealed class SchemaDriftException : SqlFlowException
{
    public SchemaDriftException(TableSchema desired, IReadOnlyList<ColumnDefinition> missing)
        : base($"Schema drift on [{desired.Schema}].[{desired.Table}]: target is missing {missing.Count} column(s) " +
               $"({string.Join(", ", missing.Select(c => c.Name))}) and the evolve policy is 'strict'.")
    {
    }
}
