using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The Azure Storage Table transport: runs a (templated) OData filter against a table and lands the matched entities
/// as a single raw JSON array, preserving every property. Options (in <c>source.options</c>): <c>tableName</c>
/// (required); either <c>connectionString</c> (secret) or <c>accountUrl</c> + <c>sasToken</c> (secret); an optional
/// <c>filter</c> (OData, templated with the iteration variables) and <c>select</c> (comma-separated columns).
/// </summary>
public sealed class AzureTableTransport : IAcquireTransport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.AzureTable;

    public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var options = fetch.Source.Options;
        var tableName = options.GetString("tableName", string.Empty);
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new SqlFlowException("An Azure Table source requires 'tableName' in source.options.");
        }

        var client = await BuildClientAsync(fetch, tableName, ct).ConfigureAwait(false);
        var filter = options.TryGetValue("filter", out var rawFilter) && !string.IsNullOrWhiteSpace(rawFilter)
            ? TemplateEngine.Render(rawFilter!, fetch.Vars)
            : null;
        var select = options.TryGetValue("select", out var rawSelect) && !string.IsNullOrWhiteSpace(rawSelect)
            ? rawSelect!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        var records = new List<Dictionary<string, object?>>();
        await foreach (var entity in client.QueryAsync<TableEntity>(filter: filter, select: select, cancellationToken: ct).ConfigureAwait(false))
        {
            var record = new Dictionary<string, object?>(entity.Count, StringComparer.Ordinal);
            foreach (var (key, value) in entity)
            {
                record[key] = Normalize(value);
            }

            records.Add(record);
        }

        fetch.Pages++;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, Json);
        await fetch.Landing.LandAsync(
            new LandedItem(bytes, "application/json", "entities", records.Count, Headers: null),
            fetch.Vars, ct).ConfigureAwait(false);

        fetch.Log.Log(RunLogLevel.Info, "azuretable", $"queried {records.Count} entit(y/ies) from '{tableName}'.");
    }

    private static async Task<TableClient> BuildClientAsync(AcquireFetch fetch, string tableName, CancellationToken ct)
    {
        var options = fetch.Source.Options;
        if (options.TryGetValue("connectionString", out var connRef) && !string.IsNullOrWhiteSpace(connRef))
        {
            var connectionString = await fetch.Secrets.ResolveAsync(connRef!, ct).ConfigureAwait(false);
            return new TableClient(connectionString, tableName);
        }

        var accountUrl = options.GetString("accountUrl", fetch.Source.BaseUrl);
        if (string.IsNullOrWhiteSpace(accountUrl))
        {
            throw new SqlFlowException("An Azure Table source requires 'connectionString' or 'accountUrl'+'sasToken' in source.options.");
        }

        if (options.TryGetValue("sasToken", out var sasRef) && !string.IsNullOrWhiteSpace(sasRef))
        {
            var sas = await fetch.Secrets.ResolveAsync(sasRef!, ct).ConfigureAwait(false);
            // The SAS TableClient takes the table endpoint (account URL + table name) plus the SAS credential.
            var endpoint = new Uri($"{accountUrl.TrimEnd('/')}/{tableName}");
            return new TableClient(endpoint, new AzureSasCredential(sas));
        }

        throw new SqlFlowException("An Azure Table source needs 'sasToken' (with 'accountUrl') or a 'connectionString' in source.options.");
    }

    private static object? Normalize(object? value) => value switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTimeOffset dto => dto.ToString("o"),
        DateTime dt => dt.ToString("o"),
        _ => value,
    };
}
