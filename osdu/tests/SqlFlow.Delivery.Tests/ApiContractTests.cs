using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The contract checker the route tests rely on: it finds a request's operation however the service is mounted, and
/// reports what a request gets wrong (a missing required parameter, an undeclared one, a content type or a body the
/// operation does not take), in OpenAPI 3 and Swagger 2.0 alike.
/// </summary>
public sealed class ApiContractTests : IDisposable
{
    private const string OpenApi = """
        openapi: 3.0.1
        info: { title: Test service, version: '1' }
        servers:
          - url: https://example.org/api/test/v1
        paths:
          /items/{id}:
            put:
              parameters:
                - { name: id, in: path, required: true, schema: { type: integer } }
                - { name: data-partition-id, in: header, required: true, schema: { type: string } }
                - { name: Authorization, in: header, required: true, schema: { type: string } }
                - { name: mode, in: query, required: false, schema: { type: string, enum: [overwrite, update] } }
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/Item' }
          /items/{id}:delete:
            post:
              parameters:
                - { name: id, in: path, required: true, schema: { type: integer } }
          /items/{id}/data:
            post:
              parameters:
                - { name: id, in: path, required: true, schema: { type: integer } }
              requestBody:
                content:
                  application/x-parquet: {}
            get:
              parameters:
                - { name: id, in: path, required: true, schema: { type: integer } }
                - { name: describe, in: query, schema: { anyOf: [{ type: boolean }, { type: 'null' }] } }
                - { name: curves, in: query, schema: { anyOf: [{ type: array, items: { type: integer } }, { type: 'null' }] } }
                - { name: columns, in: query, style: form, explode: false, schema: { type: array, items: { type: string, enum: [MD, GR] } } }
        components:
          schemas:
            Item:
              type: object
              required: [kind, tags, status]
              additionalProperties: false
              properties:
                kind: { type: string, pattern: '^[a-z]+:[a-z]+$' }
                tags: { type: array, minItems: 1, items: { type: string } }
                count: { type: integer, minimum: 0, nullable: true }
                status: { type: string, readOnly: true }
        """;

    private const string Swagger = """
        {
          "swagger": "2.0",
          "info": { "title": "Old service", "version": "1" },
          "basePath": "/api/old/v2",
          "consumes": ["application/json"],
          "paths": {
            "/entities": {
              "post": {
                "parameters": [
                  { "name": "limit", "in": "query", "type": "integer", "maximum": 100 },
                  { "name": "body", "in": "body", "required": true, "schema": { "$ref": "#/definitions/Entity" } }
                ]
              }
            }
          },
          "definitions": {
            "Entity": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }
          }
        }
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("contracts-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_conforming_request_passes_wherever_the_service_is_mounted()
    {
        var contract = Contract("openapi.yaml", OpenApi);
        var put = Request(HttpMethod.Put, "https://gateway.example.org/platform/api/test/v1/items/42?mode=update", """{"kind":"a:b","tags":["x"],"count":null}""");
        Assert.Empty(contract.Check(put));

        // Mounted elsewhere, the operation is still found by the end of the path, and a segment shared with a literal
        // (the delete action) is matched as one.
        Assert.Empty(contract.Check(Request(HttpMethod.Put, "http://localhost/items/42", """{"kind":"a:b","tags":["x"]}""")));
        Assert.Equal("/items/{id}:delete", contract.Find("POST", "/api/test/v1/items/42:delete")!.Value.Operation.Template);
        Assert.Empty(contract.Check(Request(HttpMethod.Post, "http://localhost/items/7/data", "PAR1", "application/x-parquet")));
    }

    [Fact]
    public void What_a_request_gets_wrong_is_reported_by_name()
    {
        var contract = Contract("openapi.yaml", OpenApi);
        var violations = contract.Check(Request(
            HttpMethod.Put,
            "http://localhost/items/abc?mode=replace&extra=1",
            """{"kind":"A-B","tags":[],"count":-1,"colour":"red"}""",
            headers: false));
        Assert.Contains(violations, v => v.Contains("path parameter id", StringComparison.Ordinal) && v.Contains("integer", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'data-partition-id' is missing", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("query parameter mode", StringComparison.Ordinal) && v.Contains("not one of", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'extra' is not declared", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("body.kind", StringComparison.Ordinal) && v.Contains("pattern", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("body.tags", StringComparison.Ordinal) && v.Contains("fewer than", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("body.count", StringComparison.Ordinal) && v.Contains("minimum", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("no property 'colour'", StringComparison.Ordinal));

        // The status is the service's to fill in, so leaving it out is not a violation; Authorization is not checked here.
        Assert.DoesNotContain(violations, v => v.Contains("status", StringComparison.Ordinal) || v.Contains("Authorization", StringComparison.Ordinal));

        Assert.Contains(contract.Check(Request(HttpMethod.Put, "http://localhost/items/1", "kind=x", "application/x-www-form-urlencoded")), v => v.Contains("is not one of application/json", StringComparison.Ordinal));
        Assert.Contains(contract.Check(Request(HttpMethod.Put, "http://localhost/items/1", null)), v => v.Contains("required body is missing", StringComparison.Ordinal));
        Assert.Contains(contract.Check(Request(HttpMethod.Delete, "http://localhost/items/1", null)), v => v.Contains("declares no such operation", StringComparison.Ordinal));
        Assert.Contains(contract.Check(Request(HttpMethod.Post, "http://localhost/items/1:delete", "{}")), v => v.Contains("takes no body", StringComparison.Ordinal));
    }

    [Fact]
    public void Query_values_are_read_as_the_types_and_the_serialization_their_parameter_declares()
    {
        var contract = Contract("openapi.yaml", OpenApi);

        // An optional boolean written as anyOf, an exploded array of integers and a comma-joined array all conform.
        Assert.Empty(contract.Check(Request(HttpMethod.Get, "http://localhost/items/7/data?describe=true&curves=1&curves=2&columns=MD,GR", null)));

        var violations = contract.Check(Request(HttpMethod.Get, "http://localhost/items/7/data?describe=maybe&curves=1,2&columns=MD,SP&describe=false", null));
        Assert.Contains(violations, v => v.Contains("query parameter describe", StringComparison.Ordinal) && v.Contains("anyOf", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'describe' is sent 2 times", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("query parameter curves", StringComparison.Ordinal) && v.Contains("anyOf", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("query parameter columns", StringComparison.Ordinal) && v.Contains("\"SP\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_swagger_contract_checks_its_inline_parameters_and_its_body_parameter()
    {
        var contract = Contract("swagger.json", Swagger);
        Assert.Empty(contract.Check(Request(HttpMethod.Post, "http://localhost/api/old/v2/entities?limit=10", """{"name":"n"}""")));
        var violations = contract.Check(Request(HttpMethod.Post, "http://localhost/api/old/v2/entities?limit=500", """{"title":"t"}"""));
        Assert.Contains(violations, v => v.Contains("query parameter limit", StringComparison.Ordinal) && v.Contains("maximum", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("'name' is missing", StringComparison.Ordinal));
        Assert.Contains(contract.Check(Request(HttpMethod.Post, "http://localhost/api/old/v2/entities", "<entity/>", "application/xml")), v => v.Contains("is not one of application/json", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_pinned_contract_loads_with_its_operations()
    {
        foreach (var contract in OsduContracts.All)
        {
            Assert.True(contract.Operations.Any(), $"{contract.Name} ({contract.File}) declares no operation.");
        }

        // The services the engine writes to today are all there.
        Assert.NotNull(OsduContracts.Storage.Find("PUT", "/api/storage/v2/records"));
        Assert.NotNull(OsduContracts.File.Find("POST", "/api/file/v2/files/metadata"));
        Assert.NotNull(OsduContracts.Workflow.Find("POST", "/api/workflow/v1/workflow/Osdu_ingest/workflowRun"));
        Assert.NotNull(OsduContracts.WellboreDdms.Find("POST", "/api/os-wellbore-ddms/ddms/v3/welllogs/opendes:work-product-component--WellLog:1/sessions"));

        // The generated Reservoir Management DDMS document declares its header collections' writes.
        Assert.NotNull(OsduContracts.ReservoirManagementDdms.Find("PUT", "/ddms/pvt-properties"));
    }

    private ApiContract Contract(string name, string text)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, text);
        return ApiContract.Load(name, file);
    }

    private static FakeHttpHandler.Request Request(HttpMethod method, string uri, string? body, string contentType = "application/json", bool headers = true)
    {
        var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers)
        {
            sent["data-partition-id"] = "opendes";
        }

        using var content = body is null ? null : new StringContent(body, Encoding.UTF8);
        if (content is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return new FakeHttpHandler.Request(method, new Uri(uri), body, body is null ? null : contentType, sent);
    }
}
