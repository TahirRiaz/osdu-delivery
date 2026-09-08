using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.KnownState;

/// <summary>
/// Publishes the compact known state of a flow (design.md section 6.7): one parquet file the preparing side reads
/// at the start of its next run to decide what changed, plus a JSON summary. Streams the ledger in key order into
/// row groups, spooled through a temporary file, so a flow of any size publishes in bounded memory.
/// </summary>
public sealed class KnownStatePublisher
{
    public const string FileName = "known-state.parquet";
    public const string SummaryName = "known-state.json";

    /// <summary>Rows per parquet row group.</summary>
    public const int RowGroupSize = 50_000;

    private readonly ILedger _ledger;
    private readonly FileStoreRegistry _stores;
    private readonly TimeProvider _time;
    private readonly ILogger<KnownStatePublisher> _logger;

    public KnownStatePublisher(ILedger ledger, FileStoreRegistry stores, TimeProvider time, ILogger<KnownStatePublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _ledger = ledger;
        _stores = stores;
        _time = time;
        _logger = logger;
    }

    public async Task<long> PublishAsync(FlowDefinition flow, string location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var root = location.TrimEnd('/', '\\');
        var dataPath = Join(root, FileName);
        var summaryPath = Join(root, SummaryName);

        var columns = new (string Name, Type ClrType)[]
        {
            ("deliveryKey", typeof(string)),
            ("sourceKey", typeof(string)),
            ("sourceFingerprint", typeof(string)),
            ("metadataHash", typeof(string)),
            ("payloadHash", typeof(string)),
            ("status", typeof(string)),
            ("targetId", typeof(string)),
            ("targetVersion", typeof(long)),
        };

        long total = 0, delivered = 0, held = 0, pending = 0, failed = 0;
        var spool = Path.Combine(Path.GetTempPath(), "osdu-delivery-known-state-" + Guid.NewGuid().ToString("N") + ".parquet");
        try
        {
            await using (var file = new FileStream(spool, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await using var writer = await ParquetRowGroupWriter.CreateAsync(file, columns, ct).ConfigureAwait(false);
                var rows = new List<IReadOnlyDictionary<string, object?>>(RowGroupSize);
                await foreach (var r in _ledger.StreamKnownStateAsync(flow.Id, RowGroupSize, ct).ConfigureAwait(false))
                {
                    total++;
                    switch (r.Status)
                    {
                        case RecordStatus.Delivered:
                            delivered++;
                            break;
                        case RecordStatus.Held:
                            held++;
                            break;
                        case RecordStatus.Pending or RecordStatus.Delivering:
                            pending++;
                            break;
                        case RecordStatus.Failed:
                            failed++;
                            break;
                    }

                    rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["deliveryKey"] = r.DeliveryKey.Value.ToString("D"),
                        ["sourceKey"] = r.SourceKey,
                        ["sourceFingerprint"] = r.SourceFingerprint,
                        ["metadataHash"] = r.MetadataHash,
                        ["payloadHash"] = r.PayloadHash,
                        ["status"] = r.Status.ToString().ToLowerInvariant(),
                        ["targetId"] = r.TargetId,
                        ["targetVersion"] = r.TargetVersion,
                    });
                    if (rows.Count >= RowGroupSize)
                    {
                        await writer.WriteRowGroupAsync(rows, ct).ConfigureAwait(false);
                        rows.Clear();
                    }
                }

                // A flow with no records still publishes an empty, well-formed file.
                if (rows.Count > 0 || total == 0)
                {
                    await writer.WriteRowGroupAsync(rows, ct).ConfigureAwait(false);
                }
            }

            await using (var spooled = new FileStream(spool, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _stores.WriteAsync(dataPath, spooled, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                File.Delete(spool);
            }
            catch (IOException)
            {
                // The temp directory is cleaned up on its own schedule.
            }
        }

        var summary = new JsonObject
        {
            ["flow"] = flow.Name,
            ["flowId"] = flow.Id.ToString("D"),
            ["publishedUtc"] = CanonicalJson.FormatDateTime(_time.GetUtcNow()),
            ["records"] = total,
            ["delivered"] = delivered,
            ["held"] = held,
            ["pending"] = pending,
            ["failed"] = failed,
            ["file"] = FileName,
        };
        using (var summaryStream = new MemoryStream(Encoding.UTF8.GetBytes(CanonicalJson.Pretty(summary))))
        {
            await _stores.WriteAsync(summaryPath, summaryStream, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Published known state for {Flow}: {Count} record(s) to {Location}.", flow.Name, total.ToString(CultureInfo.InvariantCulture), dataPath);
        return total;
    }

    private static string Join(string root, string name)
        => root.Contains("://", StringComparison.Ordinal) ? root + "/" + name : Path.Combine(root, name);
}
