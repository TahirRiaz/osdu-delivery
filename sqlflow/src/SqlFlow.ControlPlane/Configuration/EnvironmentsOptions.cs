using System.Diagnostics.CodeAnalysis;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// The environments an estate runs as separate deployments of the control plane (dev, test and prod, for instance), so a
/// person moves between them from the GUI's title bar rather than from bookmarks. Bound from
/// <c>ControlPlane:Environments</c>: <see cref="Current"/> names the environment this control plane is, and
/// <see cref="Links"/> lists every environment of the estate, this one among them, in the order the title bar's menu shows
/// them, each with the address of its GUI. Every deployment can be given the same list, so they differ only in
/// <see cref="Current"/>. Nothing is configured by default, and the title bar then names no environment.
/// </summary>
public sealed class EnvironmentsOptions
{
    /// <summary>The most environments the list may hold.</summary>
    public const int MaxLinks = 20;

    /// <summary>The longest an environment's name may be.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest an environment's description may be.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The longest an environment's address may be.</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>
    /// The name of the environment this control plane is. Required when <see cref="Links"/> lists any, and then one of
    /// theirs; set alone, the title bar names the environment and offers no other.
    /// </summary>
    public string? Current { get; set; }

    /// <summary>Every environment of the estate, this one among them, in the order the title bar's menu lists them.</summary>
    public List<EnvironmentLinkOptions?> Links { get; set; } = [];

    /// <summary>
    /// The name of the environment this control plane is, spelled as <see cref="Links"/> spells it, or null when none is
    /// configured. Read after <see cref="Validate"/>.
    /// </summary>
    public string? CurrentName()
    {
        var current = Current?.Trim();
        if (string.IsNullOrEmpty(current))
        {
            return null;
        }

        return Links
            .Select(link => link?.Name?.Trim())
            .FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) ?? current;
    }

    /// <summary>
    /// Refuses a list the title bar could not show: a name that is not one, an environment listed twice, an address that is
    /// not an http(s) address of a GUI, or a current environment the list does not hold. An address is never repeated in
    /// a refusal, since a mistyped one could carry a credential.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting the title bar cannot show, naming the setting.</exception>
    public void Validate()
    {
        if (Links.Count > MaxLinks)
        {
            throw new InvalidOperationException(
                $"ControlPlane:Environments:Links lists {Links.Count} environments; at most {MaxLinks} are listed.");
        }

        var names = new List<string>(Links.Count);
        for (var i = 0; i < Links.Count; i++)
        {
            var setting = $"ControlPlane:Environments:Links:{i}";
            var link = Links[i]
                ?? throw new InvalidOperationException($"{setting} is empty: give it a Name and a Url, or take it out of the list.");

            var name = link.Name?.Trim();
            if (!IsName(name))
            {
                throw new InvalidOperationException(
                    $"{setting}:Name {Quoted(link.Name)} is not an environment name: letters, digits, '-', '_' and '.', starting "
                    + $"with a letter or a digit, at most {MaxNameLength} characters.");
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{setting}:Name '{name}' names an environment an earlier entry of ControlPlane:Environments:Links names; "
                    + "list each environment once.");
            }

            names.Add(name);
            ValidateUrl(link.Url, setting + ":Url");
            ValidateDescription(link.Description, setting + ":Description");
        }

        var current = Current?.Trim();
        if (string.IsNullOrEmpty(current))
        {
            if (names.Count > 0)
            {
                throw new InvalidOperationException(
                    $"ControlPlane:Environments:Links lists {string.Join(", ", names)}, and ControlPlane:Environments:Current names "
                    + "none of them: set it to the environment this control plane is.");
            }

            return;
        }

        if (!IsName(current))
        {
            throw new InvalidOperationException(
                $"ControlPlane:Environments:Current {Quoted(Current)} is not an environment name: letters, digits, '-', '_' and "
                + $"'.', starting with a letter or a digit, at most {MaxNameLength} characters.");
        }

        if (names.Count > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ControlPlane:Environments:Current is '{current}', which ControlPlane:Environments:Links does not list "
                + $"({string.Join(", ", names)}): name one of them, or add this environment to the list with its address.");
        }
    }

    private static bool IsName([NotNullWhen(true)] string? name)
        => name is { Length: > 0 and <= MaxNameLength }
            && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string Quoted(string? value)
        => string.IsNullOrWhiteSpace(value) ? "is missing, so it" : $"'{value.Trim()}'";

    private static void ValidateUrl(string? url, string setting)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"{setting} is required: the address of the environment's GUI, for example https://sqlflow-test.example.com.");
        }

        var trimmed = url.Trim();
        if (trimmed.Length > MaxUrlLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{setting} is not an absolute http or https address of the environment's GUI (at most {MaxUrlLength} characters).");
        }

        if (address.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{setting} carries a user name or a password: give the address of the environment's GUI alone, since each "
                + "environment signs a person in itself.");
        }

        if (address.Query.Length > 0 || address.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{setting} carries a query or a fragment: give the address of the environment's GUI alone.");
        }
    }

    private static void ValidateDescription(string? description, string setting)
    {
        if (description is null)
        {
            return;
        }

        var trimmed = description.Trim();
        if (trimmed.Length > MaxDescriptionLength || trimmed.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"{setting} is longer than {MaxDescriptionLength} characters or holds a control character: describe the "
                + "environment in one line.");
        }
    }
}

/// <summary>One environment of the estate, as <c>ControlPlane:Environments:Links:&lt;n&gt;</c> lists it.</summary>
public sealed class EnvironmentLinkOptions
{
    /// <summary>The environment's name, as the title bar shows it: <c>dev</c>, <c>test</c>, <c>prod</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The address of the environment's GUI, which the title bar's menu opens.</summary>
    public string? Url { get; set; }

    /// <summary>What the environment is for, in one line; shown under its name in the menu.</summary>
    public string? Description { get; set; }
}
