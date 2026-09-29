using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The catalog keeps every flow document it syncs as JSON (the pipeline's definition), serialized from the document the
/// module's kind parses, with every public property read. A delivery or cache flow that names its partitions, or leaves them
/// to the registry, is parsed bound to no partition, and the values derived from a partition (a ledger's name and identity,
/// a cache's scope) refuse to be read until one is bound. None of them may be what the serialization reads, or a repository
/// holding such a flow could not be synced at all.
/// </summary>
public sealed class DocumentDefinitionJsonTests
{
    /// <summary>The options the catalog serializes a flow's definition with.</summary>
    private static readonly JsonSerializerOptions CatalogOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DeliveryDocumentLoader _loader = new();

    private static string DeliveryYaml(string partitions) => $$"""
        flowType: delivery
        name: definition-json
        {{partitions}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.arc.WellLog, key: [log_id] }
          lastModified: update_date
          work: work/welllog
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
        """.ReplaceLineEndings("\n");

    private static string CacheYaml(string partitions) => $$"""
        flowType: cache
        name: definition-json-cache
        {{partitions}}
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: none
        types:
          - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            name: UnitOfMeasure
            fields: [data.Code, data.Name]
        """.ReplaceLineEndings("\n");

    [Theory]
    [InlineData("partitions: [dev, test]")]
    [InlineData("keepLedger: dev")]
    [InlineData("")]
    public void A_delivery_flow_bound_to_no_partition_serializes_as_the_catalog_keeps_it(string partitions)
    {
        var document = new DeliveryFlowKind(_loader).Parse(DeliveryYaml(partitions), "flows/definition-json.yaml");

        var json = JsonSerializer.Serialize(document, document.GetType(), CatalogOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("definition-json", parsed.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("\"ledgerName\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("partitions: [dev, test]")]
    [InlineData("")]
    public void A_cache_flow_bound_to_no_partition_serializes_as_the_catalog_keeps_it(string partitions)
    {
        var document = new CacheFlowKind(_loader).Parse(CacheYaml(partitions), "cache/definition-json-cache.yaml");

        var json = JsonSerializer.Serialize(document, document.GetType(), CatalogOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("definition-json-cache", parsed.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("\"scope\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("partitions: [dev, test]")]
    [InlineData("")]
    [InlineData("source: { endpoint: http://x, headers: { data-partition-id: dev } }")]
    public void An_assertion_flow_bound_to_no_partition_serializes_as_the_catalog_keeps_it(string partitions)
    {
        var yaml = AssertionDocumentTests.Full.Replace("partitions: [dev, test]", partitions.StartsWith("source", StringComparison.Ordinal) ? string.Empty : partitions, StringComparison.Ordinal);
        if (partitions.StartsWith("source", StringComparison.Ordinal))
        {
            yaml = yaml.Replace("source:\n  endpoint: http://localhost\n", partitions + "\n", StringComparison.Ordinal)
                .Replace("    partitions: [dev]\n", string.Empty, StringComparison.Ordinal);
        }

        var document = new AssertionFlowKind(_loader).Parse(yaml.ReplaceLineEndings("\n"), "tests/definition-json-assertion.yaml");

        var json = JsonSerializer.Serialize(document, document.GetType(), CatalogOptions);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("recall-welllog-04-header-assertion", parsed.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("\"ledgerName\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ledgerId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\":\"groupBy\"", json, StringComparison.Ordinal);
    }
}
