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

/// <summary>What writing an inline submission's drop did: where the drop is, its manifest, whether this call wrote it, and what to tell the operator.</summary>
public sealed record InlineDropResult(string Location, DropManifest Manifest, bool Written, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes an inline submission out as a drop (design.md section 3.4), so the records a source sent in a request take the
/// path a prepared drop takes: the manifest check, the preflight gate, the change gates, the ledger and the drain. The
/// drop lives under the flow's work location, <c>{work}/inline/{submissionId}</c>, never at the flow's declared source
/// location, which belongs to the preparing side. It is written from the ledger's copy of the records whenever a run
/// takes the submission and finds no manifest there, the data files first and the manifest last, so a re-run after the
/// work location was cleaned up writes it again and no run reads a half-written one.
/// <para>
/// JSON leaves a column out where a drop writes a null, so the drop declares every column the mapping reads and every
/// column the flow names, and a column no record sent is null in every row. When the mapping iterates child scopes the
/// drop is keyed: each root row carries its delivery key (the one the record sent, or the one derived from its natural
/// key) and each child row its record's key, sorted, so the intake merge-joins them. A record whose natural key is
/// incomplete gets a key of its own for that join only; the renderer derives none, so the intake reports the record as
/// untracked exactly as it would for such a row in a prepared drop.
/// </para>
/// </summary>
public static class InlineDrop
{
    public const string Folder = "inline";

    public const string RecordFile = "record/part-00000.parquet";

    /// <summary>The namespace of the join keys given to records whose natural key is incomplete.</summary>
    private static readonly Guid UntrackedKeys = DeterministicGuid.Namespace("inline-submission-untracked-record");

    /// <summary>The drop location of an inline submission: under the flow's work location for these parameter values.</summary>
    public static string Location(FlowDefinition flow, IReadOnlyDictionary<string, string> values, Guid submissionId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var work = FlowParameters.WorkLocation(flow, values, FlowParameters.DropLocation(flow, values));
        return FileStoreRegistry.Join(FileStoreRegistry.Join(work, Folder), submissionId.ToString("D"));
    }

    /// <summary>
    /// Why <paramref name="flow"/> cannot take records sent in the request, or null when it can: a flow says so itself
    /// with <c>source.manualSubmission</c>, and a flow whose protocol streams payload files cannot say it at all.
    /// </summary>
    public static string? Refusal(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (Planner.PayloadName(flow) is { } payload)
        {
            return $"Flow '{flow.Name}' delivers payload files ('{payload}') through the {flow.Target.Protocol} protocol, and a submission of records carries metadata only; its records and their files are delivered as a drop.";
        }

        return flow.Source.ManualSubmission
            ? null
            : $"Flow '{flow.Name}' does not take records sent in the request: its document declares no 'source.manualSubmission'. Add 'manualSubmission: true' to the flow's source block to let records be submitted for it, or deliver them as a drop.";
    }

    public static string ScopeFile(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return "scopes/" + scope + "/part-00000.parquet";
    }

    /// <summary>
    /// Writes the drop at <paramref name="location"/> unless a manifest for the same submission is already there. The
    /// mapping is the resolved one the run renders with: the drop declares the columns it reads, and derives the
    /// delivery keys it joins child scopes by.
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
            throw new DeliveryException($"Inline submission {submission.SubmissionId:D} belongs to flow '{submission.FlowName}', not '{flow.Name}'.");
        }

        var root = new Drop(location.TrimEnd('/', '\\'), null!);
        var manifestPath = root.Resolve(flow.Source.Manifest);
        if (await stores.ExistsAsync(manifestPath, ct).ConfigureAwait(false))
        {
            var existing = await drops.OpenAsync(root.Location, flow.Source.Manifest, ct).ConfigureAwait(false);
            if (existing.Manifest.SubmissionId != submission.SubmissionId)
            {
                throw new DeliveryException(
                    $"{manifestPath} is the manifest of submission {existing.Manifest.SubmissionId:D}, not {submission.SubmissionId:D}; an inline submission's drop is written only by the runs that take it.");
            }

            return new InlineDropResult(root.Location, existing.Manifest, Written: false, []);
        }

        var records = InlineRecords.Parse(submission.RecordsJson);
        var read = MappingColumns.Read(mapping.Mapping);
        var warnings = new List<string>();
        var where = string.Create(CultureInfo.InvariantCulture, $"Inline submission {submission.SubmissionId:D}");

        // Every scope the mapping iterates is declared, sent or not; a scope the mapping does not iterate is not written.
        var sentScopes = records.ScopeColumns.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var ignored in sentScopes.Keys.Where(s => read.Scopes.All(m => !m.Scope.Equals(s, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"{where}: scope '{ignored}' is not one the mapping iterates, so its rows are not written to the drop.");
        }

        var keyed = read.Scopes.Count > 0;
        var keys = Keys(records, mapping.Renderer, submission, keyed);
        var flowColumns = new[] { flow.Source.LastModified, flow.Source.Fingerprint }.OfType<string>().ToList();
        var rootColumns = Declare(DropManifest.RootScope, records.RootColumns, read.Record, flowColumns, keyed, warnings, where);

        var order = Enumerable.Range(0, records.Records.Count).ToList();
        if (keyed)
        {
            // A keyed drop is written partitioned: the root file and every scope file sorted by the key's text.
            order = [.. order.OrderBy(i => keys[i], StringComparer.Ordinal)];
        }

        var scopes = new Dictionary<string, ManifestScope>(StringComparer.Ordinal);
        await WriteFileAsync(
            stores, root.Resolve(RecordFile), rootColumns,
            order.Select(i => Row(records.Records[i].Row, rootColumns, keyed ? keys[i] : null)).ToList(), ct).ConfigureAwait(false);
        scopes[DropManifest.RootScope] = new ManifestScope { Files = [RecordFile], Columns = Manifest(rootColumns) };

        foreach (var scope in read.Scopes)
        {
            var sent = sentScopes.TryGetValue(scope.Scope, out var columns) ? columns : [];
            var scopeColumns = Declare(scope.Scope, sent, scope.Columns, [], keyed: true, warnings, where);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var i in order)
            {
                if (records.Records[i].Scopes.TryGetValue(scope.Scope, out var childRows))
                {
                    rows.AddRange(childRows.Select(child => Row(child, scopeColumns, keys[i])));
                }
            }

            var file = ScopeFile(scope.Scope);
            await WriteFileAsync(stores, root.Resolve(file), scopeColumns, rows, ct).ConfigureAwait(false);
            scopes[scope.Scope] = new ManifestScope { Files = [file], Columns = Manifest(scopeColumns), ParentKey = DropReader.DeliveryKeyColumn };
        }

        var manifest = new DropManifest
        {
            SubmissionId = submission.SubmissionId,
            Flow = submission.FlowName,
            Mapping = submission.MappingReference,
            Parameters = new Dictionary<string, string>(submission.Parameters(), StringComparer.Ordinal),
            CreatedUtc = new DateTimeOffset(DateTime.SpecifyKind(submission.ReceivedUtc, DateTimeKind.Utc)),
            RecordCount = records.Records.Count,
            Partitioned = keyed,
            Scopes = scopes,
        };
        manifest.Validate(manifestPath);

        // The manifest is written last: its presence is what says the drop is complete.
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes(manifest.ToJson())))
        {
            await stores.WriteAsync(manifestPath, content, ct).ConfigureAwait(false);
        }

        return new InlineDropResult(root.Location, manifest, Written: true, warnings);
    }

    /// <summary>
    /// The key each record is written under: the one it sent, the one its natural key derives, or (in a keyed drop, for a
    /// record whose natural key is incomplete) a join key of its own. Two records that are the same record are refused.
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
            warnings.Add($"{where}: scope '{scope}' carries column(s) the mapping does not read, so they change nothing: {string.Join(", ", unread)}.");
        }

        if (missing.Count > 0)
        {
            warnings.Add($"{where}: no record sent column(s) of scope '{scope}' that the mapping or the flow reads, so they are null in every row: {string.Join(", ", missing)}.");
        }

        return columns;
    }

    private static IReadOnlyDictionary<string, object?> Row(IReadOnlyDictionary<string, object?> sent, IReadOnlyList<InlineColumn> columns, string? key)
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
