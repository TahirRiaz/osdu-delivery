using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads a generic API-acquisition flow (flowType: api) from YAML into a validated <see cref="AcquireFlow"/>.
/// YamlDotNet handles the grammar; this class maps the parsed document, normalizes the discriminated enums
/// (transport / auth type / body kind / pagination strategy / iteration kind), and enforces the cross-field
/// requirements (an HTTP source needs a request, an idsFrom iteration needs an id request and path, and so on).
/// </summary>
public sealed class YamlAcquireFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public AcquireFlow LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public AcquireFlow Parse(string yaml, string source = "<inline>")
    {
        AcquireDocumentYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<AcquireDocumentYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static AcquireFlow Map(AcquireDocumentYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "an api flow", source);
        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");

        var flow = new AcquireFlow
        {
            Name = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Items = MapItems(y, sourceYaml, source),
            Incremental = MapIncremental(y.Incremental, source),
            Params = MapParams(y.Params, source),
        };

        // A lake-sourced watermark reads the resume point back out of the landed file names, so the landing template
        // must actually encode it. Proving that here means a misconfigured resume fails validation, rather than
        // surfacing as a silent full re-walk on the next scheduled run.
        if (flow.Incremental is { Source: AcquireWatermarkSource.Lake } incremental)
        {
            try
            {
                LakeWatermarkReader.Compile(flow.Items[0].Landing.PathTemplate, incremental.Column);
            }
            catch (SqlFlowException ex)
            {
                throw new FlowValidationException($"{source}: {ex.Message}", ex);
            }
        }

        return flow;
    }

    /// <summary>Builds the flow's endpoints from either the singular top-level <c>source.request</c>/<c>landing</c> (one
    /// item, the simple case) or the plural <c>items:</c> list (one item per entry, each with its own request and landing
    /// over the shared <c>source</c> connection envelope). Exactly one form is allowed: mixing the top-level landing with
    /// items, or putting a per-endpoint request/pagination/iterate on the shared source in the multi-item form, fails at
    /// parse rather than silently landing to the wrong place.</summary>
    private static IReadOnlyList<AcquireItem> MapItems(AcquireDocumentYaml y, AcquireSourceYaml sourceYaml, string source)
    {
        var transport = ParseEnum(sourceYaml.Transport, AcquireTransport.Http, "source.transport", source);
        var baseUrl = Require(sourceYaml.BaseUrl, "source.baseUrl", source);

        if (y.Items is { Count: > 0 })
        {
            if (y.Landing is not null)
            {
                throw new FlowValidationException(
                    $"{source}: a multi-item api flow declares each landing under 'items[].landing'; remove the top-level 'landing'.");
            }

            if (sourceYaml.Request is not null || sourceYaml.Pagination is not null || sourceYaml.Iterate is { Count: > 0 })
            {
                throw new FlowValidationException(
                    $"{source}: in a multi-item api flow, 'request'/'pagination'/'iterate' belong under each 'items[]' entry, not the shared 'source'.");
            }

            if (y.Incremental is not null)
            {
                throw new FlowValidationException(
                    $"{source}: 'incremental' is only valid on a single-endpoint api flow (the run watermark is one value per run). " +
                    "Express a multi-item flow's incrementality through each item's date-window 'iterate'.");
            }

            var auth = MapAuth(sourceYaml.Auth, source);
            var reliability = MapReliability(sourceYaml.Reliability);
            var options = MapOptions(sourceYaml.Options);

            var items = new List<AcquireItem>(y.Items.Count);
            for (var i = 0; i < y.Items.Count; i++)
            {
                var item = y.Items[i] ?? throw new FlowValidationException($"{source}: 'items[{i}]' must be a map.");
                var landingYaml = item.Landing ?? throw new FlowValidationException($"{source}: 'items[{i}].landing' is required.");
                var itemSource = new AcquireSource
                {
                    Transport = transport,
                    BaseUrl = baseUrl,
                    Auth = auth,
                    Request = MapRequest(item.Request, source),
                    Pagination = MapPagination(item.Pagination, source),
                    Iterations = (item.Iterate ?? []).Select(it => MapIteration(it, source)).ToList(),
                    Reliability = reliability,
                    Options = options,
                };

                if (transport == AcquireTransport.Http && itemSource.Request is null)
                {
                    throw new FlowValidationException($"{source}: an http item requires an 'items[{i}].request' block.");
                }

                items.Add(new AcquireItem
                {
                    Name = YamlDocumentParts.NullIfBlank(item.Name),
                    Source = itemSource,
                    Landing = MapLanding(landingYaml, source),
                });
            }

            return items;
        }

        var topLanding = y.Landing ?? throw new FlowValidationException($"{source}: 'landing' is required.");
        var singleSource = new AcquireSource
        {
            Transport = transport,
            BaseUrl = baseUrl,
            Auth = MapAuth(sourceYaml.Auth, source),
            Request = MapRequest(sourceYaml.Request, source),
            Pagination = MapPagination(sourceYaml.Pagination, source),
            Iterations = (sourceYaml.Iterate ?? []).Select(it => MapIteration(it, source)).ToList(),
            Reliability = MapReliability(sourceYaml.Reliability),
            Options = MapOptions(sourceYaml.Options),
        };

        if (transport == AcquireTransport.Http && singleSource.Request is null)
        {
            throw new FlowValidationException($"{source}: an http source requires a 'source.request' block.");
        }

        return [new AcquireItem { Name = null, Source = singleSource, Landing = MapLanding(topLanding, source) }];
    }

    /// <summary>
    /// Declared runtime parameters: name to default value. An empty/whitespace default is normalized to null
    /// (no fallback: the run must supply the value). Names must be valid template identifiers so a declared
    /// parameter is always addressable as <c>{name}</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> MapParams(Dictionary<string, string?>? paramsYaml, string source)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (paramsYaml is null)
        {
            return result;
        }

        foreach (var (name, fallback) in paramsYaml)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => c is '{' or '}' or ':'))
            {
                throw new FlowValidationException(
                    $"{source}: parameter name '{name}' is not a valid template identifier (no braces or colons, not blank).");
            }

            result[name.Trim()] = string.IsNullOrWhiteSpace(fallback) ? null : fallback;
        }

        return result;
    }

    private static AcquireAuth MapAuth(AcquireAuthYaml? y, string source)
    {
        if (y is null)
        {
            return new AcquireAuth { Type = AcquireAuthType.None };
        }

        return new AcquireAuth
        {
            Type = ParseEnum(y.Type, AcquireAuthType.None, "source.auth.type", source),
            SecretRef = YamlDocumentParts.NullIfBlank(y.SecretRef),
            SecondarySecretRef = YamlDocumentParts.NullIfBlank(y.SecondarySecretRef),
            HeaderName = YamlDocumentParts.NullIfBlank(y.HeaderName),
            ParamName = YamlDocumentParts.NullIfBlank(y.ParamName),
            ValuePrefix = y.ValuePrefix,
            Token = MapToken(y.Token, source),
        };
    }

    private static AcquireTokenEndpoint? MapToken(AcquireTokenYaml? y, string source)
    {
        if (y is null)
        {
            return null;
        }

        return new AcquireTokenEndpoint
        {
            Url = YamlDocumentParts.NullIfBlank(y.Url),
            DiscoveryUrl = YamlDocumentParts.NullIfBlank(y.DiscoveryUrl),
            Method = string.IsNullOrWhiteSpace(y.Method) ? "POST" : y.Method!.Trim(),
            BodyKind = ParseEnum(y.BodyKind, AcquireBodyKind.Form, "source.auth.token.bodyKind", source),
            Body = y.Body ?? new Dictionary<string, string>(StringComparer.Ordinal),
            RawBody = YamlDocumentParts.NullIfBlank(y.RawBody),
            Headers = y.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            BasicAuthClient = y.BasicAuthClient ?? false,
            TokenPath = string.IsNullOrWhiteSpace(y.TokenPath) ? "access_token" : y.TokenPath!.Trim(),
            ApplyHeaderName = YamlDocumentParts.NullIfBlank(y.ApplyHeaderName),
            ApplyPrefix = y.ApplyPrefix ?? "Bearer ",
            RefreshPerIteration = y.RefreshPerIteration ?? false,
        };
    }

    private static AcquireRequest? MapRequest(AcquireRequestYaml? y, string source)
    {
        if (y is null)
        {
            return null;
        }

        return new AcquireRequest
        {
            Method = string.IsNullOrWhiteSpace(y.Method) ? "GET" : y.Method!.Trim(),
            Path = y.Path ?? string.Empty,
            Headers = y.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Query = y.Query ?? new Dictionary<string, string>(StringComparer.Ordinal),
            BodyKind = ParseEnum(y.BodyKind, AcquireBodyKind.None, "source.request.bodyKind", source),
            Body = y.Body,
            BodyFields = y.BodyFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ContentType = YamlDocumentParts.NullIfBlank(y.ContentType),
            ResponseCharset = YamlDocumentParts.NullIfBlank(y.ResponseCharset),
        };
    }

    private static AcquirePagination MapPagination(AcquirePaginationYaml? y, string source)
    {
        if (y is null)
        {
            return new AcquirePagination();
        }

        return new AcquirePagination
        {
            Strategy = ParseEnum(y.Strategy, AcquirePaginationStrategy.None, "source.pagination.strategy", source),
            MaxPages = y.MaxPages ?? 50,
            PageParam = y.PageParam ?? "page",
            StartPage = y.StartPage ?? 1,
            OffsetParam = y.OffsetParam ?? "offset",
            LimitParam = y.LimitParam ?? "limit",
            Limit = y.Limit ?? 100,
            CursorParam = y.CursorParam ?? "cursor",
            CursorPath = YamlDocumentParts.NullIfBlank(y.CursorPath),
            KeysetParam = y.KeysetParam ?? "idAfter",
            KeysetIdPath = YamlDocumentParts.NullIfBlank(y.KeysetIdPath),
            KeysetIdHeader = YamlDocumentParts.NullIfBlank(y.KeysetIdHeader),
            StopOnStatus = y.StopOnStatus,
            RecordsPath = YamlDocumentParts.NullIfBlank(y.RecordsPath),
        };
    }

    private static AcquireIteration MapIteration(AcquireIterationYaml y, string source)
    {
        var kind = ParseEnum(y.Kind, AcquireIterationKind.List, "source.iterate.kind", source);
        var iteration = new AcquireIteration
        {
            Kind = kind,
            Variable = YamlDocumentParts.NullIfBlank(y.Variable),
            Granularity = ParseEnum(y.Granularity, AcquireWindowGranularity.Day, "source.iterate.granularity", source),
            From = YamlDocumentParts.NullIfBlank(y.From),
            To = YamlDocumentParts.NullIfBlank(y.To),
            FromVariable = string.IsNullOrWhiteSpace(y.FromVariable) ? "window.from" : y.FromVariable!.Trim(),
            ToVariable = string.IsNullOrWhiteSpace(y.ToVariable) ? "window.to" : y.ToVariable!.Trim(),
            Values = y.Values ?? [],
            IdRequest = MapRequest(y.IdRequest, source),
            IdPath = YamlDocumentParts.NullIfBlank(y.IdPath),
            BatchSize = y.BatchSize ?? 1,
            BatchSeparator = y.BatchSeparator ?? ",",
        };

        switch (kind)
        {
            case AcquireIterationKind.List when iteration.Variable is null || iteration.Values.Count == 0:
                throw new FlowValidationException($"{source}: a list iteration requires 'variable' and non-empty 'values'.");
            case AcquireIterationKind.IdsFrom when iteration.Variable is null || iteration.IdRequest is null || iteration.IdPath is null:
                throw new FlowValidationException($"{source}: an idsFrom iteration requires 'variable', 'idRequest', and 'idPath'.");
        }

        return iteration;
    }

    private static AcquireReliability MapReliability(AcquireReliabilityYaml? y)
    {
        if (y is null)
        {
            return new AcquireReliability();
        }

        return new AcquireReliability
        {
            TimeoutSeconds = y.TimeoutSeconds ?? 100,
            RateLimitRps = y.RateLimitRps ?? 0,
            Concurrency = Math.Max(1, y.Concurrency ?? 8),
            MaxResponseBytes = y.MaxResponseBytes ?? (500L * 1024 * 1024),
            VerifyTls = y.VerifyTls ?? true,
            UrlAllowlist = y.UrlAllowlist ?? [],
            Retry = y.Retry is null
                ? new AcquireRetry()
                : new AcquireRetry
                {
                    MaxAttempts = y.Retry.MaxAttempts ?? 4,
                    BaseDelayMs = y.Retry.BaseDelayMs ?? 500,
                    MaxDelayMs = y.Retry.MaxDelayMs ?? 30_000,
                    HonorRetryAfter = y.Retry.HonorRetryAfter ?? true,
                },
        };
    }

    private static AcquireLanding MapLanding(AcquireLandingYaml y, string source)
        => new()
        {
            Target = Require(y.Target, "landing.target", source),
            PathTemplate = Require(y.PathTemplate, "landing.pathTemplate", source),
            Format = string.IsNullOrWhiteSpace(y.Format) ? "auto" : y.Format!.Trim(),
            Compression = ParseEnum(y.Compression, AcquireCompression.None, "landing.compression", source),
            Overwrite = y.Overwrite ?? true,
            PersistHeaders = y.PersistHeaders ?? false,
            SkipEmpty = y.SkipEmpty ?? true,
            SkipUnchanged = y.SkipUnchanged ?? true,
            Protect = MapProtect(y.Protect, source),
        };

    private static IReadOnlyList<AcquireProtectRule> MapProtect(List<AcquireProtectYaml>? rules, string source)
    {
        if (rules is null || rules.Count == 0)
        {
            return [];
        }

        var mapped = new List<AcquireProtectRule>(rules.Count);
        foreach (var y in rules)
        {
            // Require() rejects a blank action, so ParseEnum's fallback can never be hit.
            var action = ParseEnum(
                Require(y.Action, "landing.protect[].action", source), AcquireProtectAction.Remove, "landing.protect[].action", source);
            var rule = new AcquireProtectRule
            {
                Path = Require(y.Path, "landing.protect[].path", source),
                Action = action,
                Secret = YamlDocumentParts.NullIfBlank(y.Secret),
                Scope = ParseEnum(y.Scope, AcquireProtectScope.Relationship, "landing.protect[].scope", source),
                Params = MapProtectParams(y),
            };

            if (rule.Secret is null && action is AcquireProtectAction.Hmac or AcquireProtectAction.Encrypt)
            {
                throw new FlowValidationException(
                    $"{source}: 'landing.protect' action '{y.Action}' on path '{rule.Path}' requires 'secret' (the key material reference).");
            }

            mapped.Add(rule);
        }

        return mapped;
    }

    private static Dictionary<string, string> MapProtectParams(AcquireProtectYaml y)
    {
        // Every non-structural scalar on the rule is a transform parameter, so new transform knobs never need a
        // loader change; the reserved keys (path/action/secret/scope) are the structure.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (y.Params is not null)
        {
            foreach (var (key, value) in y.Params)
            {
                result[key] = value;
            }
        }

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result[key] = value!.Trim();
            }
        }

        Add("mode", y.Mode);
        Add("replacement", y.Replacement);
        Add("maskChar", y.MaskChar);
        Add("keepFirst", y.KeepFirst);
        Add("keepLast", y.KeepLast);
        Add("show", y.Show);
        Add("showLast", y.ShowLast);
        Add("algorithm", y.Algorithm);
        Add("iterations", y.Iterations);
        Add("outputLength", y.OutputLength);
        Add("format", y.Format);
        Add("relationship", y.Relationship);
        Add("bucket", y.Bucket);
        Add("step", y.Step);
        return result;
    }

    private static AcquireIncremental? MapIncremental(AcquireIncrementalYaml? y, string source)
    {
        if (y is null)
        {
            return null;
        }

        return new AcquireIncremental
        {
            Source = ParseEnum(y.Source, AcquireWatermarkSource.Response, "incremental.source", source),
            Column = YamlDocumentParts.NullIfBlank(y.Column),
            BindVariable = YamlDocumentParts.NullIfBlank(y.BindVariable),
            Seed = YamlDocumentParts.NullIfBlank(y.Seed),
        };
    }

    private static IReadOnlyDictionary<string, string?> MapOptions(Dictionary<string, string>? options)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (options is not null)
        {
            foreach (var (key, value) in options)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string Require(string? value, string field, string source)
        => string.IsNullOrWhiteSpace(value) ? throw new FlowValidationException($"{source}: '{field}' is required.") : value.Trim();

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(name);
            }
        }

        throw new FlowValidationException(
            $"{source}: '{value}' is not a valid value for '{field}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Binding DTOs (mutable, nullable; YamlDotNet only). Mirrors the file/ingestion loaders' DTO+record split.
// ---------------------------------------------------------------------------------------------------------------

internal sealed class AcquireDocumentYaml
{
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public AcquireSourceYaml? Source { get; set; }
    public AcquireLandingYaml? Landing { get; set; }

    /// <summary>Several endpoints in one flow: one entry per request+landing pair, over the shared top-level
    /// <c>source</c> envelope. The alternative to the single top-level <c>source.request</c>/<c>landing</c>.</summary>
    public List<AcquireItemYaml>? Items { get; set; }

    public AcquireIncrementalYaml? Incremental { get; set; }
    public Dictionary<string, string?>? Params { get; set; }
}

internal sealed class AcquireItemYaml
{
    public string? Name { get; set; }
    public AcquireRequestYaml? Request { get; set; }
    public AcquirePaginationYaml? Pagination { get; set; }
    public List<AcquireIterationYaml>? Iterate { get; set; }
    public AcquireLandingYaml? Landing { get; set; }
}

internal sealed class AcquireSourceYaml
{
    public string? Transport { get; set; }
    public string? BaseUrl { get; set; }
    public AcquireAuthYaml? Auth { get; set; }
    public AcquireRequestYaml? Request { get; set; }
    public AcquirePaginationYaml? Pagination { get; set; }
    public List<AcquireIterationYaml>? Iterate { get; set; }
    public AcquireReliabilityYaml? Reliability { get; set; }
    public Dictionary<string, string>? Options { get; set; }
}

internal sealed class AcquireAuthYaml
{
    public string? Type { get; set; }
    public string? SecretRef { get; set; }
    public string? SecondarySecretRef { get; set; }
    public string? HeaderName { get; set; }
    public string? ParamName { get; set; }
    public string? ValuePrefix { get; set; }
    public AcquireTokenYaml? Token { get; set; }
}

internal sealed class AcquireTokenYaml
{
    public string? Url { get; set; }
    public string? DiscoveryUrl { get; set; }
    public string? Method { get; set; }
    public string? BodyKind { get; set; }
    public Dictionary<string, string>? Body { get; set; }
    public string? RawBody { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool? BasicAuthClient { get; set; }
    public string? TokenPath { get; set; }
    public string? ApplyHeaderName { get; set; }
    public string? ApplyPrefix { get; set; }
    public bool? RefreshPerIteration { get; set; }
}

internal sealed class AcquireRequestYaml
{
    public string? Method { get; set; }
    public string? Path { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? Query { get; set; }
    public string? BodyKind { get; set; }
    public string? Body { get; set; }
    public Dictionary<string, string>? BodyFields { get; set; }
    public string? ContentType { get; set; }
    public string? ResponseCharset { get; set; }
}

internal sealed class AcquirePaginationYaml
{
    public string? Strategy { get; set; }
    public int? MaxPages { get; set; }
    public string? PageParam { get; set; }
    public int? StartPage { get; set; }
    public string? OffsetParam { get; set; }
    public string? LimitParam { get; set; }
    public int? Limit { get; set; }
    public string? CursorParam { get; set; }
    public string? CursorPath { get; set; }
    public string? KeysetParam { get; set; }
    public string? KeysetIdPath { get; set; }
    public string? KeysetIdHeader { get; set; }
    public int? StopOnStatus { get; set; }
    public string? RecordsPath { get; set; }
}

internal sealed class AcquireIterationYaml
{
    public string? Kind { get; set; }
    public string? Variable { get; set; }
    public string? Granularity { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? FromVariable { get; set; }
    public string? ToVariable { get; set; }
    public List<string>? Values { get; set; }
    public AcquireRequestYaml? IdRequest { get; set; }
    public string? IdPath { get; set; }
    public int? BatchSize { get; set; }
    public string? BatchSeparator { get; set; }
}

internal sealed class AcquireReliabilityYaml
{
    public int? TimeoutSeconds { get; set; }
    public double? RateLimitRps { get; set; }
    public int? Concurrency { get; set; }
    public long? MaxResponseBytes { get; set; }
    public bool? VerifyTls { get; set; }
    public List<string>? UrlAllowlist { get; set; }
    public AcquireRetryYaml? Retry { get; set; }
}

internal sealed class AcquireRetryYaml
{
    public int? MaxAttempts { get; set; }
    public int? BaseDelayMs { get; set; }
    public int? MaxDelayMs { get; set; }
    public bool? HonorRetryAfter { get; set; }
}

internal sealed class AcquireLandingYaml
{
    public string? Target { get; set; }
    public string? PathTemplate { get; set; }
    public string? Format { get; set; }
    public string? Compression { get; set; }
    public bool? Overwrite { get; set; }
    public bool? PersistHeaders { get; set; }
    public bool? SkipEmpty { get; set; }
    public bool? SkipUnchanged { get; set; }
    public List<AcquireProtectYaml>? Protect { get; set; }
}

/// <summary>One landing.protect rule. The transform knobs are flat scalars (mode/replacement/maskChar/...) so the
/// YAML reads naturally; anything unanticipated can go under <c>params</c>.</summary>
internal sealed class AcquireProtectYaml
{
    public string? Path { get; set; }
    public string? Action { get; set; }
    public string? Secret { get; set; }
    public string? Scope { get; set; }
    public string? Mode { get; set; }
    public string? Replacement { get; set; }
    public string? MaskChar { get; set; }
    public string? KeepFirst { get; set; }
    public string? KeepLast { get; set; }
    public string? Show { get; set; }
    public string? ShowLast { get; set; }
    public string? Algorithm { get; set; }
    public string? Iterations { get; set; }
    public string? OutputLength { get; set; }
    public string? Format { get; set; }
    public string? Relationship { get; set; }
    public string? Bucket { get; set; }
    public string? Step { get; set; }
    public Dictionary<string, string>? Params { get; set; }
}

internal sealed class AcquireIncrementalYaml
{
    public string? Source { get; set; }
    public string? Column { get; set; }
    public string? BindVariable { get; set; }
    public string? Seed { get; set; }
}
