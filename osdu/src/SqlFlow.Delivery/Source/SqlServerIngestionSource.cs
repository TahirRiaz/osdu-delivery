using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Source;

/// <summary>Opens a flow's ingestion tables on SQL Server or Azure SQL, with the flow's own connection reference.</summary>
public sealed class SqlServerIngestionSourceFactory : IIngestionSourceFactory
{
    private readonly ISecretResolver _secrets;
    private readonly ILoggerFactory _loggers;

    public SqlServerIngestionSourceFactory(ISecretResolver secrets, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggers);
        _secrets = secrets;
        _loggers = loggers;
    }

    public IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        return new SqlServerIngestionSource(flow, values, _secrets, _loggers.CreateLogger<SqlServerIngestionSource>());
    }
}

/// <summary>
/// Reads a flow's records out of the ingestion tables SQLFlow loads (docs/stage4-design.md sections 2.2 and 2.3). A read
/// fixes its window at open, pages candidate record keys in key order, and then reads one page's rows as one command: the
/// record rows and one result set per child dataset, under snapshot isolation by default so a record and its child rows
/// always describe the same moment.
/// </summary>
public sealed class SqlServerIngestionSource : IIngestionSource
{
    private const int DeadlockError = 1205;
    private const int SnapshotNotAllowed = 3952;
    private const int SnapshotDisabled = 3951;
    private const int MaxDeadlockRetries = 3;

    /// <summary>The longest origin file name the ledger stores for a record.</summary>
    private const int MaxFileNameLength = 800;

    private readonly FlowDefinition _flow;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<SqlServerIngestionSource> _logger;
    private readonly Dictionary<string, IReadOnlyDictionary<string, SourceColumn>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private IngestionLayout? _layout;
    private string? _fileNameColumn;
    private string? _rowNumberColumn;

    public SqlServerIngestionSource(FlowDefinition flow, IReadOnlyDictionary<string, string> values, ISecretResolver secrets, ILogger<SqlServerIngestionSource> logger)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _flow = flow;
        _values = values;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<SourceHeader> OpenAsync(SourceSelection selection, SourceWindow? stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var layout = await BuildLayoutAsync(connection, ct).ConfigureAwait(false);
        var upper = stored?.UpperUtc ?? await NowAsync(connection, ct).ConfigureAwait(false);
        var lower = stored is not null
            ? stored.LowerUtc
            : selection.Kind == SourceSelectionKind.Incremental ? selection.LowerUtc : null;
        var window = new SourceWindow(lower, upper);
        var columns = _tables.ToDictionary(t => t.Key, t => (IReadOnlySet<string>)t.Value.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<KeyTuple> missing = [];
        IReadOnlyList<KeyTuple> outOfScope = [];
        long candidates;
        if (selection.Kind == SourceSelectionKind.Keys)
        {
            var probe = await ProbeKeysAsync(connection, layout, selection.Keys, ct).ConfigureAwait(false);
            missing = probe.Missing;
            outOfScope = probe.OutOfScope;
            candidates = probe.InScope;
        }
        else
        {
            candidates = await CountAsync(connection, layout, selection, window, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Source {Object} opened for {Selection}: window up to {Upper:O}, {Candidates} candidate record(s).",
            layout.Record.ToString(), selection.Describe(), window.UpperUtc, candidates);

        return new SourceHeader
        {
            Selection = selection,
            Window = window,
            Columns = columns,
            KeyColumns = layout.Key,
            EstimatedCandidates = candidates,
            HasChanges = candidates > 0,
            MissingKeys = missing,
            OutOfScopeKeys = outOfScope,
        };
    }

    public async Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(SourceHeader header, int slices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
        var layout = Layout();
        if (slices == 1 || header.EstimatedCandidates <= 1)
        {
            return [new KeyRange(0, null, null)];
        }

        var size = (long)Math.Ceiling(header.EstimatedCandidates / (double)slices);
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        await using var command = Command(connection, IngestionSql.SliceBounds(layout, header.Selection.Kind, header.Window.LowerUtc is not null));
        Bind(command, layout, header, IngestionSql.KeyBounds.None, null, null);
        command.Parameters.Add(new SqlParameter(IngestionSql.SliceSizeParameter, SqlDbType.BigInt) { Value = size });

        var bounds = new List<KeyTuple>();
        await using (var reader = await ExecuteReaderAsync(command, "read the slice bounds", ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                bounds.Add(ReadKey(reader, layout));
            }
        }

        // The last bound would end where the key space does, leaving an empty slice behind it.
        while (bounds.Count > slices - 1)
        {
            bounds.RemoveAt(bounds.Count - 1);
        }

        var ranges = new List<KeyRange>(bounds.Count + 1);
        KeyTuple? from = null;
        for (var i = 0; i < bounds.Count; i++)
        {
            ranges.Add(new KeyRange(i, from, bounds[i]));
            from = bounds[i];
        }

        ranges.Add(new KeyRange(bounds.Count, from, null));
        return ranges;
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceHeader header, KeyRange? range, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        var layout = Layout();
        var pageSize = _flow.Source.Incremental.PageSize;
        await using var connection = await IngestionConnection.OpenAsync(_flow.Source.Connection, _flow.Name, _secrets, ct).ConfigureAwait(false);
        var after = range?.From;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await CandidatePageAsync(connection, layout, header, after, range, pageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (var record in await PageRecordsAsync(connection, layout, page, ct).ConfigureAwait(false))
            {
                yield return record;
            }

            if (page.Count < pageSize)
            {
                yield break;
            }

            after = page[^1];
        }
    }

    private IngestionLayout Layout()
        => _layout ?? throw new DeliveryException($"Flow '{_flow.Name}': the source has to be opened before it is read.");

    private SqlCommand Command(SqlConnection connection, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = _flow.Source.Incremental.CommandTimeoutSeconds;
        return command;
    }

    private async Task<DateTime> NowAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.UpperBound());
        var value = await ExecuteScalarAsync(command, "read the source database's clock", ct).ConfigureAwait(false);
        return value is DateTime moment
            ? DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            : throw new DeliveryException($"Flow '{_flow.Name}': the source database did not answer with a moment for the read's upper bound.");
    }

    private async Task<IngestionLayout> BuildLayoutAsync(SqlConnection connection, CancellationToken ct)
    {
        var source = _flow.Source;
        var where = _flow.SourcePath ?? _flow.Name;
        var recordName = SourceObjectName.Parse(source.Record.Object);
        var record = await ReadColumnsAsync(connection, recordName, "source.record.object", ct).ConfigureAwait(false);
        _tables[SourceDatasets.Record] = record;

        var key = new List<SourceKeyColumn>(source.Record.Key.Count);

        // The key columns whose declaration allows a null. SQLFlow's ingestion creates a target's data columns nullable
        // and never tightens them, so requiring the declaration to say NOT NULL would refuse every table its own flows
        // build. What a delivery actually needs is that no record it would deliver carries an unknown key, which is
        // checked against the rows below once the layout is known.
        var nullableKeys = new List<string>();
        foreach (var column in source.Record.Key)
        {
            var declared = Column(record, recordName, column, $"{where}: source.record.key names column '{column}'");
            if (declared.Nullable)
            {
                nullableKeys.Add(declared.Name);
            }

            if (!declared.Comparable)
            {
                throw new FlowValidationException(
                    $"{where}: source.record.key names column '{column}' of {recordName}, which is {declared.SqlType}. A key column has to be a comparable type (text with a length, a number, a date, a uniqueidentifier).");
            }

            key.Add(new SourceKeyColumn(column, declared.SqlType));
        }

        var updated = Column(record, recordName, source.SystemColumns.Updated, $"{where}: source.systemColumns.updated names column '{source.SystemColumns.Updated}'");
        if (!updated.IsMoment)
        {
            throw new FlowValidationException(
                $"{where}: source.systemColumns.updated names column '{source.SystemColumns.Updated}' of {recordName}, which is {updated.SqlType}. An incremental read windows on a date and time column (SQLFlow's UpdatedDate_DW).");
        }

        var deleted = Optional(record, source.SystemColumns.Deleted);
        if (source.SystemColumns.DeletedDeclared && source.SystemColumns.Deleted is { } declaredDeleted && deleted is null)
        {
            throw new FlowValidationException($"{where}: source.systemColumns.deleted names column '{declaredDeleted}', which the record table {recordName} does not hold.");
        }

        _fileNameColumn = Optional(record, source.SystemColumns.FileName)?.Name;
        if (_fileNameColumn is not null && record[_fileNameColumn].MaxCharacters > MaxFileNameLength)
        {
            throw new FlowValidationException(
                $"{where}: source.systemColumns.fileName names column '{_fileNameColumn}' of {recordName}, which holds up to {record[_fileNameColumn].MaxCharacters.ToString(CultureInfo.InvariantCulture)} characters. "
                + $"The ledger stores a record's origin file in {MaxFileNameLength.ToString(CultureInfo.InvariantCulture)} characters; land the files under a shorter root, or opt out with source.systemColumns.fileName: ~.");
        }

        _rowNumberColumn = Optional(record, source.SystemColumns.RowNumber)?.Name;

        var datasets = new List<IngestionDataset>(source.Datasets.Count);
        foreach (var (name, dataset) in source.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            var objectName = SourceObjectName.Parse(dataset.Object);
            var columns = await ReadColumnsAsync(connection, objectName, $"source.datasets.{name}.object", ct).ConfigureAwait(false);
            _tables[name] = columns;
            var join = new List<KeyValuePair<string, string>>(dataset.Join.Count);
            foreach (var recordColumn in source.Record.Key)
            {
                var childColumn = dataset.Join.FirstOrDefault(j => j.Value.Equals(recordColumn, StringComparison.OrdinalIgnoreCase)).Key
                    ?? throw new FlowValidationException($"{where}: source.datasets.{name}.join does not cover key column '{recordColumn}' of the record table.");
                Column(columns, objectName, childColumn, $"{where}: source.datasets.{name}.join names child column '{childColumn}'");
                join.Add(new KeyValuePair<string, string>(childColumn, recordColumn));
            }

            foreach (var order in dataset.OrderBy)
            {
                Column(columns, objectName, order, $"{where}: source.datasets.{name}.orderBy names column '{order}'");
            }

            datasets.Add(new IngestionDataset(
                name,
                objectName,
                join,
                dataset.OrderBy,
                Optional(columns, source.SystemColumns.Updated)?.Name,
                Optional(columns, source.SystemColumns.Deleted)?.Name,
                dataset.MaxRowsPerRecord));
        }

        _layout = new IngestionLayout
        {
            Record = recordName,
            Key = key,
            Updated = updated.Name,
            Deleted = deleted?.Name,
            Scope = source.Record.Scope.Select(s => new KeyValuePair<string, string>(s.Key, s.Value)).ToList(),
            Datasets = datasets,
        };

        if (nullableKeys.Count > 0)
        {
            await GuardKnownKeysAsync(connection, _layout, recordName, nullableKeys, where, ct).ConfigureAwait(false);
        }

        return _layout;
    }

    /// <summary>
    /// Refuses a run whose record table holds a row with an unknown key, naming the column. Two records with a null key
    /// would share one identity, so a delivery cannot tell them apart or address either of them; the run stops before
    /// anything is planned rather than delivering one of them under the other's id. Only the columns whose declaration
    /// allows a null are tested, so a table that already forbids them costs nothing.
    /// </summary>
    private async Task GuardKnownKeysAsync(
        SqlConnection connection, IngestionLayout layout, SourceObjectName recordName, IReadOnlyList<string> nullableKeys,
        string where, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.NullKeyProbe(layout, nullableKeys));
        BindScope(command, layout);
        await using var reader = await ExecuteReaderAsync(command, "check the record table's keys for unknown values", ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var unknown = new List<string>(nullableKeys.Count);
        for (var i = 0; i < nullableKeys.Count; i++)
        {
            if (!reader.IsDBNull(i) && reader.GetInt32(i) == 1)
            {
                unknown.Add(nullableKeys[i]);
            }
        }

        throw new FlowValidationException(
            $"{where}: the record table {recordName} holds a row whose key column {(unknown.Count == 1 ? $"'{unknown[0]}' is" : $"{string.Join(" and ", unknown.Select(u => $"'{u}'"))} are")} null, "
            + "so that row has no identity to deliver under and could not be told apart from another like it. "
            + "Give every row a key, or narrow source.record.scope to the rows that have one.");
    }

    private async Task<IReadOnlyDictionary<string, SourceColumn>> ReadColumnsAsync(SqlConnection connection, SourceObjectName table, string declaredAt, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.Columns(table));
        command.Parameters.Add(new SqlParameter(IngestionSql.ObjectParameter, SqlDbType.NVarChar, 386) { Value = table.ToString() });
        var columns = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await ExecuteReaderAsync(command, $"read the columns of {table}", ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var column = new SourceColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetByte(4),
                    reader.GetBoolean(5));
                columns[column.Name] = column;
            }
        }

        if (columns.Count == 0)
        {
            throw new DeliveryException(
                $"Flow '{_flow.Name}': the table {table}, declared under {declaredAt}, was not found on the source database, or the identity this node connects with cannot see it. "
                + "Check the ingestion flow that loads it has run, and that the node's login is granted SELECT on it.");
        }

        return columns;
    }

    private static SourceColumn Column(IReadOnlyDictionary<string, SourceColumn> columns, SourceObjectName table, string name, string what)
        => columns.TryGetValue(name, out var column)
            ? column
            : throw new FlowValidationException($"{what}, which the table {table} does not hold. Columns: {string.Join(", ", columns.Keys.OrderBy(c => c, StringComparer.Ordinal))}.");

    private static SourceColumn? Optional(IReadOnlyDictionary<string, SourceColumn> columns, string? name)
        => name is not null && columns.TryGetValue(name, out var column) ? column : null;

    private async Task<long> CountAsync(SqlConnection connection, IngestionLayout layout, SourceSelection selection, SourceWindow window, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.CandidateCount(layout, selection.Kind, window.LowerUtc is not null));
        BindWindow(command, layout, window);
        BindScope(command, layout);
        var value = await ExecuteScalarAsync(command, "count the candidate records", ct).ConfigureAwait(false);
        return value is long count ? count : 0;
    }

    private async Task<(long InScope, IReadOnlyList<KeyTuple> Missing, IReadOnlyList<KeyTuple> OutOfScope)> ProbeKeysAsync(
        SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return (0, [], []);
        }

        await using var command = Command(connection, IngestionSql.KeyProbe(layout));
        BindScope(command, layout);
        BindKeys(command, keys);
        var found = new HashSet<KeyTuple>();
        var outOfScope = new List<KeyTuple>();
        await using (var reader = await ExecuteReaderAsync(command, "look the named record keys up", ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = ReadKey(reader, layout);
                found.Add(key);
                if (reader.GetInt32(layout.Key.Count) == 0)
                {
                    outOfScope.Add(key);
                }
            }
        }

        var missing = keys.Where(k => !found.Contains(k)).ToList();
        return (found.Count - outOfScope.Count, missing, outOfScope);
    }

    private async Task<IReadOnlyList<KeyTuple>> CandidatePageAsync(
        SqlConnection connection, IngestionLayout layout, SourceHeader header, KeyTuple? after, KeyRange? range, int pageSize, CancellationToken ct)
    {
        var bounds = new IngestionSql.KeyBounds(after is not null, false, range?.To is not null);
        await using var command = Command(connection, IngestionSql.CandidatePage(layout, header.Selection.Kind, header.Window.LowerUtc is not null, bounds));
        Bind(command, layout, header, bounds, after, range?.To);
        command.Parameters.Add(new SqlParameter(IngestionSql.PageParameter, SqlDbType.Int) { Value = pageSize });

        var page = new List<KeyTuple>(pageSize);
        await using var reader = await ExecuteReaderAsync(command, "read a page of candidate record keys", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            page.Add(ReadKey(reader, layout));
        }

        return page;
    }

    private void Bind(SqlCommand command, IngestionLayout layout, SourceHeader header, IngestionSql.KeyBounds bounds, KeyTuple? after, KeyTuple? to)
    {
        if (header.Selection.Kind == SourceSelectionKind.Keys)
        {
            BindKeys(command, header.Selection.Keys);
        }
        else
        {
            BindWindow(command, layout, header.Window);
        }

        BindScope(command, layout);
        if (bounds.After && after is not null)
        {
            BindKey(command, layout, IngestionSql.AfterPrefix, after);
        }

        if (bounds.To && to is not null)
        {
            BindKey(command, layout, IngestionSql.ToPrefix, to);
        }
    }

    private void BindWindow(SqlCommand command, IngestionLayout layout, SourceWindow window)
    {
        var updated = _tables[SourceDatasets.Record][layout.Updated];
        if (window.LowerUtc is { } lower)
        {
            command.Parameters.Add(updated.Parameter(IngestionSql.LowerParameter, lower));
        }

        command.Parameters.Add(updated.Parameter(IngestionSql.UpperParameter, window.UpperUtc));
    }

    private void BindScope(SqlCommand command, IngestionLayout layout)
    {
        var record = _tables[SourceDatasets.Record];
        for (var i = 0; i < layout.Scope.Count; i++)
        {
            var (column, parameter) = layout.Scope[i];
            if (!_values.TryGetValue(parameter, out var value))
            {
                throw new DeliveryException(
                    $"Flow '{_flow.Name}': source.record.scope binds column '{column}' to parameter '{parameter}', and this run supplies no value for it. Supply it with --set {parameter}=value or the run's values.");
            }

            command.Parameters.Add(record[column].Parameter(IngestionSql.ScopePrefix + i.ToString(CultureInfo.InvariantCulture), value));
        }
    }

    private void BindKey(SqlCommand command, IngestionLayout layout, string prefix, KeyTuple key)
    {
        var record = _tables[SourceDatasets.Record];
        for (var i = 0; i < layout.Key.Count; i++)
        {
            command.Parameters.Add(record[layout.Key[i].Name].Parameter(prefix + i.ToString(CultureInfo.InvariantCulture), key.Values[i]));
        }
    }

    private static void BindKeys(SqlCommand command, IReadOnlyList<KeyTuple> keys)
        => command.Parameters.Add(new SqlParameter(IngestionSql.KeysParameter, SqlDbType.NVarChar, -1)
        {
            Value = JsonSerializer.Serialize(keys.Select(k => k.Values).ToList()),
        });

    private static KeyTuple ReadKey(SqlDataReader reader, IngestionLayout layout)
    {
        var parts = new string[layout.Key.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(i)))
                ?? throw new DeliveryException($"The record table {layout.Record} returned a null in key column '{layout.Key[i].Name}', which cannot name a record.");
        }

        return new KeyTuple(parts);
    }

    private async Task<IReadOnlyList<SourceRecord>> PageRecordsAsync(SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> page, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadPageAsync(connection, layout, page, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == DeadlockError && attempt <= MaxDeadlockRetries)
            {
                _logger.LogWarning(
                    "The source page of {Count} record(s) was chosen as a deadlock victim (attempt {Attempt} of {Attempts}); reading it again.",
                    page.Count, attempt, MaxDeadlockRetries + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<SourceRecord>> ReadPageAsync(SqlConnection connection, IngestionLayout layout, IReadOnlyList<KeyTuple> page, CancellationToken ct)
    {
        var snapshot = _flow.Source.Incremental.Isolation == SourceIsolation.Snapshot;
        SqlTransaction transaction;
        try
        {
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (snapshot && ex.Number is SnapshotNotAllowed or SnapshotDisabled)
        {
            throw SnapshotRefusal(ex);
        }

        await using (transaction.ConfigureAwait(false))
        {
            await using var command = Command(connection, IngestionSql.PageRows(layout));
            command.Transaction = transaction;
            BindKeys(command, page);

            var rows = new Dictionary<KeyTuple, RecordBuilder>();
            var order = new List<KeyTuple>(page.Count);
            SqlDataReader reader;
            try
            {
                reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (snapshot && ex.Number is SnapshotNotAllowed or SnapshotDisabled)
            {
                throw SnapshotRefusal(ex);
            }
            catch (SqlException ex) when (ex.Number != DeadlockError)
            {
                throw Failure(ex, "read a page of record rows");
            }

            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = ReadRow(reader);
                    var key = KeyOf(layout, row);
                    if (rows.TryAdd(key, new RecordBuilder(row)))
                    {
                        order.Add(key);
                    }
                }

                foreach (var dataset in layout.Datasets)
                {
                    if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
                    {
                        throw new DeliveryException($"Flow '{_flow.Name}': the source page did not return the rows of child dataset '{dataset.Name}'.");
                    }

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var row = ReadRow(reader);
                        var key = ChildKeyOf(dataset, row);
                        if (rows.TryGetValue(key, out var builder))
                        {
                            builder.Add(dataset, row);
                        }
                    }
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return order.Where(rows.ContainsKey).Select(key => Build(layout, key, rows[key])).ToList();
        }
    }

    private SourceRecord Build(IngestionLayout layout, KeyTuple key, RecordBuilder builder)
    {
        var updated = Moment(builder.Row.Get(layout.Updated));
        var datasets = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, DatasetVersion>(StringComparer.Ordinal);
        foreach (var dataset in layout.Datasets)
        {
            var rows = builder.Rows(dataset.Name);
            datasets[dataset.Name] = rows;
            DateTime? newest = null;
            if (dataset.Updated is { } column)
            {
                foreach (var row in rows)
                {
                    if (Moment(row.Get(column)) is { } moment && (newest is null || moment > newest))
                    {
                        newest = moment;
                    }
                }
            }

            versions[dataset.Name] = new DatasetVersion(rows.Count, newest);
        }

        return new SourceRecord
        {
            Row = builder.Row,
            Scopes = datasets,
            Origin = new SourceOrigin(
                _fileNameColumn is null ? null : builder.Row.GetString(_fileNameColumn),
                _rowNumberColumn is null ? null : builder.Row.Get(_rowNumberColumn) as long?,
                updated),
            Version = SourceVersion.Of(IngestionFingerprint.Of(updated, versions)),
            SourceKeyJson = key.Json,
            DeletedUtc = layout.Deleted is { } deleted ? Moment(builder.Row.Get(deleted)) : null,
            Hold = builder.Hold,
        };
    }

    private static KeyTuple KeyOf(IngestionLayout layout, SourceRow row)
        => new(layout.Key.Select(k => SourceRow.Stringify(row.Get(k.Name)) ?? string.Empty).ToList());

    private static KeyTuple ChildKeyOf(IngestionDataset dataset, SourceRow row)
        => new(dataset.Join.Select(j => SourceRow.Stringify(row.Get(j.Key)) ?? string.Empty).ToList());

    private static SourceRow ReadRow(SqlDataReader reader)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values[reader.GetName(i)] = SourceValues.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
        }

        return new SourceRow(values);
    }

    private static DateTime? Moment(object? value) => value switch
    {
        DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null,
    };

    private async Task<SqlDataReader> ExecuteReaderAsync(SqlCommand command, string what, CancellationToken ct)
    {
        try
        {
            return await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw Failure(ex, what);
        }
    }

    private async Task<object?> ExecuteScalarAsync(SqlCommand command, string what, CancellationToken ct)
    {
        try
        {
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is DBNull ? null : value;
        }
        catch (SqlException ex)
        {
            throw Failure(ex, what);
        }
    }

    private DeliveryException Failure(SqlException ex, string what)
        => new($"Flow '{_flow.Name}': the source database refused to {what} for {_flow.Source.Record.Object} (SQL error {ex.Number}): {SecretHygiene.RedactedMessage(ex.Message)}", ex);

    private DeliveryException SnapshotRefusal(SqlException ex)
        => new(
            $"Flow '{_flow.Name}': the source database does not allow snapshot isolation, which is how a record and its child rows are read as one moment (SQL error {ex.Number}). "
            + $"Enable it with ALTER DATABASE [{_layout?.Record.Database ?? _flow.Source.Record.Object}] SET ALLOW_SNAPSHOT_ISOLATION ON, "
            + "or read each result set as it is committed with source.incremental.isolation: readCommitted.",
            ex);

    /// <summary>One column of a source table, as the database describes it.</summary>
    private sealed record SourceColumn(string Name, string TypeName, short MaxLength, byte Precision, byte Scale, bool Nullable)
    {
        private static readonly HashSet<string> Uncomparable = new(StringComparer.OrdinalIgnoreCase)
        {
            "text", "ntext", "image", "xml", "sql_variant", "geography", "geometry", "hierarchyid",
        };

        /// <summary>The type as SQL spells it, with the length, precision and scale a parameter or a table variable needs.</summary>
        public string SqlType => TypeName.ToLowerInvariant() switch
        {
            "nchar" or "nvarchar" => $"{TypeName}({(MaxLength < 0 ? "max" : (MaxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "char" or "varchar" or "binary" or "varbinary" => $"{TypeName}({(MaxLength < 0 ? "max" : MaxLength.ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{TypeName}({Precision.ToString(CultureInfo.InvariantCulture)},{Scale.ToString(CultureInfo.InvariantCulture)})",
            "datetime2" or "datetimeoffset" or "time" => $"{TypeName}({Scale.ToString(CultureInfo.InvariantCulture)})",
            _ => TypeName,
        };

        /// <summary>How many characters the column holds, or <see cref="int.MaxValue"/> for an unbounded one.</summary>
        public int MaxCharacters => MaxLength < 0
            ? int.MaxValue
            : TypeName.StartsWith('n') ? MaxLength / 2 : MaxLength;

        /// <summary>Whether the column can be compared, indexed and carried in a table variable's column list.</summary>
        public bool Comparable => !Uncomparable.Contains(TypeName) && MaxLength >= 0;

        /// <summary>Whether the column holds a date and time.</summary>
        public bool IsMoment => TypeName.ToLowerInvariant() is "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "date";

        /// <summary>A parameter of this column's own type, so a comparison against it stays index seekable.</summary>
        public SqlParameter Parameter(string name, object value)
        {
            var parameter = new SqlParameter(name, SqlDbType.NVarChar) { Value = value };
            switch (TypeName.ToLowerInvariant())
            {
                case "bigint":
                case "int":
                case "smallint":
                case "tinyint":
                    parameter.SqlDbType = SqlDbType.BigInt;
                    parameter.Value = Number(value);
                    break;
                case "bit":
                    parameter.SqlDbType = SqlDbType.Bit;
                    parameter.Value = value is bool flag ? flag : bool.Parse(Text(value));
                    break;
                case "uniqueidentifier":
                    parameter.SqlDbType = SqlDbType.UniqueIdentifier;
                    parameter.Value = value is Guid guid ? guid : Guid.Parse(Text(value));
                    break;
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney":
                    parameter.SqlDbType = SqlDbType.Decimal;
                    parameter.Precision = Precision;
                    parameter.Scale = Scale;
                    parameter.Value = value is decimal number ? number : decimal.Parse(Text(value), CultureInfo.InvariantCulture);
                    break;
                case "float":
                case "real":
                    parameter.SqlDbType = SqlDbType.Float;
                    parameter.Value = value is double real ? real : double.Parse(Text(value), CultureInfo.InvariantCulture);
                    break;
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                    parameter.SqlDbType = TypeName.Equals("datetime", StringComparison.OrdinalIgnoreCase) ? SqlDbType.DateTime : SqlDbType.DateTime2;
                    parameter.Value = value is DateTime moment ? moment : DateTime.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    break;
                case "datetimeoffset":
                    parameter.SqlDbType = SqlDbType.DateTimeOffset;
                    parameter.Value = value switch
                    {
                        DateTimeOffset offset => offset,
                        DateTime plain => new DateTimeOffset(DateTime.SpecifyKind(plain, DateTimeKind.Utc)),
                        _ => DateTimeOffset.Parse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    };
                    break;
                case "char":
                case "varchar":
                    parameter.SqlDbType = SqlDbType.VarChar;
                    parameter.Size = MaxLength < 0 ? -1 : MaxLength;
                    parameter.Value = Text(value);
                    break;
                default:
                    parameter.Size = MaxLength < 0 ? -1 : MaxCharacters;
                    parameter.Value = Text(value);
                    break;
            }

            return parameter;
        }

        private static string Text(object value) => SourceRow.Stringify(value) ?? string.Empty;

        private static long Number(object value)
            => value is long number ? number : long.Parse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>One record's rows while a page is read: the record row, and the child rows met so far, per dataset.</summary>
    private sealed class RecordBuilder(SourceRow row)
    {
        private readonly Dictionary<string, List<SourceRow>> _children = new(StringComparer.OrdinalIgnoreCase);

        public SourceRow Row { get; } = row;

        /// <summary>Why this record cannot be delivered as it stands, or null; set when a child dataset overruns its ceiling.</summary>
        public string? Hold { get; private set; }

        public void Add(IngestionDataset dataset, SourceRow child)
        {
            if (!_children.TryGetValue(dataset.Name, out var rows))
            {
                rows = [];
                _children[dataset.Name] = rows;
            }

            if (rows.Count >= dataset.MaxRowsPerRecord)
            {
                Hold ??= $"child dataset '{dataset.Name}' holds more than {dataset.MaxRowsPerRecord.ToString(CultureInfo.InvariantCulture)} rows for this record, "
                    + $"which is source.datasets.{dataset.Name}.maxRowsPerRecord; raise the ceiling deliberately, or split the record in the source";
                return;
            }

            rows.Add(child);
        }

        public IReadOnlyList<SourceRow> Rows(string dataset) => _children.TryGetValue(dataset, out var rows) ? rows : [];
    }
}

/// <summary>Collapses what the SQL Server client returns to the scalars a mapping renders from.</summary>
public static class SourceValues
{
    /// <summary>
    /// The value as the renderer sees it: whole numbers as <see cref="long"/>, exact numbers as <see cref="decimal"/>,
    /// approximate ones as <see cref="double"/>, moments as UTC, bytes as base64 text, and everything else as text.
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong ul => ul <= long.MaxValue ? (long)ul : (decimal)ul,
        float f => float.IsNaN(f) ? null : NumberValues.Widen(f),
        double d => double.IsNaN(d) ? null : d,
        decimal m => m,
        DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateOnly day => day,
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value.ToString(),
    };
}
