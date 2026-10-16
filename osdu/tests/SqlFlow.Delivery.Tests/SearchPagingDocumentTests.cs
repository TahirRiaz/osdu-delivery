using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>source.paging</c>, which every flow kind that reads the search index whole declares the same way: cursor, the default,
/// or createTime, in any case, and nothing else.
/// </summary>
public sealed class SearchPagingDocumentTests
{
    private const string Retrieval = """
        flowType: retrieval
        name: wellbores-out
        source:
          endpoint: http://localhost/osdu
          headers: { data-partition-id: dev }
          kinds: ["osdu:wks:master-data--Wellbore:1.*.*"]
        target:
          location: /lake/out
        """;

    private const string Inventory = """
        flowType: inventory
        name: welllog-inventory
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
        inventories:
          - name: WellLogs
            kind: "*:*:work-product-component--WellLog:*"
        """;

    private const string Dimension = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
        dimensions:
          - name: CurveMnemonic
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.Mnemonic
        """;

    private const string Cache = """
        flowType: cache
        name: welldb-osdu-00-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
        types:
          - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            fields: [data.Code]
        """;

    public static TheoryData<string> Kinds() => ["retrieval", "inventory", "dimension", "assertion", "cache"];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_flow_that_names_no_paging_reads_through_the_cursor(string kind)
        => Assert.Equal(SearchPaging.Cursor, Paging(kind, null));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_flow_reads_in_createTime_order_when_its_source_says_so_in_any_case(string kind)
    {
        Assert.Equal(SearchPaging.CreateTime, Paging(kind, "createTime"));
        Assert.Equal(SearchPaging.CreateTime, Paging(kind, "CREATETIME"));
        Assert.Equal(SearchPaging.Cursor, Paging(kind, "cursor"));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_paging_that_is_neither_is_refused_naming_the_key_and_both(string kind)
    {
        var error = Assert.Throws<FlowValidationException>(() => Paging(kind, "scroll"));

        Assert.Contains("'source.paging' value 'scroll' is not one of cursor, createTime.", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The paging the kind's document parses to, with <c>source.paging</c> written as given or left out.</summary>
    private static SearchPaging Paging(string kind, string? paging)
    {
        var document = kind switch
        {
            "retrieval" => Retrieval,
            "inventory" => Inventory,
            "dimension" => Dimension,
            "assertion" => AssertionDocumentTests.Full,
            "cache" => Cache,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a flow kind that searches."),
        };
        if (paging is not null)
        {
            document = Regex.Replace(document, @"^source:(\r?\n)", $"source:$1  paging: {paging}$1", RegexOptions.Multiline);
        }

        var loader = new DeliveryDocumentLoader();
        return kind switch
        {
            "retrieval" => loader.ParseRetrieval(document, "flows/retrieval.yaml").Source.Paging,
            "inventory" => loader.ParseInventory(document, "flows/inventory.yaml").Source.Paging,
            "dimension" => loader.ParseDimension(document, "flows/dimension.yaml").Source.Paging,
            "assertion" => loader.ParseAssertion(document, "flows/assertion.yaml").Source.Paging,
            _ => loader.ParseCache(document, "cache/cache.yaml").Source.Paging,
        };
    }
}
