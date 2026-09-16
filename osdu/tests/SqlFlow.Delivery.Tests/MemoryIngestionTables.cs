using System.Globalization;
using System.Runtime.CompilerServices;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One record as the in-memory ingestion tables hold it: the record row, the rows of each child dataset, and the system
/// columns SQLFlow's own ingestion would have stamped on them (when the row last changed, which file it came from, and
/// whether it is soft-deleted).
/// </summary>
public sealed class MemoryRecord
{
    public required IDictionary<string, object?> Row { get; init; }

    public Dictionary<string, List<IDictionary<string, object?>>> Datasets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime UpdatedUtc { get; set; }

    public string? FileName { get; set; }

    public long? RowNumber { get; set; }

    public DateTime? DeletedUtc { get; set; }

    /// <summary>The newest change time among a child dataset's rows, which the fingerprint folds in.</summary>
    public Dictionary<string, DateTime> DatasetUpdatedUtc { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryRecord With(string column, object? value)
    {
        Row[column] = value;
        return this;
    }

    public MemoryRecord AddChild(string dataset, IDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!Datasets.TryGetValue(dataset, out var rows))
        {
            rows = [];
            Datasets[dataset] = rows;
        }

        rows.Add(row);
        return this;
    }
}

/// <summary>
/// The ingestion tables a flow reads, in memory: the fast suites' stand-in for SQL Server. It answers exactly what the
/// SQL Server source answers (the window, the scope predicate, key selections, key slices, the ingestion fingerprint and
/// each record's origin), so the planner, the intake and the worker run their real code paths on every machine, and the
/// SQL Server contract suite proves the two agree.
/// </summary>
public sealed class MemoryIngestionTables : IIngestionSourceFactory
{
    private readonly TimeProvider _time;

    public MemoryIngestionTables(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The records the tables hold, in key order as the source reads them.</summary>
    public List<MemoryRecord> Records { get; } = [];

    /// <summary>How many reads were opened, which a test uses to prove a run read the source once.</summary>
    public int Opens { get; private set; }

    /// <summary>Every selection a read was opened for, in order.</summary>
    public List<SourceSelection> Selections { get; } = [];

    public MemoryRecord Add(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Records.Add(record);
        return record;
    }

    public IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        return new MemorySource(this, flow, values, _time);
    }

    internal void Opened(SourceSelection selection)
    {
        Opens++;
        Selections.Add(selection);
    }

    private sealed class MemorySource : IIngestionSource
    {
        private readonly MemoryIngestionTables _tables;
        private readonly FlowDefinition _flow;
        private readonly IReadOnlyDictionary<string, string> _values;
        private readonly TimeProvider _time;

        public MemorySource(MemoryIngestionTables tables, FlowDefinition flow, IReadOnlyDictionary<string, string> values, TimeProvider time)
        {
            _tables = tables;
            _flow = flow;
            _values = values;
            _time = time;
        }

        public Task<SourceHeader> OpenAsync(SourceSelection selection, SourceWindow? stored, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(selection);
            _tables.Opened(selection);
            var upper = stored?.UpperUtc ?? _time.GetUtcNow().UtcDateTime;
            var lower = stored is not null ? stored.LowerUtc : selection.Kind == SourceSelectionKind.Incremental ? selection.LowerUtc : null;
            var window = new SourceWindow(lower, upper);

            var inScope = _tables.Records.Where(InScope).ToList();
            IReadOnlyList<KeyTuple> missing = [];
            IReadOnlyList<KeyTuple> outOfScope = [];
            long candidates;
            if (selection.Kind == SourceSelectionKind.Keys)
            {
                var all = _tables.Records.ToDictionary(KeyOf, r => r);
                missing = selection.Keys.Where(k => !all.ContainsKey(k)).ToList();
                outOfScope = selection.Keys.Where(k => all.TryGetValue(k, out var record) && !InScope(record)).ToList();
                candidates = selection.Keys.Count - missing.Count - outOfScope.Count;
            }
            else
            {
                candidates = inScope.Count(r => InWindow(r, window, selection.Kind));
            }

            return Task.FromResult(new SourceHeader
            {
                Selection = selection,
                Window = window,
                Columns = Columns(),
                KeyColumns = _flow.Source.Record.Key.Select(k => new SourceKeyColumn(k, "nvarchar(400)")).ToList(),
                EstimatedCandidates = candidates,
                HasChanges = candidates > 0,
                MissingKeys = missing,
                OutOfScopeKeys = outOfScope,
            });
        }

        public Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(SourceHeader header, int slices, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(header);
            ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
            var keys = Candidates(header).Select(KeyOf).OrderBy(k => k.Json, StringComparer.Ordinal).ToList();
            if (slices == 1 || keys.Count <= 1)
            {
                return Task.FromResult<IReadOnlyList<KeyRange>>([new KeyRange(0, null, null)]);
            }

            var size = (int)Math.Ceiling(keys.Count / (double)slices);
            var ranges = new List<KeyRange>();
            KeyTuple? from = null;
            for (var cut = size - 1; cut < keys.Count - 1; cut += size)
            {
                ranges.Add(new KeyRange(ranges.Count, from, keys[cut]));
                from = keys[cut];
            }

            ranges.Add(new KeyRange(ranges.Count, from, null));
            return Task.FromResult<IReadOnlyList<KeyRange>>(ranges);
        }

        public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceHeader header, KeyRange? range, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(header);
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var record in Candidates(header).OrderBy(r => KeyOf(r).Json, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var key = KeyOf(record);
                if (range is { } bounds)
                {
                    if (bounds.From is { } from && string.CompareOrdinal(key.Json, from.Json) <= 0)
                    {
                        continue;
                    }

                    if (bounds.To is { } to && string.CompareOrdinal(key.Json, to.Json) > 0)
                    {
                        continue;
                    }
                }

                yield return Build(record, key);
            }
        }

        private SourceRecord Build(MemoryRecord record, KeyTuple key)
        {
            var datasets = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
            var versions = new Dictionary<string, DatasetVersion>(StringComparer.Ordinal);
            string? hold = null;
            foreach (var (name, dataset) in _flow.Source.Datasets)
            {
                var rows = record.Datasets.TryGetValue(name, out var children) ? children : [];
                if (rows.Count > dataset.MaxRowsPerRecord)
                {
                    hold ??= $"child dataset '{name}' holds more than {dataset.MaxRowsPerRecord.ToString(CultureInfo.InvariantCulture)} rows for this record, "
                        + $"which is source.datasets.{name}.maxRowsPerRecord; raise the ceiling deliberately, or split the record in the source";
                    rows = rows.Take(dataset.MaxRowsPerRecord).ToList();
                }

                datasets[name] = rows.Select(r => new SourceRow(new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))).ToList();
                versions[name] = new DatasetVersion(rows.Count, record.DatasetUpdatedUtc.TryGetValue(name, out var updated) ? updated : record.UpdatedUtc);
            }

            return new SourceRecord
            {
                Row = new SourceRow(new Dictionary<string, object?>(record.Row, StringComparer.OrdinalIgnoreCase)),
                Scopes = datasets,
                Origin = new SourceOrigin(record.FileName, record.RowNumber, record.UpdatedUtc),
                Version = SourceVersion.Of(IngestionFingerprint.Of(record.UpdatedUtc, versions)),
                SourceKeyJson = key.Json,
                DeletedUtc = record.DeletedUtc,
                Hold = hold,
            };
        }

        private IEnumerable<MemoryRecord> Candidates(SourceHeader header)
        {
            if (header.Selection.Kind == SourceSelectionKind.Keys)
            {
                var wanted = header.Selection.Keys.ToHashSet();
                return _tables.Records.Where(r => InScope(r) && wanted.Contains(KeyOf(r)));
            }

            return _tables.Records.Where(r => InScope(r) && InWindow(r, header.Window, header.Selection.Kind));
        }

        private static bool InWindow(MemoryRecord record, SourceWindow window, SourceSelectionKind kind)
        {
            if (kind != SourceSelectionKind.Incremental)
            {
                return true;
            }

            return record.UpdatedUtc <= window.UpperUtc && (window.LowerUtc is not { } lower || record.UpdatedUtc > lower);
        }

        private bool InScope(MemoryRecord record)
        {
            foreach (var (column, parameter) in _flow.Source.Record.Scope)
            {
                var wanted = _values.TryGetValue(parameter, out var value) ? value : null;
                if (!string.Equals(SourceRow.Stringify(record.Row.TryGetValue(column, out var held) ? held : null), wanted, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private KeyTuple KeyOf(MemoryRecord record)
            => new(_flow.Source.Record.Key
                .Select(column => SourceRow.Stringify(record.Row.TryGetValue(column, out var value) ? value : null) ?? string.Empty)
                .ToList());

        private IReadOnlyDictionary<string, IReadOnlySet<string>> Columns()
        {
            var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [SourceDatasets.Record] = _tables.Records
                    .SelectMany(r => r.Row.Keys)
                    .Concat(_flow.Source.Record.Key)
                    .Concat(_flow.Source.LastModified is { } lastModified ? [lastModified] : Array.Empty<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
            };

            foreach (var (name, _) in _flow.Source.Datasets)
            {
                columns[name] = _tables.Records
                    .SelectMany(r => r.Datasets.TryGetValue(name, out var rows) ? rows.SelectMany(row => row.Keys) : [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return columns;
        }
    }
}
