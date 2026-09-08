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
/// Publishes the compact known-state snapshot Databricks reads at the start of a prepare run (design.md section
/// 6.7): one parquet file of (deliveryKey, sourceKey, sourceFingerprint, metadataHash, payloadHash, status,
/// targetId, targetVersion) plus a small JSON summary. This is what turns a quiet run from a full Spark job into
/// almost nothing.
/// </summary>
public sealed class KnownStatePublisher
{
    public const string FileName = "known-state.parquet";
    public const string SummaryName = "known-state.json";

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

    /// <summary>Writes the snapshot under <paramref name="location"/> (a directory or blob prefix). Returns the row count.</summary>
    public async Task<int> PublishAsync(FlowDefinition flow, string location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var rows = await _ledger.KnownStateAsync(flow.Id, ct).ConfigureAwait(false);
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
        var records = rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["deliveryKey"] = r.DeliveryKey.Value.ToString("D"),
            ["sourceKey"] = r.SourceKey,
            ["sourceFingerprint"] = r.SourceFingerprint,
            ["metadataHash"] = r.MetadataHash,
            ["payloadHash"] = r.PayloadHash,
            ["status"] = r.Status.ToString().ToLowerInvariant(),
            ["targetId"] = r.TargetId,
            ["targetVersion"] = r.TargetVersion,
        }).ToList();

        using (var buffer = new MemoryStream())
        {
            await ParquetScopeReader.WriteAsync(buffer, columns, records, ct).ConfigureAwait(false);
            buffer.Position = 0;
            await _stores.WriteAsync(dataPath, buffer, ct).ConfigureAwait(false);
        }

        var summary = new JsonObject
        {
            ["flow"] = flow.Name,
            ["flowId"] = flow.Id.ToString("D"),
            ["publishedUtc"] = CanonicalJson.FormatDateTime(_time.GetUtcNow()),
            ["records"] = rows.Count,
            ["delivered"] = rows.Count(r => r.Status == RecordStatus.Delivered),
            ["held"] = rows.Count(r => r.Status == RecordStatus.Held),
            ["pending"] = rows.Count(r => r.Status is RecordStatus.Pending or RecordStatus.Delivering),
            ["failed"] = rows.Count(r => r.Status == RecordStatus.Failed),
            ["file"] = FileName,
        };
        using (var summaryStream = new MemoryStream(Encoding.UTF8.GetBytes(CanonicalJson.Pretty(summary))))
        {
            await _stores.WriteAsync(summaryPath, summaryStream, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Published known state for {Flow}: {Count} record(s) to {Location}.", flow.Name, rows.Count.ToString(CultureInfo.InvariantCulture), dataPath);
        return rows.Count;
    }

    private static string Join(string root, string name)
        => root.Contains("://", StringComparison.Ordinal) ? root + "/" + name : Path.Combine(root, name);
}
