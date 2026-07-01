using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>
/// Computes the incremental watermark with a dynamic query against the target. For the file-date watermark it
/// resolves the column's type from the catalog and returns <c>DATEADD(DAY, -overlap, MAX([col]))</c>. For the
/// row-level watermark it resolves the column's type to a <see cref="WatermarkKind"/>, returns <c>MAX([col])</c>
/// shifted back by the overlap, and lets the engine compare source rows against it. A missing table or empty
/// result yields null (full load). No SMO; a fresh connection per call.
/// </summary>
public sealed class SqlServerIncrementalProbe : IIncrementalProbe
{
    public async Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(dateColumn);
        if (overlapDays < 0)
        {
            throw new SqlFlowException($"Incremental overlapDays must be zero or positive, was {overlapDays}.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Missing target table -> nothing has been loaded yet -> full load.
        if (await ObjectIdAsync(connection, table, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var typeName = await ColumnTypeNameAsync(connection, table, dateColumn, ct).ConfigureAwait(false);
        if (typeName is null)
        {
            throw new SqlFlowException($"Incremental dateColumn '{dateColumn}' was not found on {table}.");
        }

        if (!IsDateTime(typeName))
        {
            throw new SqlFlowException(
                $"Incremental dateColumn '{dateColumn}' on {table} is not a date/time type; the file-date watermark requires one.");
        }

        var escapedColumn = $"[{dateColumn.Replace("]", "]]", StringComparison.Ordinal)}]";
        var sql = $"SELECT DATEADD(DAY, -@overlap, MAX({escapedColumn})) FROM {table};";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@overlap", overlapDays);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return value is null or DBNull ? null : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    public async Task<WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (overlap < 0)
        {
            throw new SqlFlowException($"Incremental watermark overlap must be zero or positive, was {overlap}.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Missing target table -> nothing has been loaded yet -> full load.
        if (await ObjectIdAsync(connection, table, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var typeName = await ColumnTypeNameAsync(connection, table, column, ct).ConfigureAwait(false);
        if (typeName is null)
        {
            throw new SqlFlowException($"Incremental watermarkColumn '{column}' was not found on {table}.");
        }

        var kind = WatermarkKindOf(typeName)
            ?? throw new SqlFlowException(
                $"Incremental watermarkColumn '{column}' on {table} is type '{typeName}', which is not orderable as a watermark. Use an integer, decimal, float, date/time, or text column.");

        var escapedColumn = $"[{column.Replace("]", "]]", StringComparison.Ordinal)}]";
        await using var command = new SqlCommand($"SELECT MAX({escapedColumn}) FROM {table};", connection);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return null;
        }

        return new WatermarkValue { Kind = kind, Value = ApplyOverlap(kind, value, overlap) };
    }

    private static object ApplyOverlap(WatermarkKind kind, object value, double overlap) => kind switch
    {
        WatermarkKind.Whole => Convert.ToInt64(value, CultureInfo.InvariantCulture) - (long)overlap,
        WatermarkKind.Fixed => Convert.ToDecimal(value, CultureInfo.InvariantCulture) - (decimal)overlap,
        WatermarkKind.Floating => Convert.ToDouble(value, CultureInfo.InvariantCulture) - overlap,
        WatermarkKind.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture).AddDays(-overlap),
        WatermarkKind.DateTimeOffset => ((DateTimeOffset)value).AddDays(-overlap),
        WatermarkKind.Text => (string)value,
        _ => throw new SqlFlowException($"Unsupported watermark kind '{kind}'."),
    };

    private static bool IsDateTime(string typeName)
        => typeName is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset";

    private static WatermarkKind? WatermarkKindOf(string typeName) => typeName switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => WatermarkKind.Whole,
        "decimal" or "numeric" or "money" or "smallmoney" => WatermarkKind.Fixed,
        "real" or "float" => WatermarkKind.Floating,
        "date" or "datetime" or "datetime2" or "smalldatetime" => WatermarkKind.DateTime,
        "datetimeoffset" => WatermarkKind.DateTimeOffset,
        "char" or "varchar" or "nchar" or "nvarchar" => WatermarkKind.Text,
        _ => null,
    };

    private static async Task<int?> ObjectIdAsync(SqlConnection connection, string table, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT OBJECT_ID(@t, 'U');", connection);
        command.Parameters.AddWithValue("@t", table);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : (int)result;
    }

    private static async Task<string?> ColumnTypeNameAsync(SqlConnection connection, string table, string column, CancellationToken ct)
    {
        const string sql = """
            SELECT t.name
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@t, 'U') AND c.name = @c;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@t", table);
        command.Parameters.AddWithValue("@c", column);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }
}
