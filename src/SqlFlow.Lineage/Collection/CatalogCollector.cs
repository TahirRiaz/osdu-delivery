using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Phases one and two of the derived tier, connected: per SQL Server the documents reference, one read-only
/// catalog inventory (objects, synonyms) and one verbatim module harvest (sys.sql_modules), each module's
/// definition parsed through the same operation-wise extractor. This is the SMO replacement: plain set-based
/// reads, one parser instance per module, no shared state, servers processed in parallel. An unreachable
/// server or an encrypted module degrades to a warning, never a failure: the derived tier is an enrichment
/// of the offline graph, not a prerequisite for it.
/// </summary>
public sealed class CatalogCollector
{
    private readonly IConnectionResolver _resolver;

    public CatalogCollector(IConnectionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public async Task<CollectionResult> CollectAsync(
        IReadOnlyDictionary<string, (string RawReference, DataSourceKind Kind)> servers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var sqlServers = servers
            .Where(s => s.Value.Kind is DataSourceKind.MSSQL or DataSourceKind.AZDB)
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var merged = new CollectionResult();

        // Identity proof pass: two references resolving to the same canonical connection string ARE the
        // same server. The ordinal-smallest identity represents the group; the rest alias to it, so the
        // graph stops splitting one physical estate across reference spellings.
        var representatives = new List<(string ServerRef, string RawReference)>();
        var byCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (serverRef, value) in sqlServers)
        {
            string canonical;
            try
            {
                canonical = (await _resolver.ResolveAsync(value.RawReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false)).CanonicalString;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                merged.Warnings.Add($"server '{serverRef}': derived lineage unavailable ({Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)}); the offline tiers still apply.");
                continue;
            }

            if (byCanonical.TryGetValue(canonical, out var representative))
            {
                merged.ServerAliases[serverRef] = representative;
            }
            else
            {
                byCanonical[canonical] = serverRef;
                representatives.Add((serverRef, value.RawReference));
            }
        }

        var results = await Task.WhenAll(representatives.Select(server => CollectServerAsync(server.ServerRef, server.RawReference, ct)))
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            merged.Merge(result);
        }

        foreach (var skipped in servers.Where(s => s.Value.Kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB)))
        {
            if (skipped.Key != ServerIdentity.FileSystem)
            {
                merged.Warnings.Add(
                    $"server '{skipped.Key}' is {skipped.Value.Kind}; module-level lineage derivation covers SQL Server only.");
            }
        }

        return merged;
    }

    private async Task<CollectionResult> CollectServerAsync(string serverRef, string rawReference, CancellationToken ct)
    {
        var result = new CollectionResult();
        try
        {
            var resolved = await _resolver.ResolveAsync(rawReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false);

            await using var connection = new SqlConnection(resolved.CanonicalString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var database = (string?)await Scalar(connection, "SELECT DB_NAME();", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the connection has no default database");

            // The connected default catalog is node-identity ground truth: the builder completes this
            // server's two-part identities (database-less facts) against it.
            result.ServerDefaultDatabases[serverRef] = database;

            await InventoryAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            await SynonymsAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            await ModulesAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Warnings.Add($"server '{serverRef}': derived lineage unavailable ({Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)}); the offline tiers still apply.");
        }

        return result;
    }

    private static async Task InventoryAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        var columnsByObject = await ColumnsAsync(connection, ct).ConfigureAwait(false);

        const string sql = """
            SELECT s.name, o.name, o.type
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','FN','IF','TF','TR') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = reader.GetString(2).TrimEnd() switch
                {
                    "U" => LineageNodeKind.Table,
                    "V" => LineageNodeKind.View,
                    "P" => LineageNodeKind.Procedure,
                    "FN" or "IF" or "TF" => LineageNodeKind.Function,
                    "TR" => LineageNodeKind.Trigger,
                    _ => LineageNodeKind.Unknown,
                },
                Columns = columnsByObject.TryGetValue(schema + "|" + name, out var columns) ? columns : [],
            });
        }
    }

    /// <summary>The columns of every table/view/table-valued function, keyed by <c>schema|name</c>
    /// (case-insensitive, matching SQL Server's default object-name collation), so the inventory can attach a
    /// data dictionary to each object for cross-repo column search.</summary>
    private static async Task<Dictionary<string, List<LineageColumn>>> ColumnsAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, o.name, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V','IF','TF') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id;
            """;

        var map = new Dictionary<string, List<LineageColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0) + "|" + reader.GetString(1);
            if (!map.TryGetValue(key, out var columns))
            {
                map[key] = columns = [];
            }

            columns.Add(new LineageColumn
            {
                Ordinal = reader.GetInt32(2),
                Name = reader.GetString(3),
                DataType = RenderType(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                Nullable = reader.GetBoolean(8),
            });
        }

        return map;
    }

    /// <summary>Renders a SQL Server type with its length/precision the way it reads in DDL (best-effort, for
    /// display and search): <c>nvarchar(100)</c>, <c>nvarchar(max)</c>, <c>decimal(18,2)</c>, <c>datetime2(7)</c>.</summary>
    private static string RenderType(string typeName, short maxLength, byte precision, byte scale)
        => typeName switch
        {
            "nvarchar" or "nchar" => $"{typeName}({Length(maxLength == -1 ? -1 : maxLength / 2)})",
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({Length(maxLength)})",
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision},{scale})"),
            "datetime2" or "datetimeoffset" or "time" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})"),
            _ => typeName,
        };

    private static string Length(int maxLength) => maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture);

    private static async Task SynonymsAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        // PARSENAME splits the base name server-side; a four-part (linked server) base comes back with a
        // server part and is surfaced as unresolvable rather than guessed at.
        const string sql = """
            SELECT s.name, sy.name,
                   PARSENAME(sy.base_object_name, 1), PARSENAME(sy.base_object_name, 2),
                   PARSENAME(sy.base_object_name, 3), PARSENAME(sy.base_object_name, 4)
            FROM sys.synonyms sy
            JOIN sys.schemas s ON s.schema_id = sy.schema_id
            ORDER BY s.name, sy.name;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var baseName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var linkedServer = reader.IsDBNull(5) ? null : reader.GetString(5);

            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = LineageNodeKind.Synonym,
                Warning = linkedServer is null
                    ? null
                    : $"synonym base lives on linked server '{linkedServer}'; not resolvable from here.",
            });

            if (baseName is not null && linkedServer is null)
            {
                result.Synonyms.Add(new SynonymLink
                {
                    ServerRef = serverRef,
                    Database = database,
                    Schema = schema,
                    Name = name,
                    TargetDatabase = reader.IsDBNull(4) ? database : reader.GetString(4),
                    TargetSchema = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TargetName = baseName,
                });
            }
        }
    }

    private static async Task ModulesAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, o.name, m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        var modules = new List<(string Schema, string Name, string? Definition)>();
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                modules.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var (schema, name, definition) in modules)
        {
            ct.ThrowIfCancellationRequested();
            var moduleKey = NodeKey.For(serverRef, database, schema, name);
            var label = $"{serverRef}:{database}.{schema}.{name}";

            if (definition is null)
            {
                // WITH ENCRYPTION: the stored text is unreadable by design; the gap is declared, not hidden.
                result.CatalogObjects.Add(new CatalogObject
                {
                    ServerRef = serverRef,
                    Database = database,
                    Schema = schema,
                    Name = name,
                    Kind = LineageNodeKind.Unknown,
                    Warning = "module is encrypted (WITH ENCRYPTION); its definition cannot be read, so its lineage is unknown.",
                });
                continue;
            }

            // Retain the module body so the catalog is searchable code (the kind is merged in from the inventory
            // entry; this entry contributes only the definition).
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = LineageNodeKind.Unknown,
                Definition = definition,
            });

            var deps = Extraction.TSqlLineageExtractor.Extract(definition, label, defaultDatabase: database);
            result.Warnings.AddRange(deps.Warnings);

            foreach (var fact in ScriptFactBuilder.Facts(
                         deps, flow: null, viaModuleKey: moduleKey, serverRef, LineageTier.Derived, minimumParts: 1))
            {
                // The module's own CREATE statement points at itself; self-facts carry nothing.
                if (NodeKey.For(serverRef, fact.Database ?? database, fact.Schema, fact.Name) != moduleKey)
                {
                    result.Facts.Add(fact with { Database = fact.Database ?? database });
                }
            }
        }
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
