using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Intake;

/// <summary>What writing a submission's drop did: where the drop is, its manifest, whether this call wrote it, and what to tell the operator.</summary>
public sealed record InlineDropResult(string Location, DropManifest Manifest, bool Written, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a submission out as a drop (design.md section 3.4), so the records a source sent in a request take the path a
/// prepared drop takes: the manifest check, the preflight gate, the change gates, the ledger and the drain. The drop
/// lives under the flow's work location, <c>{work}/inline/{submissionId}</c>, never at the flow's declared source
/// location, which belongs to the preparing side. It is written from the ledger's copy of the records whenever a run
/// takes the submission and finds no manifest there, the data files first and the manifest last, so a re-run after the
/// work location was cleaned up writes it again and no run reads a half-written one.
/// <para>
/// Payload files are never copied here. A record says where its files already are, and the drop's manifest declares a
/// location column carrying it, so the node opens them with its own identity when it delivers and re-opens them on every
/// retry. That is the same "past, not through" handling a prepared drop gets (design.md section 3.2), and it is what
/// lets a submission deliver through the protocols that stream files: the wellbore DDMS, the file service and manifest
/// ingestion.
/// </para>
/// <para>
/// JSON leaves a column out where a drop writes a null, so the drop declares every column the mapping reads and every
/// column the flow names, and a column no record sent is null in every row. Each child dataset the mapping repeats is
/// written as a drop scope of the same name. The drop is keyed when the mapping repeats a child dataset or the flow
/// streams a payload, since both are joined to their record by its key: each dataset row carries its delivery key and each
/// child row its record's key, sorted, so the intake merge-joins them. A record whose dataset key is incomplete gets a key
/// of its own for that join only; the renderer derives none, so the intake reports the record as untracked exactly as it
/// would for such a row in a prepared drop.
/// </para>
/// </summary>
public static class InlineDrop
{
    public const string Folder = "inline";

    public const string RecordFile = "record/part-00000.parquet";

    /// <summary>The root-scope column a submission's drop carries each record's payload location in.</summary>
    public const string LocationColumnPrefix = "payloadLocation__";

    /// <summary>The root-scope column a submission's drop carries each record's payload content hash in.</summary>
    public const string HashColumnPrefix = "payloadHash__";

    /// <summary>The namespace of the join keys given to records whose dataset key is incomplete.</summary>
    private static readonly Guid UntrackedKeys = DeterministicGuid.Namespace("inline-submission-untracked-record");

    /// <summary>The drop location of a submission: under the flow's work location for these parameter values.</summary>
    public static string Location(FlowDefinition flow, IReadOnlyDictionary<string, string> values, Guid submissionId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var work = FlowParameters.WorkLocation(flow, values, FlowParameters.DropLocation(flow, values));
        return FileStoreRegistry.Join(FileStoreRegistry.Join(work, Folder), submissionId.ToString("D"));
    }

    /// <summary>Why <paramref name="flow"/> takes no records sent in a request, or null when it does: its document says so.</summary>
    public static string? Refusal(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Source.ManualSubmission
            ? null
            : $"Flow '{flow.Name}' does not take records sent in the request: its document declares no 'source.manualSubmission'. Add 'manualSubmission: true' to the flow's source block to let records be submitted for it, or deliver them as a drop.";
    }

    /// <summary>
    /// What a submission to a flow says about files: the payload the flow streams (null when it streams none, and then a
    /// record carries no files at all), whether each record has to carry a content hash for it, and the roots a record
    /// may point inside.
    /// </summary>
    public sealed record InlinePayloadContract(string? PayloadName, bool HashRequired, IReadOnlyList<string> Roots);

    /// <summary>What <paramref name="flow"/> expects a submission to say about files, for a caller filling one in.</summary>
    public static InlinePayloadContract PayloadContract(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var payloadName = Planner.PayloadName(flow);
        return new InlinePayloadContract(
            payloadName,
            payloadName is not null && flow.Change.PayloadDetect != ChangeDetection.LastModified,
            payloadName is null ? [] : PayloadRoots.Of(flow));
    }

    /// <summary>
    /// Why the records cannot be delivered by <paramref name="flow"/> as they stand, or null when they can: the payload
    /// the flow streams has to be pointed at, by every record, somewhere the flow allows, and with a hash when the flow
    /// decides payload changes by hash. Checked when a request is accepted, and again here before anything is written.
    /// </summary>
    public static string? FilesRefusal(FlowDefinition flow, InlineRecords records)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(records);
        var payloadName = Planner.PayloadName(flow);
        if (payloadName is null)
        {
            return records.FileSets.Count == 0
                ? null
                : $"Flow '{flow.Name}' streams no payload files, so its records carry none; 'files' names {string.Join(", ", records.FileSets)}.";
        }

        foreach (var set in records.FileSets)
        {
            if (!set.Equals(payloadName, StringComparison.OrdinalIgnoreCase))
            {
                return $"Flow '{flow.Name}' streams the payload '{payloadName}'; a record names files under '{set}', which it does not stream.";
            }
        }

        var needsHash = flow.Change.PayloadDetect != ChangeDetection.LastModified;
        for (var i = 0; i < records.Records.Count; i++)
        {
            if (!records.Records[i].Files.TryGetValue(payloadName, out var file))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"records[{i}] points at no files: flow '{flow.Name}' streams the payload '{payloadName}', so every record says where its files are under files.{payloadName}.");
            }

            if (PayloadRoots.Refusal(flow, file.Location) is { } refusal)
            {
                return string.Create(CultureInfo.InvariantCulture, $"records[{i}]: {refusal}");
            }

            if (needsHash && string.IsNullOrWhiteSpace(file.Hash))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"records[{i}] gives no hash for payload '{payloadName}': flow '{flow.Name}' decides payload changes by content hash, so each record carries one under files.{payloadName}.hash, or the flow takes the files' modified times instead (change.payloadDetect: lastModified).");
            }
        }

        return null;
    }

    public static string ScopeFile(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return "scopes/" + scope + "/part-00000.parquet";
    }

    /// <summary>
    /// Writes the drop at <paramref name="location"/> unless a manifest for the same submission is already there. The
    /// mapping is the resolved one the run renders with: the drop declares the columns it reads, and derives the
    /// delivery keys it joins child datasets by.
    /// </summary>
    public static async Task<InlineDropResult> WriteAsync(
        IDropReader drops, FileStoreRegistry stores, string location, FlowDefinition flow, ResolvedMapping mapping, InlineSubmissionState submission,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(drops);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(submission);
        if (Refusal(flow) is { } refusal)
        {
            throw new DeliveryException(refusal);
        }

        if (submission.FlowId != flow.Id)
        {
            throw new DeliveryException($"Submission {submission.SubmissionId:D} belongs to flow '{submission.FlowName}', not '{flow.Name}'.");
        }

        var root = new Drop(location.TrimEnd('/', '\\'), null!);
        var manifestPath = root.Resolve(flow.Source.Manifest);
        if (await stores.ExistsAsync(manifestPath, ct).ConfigureAwait(false))
        {
            var existing = await drops.OpenAsync(root.Location, flow.Source.Manifest, ct).ConfigureAwait(false);
            if (existing.Manifest.SubmissionId != submission.SubmissionId)
            {
                throw new DeliveryException(
                    $"{manifestPath} is the manifest of submission {existing.Manifest.SubmissionId:D}, not {submission.SubmissionId:D}; a submission's drop is written only by the runs that take it.");
            }

            return new InlineDropResult(root.Location, existing.Manifest, Written: false, []);
        }

        var records = InlineRecords.Parse(submission.RecordsJson);
        if (FilesRefusal(flow, records) is { } filesRefusal)
        {
            throw new DeliveryException($"Submission {submission.SubmissionId:D}: {filesRefusal}");
        }

        var read = MappingColumns.Read(mapping.Mapping);
        var warnings = new List<string>();
        var where = string.Create(CultureInfo.InvariantCulture, $"Inline submission {submission.SubmissionId:D}");

        // Every child dataset the mapping repeats is declared, sent or not; one the mapping does not repeat is not written.
        var sentDatasets = records.DatasetColumns.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var ignored in sentDatasets.Keys.Where(s => read.Datasets.All(m => !m.Name.Equals(s, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"{where}: child dataset '{ignored}' is not one the mapping repeats, so its rows are not written to the drop.");
        }

        var payloadName = Planner.PayloadName(flow);
        var payload = payloadName is null ? null : PayloadColumns.Of(payloadName, records);
        // A payload is joined to its record by the delivery key, so a drop that declares one is keyed even when the
        // mapping repeats no child dataset at all: the reader refuses root rows without a key whenever the drop has
        // child scopes or payloads.
        var keyed = read.Datasets.Count > 0 || payloadName is not null;
        var keys = Keys(records, mapping.Renderer, submission, keyed);
        var flowColumns = new[] { flow.Source.LastModified, flow.Source.Fingerprint }.OfType<string>().ToList();
        var rootColumns = Declare(DropManifest.RootScope, records.RootColumns, read.RecordNames, flowColumns, keyed, warnings, where);
        if (payload is not null)
        {
            foreach (var reserved in payload.Columns)
            {
                if (rootColumns.Any(c => c.Name.Equals(reserved.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DeliveryException(
                        $"{where}: column '{reserved.Name}' is reserved for where the payload '{payloadName}' is; a record must not carry a column of that name.");
                }

                rootColumns.Add(reserved);
            }
        }

        var order = Enumerable.Range(0, records.Records.Count).ToList();
        if (keyed)
        {
            // A keyed drop is written partitioned: the root file and every scope file sorted by the key's text.
            order = [.. order.OrderBy(i => keys[i], StringComparer.Ordinal)];
        }

        var scopes = new Dictionary<string, ManifestScope>(StringComparer.Ordinal);
        await WriteFileAsync(
            stores, root.Resolve(RecordFile), rootColumns,
            order.Select(i => Row(records.Records[i], rootColumns, keyed ? keys[i] : null, payload)).ToList(), ct).ConfigureAwait(false);
        scopes[DropManifest.RootScope] = new ManifestScope { Files = [RecordFile], Columns = Manifest(rootColumns) };

        // Each child dataset is written as a scope of the drop under its own name.
        foreach (var dataset in read.Datasets)
        {
            var sent = sentDatasets.TryGetValue(dataset.Name, out var columns) ? columns : [];
            var datasetColumns = Declare(dataset.Name, sent, dataset.ColumnNames, [], keyed: true, warnings, where);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var i in order)
            {
                if (records.Records[i].Datasets.TryGetValue(dataset.Name, out var childRows))
                {
                    rows.AddRange(childRows.Select(child => Row(child, datasetColumns, keys[i])));
                }
            }

            var file = ScopeFile(dataset.Name);
            await WriteFileAsync(stores, root.Resolve(file), datasetColumns, rows, ct).ConfigureAwait(false);
            scopes[dataset.Name] = new ManifestScope { Files = [file], Columns = Manifest(datasetColumns), ParentKey = DropReader.DeliveryKeyColumn };
        }

        var manifest = new DropManifest
        {
            SubmissionId = submission.SubmissionId,
            Flow = submission.FlowName,
            Mapping = submission.MappingReference,
            // The caller's own name for the work travels on the drop, so the intake registers the submission under it
            // without the intake having to know an inline submission from a prepared one.
            Reference = submission.Reference,
            Parameters = new Dictionary<string, string>(submission.Parameters(), StringComparer.Ordinal),
            CreatedUtc = new DateTimeOffset(DateTime.SpecifyKind(submission.ReceivedUtc, DateTimeKind.Utc)),
            RecordCount = records.Records.Count,
            Partitioned = keyed,
            Scopes = scopes,
            Payloads = payload is null
                ? new Dictionary<string, ManifestPayload>(StringComparer.Ordinal)
                : new Dictionary<string, ManifestPayload>(StringComparer.Ordinal)
                {
                    [payloadName!] = new()
                    {
                        LocationColumn = payload.Location.Name,
                        HashColumn = payload.Hash?.Name,
                        ContentType = flow.Target.ProtocolOptions.PayloadContentType,
                    },
                },
        };
        manifest.Validate(manifestPath);

        // The manifest is written last: its presence is what says the drop is complete.
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToJson())))
        {
            await stores.WriteAsync(manifestPath, content, ct).ConfigureAwait(false);
        }

        return new InlineDropResult(root.Location, manifest, Written: true, warnings);
    }

    /// <summary>The reserved root-scope columns a submission's drop carries its payload location (and hash) in.</summary>
    private sealed record PayloadColumns(InlineColumn Location, InlineColumn? Hash)
    {
        public IEnumerable<InlineColumn> Columns => Hash is null ? [Location] : [Location, Hash];

        public static PayloadColumns Of(string payloadName, InlineRecords records) => new(
            new InlineColumn(LocationColumnPrefix + payloadName, InlineColumnTypes.Text),
            records.Records.Any(r => r.Files.TryGetValue(payloadName, out var file) && file.Hash is not null)
                ? new InlineColumn(HashColumnPrefix + payloadName, InlineColumnTypes.Text)
                : null);

        public string PayloadName => Location.Name[LocationColumnPrefix.Length..];
    }

    /// <summary>
    /// The key each record is written under: the one it sent, the one its dataset key derives, or (in a keyed drop, for a
    /// record whose dataset key is incomplete) a join key of its own. Two records that are the same record are refused.
    /// </summary>
    private static string?[] Keys(InlineRecords records, MappingRenderer renderer, InlineSubmissionState submission, bool keyed)
    {
        var keys = new string?[records.Records.Count];
        var seen = new Dictionary<Guid, int>();
        for (var i = 0; i < records.Records.Count; i++)
        {
            var row = records.Records[i].Row;
            Guid? key = row.TryGetValue(DropReader.DeliveryKeyColumn, out var sent) && sent is string text
                ? Guid.Parse(text, CultureInfo.InvariantCulture)
                : renderer.DeriveKey(new SourceRow(row), out _)?.Value;
            if (key is { } k)
            {
                if (seen.TryGetValue(k, out var first))
                {
                    throw new DeliveryException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Inline submission {submission.SubmissionId:D}: records[{first}] and records[{i}] are the same record (delivery key {k:D}). A submission sends each record once."));
                }

                seen[k] = i;
                keys[i] = k.ToString("D");
            }
            else if (keyed)
            {
                keys[i] = DeterministicGuid.V5(UntrackedKeys, string.Create(CultureInfo.InvariantCulture, $"{submission.SubmissionId:D}/{i}")).ToString("D");
            }
        }

        return keys;
    }

    /// <summary>
    /// The columns a scope's file declares: the columns sent, in the order sent, then each column the mapping or the flow
    /// reads that no row sent (a string column of nulls); a keyed scope leads with the delivery key unless the rows sent it.
    /// </summary>
    private static List<InlineColumn> Declare(
        string scope, IReadOnlyList<InlineColumn> sent, IReadOnlyList<string> read, IReadOnlyList<string> flowColumns, bool keyed, List<string> warnings, string where)
    {
        var columns = new List<InlineColumn>();
        if (keyed && !sent.Any(c => c.Name.Equals(DropReader.DeliveryKeyColumn, StringComparison.OrdinalIgnoreCase)))
        {
            columns.Add(new InlineColumn(DropReader.DeliveryKeyColumn, InlineColumnTypes.Text));
        }

        columns.AddRange(sent);
        var missing = read.Concat(flowColumns)
            .Where(name => !columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        columns.AddRange(missing.Select(name => new InlineColumn(name, InlineColumnTypes.Text)));

        var unread = sent
            .Select(c => c.Name)
            .Where(name => !name.Equals(DropReader.DeliveryKeyColumn, StringComparison.OrdinalIgnoreCase)
                && !read.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !flowColumns.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unread.Count > 0)
        {
            warnings.Add($"{where}: {Describe(scope)} carries column(s) the mapping does not read, so they change nothing: {string.Join(", ", unread)}.");
        }

        if (missing.Count > 0)
        {
            warnings.Add($"{where}: no record sent column(s) of {Describe(scope)} that the mapping or the flow reads, so they are null in every row: {string.Join(", ", missing)}.");
        }

        return columns;
    }

    /// <summary>How messages name a scope of the drop: the dataset row, or the child dataset written under that name.</summary>
    private static string Describe(string scope)
        => scope.Equals(DropManifest.RootScope, StringComparison.Ordinal) ? "the dataset row" : $"child dataset '{scope}'";

    private static IReadOnlyDictionary<string, object?> Row(InlineRecord record, IReadOnlyList<InlineColumn> columns, string? key, PayloadColumns? payload)
    {
        var row = Row(record.Row, columns, key);
        if (payload is not null && record.Files.TryGetValue(payload.PayloadName, out var file))
        {
            var writable = (Dictionary<string, object?>)row;
            writable[payload.Location.Name] = file.Location;
            if (payload.Hash is { } hash)
            {
                writable[hash.Name] = file.Hash;
            }
        }

        return row;
    }

    private static Dictionary<string, object?> Row(IReadOnlyDictionary<string, object?> sent, IReadOnlyList<InlineColumn> columns, string? key)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        foreach (var column in columns)
        {
            row[column.Name] = key is not null && column.Name.Equals(DropReader.DeliveryKeyColumn, StringComparison.OrdinalIgnoreCase)
                ? key
                : sent.TryGetValue(column.Name, out var value) ? value : null;
        }

        return row;
    }

    private static List<ManifestColumn> Manifest(IReadOnlyList<InlineColumn> columns)
        => columns.Select(c => new ManifestColumn { Name = c.Name, Type = c.Type }).ToList();

    private static async Task WriteFileAsync(
        FileStoreRegistry stores, string path, IReadOnlyList<InlineColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct)
    {
        using var content = new MemoryStream();
        await ParquetScopeReader.WriteAsync(content, columns.Select(c => (c.Name, InlineColumnTypes.ClrType(c.Type))).ToList(), rows, ct).ConfigureAwait(false);
        content.Position = 0;
        await stores.WriteAsync(path, content, ct).ConfigureAwait(false);
    }
}
