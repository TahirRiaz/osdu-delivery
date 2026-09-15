using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Submissions;

/// <summary>
/// One file an API submission lands for a pre-ingestion flow: which dataset it carries, which pre flow reads it, where it
/// goes, what form it takes, and the rows themselves. The file name is what the ingestion tables end up holding in their
/// file column, so a delivered record's origin names the submission it was sent in.
/// </summary>
public sealed record LandingFile(
    string Dataset,
    string PreFlowName,
    string Folder,
    string FileName,
    string Format,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    /// <summary>The full location the file is written to.</summary>
    public string Location => FileStoreRegistry.Join(Folder, FileName);
}

/// <summary>What a submission may point its payload files at: the payload sets the flow declares, and the roots they must sit under.</summary>
public sealed record SubmissionPayloadContract(IReadOnlyList<string> Payloads, IReadOnlyList<string> Roots, bool RequiresHash);

/// <summary>
/// Turns the records a source sent through the API into the files its pre-ingestion flows read (docs/stage4-design.md
/// section 4.1). Nothing is delivered from the request itself: the rows land as files, the pre and ing flows load them
/// into the same ingestion tables a file drop would, and the OSDU flow then reads them by key like any other record. That
/// is what keeps one path from the source to OSDU however the records arrive.
/// </summary>
public static class SubmissionLanding
{
    /// <summary>Why this flow cannot take API submissions, or null when it can.</summary>
    /// <param name="flow">The flow the records were sent for.</param>
    /// <param name="preFlows">The names of the flows the repository holds, which the declared pre flows must be among.</param>
    public static string? Refusal(FlowDefinition flow, IReadOnlyCollection<string> preFlows)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(preFlows);
        if (flow.Source.Submissions is not { } submissions)
        {
            return $"flow '{flow.Name}' takes no records through the API: it declares no source.submissions, so there is nowhere for them to land.";
        }

        if (!preFlows.Contains(submissions.Record.PreFlow, StringComparer.OrdinalIgnoreCase))
        {
            return $"flow '{flow.Name}' lands its records for pre flow '{submissions.Record.PreFlow}', which this repository does not hold.";
        }

        foreach (var (name, dataset) in submissions.Datasets)
        {
            if (!flow.Source.Datasets.ContainsKey(name))
            {
                return $"flow '{flow.Name}' lands dataset '{name}', which source.datasets does not declare.";
            }

            if (!preFlows.Contains(dataset.PreFlow, StringComparer.OrdinalIgnoreCase))
            {
                return $"flow '{flow.Name}' lands its '{name}' rows for pre flow '{dataset.PreFlow}', which this repository does not hold.";
            }
        }

        return null;
    }

    /// <summary>What a submission may say about its payload files: which sets it can name, and where those files must sit.</summary>
    public static SubmissionPayloadContract PayloadContract(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        return new SubmissionPayloadContract(
            flow.Source.Payloads.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            PayloadRoots.Of(flow, values),
            flow.Change.PayloadDetect != ChangeDetection.LastModified);
    }

    /// <summary>
    /// Why the files the records point at cannot be accepted, or null when they can: a set the flow does not declare, a
    /// location outside the roots the flow allows, or a missing content hash for a flow that decides payload changes by
    /// hash. The node opens these locations with its own identity when it delivers, so this is the gate that keeps a
    /// request from having any readable file shipped to OSDU.
    /// </summary>
    public static string? FilesRefusal(FlowDefinition flow, IReadOnlyDictionary<string, string> values, InlineRecords records)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(records);
        var contract = PayloadContract(flow, values);
        for (var i = 0; i < records.Records.Count; i++)
        {
            foreach (var (name, file) in records.Records[i].Files)
            {
                var at = $"record {(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} of the submission, files.{name}";
                if (!flow.Source.Payloads.ContainsKey(name))
                {
                    return $"{at}: flow '{flow.Name}' declares no payload '{name}'; it declares {(contract.Payloads.Count == 0 ? "none" : string.Join(", ", contract.Payloads))}.";
                }

                if (PayloadRoots.Refusal(flow, values, file.Location) is { } refusal)
                {
                    return $"{at}: {refusal}";
                }

                if (contract.RequiresHash && string.IsNullOrWhiteSpace(file.Hash))
                {
                    return $"{at}: flow '{flow.Name}' decides payload changes by content hash, so every submitted payload names its hash.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The files this submission lands: the record rows for their pre flow, then one file per child dataset the records
    /// carry rows for. The columns are the ones the mapping reads, plus the flow's business version column and the
    /// payload columns, so the ingestion tables end up holding exactly what a delivery needs.
    /// </summary>
    public static IReadOnlyList<LandingFile> Plan(
        InlineSubmissionState submission, FlowDefinition flow, MappingDefinition mapping, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(values);
        var submissions = flow.Source.Submissions
            ?? throw new DeliveryException($"Flow '{flow.Name}' declares no source.submissions, so submission {submission.SubmissionId:D} has nowhere to land.");
        var records = InlineRecords.Parse(submission.RecordsJson);
        var columns = MappingColumns.Read(mapping);
        var files = new List<LandingFile>(1 + submissions.Datasets.Count);

        var recordColumns = RecordColumns(flow, columns);
        files.Add(new LandingFile(
            SourceDatasets.Record,
            submissions.Record.PreFlow,
            FlowParameters.ResolvePath(flow, submissions.Record.Landing, values, "source.submissions.record.landing"),
            FileName(submission.SubmissionId, SourceDatasets.Record, submissions.Record.Format),
            submissions.Record.Format,
            recordColumns,
            records.Records.Select(record => RecordRow(flow, recordColumns, record)).ToList()));

        foreach (var (name, landing) in submissions.Datasets.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!records.Records.Any(r => r.Datasets.ContainsKey(name)))
            {
                continue;
            }

            var dataset = flow.Source.Datasets[name];
            var childColumns = ChildColumns(dataset, columns, name);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var record in records.Records)
            {
                if (!record.Datasets.TryGetValue(name, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    rows.Add(ChildRow(flow, dataset, childColumns, record, child));
                }
            }

            files.Add(new LandingFile(
                name,
                landing.PreFlow,
                FlowParameters.ResolvePath(flow, landing.Landing, values, $"source.submissions.datasets.{name}.landing"),
                FileName(submission.SubmissionId, name, landing.Format),
                landing.Format,
                childColumns,
                rows));
        }

        foreach (var name in records.Records.SelectMany(r => r.Datasets.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!submissions.Datasets.ContainsKey(name))
            {
                throw new DeliveryException(
                    $"Submission {submission.SubmissionId:D} carries rows for dataset '{name}', which flow '{flow.Name}' does not land (source.submissions.datasets declares {(submissions.Datasets.Count == 0 ? "none" : string.Join(", ", submissions.Datasets.Keys))}).");
            }
        }

        return files;
    }

    /// <summary>The file name a landed dataset takes: the submission's id and the dataset, which is what the ingestion table's file column then holds.</summary>
    public static string FileName(Guid submissionId, string dataset, string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        return $"{submissionId:N}_{dataset}{LandingFormats.Extension(format)}";
    }

    private static IReadOnlyList<string> RecordColumns(FlowDefinition flow, MappingSourceColumns columns)
    {
        var names = new List<string>();
        foreach (var column in flow.Source.Record.Key.Concat(columns.RecordNames))
        {
            Add(names, column);
        }

        foreach (var (column, _) in flow.Source.Record.Scope)
        {
            Add(names, column);
        }

        if (flow.Source.LastModified is { } lastModified)
        {
            Add(names, lastModified);
        }

        foreach (var payload in flow.Source.Payloads.Values)
        {
            Add(names, payload.LocationColumn);
            Add(names, payload.HashColumn);
            Add(names, payload.ChunkCountColumn);
        }

        return names;
    }

    private static IReadOnlyList<string> ChildColumns(FlowSourceDataset dataset, MappingSourceColumns columns, string name)
    {
        var names = new List<string>();
        foreach (var (child, _) in dataset.Join)
        {
            Add(names, child);
        }

        foreach (var column in columns.Datasets.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ColumnNames ?? [])
        {
            Add(names, column);
        }

        foreach (var column in dataset.OrderBy)
        {
            Add(names, column);
        }

        return names;
    }

    private static IReadOnlyDictionary<string, object?> RecordRow(FlowDefinition flow, IReadOnlyList<string> columns, InlineRecord record)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            row[column] = Value(record.Row, column);
        }

        foreach (var (name, payload) in flow.Source.Payloads)
        {
            if (!record.Files.TryGetValue(name, out var file))
            {
                continue;
            }

            if (payload.LocationColumn is { } location)
            {
                row[location] = file.Location;
            }

            if (payload.HashColumn is { } hash && file.Hash is { } value)
            {
                row[hash] = value;
            }
        }

        return row;
    }

    private static IReadOnlyDictionary<string, object?> ChildRow(
        FlowDefinition flow, FlowSourceDataset dataset, IReadOnlyList<string> columns, InlineRecord record, IReadOnlyDictionary<string, object?> child)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            row[column] = Value(child, column);
        }

        // A submitted child row need not repeat its record's key: the landing file carries it, because that is what
        // joins the rows back together once the ing flows have loaded them.
        foreach (var (childColumn, recordColumn) in dataset.Join)
        {
            if (row.TryGetValue(childColumn, out var carried) && carried is not null)
            {
                continue;
            }

            row[childColumn] = Value(record.Row, recordColumn);
        }

        return row;
    }

    private static object? Value(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) ? value : null;

    private static void Add(List<string> names, string? column)
    {
        if (!string.IsNullOrWhiteSpace(column) && !names.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(column);
        }
    }
}
