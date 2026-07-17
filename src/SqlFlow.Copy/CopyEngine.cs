using System.Diagnostics;
using System.IO.Compression;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// Runs a copy flow: for each of the flow's steps it selects the source and target endpoints by their location
/// scheme, lists the matched source files, and moves them - verbatim (<c>copy</c>), bundled into one archive
/// (<c>zip</c>), or extracted from archives (<c>unzip</c>) - aggregating the matched/written counts across every
/// step so one pipeline can copy a whole source system in one run. It is direction-agnostic: any endpoint pair works,
/// because the transformation happens on the bytes between an endpoint read and an endpoint write. It never throws
/// for a transfer failure; a failed/partial <see cref="CopyRunResult"/> is returned so a batch member behaves like a
/// directly-invoked flow.
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

    public async Task<CopyRunResult> RunAsync(
        CopyFlow flow, Guid runId, IRunEventSink log, CancellationToken ct, RunParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var runParams = parameters ?? RunParameters.None;
        var now = _time.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var written = new List<CopyFileResult>();
        var matched = 0;

        try
        {
            // One flow performs many steps in order; each is an independent source-to-target transfer. The matched
            // and written counts aggregate across every step, so one pipeline that copies a whole source system
            // reports as a single run.
            var multiStep = flow.Steps.Count > 1;
            for (var i = 0; i < flow.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = flow.Steps[i];
                var source = Select(step.Source.Location, "source");
                var target = Select(step.Target.Location, "target");

                var window = ResolveWindow(step.Source, runParams, now);
                var items = new List<CopyItem>();
                await foreach (var item in source.ListAsync(step.Source, window, ct).ConfigureAwait(false))
                {
                    items.Add(item);
                }

                matched += items.Count;
                log.Log(RunLogLevel.Info, "copy.list",
                    $"matched {items.Count} file(s) at '{step.Source.Location}'{DescribeWindow(window)}.");

                switch (flow.Operation)
                {
                    case CopyOperation.Copy:
                        await CopyAsync(flow, step, source, target, items, written, log, ct).ConfigureAwait(false);
                        break;
                    case CopyOperation.Zip:
                        await ZipAsync(flow, step, i, multiStep, source, target, items, written, log, ct).ConfigureAwait(false);
                        break;
                    case CopyOperation.Unzip:
                        await UnzipAsync(flow, step, source, target, items, written, log, ct).ConfigureAwait(false);
                        break;
                }
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
        // A cancellation is deliberately NOT caught here: cancelled is a distinct terminal state from failed, and it
        // is the caller that knows which one this is (an operator cancel of a queued run vs. a node shutdown). Turning
        // it into a failed result here would record an operator's cancel as a failure and strand a shutdown-interrupted
        // run that should be requeued, so it propagates to RunWorker / the CLI, which own that decision.
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SqlFlowException or IOException or InvalidOperationException)
        {
            sw.Stop();
            var message = SecretHygiene.RedactedMessage(ex.Message);
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
        CopyFlow flow, CopyStep step, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
            var relative = flow.Options.PreserveStructure ? item.RelativePath : item.Name;
            var location = await target.WriteAsync(step.Target, relative, bytes, flow.Options.Overwrite, ct).ConfigureAwait(false);
            written.Add(new CopyFileResult(location, bytes.Length));
            log.Log(RunLogLevel.Info, "copy.write", $"copied {bytes.Length} byte(s) -> '{location}'.");
        }
    }

    private async Task ZipAsync(
        CopyFlow flow, CopyStep step, int stepIndex, bool multiStep, ICopyEndpoint source, ICopyEndpoint target,
        IReadOnlyList<CopyItem> items, List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            log.Log(RunLogLevel.Info, "copy.zip", $"no files matched at '{step.Source.Location}'; no archive written.");
            return;
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
                var entryName = (flow.Options.PreserveStructure ? item.RelativePath : item.Name).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }

        // A single explicit ZipName names the one archive; across several steps it is per-step (the source leaf keeps
        // each step's archive distinct) so multiple steps never overwrite one bundle.
        var zipName = !multiStep && !string.IsNullOrWhiteSpace(flow.Options.ZipName)
            ? flow.Options.ZipName!
            : $"{flow.Name}_{SourceLeaf(step.Source.Location)}_{_time.GetUtcNow():yyyyMMddHHmmss}.zip";
        var payload = buffer.ToArray();
        var location = await target.WriteAsync(step.Target, zipName, payload, flow.Options.Overwrite, ct).ConfigureAwait(false);
        written.Add(new CopyFileResult(location, payload.Length));
        log.Log(RunLogLevel.Info, "copy.zip", $"zipped {items.Count} file(s) ({payload.Length} byte(s)) -> '{location}'.");
    }

    private static async Task UnzipAsync(
        CopyFlow flow, CopyStep step, ICopyEndpoint source, ICopyEndpoint target, IReadOnlyList<CopyItem> items,
        List<CopyFileResult> written, IRunEventSink log, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await source.ReadAsync(step.Source, item.AbsolutePath, ct).ConfigureAwait(false);
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
                var location = await target.WriteAsync(step.Target, relative, payload, flow.Options.Overwrite, ct).ConfigureAwait(false);
                written.Add(new CopyFileResult(location, payload.Length));
                log.Log(RunLogLevel.Info, "copy.unzip", $"extracted {payload.Length} byte(s) -> '{location}'.");
            }
        }
    }

    private static string SourceLeaf(string location)
    {
        var trimmed = location.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        // Keep only path-safe characters so the archive name is always writable (an abfss authority leaf could carry
        // an '@' or ':'); fall back to the step position when nothing usable remains.
        var clean = new string(leaf.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return clean.Length > 0 ? clean : "step";
    }

    /// <summary>
    /// The effective modified-date filter for a source step: an operational backfill window (from the run's
    /// parameters) overrides the flow's declared <c>modifiedWithinDays</c>; a full-load run copies everything the
    /// definition selects (no date filter); otherwise the endpoint's <c>modifiedWithinDays</c> default applies,
    /// computed against the engine's clock. <c>filePattern</c> and <c>assertionsOnly</c> have no meaning for a
    /// byte-for-byte copy and are ignored here (the run log still records the supplied parameters).
    /// </summary>
    private static CopyModifiedWindow ResolveWindow(CopyEndpoint source, RunParameters parameters, DateTimeOffset now)
    {
        if (parameters.BackfillFrom is { } from)
        {
            var to = parameters.BackfillTo is { } t
                ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc))
                : (DateTimeOffset?)null;
            return new CopyModifiedWindow(new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)), to);
        }

        if (parameters.FullLoad)
        {
            return CopyModifiedWindow.Unbounded;
        }

        return source.ModifiedWithinDays > 0
            ? new CopyModifiedWindow(now.AddDays(-source.ModifiedWithinDays), null)
            : CopyModifiedWindow.Unbounded;
    }

    private static string DescribeWindow(CopyModifiedWindow window)
        => window switch
        {
            { From: { } f, To: { } t } => $" (modified {f:yyyy-MM-dd} .. {t:yyyy-MM-dd})",
            { From: { } f } => $" (modified since {f:yyyy-MM-dd})",
            { To: { } t } => $" (modified until {t:yyyy-MM-dd})",
            _ => string.Empty,
        };

    private ICopyEndpoint Select(string location, string side)
        => _endpoints.FirstOrDefault(e => e.CanHandle(location))
           ?? throw new SqlFlowException(
               $"No copy endpoint handles the {side} location '{location}'. Use a local/UNC path or an Azure storage URI "
               + "(abfss://…/https://…). For SFTP, use the dedicated sftp flow type.");
}
