using System.Diagnostics;
using System.IO.Compression;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// Runs a copy flow: selects the source and target endpoints by their location scheme, lists the matched source
/// files, and moves them - verbatim (<c>copy</c>), bundled into one archive (<c>zip</c>), or extracted from archives
/// (<c>unzip</c>). It is direction-agnostic: any endpoint pair works, because the transformation happens on the bytes
/// between an endpoint read and an endpoint write. It never throws for a transfer failure; a failed/partial
/// <see cref="CopyRunResult"/> is returned so a batch member behaves like a directly-invoked flow.
/// </summary>
public sealed class CopyEngine
{
    private readonly IReadOnlyList<ICopyEndpoint> _endpoints;
    private readonly TimeProvider _time;

    public CopyEngine(IEnumerable<ICopyEndpoint> endpoints, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(time);
        _endpoints = endpoints.ToList();
        _time = time;
    }

    public async Task<CopyRunResult> RunAsync(CopyFlow flow, Guid runId, IRunEventSink log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var sw = Stopwatch.StartNew();
        var written = new List<CopyFileResult>();
        var matched = 0;

        try
        {
            var source = Select(flow.Source.Location, "source");
            var target = Select(flow.Target.Location, "target");

            var items = new List<CopyItem>();
            await foreach (var item in source.ListAsync(flow.Source, ct).ConfigureAwait(false))
            {
                items.Add(item);
            }

            matched = items.Count;
            log.Log(RunLogLevel.Info, "copy.list", $"matched {matched} file(s) at '{flow.Source.Location}'.");

            switch (flow.Operation)
            {
                case CopyOperation.Copy:
                    await CopyAsync(flow, source, target, items, written, log, ct).ConfigureAwait(false);
                    break;
                case CopyOperation.Zip:
                    await ZipAsync(flow, source, target, items, written, log, ct).ConfigureAwait(false);
                    break;
                case CopyOperation.Unzip:
                    await UnzipAsync(flow, source, target, items, written, log, ct).ConfigureAwait(false);
                    break;
            }

            sw.Stop();
            return new CopyRunResult
            {
                RunId = runId,
                Success = true,
                DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched,
                FilesWritten = written.Count,
                BytesWritten = written.Sum(f => f.SizeBytes),
                Files = written,
            };
        }
        catch (Exception ex) when (ex is SqlFlowException or IOException or OperationCanceledException or InvalidOperationException)
        {
            sw.Stop();
            var message = ex is OperationCanceledException ? "run cancelled" : SecretHygiene.RedactedMessage(ex.Message);
            log.Log(RunLogLevel.Info, "copy.error", message);
            return new CopyRunResult
            {
                RunId = runId,
                Success = false,
                Error = message,
                DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched,
                FilesWritten = written.Count,
                BytesWritten = written.Sum(f => f.SizeBytes),
                Files = written,
            };
        }
    }

    private static async Task CopyAsync(
        CopyFlow flow, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await source.ReadAsync(flow.Source, item.AbsolutePath, ct).ConfigureAwait(false);
            var relative = flow.Options.PreserveStructure ? item.RelativePath : item.Name;
            var location = await target.WriteAsync(flow.Target, relative, bytes, flow.Options.Overwrite, ct).ConfigureAwait(false);
            written.Add(new CopyFileResult(location, bytes.Length));
            log.Log(RunLogLevel.Info, "copy.write", $"copied {bytes.Length} byte(s) -> '{location}'.");
        }
    }

    private async Task ZipAsync(
        CopyFlow flow, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            log.Log(RunLogLevel.Info, "copy.zip", "no files matched; no archive written.");
            return;
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await source.ReadAsync(flow.Source, item.AbsolutePath, ct).ConfigureAwait(false);
                var entryName = (flow.Options.PreserveStructure ? item.RelativePath : item.Name).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }

        var zipName = string.IsNullOrWhiteSpace(flow.Options.ZipName)
            ? $"{flow.Name}_{_time.GetUtcNow():yyyyMMddHHmmss}.zip"
            : flow.Options.ZipName!;
        var payload = buffer.ToArray();
        var location = await target.WriteAsync(flow.Target, zipName, payload, flow.Options.Overwrite, ct).ConfigureAwait(false);
        written.Add(new CopyFileResult(location, payload.Length));
        log.Log(RunLogLevel.Info, "copy.zip", $"zipped {items.Count} file(s) ({payload.Length} byte(s)) -> '{location}'.");
    }

    private static async Task UnzipAsync(
        CopyFlow flow, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await source.ReadAsync(flow.Source, item.AbsolutePath, ct).ConfigureAwait(false);
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            // Extract each archive under a folder named for the archive (minus its extension) when preserving
            // structure, so two archives' entries never collide; flat by entry name otherwise.
            var prefix = flow.Options.PreserveStructure
                ? Path.GetFileNameWithoutExtension(item.Name) + "/"
                : string.Empty;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                await using var entryStream = entry.Open();
                using var entryBuffer = new MemoryStream();
                await entryStream.CopyToAsync(entryBuffer, ct).ConfigureAwait(false);
                var relative = prefix + (flow.Options.PreserveStructure ? entry.FullName : entry.Name);
                var payload = entryBuffer.ToArray();
                var location = await target.WriteAsync(flow.Target, relative, payload, flow.Options.Overwrite, ct).ConfigureAwait(false);
                written.Add(new CopyFileResult(location, payload.Length));
                log.Log(RunLogLevel.Info, "copy.unzip", $"extracted {payload.Length} byte(s) -> '{location}'.");
            }
        }
    }

    private ICopyEndpoint Select(string location, string side)
        => _endpoints.FirstOrDefault(e => e.CanHandle(location))
           ?? throw new SqlFlowException(
               $"No copy endpoint handles the {side} location '{location}'. Use a local/UNC path or an Azure storage URI "
               + "(abfss://…/https://…). For SFTP, use the dedicated sftp flow type.");
}
