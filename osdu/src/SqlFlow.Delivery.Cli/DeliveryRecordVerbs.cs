using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>records</c> verb: an interface's records from the ledger, and one record with every try it took. The GUI and
/// the API have shown a record's history from the start; this is the same history where an operator already is, on a
/// node or at a terminal, without a control plane to reach (the project's traceability rule, CLAUDE.md).
/// </summary>
internal static class DeliveryRecordVerbs
{
    /// <summary>Records listed when the command line asks for no count.</summary>
    private const int DefaultMax = 50;

    /// <summary>Tries shown for one record when the command line asks for no count.</summary>
    private const int DefaultAttempts = 20;

    public static async Task<int> RecordsAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the flow document whose records these are.");
        }

        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Records live in the module's database. Run 'sqlflow records' with --db <conn-ref>, or set the catalog variable.");

        var source = engine.Documents.LoadSource(flowPath);
        var flow = source.Interface(context.Arguments.GetOption("--interface"));
        var flowId = FlowId.Of(flow.LedgerName);
        var label = flow.Interface is null ? flow.Name : $"{source.Name} / {flow.Interface}";

        return verb switch
        {
            "list" => await ListAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            "show" => await ShowAsync(context, ledger, flowId, label, ct).ConfigureAwait(false),
            _ => context.UsageError("say what to do with the records: list or show."),
        };
    }

    /// <summary>The interface's records, newest first, with what each is waiting on or was refused for.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        var status = context.Arguments.GetOption("--status");
        var query = new RecordQuery
        {
            Search = context.Arguments.GetOption("--search"),
            Mode = context.Arguments.HasFlag("--contains") ? SearchMode.Contains : SearchMode.Prefix,
            Status = Status(status),
            Max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max"),
        };

        var records = await ledger.ListAsync(flowId, query, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["records"] = new JsonArray(records.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {records.Count} record(s)"));
        if (records.Count == 0)
        {
            context.Out.WriteLine("  none. A flow's records appear once its first submission has been planned.");
            return 0;
        }

        foreach (var record in records)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {record.DeliveryKey.Value:N}  {record.Status,-10}  {record.SourceKey}"));
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"      {record.TargetId ?? "(no OSDU id)"}{Version(record)}{Waiting(record)}"));
            if (record.LastError is { Length: > 0 } error)
            {
                context.Out.WriteLine("      " + One(error));
            }
        }

        return 0;
    }

    /// <summary>One record, and every try it took: what each sent, what the target answered, and why it stopped.</summary>
    private static async Task<int> ShowAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        if (context.Arguments.GetOption("--key") is not { } asked)
        {
            return context.UsageError("name the record with --key <delivery key or source key>.");
        }

        var max = Count(context.Arguments.GetOption("--attempts"), DefaultAttempts, "--attempts");
        var record = Guid.TryParse(asked, CultureInfo.InvariantCulture, out var key)
            ? await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false)
            : null;
        if (record is null)
        {
            // An operator holds the source key far more often than the delivery key, so the source key finds it too.
            var found = await ledger.ListAsync(flowId, new RecordQuery { Search = asked, Max = 2 }, ct).ConfigureAwait(false);
            record = found.Count switch
            {
                1 => found[0],
                0 => throw new FlowValidationException($"{label} has no record for '{asked}'."),
                _ => throw new FlowValidationException(
                    $"'{asked}' matches {found.Count} records of {label} ({string.Join(", ", found.Select(r => r.SourceKey))}); name one by its delivery key."),
            };
        }

        var attempts = await ledger.ListAttemptsAsync(flowId, record.DeliveryKey, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            var described = Described(record);
            described["attempts"] = new JsonArray(attempts.Select(a => (JsonNode)Described(a)).ToArray());
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["record"] = described,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {record.SourceKey}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  key        {record.DeliveryKey.Value:N}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  status     {record.Status}{Waiting(record)}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  osdu id    {record.TargetId ?? "(none)"}{Version(record)}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  mapping    {record.MappingName}"));
        if (record.SourceFileName is { Length: > 0 } file)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  from       {file}{(record.SourceRowNumber is { } row ? $" row {row}" : string.Empty)}"));
        }

        if (record.LastDeliveredUtc is { } delivered)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  delivered  {delivered:u}"));
        }

        if (record.LastVerifiedUtc is { } verified)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  verified   {verified:u} {record.LastVerifyOutcome?.ToString() ?? string.Empty}"));
        }

        if (record.LastError is { Length: > 0 } lastError)
        {
            context.Out.WriteLine("  error      " + One(lastError));
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"  {attempts.Count} attempt(s) shown, {record.AttemptCount} on the work pending now; newest first:"));
        foreach (var attempt in attempts)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    {attempt.CompletedUtc:u}  {attempt.Outcome,-9}  {attempt.Phase,-8}  {(attempt.CompletedUtc - attempt.StartedUtc).TotalSeconds:0.00}s  {attempt.Worker}"));
            if (attempt.TargetVersion is { } version)
            {
                context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"        version {version}"));
            }

            foreach (var step in Steps(attempt))
            {
                context.Out.WriteLine("        " + step);
            }

            if (attempt.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("        " + One(error));
            }
        }

        return 0;
    }

    /// <summary>The steps a try took, as its result records them: the name of each and what the target answered.</summary>
    private static IEnumerable<string> Steps(AttemptRecord attempt)
    {
        if (attempt.ResultJson is not { Length: > 0 } text)
        {
            yield break;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            yield break;
        }

        if (parsed?["steps"] is not JsonArray steps)
        {
            yield break;
        }

        foreach (var step in steps.OfType<JsonObject>())
        {
            var name = step["name"]?.GetValue<string>() ?? "step";
            var status = step["status"] is JsonValue value && value.TryGetValue<int>(out var code) ? $" {code}" : string.Empty;
            var returned = step["returned"] is JsonObject values && values.Count > 0
                ? " " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"))
                : string.Empty;
            yield return string.Create(CultureInfo.InvariantCulture, $"{name}{status}{returned}");
        }
    }

    private static JsonObject Described(RecordState record) => new()
    {
        ["deliveryKey"] = record.DeliveryKey.Value.ToString("N", CultureInfo.InvariantCulture),
        ["sourceKey"] = record.SourceKey,
        ["label"] = record.Label,
        ["status"] = record.Status.ToString(),
        ["targetId"] = record.TargetId,
        ["targetVersion"] = record.TargetVersion,
        ["waitingFor"] = record.WaitingFor,
        ["mapping"] = record.MappingName,
        ["attemptCount"] = record.AttemptCount,
        ["lastDeliveredUtc"] = record.LastDeliveredUtc,
        ["lastVerifiedUtc"] = record.LastVerifiedUtc,
        ["lastVerifyOutcome"] = record.LastVerifyOutcome?.ToString(),
        ["lastError"] = record.LastError,
        ["sourceFileName"] = record.SourceFileName,
        ["sourceRowNumber"] = record.SourceRowNumber,
    };

    private static JsonObject Described(AttemptRecord attempt) => new()
    {
        ["attemptId"] = attempt.AttemptId,
        ["startedUtc"] = attempt.StartedUtc,
        ["completedUtc"] = attempt.CompletedUtc,
        ["outcome"] = attempt.Outcome.ToString(),
        ["phase"] = attempt.Phase,
        ["worker"] = attempt.Worker,
        ["submissionId"] = attempt.SubmissionId,
        ["runId"] = attempt.RunId,
        ["targetVersion"] = attempt.TargetVersion,
        ["error"] = attempt.Error,
        ["result"] = attempt.ResultJson is { Length: > 0 } text ? JsonNode.Parse(text) : null,
    };

    private static string Version(RecordState record)
        => record.TargetVersion is { } version ? string.Create(CultureInfo.InvariantCulture, $" v{version}") : string.Empty;

    private static string Waiting(RecordState record)
        => record.WaitingFor is { Length: > 0 } waiting ? $" (waiting for {waiting})" : string.Empty;

    /// <summary>An error as one line, so a record's line stays a line whatever the target wrote.</summary>
    private static string One(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 240 ? single : single[..237] + "...";
    }

    private static RecordStatus? Status(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<RecordStatus>(value.Trim(), ignoreCase: true, out var status)
            ? status
            : throw new FlowValidationException(
                $"--status '{value}' is not a record status; it is one of {string.Join(", ", Enum.GetNames<RecordStatus>().Select(n => n.ToLowerInvariant()))}.");
    }

    private static int Count(string? value, int fallback, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? Math.Min(count, 1000)
            : throw new FlowValidationException($"{option} '{value}' is not a whole number of records above zero.");
    }
}
