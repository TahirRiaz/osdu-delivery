using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The authoring endpoint's authorization and input validation. The git-push-and-open-pull-request path needs a live
/// remote and is out of scope for the in-memory host; these cover the guards that run before any git or database
/// work: the "author" scope requirement, and the request/file validation that rejects a bad proposal up front.
/// </summary>
public sealed class FlowProposalApiTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Propose_WithoutAuthorScope_Returns403()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        // A token with read+operate but not author must not reach the authoring surface.
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add orders flows", null, null, null,
                [new ProposeFlowsFile("flows/orders.01_pre.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Propose_WithoutAnyToken_Returns401()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ProposalUri())
        {
            Content = JsonContent.Create(new ProposeFlowsRequest("t", null, null, null,
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")])),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Propose_BlankTitle_Returns400_BeforeTouchingGitOrDatabase()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("   ", null, null, null,
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Propose_NoFiles_Returns400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, null, []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../secrets.yaml")]           // traversal
    [InlineData("/etc/passwd.yaml")]          // absolute
    [InlineData("flows/./orders.flow.yaml")]  // '.' segment
    [InlineData("flows/run.exe")]             // disallowed extension
    public async Task Propose_UnsafeOrDisallowedPath_Returns400(string path)
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, null,
                [new ProposeFlowsFile(path, "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Propose_InvalidHeadBranchName_Returns400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, "bad branch name",
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Uri ProposalUri() => new($"/api/v1/repos/sources/{SourceId}/proposals", UriKind.Relative);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, ProposeFlowsRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ProposalUri())
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
