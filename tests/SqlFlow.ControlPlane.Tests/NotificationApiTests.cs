using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The self-service notification surface (<c>/api/v1/me/notifications</c>) through the in-memory host: channel
/// availability tracks configuration, subscriptions are per-account CRUD with hard validation, the test send
/// queues a real outbox row, and a session with no backing user (the bootstrap token) is politely refused.
/// DB-backed throughout (users and subscriptions live in the catalog). The notification service's poll interval
/// is set to an hour on the configured host, so its startup tick has long passed before any test acts and the
/// outbox stays observable in its queued state.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationApiTests
{
    [SkippableFact]
    public async Task Options_ReportUnavailableChannels_OnAnUnconfiguredHost()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var (token, _, email) = await NewUserSessionAsync(client);

        var options = await GetAsync<NotificationOptionsDto>(client, token, "/api/v1/me/notifications/options");
        Assert.True(options.Enabled);
        Assert.False(options.Email.Available);
        Assert.False(options.Slack.Available);
        Assert.Equal(email, options.UserEmail);
        Assert.Equal(4, options.Kinds.Count);
        Assert.Contains(NotificationEventKinds.RunFailed, options.DefaultKinds);
        Assert.Contains(NotificationEventKinds.AssertionFailed, options.DefaultKinds);

        // Creating a subscription for the unconfigured channel is rejected with the config key named.
        using var create = await SendAsync(client, token, HttpMethod.Post, "/api/v1/me/notifications/subscriptions",
            new CreateNotificationSubscriptionRequest("email", null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Contains("Email is not configured", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Subscriptions_FullLifecycle_OnAnSmtpConfiguredHost()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        using var factory = SmtpConfigured(new ControlPlaneAppFactory().WithCatalog(cs));
        using var client = factory.CreateClient();
        var (token, _, _) = await NewUserSessionAsync(client);

        var options = await GetAsync<NotificationOptionsDto>(client, token, "/api/v1/me/notifications/options");
        Assert.True(options.Email.Available);
        Assert.Equal("smtp", options.Email.Provider);
        Assert.False(options.Slack.Available);

        // Create with served defaults: immediate mode, the default kinds, the account email as destination.
        using var create = await SendAsync(client, token, HttpMethod.Post, "/api/v1/me/notifications/subscriptions",
            new CreateNotificationSubscriptionRequest("email", null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dto = await create.Content.ReadFromJsonAsync<NotificationSubscriptionDto>();
        Assert.NotNull(dto);
        Assert.Equal("email", dto.Channel);
        Assert.Equal(NotificationModes.Immediate, dto.Mode);
        Assert.Null(dto.NextDueUtc);
        Assert.Null(dto.EmailAddress);
        Assert.Equal(5, dto.CooldownMinutes);
        Assert.True(dto.Enabled);
        Assert.Equal(["run_failed", "assertion_failed"], dto.Kinds);

        // Switch to a one-hour digest with a flow filter: the next window is scheduled from now.
        using var update = await SendAsync(client, token, HttpMethod.Put, $"/api/v1/me/notifications/subscriptions/{dto.Id}",
            new UpdateNotificationSubscriptionRequest(
                NotificationModes.Digest, ["run_failed", "run_cancelled"], "sales_*", null, null, 60, null, null));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var digest = await update.Content.ReadFromJsonAsync<NotificationSubscriptionDto>();
        Assert.NotNull(digest);
        Assert.Equal(NotificationModes.Digest, digest.Mode);
        Assert.Equal("sales_*", digest.FlowPattern);
        Assert.Equal(60, digest.DigestIntervalMinutes);
        Assert.NotNull(digest.NextDueUtc);
        Assert.InRange(digest.NextDueUtc.Value, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(65));

        // The listing shows exactly this user's subscription.
        var list = await GetAsync<List<NotificationSubscriptionDto>>(client, token, "/api/v1/me/notifications/subscriptions");
        Assert.Single(list, s => s.Id == dto.Id);

        // A second user sees nothing and cannot touch it.
        var (otherToken, _, _) = await NewUserSessionAsync(client);
        Assert.Empty(await GetAsync<List<NotificationSubscriptionDto>>(client, otherToken, "/api/v1/me/notifications/subscriptions"));
        using var foreignDelete = await SendAsync(client, otherToken, HttpMethod.Delete, $"/api/v1/me/notifications/subscriptions/{dto.Id}", null);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);

        // Validation: unknown kind, hostile pattern, out-of-range cooldown, bad address.
        await AssertRejected(client, token, new CreateNotificationSubscriptionRequest("email", null, ["nonsense"], null, null, null, null, null), "Unknown event kind");
        await AssertRejected(client, token, new CreateNotificationSubscriptionRequest("email", null, null, ",,,", null, null, null, null), "Invalid flow pattern");
        await AssertRejected(client, token, new CreateNotificationSubscriptionRequest("email", null, null, null, null, null, null, 100000), "Invalid cooldown");
        await AssertRejected(client, token, new CreateNotificationSubscriptionRequest("email", null, null, null, "not-an-email", null, null, null), "Invalid email address");
        await AssertRejected(client, token, new CreateNotificationSubscriptionRequest("slack", null, null, null, null, null, null, null), "Slack is not configured");

        using var delete = await SendAsync(client, token, HttpMethod.Delete, $"/api/v1/me/notifications/subscriptions/{dto.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await GetAsync<List<NotificationSubscriptionDto>>(client, token, "/api/v1/me/notifications/subscriptions"));
    }

    [SkippableFact]
    public async Task TestSend_QueuesARealOutboxRow_VisibleInTheHistory()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        using var factory = SmtpConfigured(new ControlPlaneAppFactory().WithCatalog(cs));
        using var client = factory.CreateClient();
        var (token, _, email) = await NewUserSessionAsync(client);

        using var create = await SendAsync(client, token, HttpMethod.Post, "/api/v1/me/notifications/subscriptions",
            new CreateNotificationSubscriptionRequest("email", null, null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dto = await create.Content.ReadFromJsonAsync<NotificationSubscriptionDto>();
        Assert.NotNull(dto);

        using var test = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/me/notifications/subscriptions/{dto.Id}/test", null);
        Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);
        var accepted = await test.Content.ReadFromJsonAsync<NotificationTestSendDto>();
        Assert.NotNull(accepted);

        var deliveries = await GetAsync<List<NotificationDeliveryDto>>(client, token, "/api/v1/me/notifications/deliveries");
        var delivery = Assert.Single(deliveries, d => d.Id == accepted.DeliveryId);
        Assert.Equal("email", delivery.Channel);
        Assert.Equal(email, delivery.Target);
        Assert.Equal("SQLFlow test notification", delivery.Subject);
        Assert.Equal(dto.Id, delivery.SubscriptionId);
    }

    [SkippableFact]
    public async Task BootstrapSession_HasNoAccount_AndIsRefused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate", "admin"]));
        response.EnsureSuccessStatusCode();
        var bootstrap = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(bootstrap);

        using var list = await SendAsync(client, bootstrap.AccessToken, HttpMethod.Get, "/api/v1/me/notifications/subscriptions", null);
        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
        Assert.Contains("No user account", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>An email-capable host: SMTP configured (the relay is never contacted in these tests) and the
    /// notification poll slowed to an hour, so the startup tick is the only one and queued outbox rows stay put.</summary>
    private static WebApplicationFactory<Program> SmtpConfigured(ControlPlaneAppFactory factory)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ControlPlane:Notifications:PollSeconds", "3600");
            builder.UseSetting("ControlPlane:Notifications:Email:Provider", "smtp");
            builder.UseSetting("ControlPlane:Notifications:Email:FromAddress", "sqlflow-tests@example.com");
            builder.UseSetting("ControlPlane:Notifications:Email:Smtp:Host", "smtp.invalid.example.com");
        });

    /// <summary>Provisions a fresh viewer account (with an email) through the admin surface and signs it in,
    /// returning the session token that carries the account's <c>uid</c>.</summary>
    private static async Task<(string Token, Guid UserId, string Email)> NewUserSessionAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var username = $"notif-api-{suffix}";
        var email = $"{username}@example.com";
        const string password = "a-long-test-password-000";

        using var bootstrapResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate", "admin"]));
        bootstrapResponse.EnsureSuccessStatusCode();
        var bootstrap = await bootstrapResponse.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(bootstrap);

        using var createUser = await SendAsync(client, bootstrap.AccessToken, HttpMethod.Post, "/api/v1/users",
            new CreateUserRequest(username, password, RoleNames.Viewer, email, "Notification API test user"));
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);
        var user = await createUser.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(user);

        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative), new LoginRequest(username, password));
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(session);
        return (session.AccessToken, user.Id, email);
    }

    private static async Task AssertRejected(
        HttpClient client, string token, CreateNotificationSubscriptionRequest request, string expectedTitle)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, "/api/v1/me/notifications/subscriptions", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path, null);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(payload);
        return payload;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request);
    }
}
