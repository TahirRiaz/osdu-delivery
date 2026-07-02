using System.Security.Cryptography;
using System.Text;
using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Collection;

/// <summary>One collected lineage fact, before graph assembly: a flow or module relating to an object,
/// with provenance. The collectors produce these; the builder merges them.</summary>
public sealed record LineageFact
{
    public string? Flow { get; init; }

    /// <summary>The module node key whose definition produced this fact (derived tier).</summary>
    public string? ViaModuleKey { get; init; }

    public required LineageRelation Relation { get; init; }

    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required LineageTier Tier { get; init; }

    public LineageNodeKind KindHint { get; init; } = LineageNodeKind.Unknown;

    public Guid? RunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    public string? Step { get; init; }
}

/// <summary>One flow document as collected: its node plus what the other collectors need to attribute and
/// cross-check facts (server identities per side, the document's own write time for staleness).</summary>
public sealed record CollectedFlow
{
    public required LineageFlowNode Node { get; init; }

    /// <summary>The server identity statements run against by default (the target side).</summary>
    public required string TargetServerRef { get; init; }

    /// <summary>The source-side server identity, when the kind has one (ing/exp).</summary>
    public string? SourceServerRef { get; init; }

    /// <summary>The flow's declared schedule from its <c>schedule:</c> block, or null when it declares none; carried
    /// from the document so the catalog sync can mirror git schedules into the schedule table.</summary>
    public SqlFlow.Core.ScheduleSpec? Schedule { get; init; }

    public required DateTime FileWriteUtc { get; init; }
}

/// <summary>One inventoried catalog object (derived tier): the node-kind ground truth.</summary>
public sealed record CatalogObject
{
    public required string ServerRef { get; init; }

    public required string Database { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required LineageNodeKind Kind { get; init; }

    /// <summary>The module body (sys.sql_modules definition) for a view/procedure/function/trigger; null for a
    /// plain table or an encrypted module.</summary>
    public string? Definition { get; init; }

    /// <summary>The object's columns (tables/views/table-valued functions); empty otherwise.</summary>
    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>A node-scoped finding (an encrypted module whose definition is unreadable).</summary>
    public string? Warning { get; init; }
}

/// <summary>One synonym and what it points at (within reach of PARSENAME; a linked-server base keeps its
/// server part as an unresolvable warning).</summary>
public sealed record SynonymLink
{
    public required string ServerRef { get; init; }

    public required string Database { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public string? TargetDatabase { get; init; }

    public string? TargetSchema { get; init; }

    public required string TargetName { get; init; }
}

/// <summary>What one collector hands the builder.</summary>
public sealed class CollectionResult
{
    public List<CollectedFlow> Flows { get; } = [];

    public List<LineageFact> Facts { get; } = [];

    /// <summary>Every server identity any document referenced, with its raw reference and provider kind
    /// (the derived tier connects to the SQL Server ones).</summary>
    public Dictionary<string, (string RawReference, Core.Connections.DataSourceKind Kind)> Servers { get; }
        = new(StringComparer.Ordinal);

    public List<CatalogObject> CatalogObjects { get; } = [];

    public List<SynonymLink> Synonyms { get; } = [];

    /// <summary>Server identities proven equal at connect time (two references resolving to the same
    /// canonical connection string): alias identity to canonical identity. Offline, distinct references
    /// stay distinct servers by construction; connected, the proof merges them.</summary>
    public Dictionary<string, string> ServerAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>The default database of each server identity's connection, when known: DB_NAME() recorded by
    /// the connected tier, or the reference's Initial Catalog resolved offline. The builder completes a
    /// database-less fact (a two-part reference) against this map, mirroring how the engine itself resolves
    /// such a name at execution time; engines without a database concept never appear here, so their
    /// identities keep the empty segment by design.</summary>
    public Dictionary<string, string> ServerDefaultDatabases { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    /// <summary>Folds another collector's result into this one.</summary>
    public void Merge(CollectionResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Flows.AddRange(other.Flows);
        Facts.AddRange(other.Facts);
        CatalogObjects.AddRange(other.CatalogObjects);
        Synonyms.AddRange(other.Synonyms);
        Warnings.AddRange(other.Warnings);
        foreach (var (key, value) in other.Servers)
        {
            Servers.TryAdd(key, value);
        }

        foreach (var (alias, canonical) in other.ServerAliases)
        {
            ServerAliases.TryAdd(alias, canonical);
        }

        foreach (var (serverRef, database) in other.ServerDefaultDatabases)
        {
            ServerDefaultDatabases.TryAdd(serverRef, database);
        }
    }
}

/// <summary>THE node-identity rule: server reference, database, schema, and name, case-folded, joined with
/// '|' (absent parts empty). One rule for collectors, builder, and queries; nothing else may restate it.</summary>
public static class NodeKey
{
    public static string For(string serverRef, string? database, string? schema, string name)
        => string.Join('|',
            serverRef.ToLowerInvariant(),
            database?.ToLowerInvariant() ?? string.Empty,
            schema?.ToLowerInvariant() ?? string.Empty,
            name.ToLowerInvariant());
}

/// <summary>
/// The server side of node identity: the connection REFERENCE a document used, normalized. Equal references
/// are the same server by construction (the canonical contract makes references shared names:
/// ${env:SQLFLOW_CONN_DWH} in two documents is one server). An inline literal is identified by a content
/// hash, never by its text: a connection string can carry credentials and must not reach a report.
/// </summary>
public static class ServerIdentity
{
    /// <summary>The identity of file endpoints (a file flow's source, an export destination).</summary>
    public const string FileSystem = "file";

    public static string From(string connectionReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionReference);

        var trimmed = connectionReference.Trim();

        // Pass-through ONLY for a value that IS a reference in its entirety: one ${...} token, or one
        // @alias token. A hybrid like "${env:HOST};Password=..." is a literal carrying a secret and must
        // hash like any other literal, never echo.
        var isWholeReference =
            (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
             && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1)
            || (trimmed.StartsWith('@') && !trimmed.Any(char.IsWhiteSpace) && !trimmed.Contains(';', StringComparison.Ordinal)
                && !trimmed.Contains('=', StringComparison.Ordinal));
        if (isWholeReference)
        {
            return trimmed;
        }

        // An inline literal: hash-identified so equal strings still match without ever echoing content.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.ToLowerInvariant()));
        return "inline:" + Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}
