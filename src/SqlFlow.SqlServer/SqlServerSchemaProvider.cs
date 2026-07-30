using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>Introspects and evolves the target schema using the SQL Server catalog and DDL.</summary>
public sealed class SqlServerSchemaProvider : ISchemaProvider
{
    public async Task<TableSchema?> GetTableSchemaAsync(string connectionString, string schema, string table, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // INFORMATION_SCHEMA reads take metadata locks and queue behind any uncommitted DDL in the database,
        // so a wide batch fire pushes them past ADO.NET's 30 second default. The server bounds this wait.
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);

        var columns = new List<ColumnDefinition>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            columns.Add(new ColumnDefinition
            {
                Name = reader.GetString(0),
                SqlType = BuildSqlType(reader),
                IsNullable = string.Equals(reader.GetString(5), "YES", StringComparison.OrdinalIgnoreCase),
            });
        }

        return columns.Count == 0
            ? null
            : new TableSchema { Schema = schema, Table = table, Columns = columns };
    }

    public async Task ExecuteDdlAsync(string connectionString, IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var statement in statements)
            {
                await using var command = new SqlCommand(statement, connection, transaction) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Applies a classified DDL batch to one target object under an object-scoped, case-canonical session
    /// app lock. Metadata-only statements commit in one short READ COMMITTED transaction; each table-rewrite
    /// statement commits in its own. SET LOCK_TIMEOUT makes a blocked DDL fail fast (SQL 1222) instead of
    /// queuing in front of readers. The app lock and the schema-modification lock are released before this
    /// returns, well before any bulk load, so a schema change on one table never blocks pipelines on another.
    /// </summary>
    public static async Task ApplyDdlAsync(string connectionString, DdlBatch batch, SchemaApplyOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        if (!batch.HasChanges)
        {
            return;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ApplyDdlAsync(connection, batch, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Same protocol over a caller-supplied open connection, so a caller that has already opened one for
    /// introspection (see <c>SchemaSyncService.EvolveAsync</c>) does not pay a second open. The session's
    /// LOCK_TIMEOUT is restored to the default before returning, so the connection goes back to its owner
    /// (or the pool) without a surprise timeout baked into the session.
    /// </summary>
    public static async Task ApplyDdlAsync(SqlConnection connection, DdlBatch batch, SchemaApplyOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        if (!batch.HasChanges)
        {
            return;
        }

        var resource = SchemaLockResource(batch.Schema, batch.Table);

        // Governs the Sch-M waits of the DDL itself: a blocked ALTER aborts with 1222 instead of queuing.
        await SetSessionLockTimeoutAsync(connection, options.DdlLockTimeoutMs, ct).ConfigureAwait(false);

        // Object-scoped, case-canonical app lock: table A and table B get distinct resources, never contend.
        await AcquireAppLockOrThrowAsync(connection, resource, options.AppLockTimeoutMs, ct).ConfigureAwait(false);
        try
        {
            var metadataOnly = batch.Statements.Where(s => s.Cost == DdlCost.MetadataOnly).ToList();
            if (metadataOnly.Count > 0)
            {
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
                try
                {
                    foreach (var statement in metadataOnly)
                    {
                        await ExecuteStatementAsync(connection, tx, statement, resource, ct).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            // Each rewrite commits alone, so a rewrite failure never rolls back the additive work and a long
            // rewrite Sch-M lock is held by itself, briefly.
            foreach (var statement in batch.Statements.Where(s => s.Cost == DdlCost.Rewrite))
            {
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
                try
                {
                    await ExecuteStatementAsync(connection, tx, statement, resource, ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            await ReleaseAppLockAsync(connection, resource).ConfigureAwait(false);
            await RestoreDefaultLockTimeoutAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task RestoreDefaultLockTimeoutAsync(SqlConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new SqlCommand("SET LOCK_TIMEOUT -1;", connection) { CommandTimeout = 0 };
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Best-effort session hygiene: a shared connection is disposed or pool-reset by its owner anyway,
            // and a restore failure must not mask the original DDL exception.
        }
    }

    /// <summary>The case-canonical, collation-independent object lock resource. Two different objects yield
    /// two different resources, so a schema change on one table can never contend with another.</summary>
    internal static string SchemaLockResource(string schema, string table)
        => "SqlFlow.Schema:" + schema.ToLowerInvariant() + "." + table.ToLowerInvariant();

    private static async Task SetSessionLockTimeoutAsync(SqlConnection connection, int lockTimeoutMs, CancellationToken ct)
    {
        // lockTimeoutMs is an int, so direct interpolation is injection-safe.
        await using var command = new SqlCommand($"SET LOCK_TIMEOUT {lockTimeoutMs};", connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AcquireAppLockOrThrowAsync(SqlConnection connection, string resource, int timeoutMs, CancellationToken ct)
    {
        // The wait is bounded server-side by @LockTimeout, which is the whole point of this call: on expiry
        // sp_getapplock returns -1 and the caller gets a typed, retryable SchemaLockTimeoutException. A client
        // CommandTimeout must never be the shorter of the two, or it aborts the round trip first and the
        // graceful path becomes unreachable. ADO.NET's 30 second default is exactly AppLockTimeoutMs's default,
        // so the client always won that race; 0 hands the bound back to @LockTimeout where it belongs.
        await using var command = new SqlCommand("sys.sp_getapplock", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", timeoutMs);
        var returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var code = returnValue.Value is int value ? value : -999;
        if (code < 0)
        {
            // -1 timeout, -2 cancelled, -3 deadlock, -999 validation/parameter error.
            throw new SchemaLockTimeoutException(resource, code);
        }
    }

    private static async Task ExecuteStatementAsync(SqlConnection connection, SqlTransaction transaction, DdlStatement statement, string resource, CancellationToken ct)
    {
        await using var command = new SqlCommand(statement.Text, connection, transaction) { CommandTimeout = 0 };
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 2714 && statement.IsCreateTable)
        {
            // Another run created the table first: a benign create race, safe to ignore.
        }
        catch (SqlException ex) when (ex.Number is 1222 or 1204 or 1205)
        {
            // 1222 lock-request timeout (the polite fail-fast), 1204 lock resources, 1205 deadlock victim.
            throw new SchemaLockTimeoutException(resource, null, ex);
        }
    }

    private static async Task ReleaseAppLockAsync(SqlConnection connection, string resource)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new SqlCommand("sys.sp_releaseapplock", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 0 };
            command.Parameters.AddWithValue("@Resource", resource);
            command.Parameters.AddWithValue("@LockOwner", "Session");
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // The connection close that follows frees the session lock anyway; a release failure must not
            // mask the original DDL exception.
        }
    }

    private static string BuildSqlType(SqlDataReader reader)
    {
        var dataType = reader.GetString(1).ToUpperInvariant();
        switch (dataType)
        {
            case "NVARCHAR":
            case "VARCHAR":
            case "NCHAR":
            case "CHAR":
            case "VARBINARY":
            case "BINARY":
                if (reader.IsDBNull(2))
                {
                    return dataType;
                }

                var length = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                return length == -1 ? $"{dataType}(MAX)" : $"{dataType}({length.ToString(CultureInfo.InvariantCulture)})";

            case "DECIMAL":
            case "NUMERIC":
                var precision = reader.IsDBNull(3) ? 18 : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                var scale = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                return $"{dataType}({precision}, {scale})";

            default:
                return dataType;
        }
    }
}
