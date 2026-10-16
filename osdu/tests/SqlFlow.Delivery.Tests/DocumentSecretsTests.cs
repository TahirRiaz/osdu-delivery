using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A literal secret in a flow document is refused while the document is read, whatever the kind: every key that carries a
/// credential holds a <c>${env:...}</c> or <c>${keyvault:...}</c> reference, and the refusal names the key and the fix,
/// never the value. An identifier (a client id, a user name) and a setting (a scope) are not secrets.
/// </summary>
public class DocumentSecretsTests
{
    private const string Secret = "abc123SuperSecret";

    private const string Delivery = """
        flowType: delivery
        name: wellbore-delivery
        partitions: [dev]
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.silver.Wellbore, key: [wellbore_id] }
          lastModified: update_date
          work: work/wellbore
        render:
          mapping: Wellbore@1.0.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
          headers:
            Authorization: Bearer ${env:OSDU_TOKEN}
            x-request-source: welldb
          auth:
            type: oauth2ClientCredentials
            secondarySecretRef: client-1
            secretRef: ${keyvault:delivery-vault/osdu-client-secret}
            token:
              url: https://login.example.org/tenant/oauth2/v2.0/token
              body:
                scope: api://osdu/.default
                client_id: client-1
        """;

    private static string Parsed(string yaml) => new DeliveryDocumentLoader().ParseSource(yaml.ReplaceLineEndings("\n"), "flows/wellbore.yaml").Name;

    private static string Refused(string yaml)
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseSource(yaml.ReplaceLineEndings("\n"), "flows/wellbore.yaml"));
        Assert.StartsWith("flows/wellbore.yaml: ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, refused.Message, StringComparison.Ordinal);
        return refused.Message;
    }

    private static string With(string from, string to)
    {
        var yaml = Delivery.ReplaceLineEndings("\n");
        var changed = yaml.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(yaml, changed);
        return changed;
    }

    [Fact]
    public void A_flow_whose_credentials_are_references_and_whose_identifiers_are_literal_is_read()
        => Assert.Equal("wellbore-delivery", Parsed(Delivery));

    [Fact]
    public void A_literal_secret_ref_is_refused_naming_the_key_and_the_fix()
    {
        var message = Refused(With("secretRef: ${keyvault:delivery-vault/osdu-client-secret}", $"secretRef: {Secret}"));

        Assert.Contains("target.auth.secretRef holds a literal value", message, StringComparison.Ordinal);
        Assert.Contains("${keyvault:vault/secret} or ${env:NAME}", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${env:OSDU_CLIENT_SECRET}")]
    [InlineData("${keyvault:delivery-vault/osdu-client-secret}")]
    [InlineData("${ENV:OSDU_CLIENT_SECRET}")]
    public void A_secret_ref_that_is_a_reference_is_read(string reference)
        => Assert.Equal("wellbore-delivery", Parsed(With("secretRef: ${keyvault:delivery-vault/osdu-client-secret}", $"secretRef: \"{reference}\"")));

    [Theory]
    [InlineData("prefix${env:OSDU_CLIENT_SECRET}")]
    [InlineData("${env:OSDU_CLIENT_SECRET} trailing")]
    [InlineData("${secret:OSDU_CLIENT_SECRET}")]
    public void A_secret_ref_with_text_beside_its_reference_or_another_scheme_is_refused(string value)
        => Assert.Contains(
            "target.auth.secretRef holds a literal value",
            Refused(With("secretRef: ${keyvault:delivery-vault/osdu-client-secret}", $"secretRef: \"{value}\"")),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("client_secret")]
    [InlineData("password")]
    [InlineData("refresh_token")]
    [InlineData("client_assertion")]
    public void A_literal_token_body_field_named_as_a_secret_is_refused(string field)
    {
        var message = Refused(With("client_id: client-1", $"client_id: client-1\n        {field}: {Secret}"));

        Assert.Contains($"target.auth.token.body.{field} holds a literal value", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_body_field_named_as_a_secret_that_is_a_reference_is_read()
        => Assert.Equal("wellbore-delivery", Parsed(With("client_id: client-1", "client_id: client-1\n        client_secret: ${env:OSDU_CLIENT_SECRET}")));

    [Theory]
    [InlineData("Authorization", $"Bearer {Secret}")]
    [InlineData("Authorization", Secret)]
    [InlineData("x-api-key", Secret)]
    [InlineData("Ocp-Apim-Subscription-Key", Secret)]
    [InlineData("X-Access-Token", Secret)]
    public void A_literal_credential_header_is_refused(string header, string value)
    {
        var message = Refused(With("Authorization: Bearer ${env:OSDU_TOKEN}", $"{header}: \"{value}\""));

        Assert.Contains($"target.headers.{header} holds a literal value", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Basic ${env:OSDU_BASIC}")]
    [InlineData("${env:OSDU_TOKEN}")]
    public void A_credential_header_that_names_its_scheme_before_a_reference_is_read(string value)
        => Assert.Equal("wellbore-delivery", Parsed(With("Authorization: Bearer ${env:OSDU_TOKEN}", $"Authorization: \"{value}\"")));

    [Theory]
    [InlineData("endpoint: ${env:OSDU_URL}", "endpoint: https://loader:hunter2@osdu.example.org", "target.endpoint")]
    [InlineData("endpoint: ${env:OSDU_URL}", "endpoint: https://osdu.example.org/api?api_key=" + Secret, "target.endpoint")]
    [InlineData("url: https://login.example.org/tenant/oauth2/v2.0/token", "url: https://login.example.org/token?client_secret=" + Secret, "target.auth.token.url")]
    public void A_url_carrying_a_literal_credential_is_refused(string from, string to, string key)
    {
        var message = Refused(With(from, to));

        Assert.Contains($"{key} carries a credential in its user info or query string", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_whose_credential_is_a_reference_is_read()
        => Assert.Equal("wellbore-delivery", Parsed(With("url: https://login.example.org/tenant/oauth2/v2.0/token", "url: https://login.example.org/token?code=x&api_key=${env:TOKEN_KEY}")));

    [Fact]
    public void A_literal_secret_of_the_airflow_behind_the_target_is_refused()
    {
        var message = Refused(With(
            "  protocol: storage\n",
            $"  protocol: storage\n  airflow:\n    endpoint: https://airflow.example.org\n    auth: {{ type: basic, secondarySecretRef: admin, secretRef: {Secret} }}\n"));

        Assert.Contains("target.airflow.auth.secretRef holds a literal value", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_header_among_the_upload_headers_is_refused_whatever_its_value()
    {
        var message = Refused(With(
            "  protocol: storage\n",
            "  protocol: storage\n  protocolOptions:\n    uploadHeaders: { x-ms-blob-type: BlockBlob, Authorization: \"Bearer ${env:OSDU_TOKEN}\" }\n"));

        Assert.Contains("target.protocolOptions.uploadHeaders.Authorization carries a credential", message, StringComparison.Ordinal);
        Assert.Contains("never resolved", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_of_the_workflow_payload_named_as_a_secret_is_refused()
    {
        var message = Refused(With(
            "  protocol: storage\n",
            $"  protocol: storage\n  protocolOptions:\n    workflowPayload: {{ AppKey: loader, password: {Secret} }}\n"));

        Assert.Contains("target.protocolOptions.workflowPayload.password is named as a secret", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("retrieval")]
    [InlineData("cache")]
    [InlineData("assertion")]
    [InlineData("dimension")]
    [InlineData("inventory")]
    public void Every_other_kind_refuses_a_literal_secret_in_its_source_auth(string flowType)
    {
        var yaml = $"""
            flowType: {flowType}
            name: osdu-{flowType}
            source:
              endpoint: ${"{"}env:OSDU_URL{"}"}
              auth:
                type: bearer
                secretRef: {Secret}
            """.ReplaceLineEndings("\n");
        var loader = new DeliveryDocumentLoader();
        Action parse = flowType switch
        {
            "retrieval" => () => loader.ParseRetrieval(yaml, "flows/osdu.yaml"),
            "cache" => () => loader.ParseCache(yaml, "flows/osdu.yaml"),
            "assertion" => () => loader.ParseAssertion(yaml, "flows/osdu.yaml"),
            "dimension" => () => loader.ParseDimension(yaml, "flows/osdu.yaml"),
            _ => () => loader.ParseInventory(yaml, "flows/osdu.yaml"),
        };

        var refused = Assert.Throws<FlowValidationException>(parse);

        Assert.StartsWith("flows/osdu.yaml: source.auth.secretRef holds a literal value", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_kind_refuses_a_literal_credential_header_of_its_source()
    {
        var yaml = $"""
            flowType: cache
            name: osdu-cache
            source:
              endpoint: ${"{"}env:OSDU_URL{"}"}
              headers: {"{"} data-partition-id: dev, Authorization: "Bearer {Secret}" {"}"}
            """.ReplaceLineEndings("\n");

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseCache(yaml, "cache/osdu.yaml"));

        Assert.StartsWith("cache/osdu.yaml: source.headers.Authorization holds a literal value", refused.Message, StringComparison.Ordinal);
    }
}
