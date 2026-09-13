using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>A release of the OSDU data definitions: a version tag of the repository, and the commit it names.</summary>
public sealed record DataDefinitionsRelease(string Name, string Commit, DateTimeOffset? PublishedUtc);

/// <summary>
/// One record schema a release publishes: its kind, entity type and version, the status the release gives it (PUBLISHED,
/// DEVELOPMENT or OBSOLETE; null when the index gives none), and its file under <see cref="OsduDataDefinitions.TreeRoot"/>.
/// </summary>
public sealed record DataDefinitionsSchema(string Kind, string EntityType, string Version, string? Status, string Path);

/// <summary>Every record schema one release publishes, by entity type and newest version first.</summary>
public sealed record DataDefinitionsIndex(DataDefinitionsRelease Release, IReadOnlyList<DataDefinitionsSchema> Schemas);

/// <summary>A kind's schema bundled from one release, and the file it was bundled from.</summary>
public sealed record DataDefinitionsSchemaFile(DataDefinitionsRelease Release, string Path, SchemaSnapshot Schema)
{
    /// <summary>Where a template saved from it came from, as the template's origin records it: the release, its commit and the file.</summary>
    public string Origin
        => $"OSDU data definitions {Release.Name} ({(Release.Commit.Length > 12 ? Release.Commit[..12] : Release.Commit)}) {OsduDataDefinitions.TreeRoot}/{Path}";
}

/// <summary>
/// The OSDU data definitions could not answer. <see cref="NotFound"/> when what was asked for is not in them (a release, or
/// a kind a release does not publish); otherwise the repository could not be reached, refused, or answered with something
/// other than what it serves.
/// </summary>
public sealed class DataDefinitionsException : DeliveryException
{
    public DataDefinitionsException(string message, bool notFound)
        : base(message)
    {
        NotFound = notFound;
    }

    public DataDefinitionsException(string message, bool notFound, Exception innerException)
        : base(message, innerException)
    {
        NotFound = notFound;
    }

    public bool NotFound { get; }
}

/// <summary>
/// The OSDU data definitions: the Open Group's public repository of the OSDU schemas
/// (<see cref="DefaultWebUrl"/>), the canonical source of every <c>osdu:wks</c> kind. It is read through the repository's
/// GitLab API one release at a time: a release is a version tag, its <c>Generated/SchemaStatus.json</c> lists every kind it
/// publishes, and a kind's schema is its file under <c>Generated</c>, bundled with every file it refers to exactly as a
/// local checkout bundles (<see cref="TemplateSources.FromDirectoryAsync"/>). Files are read at the commit the tag names,
/// so what was read never changes and stays cached for the life of the instance; the list of releases is read again after
/// a few minutes.
/// </summary>
public sealed class OsduDataDefinitions
{
    /// <summary>The named HTTP client a host registers for the repository.</summary>
    public const string HttpClientName = "osdu-data-definitions";

    /// <summary>The folder of the repository that holds the schemas, laid out by group.</summary>
    public const string TreeRoot = "Generated";

    /// <summary>The release's index: every kind it publishes, with its status.</summary>
    public const string IndexFile = "SchemaStatus.json";

    /// <summary>The largest file read. The release index, the largest file, is a few hundred kilobytes.</summary>
    private const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>The release tags one read asks for, newest first; the repository has published a few dozen.</summary>
    private const int TagPage = 100;

    private const int MaxCachedIndexes = 16;

    private const int MaxCachedFiles = 8192;

    private static readonly TimeSpan ReleasesFreshFor = TimeSpan.FromMinutes(10);

    /// <summary>How old the list must be before a release it does not name makes it read again.</summary>
    private static readonly TimeSpan ReleasesRereadAfter = TimeSpan.FromMinutes(1);

    private readonly Func<HttpClient> _client;
    private readonly Uri _api;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DataDefinitionsIndex> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private ReleaseList? _releases;

    /// <param name="client">The HTTP client for a request; the caller owns its lifetime.</param>
    /// <param name="apiUrl">The GitLab API URL of the data definitions project, such as <see cref="DefaultApiUrl"/>.</param>
    /// <param name="webUrl">The project's web page, such as <see cref="DefaultWebUrl"/>, which links point into.</param>
    /// <param name="time">The clock the release list and the captured schemas are stamped with.</param>
    public OsduDataDefinitions(Func<HttpClient> client, Uri apiUrl, Uri webUrl, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(webUrl);
        ArgumentNullException.ThrowIfNull(time);
        if (!apiUrl.IsAbsoluteUri || !webUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The data definitions' API and web URLs must be absolute.");
        }

        _client = client;
        _api = WithTrailingSlash(apiUrl);
        WebUrl = WithTrailingSlash(webUrl);
        _time = time;
    }

    /// <summary>The GitLab API URL of the Open Group's data definitions project.</summary>
    public static Uri DefaultApiUrl { get; } = new("https://community.opengroup.org/api/v4/projects/osdu%2Fdata%2Fdata-definitions/");

    /// <summary>The web page of the Open Group's data definitions project.</summary>
    public static Uri DefaultWebUrl { get; } = new("https://community.opengroup.org/osdu/data/data-definitions/");

    /// <summary>The project's web page.</summary>
    public Uri WebUrl { get; }

    /// <summary>The release's schema folder on the project's web page.</summary>
    public Uri ReleaseWebUrl(DataDefinitionsRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new Uri(WebUrl, $"-/tree/{Uri.EscapeDataString(release.Name)}/{TreeRoot}");
    }

    /// <summary>A schema file of the release on the project's web page.</summary>
    public Uri FileWebUrl(DataDefinitionsRelease release, string path)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri(WebUrl, $"-/blob/{Uri.EscapeDataString(release.Name)}/{TreeRoot}/{path}");
    }

    /// <summary>The releases, newest first: the tags named <c>v</c> and a dotted version.</summary>
    public Task<IReadOnlyList<DataDefinitionsRelease>> ReleasesAsync(CancellationToken ct = default) => ReleasesAsync(reread: false, ct);

    /// <summary>The release named <paramref name="name"/>, or the newest when no name is given.</summary>
    public async Task<DataDefinitionsRelease> ReleaseAsync(string? name, CancellationToken ct = default)
    {
        var releases = await ReleasesAsync(reread: false, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
        {
            return releases[0];
        }

        var wanted = name.Trim();
        var found = releases.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.Ordinal));
        if (found is null && _releases is { } list && _time.GetUtcNow() - list.ReadUtc >= ReleasesRereadAfter)
        {
            // A release tagged since the list was read is found on a fresh read.
            releases = await ReleasesAsync(reread: true, ct).ConfigureAwait(false);
            found = releases.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.Ordinal));
        }

        return found ?? throw new DataDefinitionsException($"The OSDU data definitions have no release '{wanted}'; the latest is {releases[0].Name}.", notFound: true);
    }

    /// <summary>Every record schema the release publishes (the newest release when none is named).</summary>
    public async Task<DataDefinitionsIndex> IndexAsync(string? release, CancellationToken ct = default)
    {
        var chosen = await ReleaseAsync(release, ct).ConfigureAwait(false);
        if (_indexes.TryGetValue(chosen.Commit, out var cached))
        {
            return cached;
        }

        var what = $"{TreeRoot}/{IndexFile} at {chosen.Name}";
        if (Parse(await ReadFileAsync(chosen, IndexFile, ct).ConfigureAwait(false), what) is not JsonObject statuses)
        {
            throw new DataDefinitionsException($"{what} is not the object of kinds and their statuses a release index is.", notFound: false);
        }

        var schemas = new List<DataDefinitionsSchema>();
        foreach (var (kind, status) in statuses)
        {
            if (!FlowMapper.IsRecordKind(kind))
            {
                continue;
            }

            // Abstract schemas and the manifest are the building blocks of records, not records a mapping fills.
            var parts = kind.Split(':');
            if (!parts[2].Contains("--", StringComparison.Ordinal))
            {
                continue;
            }

            var text = status is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
            schemas.Add(new DataDefinitionsSchema(kind, parts[2], parts[3], text, SchemaBundler.KindPath(kind)));
        }

        schemas.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.EntityType, b.EntityType);
            return byType != 0 ? byType : CompareVersions(b.Version, a.Version);
        });

        var index = new DataDefinitionsIndex(chosen, schemas);
        if (_indexes.Count >= MaxCachedIndexes)
        {
            _indexes.Clear();
        }

        _indexes[chosen.Commit] = index;
        return index;
    }

    /// <summary>
    /// A kind's schema from the release (the newest when none is named), bundled with every file it refers to and checked
    /// as a template is saved: it describes the kind, refers to nothing outside itself, and declares <c>data</c>.
    /// </summary>
    public async Task<DataDefinitionsSchemaFile> FetchAsync(string? release, string kind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var wanted = kind.Trim();
        TemplateSources.RequireKind(wanted);
        var chosen = await ReleaseAsync(release, ct).ConfigureAwait(false);
        var path = SchemaBundler.KindPath(wanted);
        var bundled = await SchemaBundler.BundleTreeAsync(path, async (file, token) =>
        {
            var what = $"{TreeRoot}/{file} at {chosen.Name}";
            return Parse(await ReadFileAsync(chosen, file, token).ConfigureAwait(false), what) as JsonObject
                ?? throw new DataDefinitionsException($"{what} is not a JSON object.", notFound: false);
        }, ct).ConfigureAwait(false);

        var schema = TemplateSources.Validated(wanted, bundled, _time.GetUtcNow(), $"{TreeRoot}/{path} at {chosen.Name}");
        return new DataDefinitionsSchemaFile(chosen, path, schema);
    }

    private async Task<IReadOnlyList<DataDefinitionsRelease>> ReleasesAsync(bool reread, CancellationToken ct)
    {
        var cached = _releases;
        if (!reread && cached is not null && _time.GetUtcNow() - cached.ReadUtc < ReleasesFreshFor)
        {
            return cached.Releases;
        }

        const string what = "the list of release tags";
        var text = await GetTextAsync(string.Create(CultureInfo.InvariantCulture, $"repository/tags?per_page={TagPage}"), what, ct).ConfigureAwait(false);
        if (Parse(text, what) is not JsonArray tags)
        {
            throw new DataDefinitionsException($"The OSDU data definitions answered {what} with something other than a list.", notFound: false);
        }

        var releases = new List<DataDefinitionsRelease>();
        foreach (var tag in tags.OfType<JsonObject>())
        {
            var name = Text(tag, "name");
            var commit = tag["commit"] as JsonObject;
            var id = commit is null ? null : Text(commit, "id");
            if (name is null || id is null || name.Length < 2 || name[0] != 'v' || VersionNumbers(name[1..]) is null)
            {
                continue;
            }

            DateTimeOffset? published = commit is not null && Text(commit, "committed_date") is { } date
                && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;
            releases.Add(new DataDefinitionsRelease(name, id, published));
        }

        if (releases.Count == 0)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} have no release tag (v and a dotted version).", notFound: true);
        }

        releases.Sort((a, b) => CompareVersions(b.Name[1..], a.Name[1..]));
        _releases = new ReleaseList(releases, _time.GetUtcNow());
        return releases;
    }

    private async Task<string> ReadFileAsync(DataDefinitionsRelease release, string path, CancellationToken ct)
    {
        var key = release.Commit + ":" + path;
        if (_files.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var full = TreeRoot + "/" + path;
        var text = await GetTextAsync(
            $"repository/files/{Uri.EscapeDataString(full)}/raw?ref={Uri.EscapeDataString(release.Commit)}", $"{full} at {release.Name}", ct).ConfigureAwait(false);
        if (_files.Count >= MaxCachedFiles)
        {
            _files.Clear();
        }

        _files[key] = text;
        return text;
    }

    private async Task<string> GetTextAsync(string relative, string what, CancellationToken ct)
    {
        var url = new Uri(_api, relative);
        HttpResponseMessage response;
        try
        {
            response = await _client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} could not be reached for {what}: {ex.Message}", notFound: false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DataDefinitionsException($"The OSDU data definitions at {WebUrl} did not answer in time for {what}.", notFound: false, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new DataDefinitionsException($"The OSDU data definitions have no {what}.", notFound: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DataDefinitionsException(
                    string.Create(CultureInfo.InvariantCulture, $"The OSDU data definitions at {WebUrl} answered {(int)response.StatusCode} {response.ReasonPhrase} for {what}."),
                    notFound: false);
            }

            if (response.Content.Headers.ContentLength is > MaxFileBytes)
            {
                throw TooLarge(what);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxFileBytes)
                    {
                        throw TooLarge(what);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
            }
        }
    }

    private static DataDefinitionsException TooLarge(string what)
        => new(string.Create(CultureInfo.InvariantCulture, $"The OSDU data definitions answered {what} with more than {MaxFileBytes / 1024 / 1024} MB, more than any schema file holds."), notFound: false);

    private static JsonNode? Parse(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new DataDefinitionsException($"{what} is not valid JSON ({ex.Message}).", notFound: false, ex);
        }
    }

    private static string? Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>Orders dotted versions by their numbers, a version with a further part after the one without it; text that is not a dotted version orders as text.</summary>
    private static int CompareVersions(string left, string right)
    {
        var a = VersionNumbers(left);
        var b = VersionNumbers(right);
        if (a is null || b is null)
        {
            return string.CompareOrdinal(left, right);
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : -1;
            var y = i < b.Length ? b[i] : -1;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static int[]? VersionNumbers(string text)
    {
        var parts = text.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static Uri WithTrailingSlash(Uri url)
        => url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");

    private sealed record ReleaseList(IReadOnlyList<DataDefinitionsRelease> Releases, DateTimeOffset ReadUtc);
}
