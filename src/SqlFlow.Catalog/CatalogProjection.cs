using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlFlow.Catalog;

/// <summary>
/// Pure projections from the git/disk source of truth into shadow entities: a pipeline from its estate fields,
/// and a run from its on-disk <c>run.json</c> envelope. No EF, no IO, no clock - everything is passed in - so the
/// mapping is unit-testable in isolation. The header fields follow the stable run.json contract; metric fields
/// are read best-effort from the kind-specific result and left null when a kind does not report them.
/// </summary>
public static class CatalogProjection
{
    /// <summary>Projects a discovered estate flow into a pipeline row (Active, stamped at <paramref name="nowUtc"/>).
    /// The YAML and definition JSON are passed already secret-redacted by the caller.</summary>
    public static CatalogPipeline Pipeline(
        Guid repoId, string name, string kind, string? batch, string relativePath,
        string? sourceReference, string? targetReference, string contentHash, string yaml, string definitionJson, DateTime nowUtc,
        Core.Runs.ExecutionMode executionMode = Core.Runs.ExecutionMode.Auto,
        Core.Runs.FlowLifecycle lifecycle = Core.Runs.FlowLifecycle.Production)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind ?? string.Empty,
            Batch = NullIfBlank(batch),
            RelativePath = relativePath ?? string.Empty,
            ExecutionMode = PipelineExecutionModes.From(executionMode),
            Lifecycle = PipelineLifecycles.From(lifecycle),
            SourceServer = NullIfBlank(sourceReference),
            TargetServer = NullIfBlank(targetReference),
            ContentHash = contentHash ?? string.Empty,
            Yaml = yaml ?? string.Empty,
            DefinitionJson = definitionJson ?? string.Empty,
            Active = true,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
        };
    }

    /// <summary>Lowercase-hex SHA-256 of the (already redacted) text: the catalog's change-detection hash.</summary>
    public static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    /// <summary>Projects the run's canonical event timeline (the top-level <c>events</c> array of run.json) into
    /// catalog rows with 1-based ordinals in execution order. An entry missing a message or timestamp is skipped.</summary>
    public static IReadOnlyList<CatalogRunEvent> RunEvents(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "events") is not { ValueKind: JsonValueKind.Array } events)
        {
            return [];
        }

        var list = new List<CatalogRunEvent>();
        foreach (var entry in events.EnumerateArray())
        {
            var message = Str(entry, "message");
            if (string.IsNullOrWhiteSpace(message) || Date(entry, "timestampUtc") is not { } timestampUtc)
            {
                continue;
            }

            list.Add(new CatalogRunEvent
            {
                RunId = runId,
                RepoId = repoId,
                Ordinal = list.Count + 1,
                TimestampUtc = timestampUtc,
                Level = Truncate(Str(entry, "level") ?? "info", 16),
                Step = NullIfBlank(Str(entry, "step")) is { } step ? Truncate(step, 128) : null,
                Message = message,
                Rows = Long(entry, "rows"),
                ElapsedMs = Dbl(entry, "elapsedMs"),
            });
        }

        return list;
    }

    /// <summary>Projects a run.json root into a run row, or null when the header is not a valid artifact.</summary>
    public static CatalogRun? RunFromJson(JsonElement root, Guid repoId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // runId must be a GUID string; a missing, non-string, or non-GUID value is a corrupt/foreign artifact.
        // (TryGetGuid throws on a non-String element, so the ValueKind guard is required, not just a null check.)
        if (Prop(root, "runId") is not { ValueKind: JsonValueKind.String } runIdElement || !runIdElement.TryGetGuid(out var runId))
        {
            return null;
        }

        var flowName = Str(root, "flowName");
        var flowKind = Str(root, "flowKind");
        if (string.IsNullOrWhiteSpace(flowName) || string.IsNullOrWhiteSpace(flowKind))
        {
            return null;
        }

        var result = Prop(root, "result");
        var success = Bool(root, "success") ?? false;

        return new CatalogRun
        {
            RunId = runId,
            PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = flowKind,
            Success = success,
            // A run read straight from its on-disk artifact is already finished, so it is born in a terminal state.
            Status = success ? RunStatuses.Succeeded : RunStatuses.Failed,
            SchemaVersion = Int(Long(root, "schemaVersion")),
            WrittenUtc = Date(root, "writtenUtc") ?? default,
            Error = NullIfBlank(Str(root, "error")),
            StartUtc = result is { } r1 ? Date(r1, "startTimeUtc") : null,
            EndUtc = result is { } r2 ? Date(r2, "endTimeUtc") : null,
            DurationSeconds = DurationSeconds(result),
            // Each kind names its own headline count; the first field the artifact carries wins.
            RowsLoaded = result is { } r3 ? Long(r3, "rowsLoaded") ?? Long(r3, "totalRows") ?? Long(r3, "delivered") : null,
            // An artifact reaching the catalog without ever having been enqueued is a local CLI run that was synced
            // in afterwards. The queue overwrites this on completion for runs it dispatched, so only genuinely
            // node-local executions keep it.
            TriggerSource = RunTriggerSources.Cli,
            RowsInserted = result is { } r4 ? Long(r4, "rowsInserted") : null,
            RowsUpdated = result is { } r5 ? Long(r5, "rowsUpdated") : null,
            RowsDeleted = result is { } r6 ? Long(r6, "rowsDeleted") : null,
            Host = NullIfBlank(Str(root, "host")),
            // The delivery kind's own counts (DeliverOutcome and VerifyRunOutcome in SqlFlow.Delivery): a deliver run
            // reports what it planned and did to records, a verify run what it checked and found.
            ResultSubmissionId = result is { } r7 && Prop(r7, "submissionId") is { ValueKind: JsonValueKind.String } sid && sid.TryGetGuid(out var submissionId) ? submissionId : null,
            RecordsPlanned = result is { } r8 ? Count(Long(r8, "planned") ?? Long(r8, "checked")) : null,
            RecordsDelivered = result is { } r9 ? Count(Long(r9, "delivered") ?? Long(r9, "matched")) : null,
            RecordsHeld = result is { } r10 ? Count(Long(r10, "held") ?? Sum(Long(r10, "drifted"), Long(r10, "missing"))) : null,
            RecordsFailed = result is { } r11 ? Count(Long(r11, "failed") ?? Long(r11, "errors")) : null,
            RecordsSkipped = result is { } r12 ? Count(Long(r12, "skippedUnchanged")) : null,
        };
    }

    private static double? DurationSeconds(JsonElement? result)
    {
        if (result is not { } r)
        {
            return null;
        }

        if (Long(r, "durationSeconds") is { } seconds)
        {
            return seconds;
        }

        if (Prop(r, "totalMs") is { } ms && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var totalMs))
        {
            return totalMs / 1000.0;
        }

        return null;
    }

    private static JsonElement? Prop(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? Str(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;

    private static bool? Bool(JsonElement element, string name)
        => Prop(element, name) is { } p && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static long? Long(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var v) ? v : null;

    private static double? Dbl(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out var v) ? v : null;

    private static DateTime? Date(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.String } p && p.TryGetDateTime(out var v) ? v : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A long count saturated into int range, so an oversized run.json value never silently overflows.</summary>
    private static int Int(long? value) => (int)Math.Min(value ?? 0, int.MaxValue);

    private static int? Count(long? value) => value is { } v ? (int)Math.Clamp(v, 0, int.MaxValue) : null;

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
