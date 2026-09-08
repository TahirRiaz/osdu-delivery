using System.Globalization;
using SqlFlow.Delivery.SampleDrop;

// Usage: SampleDrop <output-root> [logSource] [submissionId] [sourceVersion] [--variant changed|payload] [--partitions N]
// Writes <output-root>/<logSource>/... as a complete drop.
var root = args.Length > 0 ? args[0] : "samples/recall-welllog/out";
var logSource = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "demo";
var submissionId = args.Length > 2 && Guid.TryParse(args[2], out var parsed) ? parsed : new Guid("7d5a2d4c-3f0e-4b6b-9c1a-0d2e8f7a6b51");
var sourceVersion = args.Length > 3 && long.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 4012;
var variant = Array.IndexOf(args, "--variant") is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : "base";
var partitions = Array.IndexOf(args, "--partitions") is var pi && pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p >= 1 ? p : 1;

var records = SampleDropBuilder.DefaultRecords(logSource);
if (variant == "changed")
{
    // A source edit on the first record's metadata only: the payload hash stays put.
    var first = records[0] with { LogRun = "1A", UpdateDate = "2026-09-05T09:00:00Z" };
    records = [first, .. records.Skip(1)];
}
else if (variant == "payload")
{
    // A curve sample edit: the payload hash moves, the metadata document does not.
    var first = records[0];
    var gr = first.Curves[0] with { Values = [.. first.Curves[0].Values.Select(x => x + 1.0)] };
    first = first with { Curves = [gr, .. first.Curves.Skip(1)], UpdateDate = "2026-09-06T09:00:00Z" };
    records = [first, .. records.Skip(1)];
}

var target = Path.Combine(root, logSource);
var manifest = await SampleDropBuilder.WriteAsync(target, logSource, records, submissionId, sourceVersion, partitions).ConfigureAwait(false);
Console.WriteLine($"Wrote drop '{target}' ({manifest.RecordCount} records in {partitions} partition(s), submission {manifest.SubmissionId}, variant {variant}).");
foreach (var record in records)
{
    Console.WriteLine($"  {record.SourceProject}/{record.LogId} -> {record.Key}");
}
