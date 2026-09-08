using System.Security.Claims;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Who is calling, as the audit trails record it: the token's subject, else the identity name.</summary>
internal static class RequestActor
{
    /// <summary>The caller's name, or null when the principal carries none (a bootstrap token, say).</summary>
    public static string? Of(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>The caller as the delivery ledger names an actor: <c>user:&lt;name&gt;</c>, or <c>user:unknown</c>.</summary>
    public static string Label(ClaimsPrincipal user) => "user:" + (Of(user) ?? "unknown");
}
