using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The single sink every transport writes through: it enforces the empty-skip policy, renders the per-item raw path
/// from the landing template (binding <c>{page}</c>/<c>{item}</c>/<c>{runId}</c> and disambiguating collisions),
/// derives the file extension from the response format, optionally gzips, writes the raw bytes verbatim through the
/// selected <see cref="IRawLandingStore"/>, writes a redacted header sidecar when asked, and accumulates the run
/// counters and per-file manifest. It never transforms the payload: the raw incoming format is preserved.
/// </summary>
public sealed class LandingPipeline
{
    private readonly AcquireLanding _config;
    private readonly IRawLandingStore _store;
    private readonly string _base;
    private readonly string _runId;
    private readonly IRunEventSink _log;
    private readonly bool _dryRun;
    private readonly HashSet<string> _writtenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LandedFile> _files = [];

    public LandingPipeline(AcquireLanding config, IRawLandingStore store, string resolvedBase, Guid runId, IRunEventSink log, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _config = config;
        _store = store;
        _base = resolvedBase;
        _runId = runId.ToString("N");
        _log = log;
        _dryRun = dryRun;
    }

    public string ResolvedBase => _base;
    public int FilesWritten { get; private set; }
    public int Skipped { get; private set; }

    /// <summary>Files that landed to a location that already held byte-identical content, so the target was left
    /// untouched (no last-modified bump, no downstream re-trigger). A subset of <see cref="FilesWritten"/>.</summary>
    public int Unchanged { get; private set; }

    public long BytesWritten { get; private set; }
    public IReadOnlyList<LandedFile> Files => _files;

    /// <summary>Lands one fetched payload, or skips it when it is empty and skip-empty is on. Returns the written
    /// file, or null when the payload was skipped. In dry-run mode the path is resolved and counted but no bytes are
    /// written (the debugger's Test invoke), so the caller sees exactly what a production Invoke would land.</summary>
    public async Task<LandedFile?> LandAsync(LandedItem item, TemplateContext vars, CancellationToken ct)
    {
        if (_config.SkipEmpty && (item.RecordCount == 0 || (item.RecordCount < 0 && item.Content.Length == 0)))
        {
            Skipped++;
            _log.Log(RunLogLevel.Info, "landing.skip", $"skipped empty payload '{item.Discriminator}'.");
            return null;
        }

        var itemVars = vars.Clone()
            .WithString("page", item.Discriminator)
            .WithString("item", item.Discriminator)
            .WithString("runId", _runId);

        var relative = TemplateEngine.Render(_config.PathTemplate, itemVars).Trim('/');
        var extension = Extension(item.ContentType);
        var suffix = _config.Compression == AcquireCompression.Gzip ? ".gz" : string.Empty;
        var name = $"{relative}.{extension}{suffix}";

        // Guarantee pages never collide: if this exact relative path was already written this run, disambiguate with
        // the item discriminator (page index / object key) before the extension.
        if (!_writtenPaths.Add(name))
        {
            name = $"{relative}.{Sanitize(item.Discriminator)}.{extension}{suffix}";
            _writtenPaths.Add(name);
        }

        var payload = _config.Compression == AcquireCompression.Gzip ? Gzip(item.Content) : item.Content;
        var location = _store.Combine(_base, name);
        var wrote = true;
        if (!_dryRun)
        {
            wrote = await _store.PutAsync(location, payload, _config.Overwrite, _config.SkipUnchanged, ct).ConfigureAwait(false);
        }

        FilesWritten++;
        BytesWritten += payload.Length;
        var landed = new LandedFile(location, payload.Length, item.ContentType, item.RecordCount);
        _files.Add(landed);
        if (_dryRun)
        {
            _log.Log(RunLogLevel.Info, "landing.write", $"would land {payload.Length} bytes to '{location}'.");
        }
        else if (wrote)
        {
            _log.Log(RunLogLevel.Info, "landing.write", $"landed {payload.Length} bytes to '{location}'.");
        }
        else
        {
            Unchanged++;
            _log.Log(RunLogLevel.Info, "landing.unchanged", $"unchanged, not rewritten: '{location}'.");
        }

        if (!_dryRun && _config.PersistHeaders && item.Headers is { Count: > 0 })
        {
            var sidecar = _store.Combine(_base, $"{relative}.headers.json");
            var json = JsonSerializer.SerializeToUtf8Bytes(HeaderRedaction.Redact(item.Headers));
            await _store.PutAsync(sidecar, json, overwrite: true, skipUnchanged: _config.SkipUnchanged, ct: ct).ConfigureAwait(false);
        }

        return landed;
    }

    private string Extension(string? contentType)
    {
        if (!string.Equals(_config.Format, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return _config.Format.TrimStart('.').ToLowerInvariant();
        }

        var media = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return media switch
        {
            null or "" => "bin",
            "application/json" or "text/json" => "json",
            "application/xml" or "text/xml" => "xml",
            "text/csv" => "csv",
            "application/x-ndjson" or "application/jsonl" => "jsonl",
            _ when media.EndsWith("+json", StringComparison.Ordinal) => "json",
            _ when media.EndsWith("+xml", StringComparison.Ordinal) => "xml",
            _ when media.StartsWith("text/", StringComparison.Ordinal) => "txt",
            _ => "bin",
        };
    }

    private static ReadOnlyMemory<byte> Gzip(ReadOnlyMemory<byte> content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(content.Span);
        }

        return output.ToArray();
    }

    private static string Sanitize(string discriminator)
    {
        var builder = new StringBuilder(discriminator.Length);
        foreach (var c in discriminator)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return builder.Length == 0 ? "part" : builder.ToString();
    }
}
