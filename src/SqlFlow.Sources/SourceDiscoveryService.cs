using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>What the caller wants discovered: a file/folder location plus the optional format, folder scan controls,
/// record-grain override (JSON/XML), and sample bounds. A blank <see cref="Format"/> means auto-detect.</summary>
public sealed record SourceDiscoveryRequest(
    string Location, string? Format, string? Pattern, bool Recursive, string? RootPath,
    int MaxFiles, int MaxRecords, int MaxDepth);

/// <summary>One discovered output column and, for a flattened nested source, the path it came from.</summary>
public sealed record DiscoveredColumn(string Name, string SqlType, bool Nullable, string? SourcePath);

/// <summary>
/// The unified discovery outcome. <see cref="Mode"/> is "flatten" for nested sources (JSON/XML: the response
/// carries <see cref="Paths"/> and a record grain) or "columnar" for tabular sources (CSV/Excel/Parquet: just
/// <see cref="Columns"/>). Both modes always carry the runnable <see cref="GeneratedYaml"/>.
/// </summary>
public sealed record SourceDiscoveryResult(
    string Mode, string SourceType, string DetectionConfidence, IReadOnlyList<string> DetectionEvidence,
    int FilesScanned, int RecordsScanned, string? AutoDetectedGrain, bool SchemaDrift,
    IReadOnlyList<SchemaPath> Paths, IReadOnlyList<DiscoveredColumn> Columns,
    IReadOnlyList<KeyValuePair<string, string>> Options, string GeneratedYaml);

/// <summary>
/// One entry point that discovers any supported file source and generates its ingestion YAML. It resolves the
/// format (explicit, then extension, then content sniffing), then dispatches: a nested source (JSON/XML) goes to the
/// flatten introspector for path discovery and a flatten formula; a tabular source (CSV/Excel/Parquet) has its
/// column schema read from a representative file and rendered as a straight columnar flow. The reader abstraction
/// (<see cref="ISourceReader"/>) is what makes "one command, any format" possible - every format already exposes its
/// schema through the same interface.
/// </summary>
public sealed class SourceDiscoveryService
{
    private const string DefaultColumnType = "nvarchar(4000)";

    private readonly IReadOnlyList<ISourceReader> _readers;
    private readonly IReadOnlyList<IFileStore> _fileStores;
    private readonly ISqlTypeMapper _typeMapper;

    public SourceDiscoveryService(IEnumerable<ISourceReader> readers, IEnumerable<IFileStore> fileStores, ISqlTypeMapper typeMapper)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(fileStores);
        _readers = readers.ToList();
        _fileStores = fileStores.ToList();
        _typeMapper = typeMapper ?? throw new ArgumentNullException(nameof(typeMapper));
    }

    /// <summary>Discovers the source at <paramref name="request"/> and returns its structure plus generated YAML.
    /// Throws <see cref="SqlFlowException"/> for a caller-fixable problem (bad location, unknown format, empty selection).</summary>
    public async Task<SourceDiscoveryResult> DiscoverAsync(SourceDiscoveryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var location = request.Location?.Trim() ?? string.Empty;
        if (location.Length == 0)
        {
            throw new SqlFlowException("A discover requires a non-blank location (a file, folder, or URI).");
        }

        var store = _fileStores.FirstOrDefault(s => s.CanHandle(location))
            ?? throw new SqlFlowException($"No file store handles location '{location}'.");

        var (type, confidence, evidence, csvDelimiter) = await ResolveFormatAsync(store, location, request, ct).ConfigureAwait(false);

        var (specType, options) = FlattenSourceSpec.BuildBase(location, type, request.Pattern, request.Recursive, request.RootPath);
        if (csvDelimiter is not null && specType == "csv")
        {
            options["delimiter"] = csvDelimiter;
        }

        var spec = new SourceSpec { Type = specType, Location = location, Options = options };

        var introspector = FlattenSourceSpec.IntrospectorFor(_readers, specType);
        return introspector is not null
            ? await FlattenAsync(introspector, spec, request, confidence, evidence, ct).ConfigureAwait(false)
            : await ColumnarAsync(spec, store, request, confidence, evidence, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves the source type in priority order: explicit format, then the location's own extension, then
    /// a listed representative file's extension, then content sniffing of that file's head.</summary>
    private async Task<(string Type, string Confidence, List<string> Evidence, string? CsvDelimiter)> ResolveFormatAsync(
        IFileStore store, string location, SourceDiscoveryRequest request, CancellationToken ct)
    {
        // An explicit "tsv" is comma-CSV's tab-delimited sibling: same reader, a pinned delimiter.
        var explicitFormat = request.Format?.Trim().ToLowerInvariant();
        if (explicitFormat == "tsv")
        {
            return ("csv", "explicit", ["format specified explicitly: TSV (tab-delimited)"], "\t");
        }

        if (!string.IsNullOrWhiteSpace(explicitFormat))
        {
            var (pinnedType, _) = FlattenSourceSpec.BuildBase(location, explicitFormat, request.Pattern, request.Recursive, null);
            return (pinnedType, "explicit", [$"format specified explicitly: {pinnedType}"], null);
        }

        // The location's own last segment often names the format (a single file, or a URI ending in .json).
        var fromLocation = SourceFormatDetector.TypeFromExtension(FlattenSourceSpec.UriLastSegment(location));
        if (fromLocation is not null)
        {
            return (fromLocation, "extension", [$"file extension of the location ({fromLocation})"], null);
        }

        // Otherwise list a representative file and try its extension, then sniff its bytes.
        var sample = await FirstFileAsync(store, location, request.Pattern ?? "*", request.Recursive, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(EmptySelectionMessage(location, request.Pattern));

        var fromSample = SourceFormatDetector.TypeFromExtension(sample.Name);
        if (fromSample is not null)
        {
            return (fromSample, "extension", [$"file extension of '{sample.Name}' ({fromSample})"], null);
        }

        var head = await ReadHeadAsync(store, sample, SourceFormatDetector.HeadSampleBytes, ct).ConfigureAwait(false);
        var detection = SourceFormatDetector.FromContent(head, sample.Name);
        if (detection.Type is null)
        {
            throw new SqlFlowException(
                $"Could not determine the format of '{sample.Name}'"
                + (detection.Evidence.Count > 0 ? $" ({string.Join("; ", detection.Evidence)})" : string.Empty)
                + ". Choose a format explicitly.");
        }

        var evidence = new List<string> { $"content-detected from '{sample.Name}'" };
        evidence.AddRange(detection.Evidence);
        return (detection.Type, detection.Confidence, evidence, detection.CsvDelimiter);
    }

    private async Task<SourceDiscoveryResult> FlattenAsync(
        IFlattenIntrospector introspector, SourceSpec spec, SourceDiscoveryRequest request,
        string confidence, List<string> evidence, CancellationToken ct)
    {
        var introspection = await introspector
            .IntrospectAsync(spec, request.MaxFiles, request.MaxRecords, request.MaxDepth, ct).ConfigureAwait(false);
        var inventory = introspection.Inventory;

        var columns = introspection.Formula.Columns
            .Select(c => new DiscoveredColumn(c.Name, c.IsLargeText ? "nvarchar(max)" : DefaultColumnType, true, c.SourcePath))
            .ToList();

        return new SourceDiscoveryResult(
            "flatten", introspection.SourceType, confidence, evidence,
            inventory.FilesScanned, inventory.RecordsScanned, introspection.AutoDetectedGrain,
            inventory.Paths.Any(p => p.RecordCount < inventory.RecordsScanned),
            inventory.Paths, columns, introspection.Options, FlattenFlowYaml.Build(spec, introspection));
    }

    private async Task<SourceDiscoveryResult> ColumnarAsync(
        SourceSpec spec, IFileStore store, SourceDiscoveryRequest request,
        string confidence, List<string> evidence, CancellationToken ct)
    {
        var reader = _readers.FirstOrDefault(r => r.CanHandle(spec.Type))
            ?? throw new SqlFlowException(
                $"No reader handles '{spec.Type}'. Supported: JSON, XML, CSV, Excel (xls/xlsx), Parquet.");

        // Read the schema from one representative file (bounded), not the whole folder: a raw landing folder's files
        // share a schema, and a real run unions and NULL-fills any drift at load time anyway.
        var effectivePattern = request.Pattern ?? FlattenSourceSpec.DefaultPatternFor(spec.Type);
        var sample = await FirstFileAsync(store, spec.Location!, effectivePattern, request.Recursive, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(EmptySelectionMessage(spec.Location!, effectivePattern));

        // Probe the sample with provenance/key columns off so the reported columns are the source's own, not the
        // generated _DW columns; the rendered YAML keeps the provenance defaults a real ingestion uses.
        var probeOptions = new Dictionary<string, string?>(spec.Options, StringComparer.OrdinalIgnoreCase);
        foreach (var key in FlattenSourceSpec.ProvenanceOptionKeys)
        {
            probeOptions[key] = "false";
        }

        var probeSpec = spec with { Location = sample.Path, Options = probeOptions };
        IReadOnlyList<SourceColumn> sourceColumns;
        try
        {
            sourceColumns = await reader.GetColumnsAsync(probeSpec, ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync(probeSpec, ct).ConfigureAwait(false);
        }

        var columns = sourceColumns
            .Select(c => new DiscoveredColumn(c.Name, _typeMapper.Map(c, null, DefaultColumnType).SqlType, c.IsNullable, null))
            .ToList();

        var yaml = FlattenFlowYaml.BuildColumnar(spec, spec.Type, sourceColumns, _typeMapper, filesScanned: 1);
        var options = spec.Options.Select(o => new KeyValuePair<string, string>(o.Key, o.Value ?? string.Empty)).ToList();

        return new SourceDiscoveryResult(
            "columnar", spec.Type, confidence, evidence,
            1, 0, null, false, [], columns, options, yaml);
    }

    /// <summary>The first file (name-ordered) under a location matching the pattern, or null when nothing matches.</summary>
    private static async Task<FileRef?> FirstFileAsync(
        IFileStore store, string location, string pattern, bool recursive, CancellationToken ct)
    {
        var files = await store
            .ListAsync(location, new FileDiscovery { Pattern = pattern, Recursive = recursive }, ct).ConfigureAwait(false);
        return files.OrderBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    private static async Task<byte[]> ReadHeadAsync(IFileStore store, FileRef file, int max, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == max ? buffer : buffer[..total];
    }

    private static string EmptySelectionMessage(string location, string? pattern)
        => pattern is null
            ? $"No files found under '{location}'."
            : $"No files under '{location}' match pattern '{pattern}'.";
}
