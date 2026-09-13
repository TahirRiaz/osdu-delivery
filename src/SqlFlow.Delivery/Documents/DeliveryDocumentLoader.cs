using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Loads the kind's documents: delivery flows and retrieval flows (behind the platform's envelope probe, which
/// dispatches on flowType) and mappings (which the platform never sees). Unknown keys are a hard parse error
/// (design.md section 10.4); every failure is a <see cref="FlowValidationException"/> prefixed with the file path.
/// </summary>
public sealed class DeliveryDocumentLoader
{
    private readonly IDeserializer _probe = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Strict: no IgnoreUnmatchedProperties. A misspelled key fails here rather than being silently ignored.
    private readonly IDeserializer _strict = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // Where a document holds a value of any shape (a mapping entry's static value, a modifier's settings), an
        // unquoted scalar keeps the type YAML reads it as, so static: 5 is a number and static: "5" is text.
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public FlowDefinition LoadFlow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseFlow(File.ReadAllText(path), path);
    }

    public RetrievalDefinition LoadRetrieval(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Flow file not found: '{path}'.");
        }

        return ParseRetrieval(File.ReadAllText(path), path);
    }

    public MappingDefinition LoadMapping(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Mapping file not found: '{path}'.");
        }

        return ParseMapping(File.ReadAllText(path), path);
    }

    /// <summary>The discriminator of a document: "delivery" or "retrieval" for a flow, "mapping" for a mapping.</summary>
    public string Probe(string yaml, string source = "<inline>")
    {
        var probe = Deserialize<DocumentProbeYaml>(_probe, yaml, source);
        if (!string.IsNullOrWhiteSpace(probe?.FlowType))
        {
            return probe!.FlowType!;
        }

        if (!string.IsNullOrWhiteSpace(probe?.DocumentType))
        {
            return probe!.DocumentType!;
        }

        throw new FlowValidationException($"{source}: the document declares no 'flowType' (delivery, retrieval) and no 'documentType: mapping'.");
    }

    public FlowDefinition ParseFlow(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {FlowDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<FlowYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return FlowMapper.Map(y, source);
    }

    public RetrievalDefinition ParseRetrieval(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {RetrievalDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<RetrievalYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return RetrievalMapper.Map(y, source);
    }

    public MappingDefinition ParseMapping(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(MappingDefinition.DocumentTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'documentType: {MappingDefinition.DocumentTypeName}', found '{kind}'.");
        }

        var y = Deserialize<MappingYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return MappingMapper.Map(y, source);
    }

    private static T? Deserialize<T>(IDeserializer deserializer, string yaml, string source)
    {
        try
        {
            return deserializer.Deserialize<T>(yaml);
        }
        catch (YamlException ex)
        {
            var detail = ex.InnerException?.Message is { } inner && !ex.Message.Contains(inner, StringComparison.Ordinal)
                ? $"{ex.Message} ({inner})"
                : ex.Message;
            throw new FlowValidationException($"{source}: invalid YAML at line {ex.Start.Line}, column {ex.Start.Column} - {detail}", ex);
        }
    }
}

internal static partial class FlowMapper
{
    /// <summary>The tenant header every OSDU service requires on every request.</summary>
    public const string PartitionHeader = "data-partition-id";

    /// <summary>
    /// True for a kind the storage service accepts on a record (openapi storage v2, Record.kind:
    /// <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$</c>). A kind that only looks like four colon-separated
    /// parts, with a space in the entity type or a two-part version, would be refused on every record of a run.
    /// </summary>
    public static bool IsRecordKind(string kind) => RecordKindPattern().IsMatch(kind);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+\.[0-9]+\.[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex RecordKindPattern();

    public static FlowDefinition Map(FlowYaml y, string source)
    {
        var name = Require(y.Name, "name", source);
        var src = y.Source ?? throw Missing("source", source);
        var render = y.Render ?? throw Missing("render", source);
        var target = y.Target ?? throw Missing("target", source);

        var protocol = ParseEnum<DeliveryProtocol>(Require(target.Protocol, "target.protocol", source), "target.protocol", source);
        var mapping = Require(render.Mapping, "render.mapping", source);
        if (!mapping.Contains('@', StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{source}: render.mapping '{mapping}' must be pinned as 'Name@version'; floating references are not allowed.");
        }

        var flow = new FlowDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Parameters = (y.Parameters ?? []).ToDictionary(
                kv => kv.Key,
                kv => new FlowParameter { Required = kv.Value?.Required ?? false, Default = kv.Value?.Default, Description = kv.Value?.Description },
                StringComparer.Ordinal),
            Source = new FlowSource
            {
                Location = Require(src.Location, "source.location", source),
                Manifest = src.Manifest ?? "manifest.json",
                Records = src.Records,
                Scopes = (src.Scopes ?? []).ToDictionary(
                    kv => kv.Key,
                    kv => new FlowScope { Records = Require(kv.Value?.Records, $"source.scopes.{kv.Key}.records", source), Key = kv.Value?.Key ?? "deliveryKey" },
                    StringComparer.Ordinal),
                Payloads = src.Payloads ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Fingerprint = string.IsNullOrWhiteSpace(src.Fingerprint) ? null : src.Fingerprint!.Trim(),
                LastModified = string.IsNullOrWhiteSpace(src.LastModified) ? null : src.LastModified!.Trim(),
                KnownState = string.IsNullOrWhiteSpace(src.KnownState) ? null : src.KnownState!.Trim(),
                Work = string.IsNullOrWhiteSpace(src.Work) ? null : src.Work!.Trim(),
                ManualSubmission = src.ManualSubmission ?? false,
                ManualSubmissionFileRoots = (src.ManualSubmissionFileRoots ?? [])
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim())
                    .ToList(),
            },
            Render = new FlowRender
            {
                Mapping = mapping,
                References = string.IsNullOrWhiteSpace(render.References) ? "pinned" : render.References!,
                Parameters = render.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                MappingsDirectory = string.IsNullOrWhiteSpace(render.Mappings) ? null : render.Mappings!.Trim(),
                SnapshotsDirectory = string.IsNullOrWhiteSpace(render.Snapshots) ? null : render.Snapshots!.Trim(),
            },
            Change = new FlowChange
            {
                Detect = ParseEnum(y.Change?.Detect, ChangeDetection.RenderedHash, "change.detect", source),
                PayloadDetect = ParseEnum(y.Change?.PayloadDetect, ChangeDetection.ContentHash, "change.payloadDetect", source),
                OnUnchanged = ParseEnum(y.Change?.OnUnchanged, UnchangedAction.Skip, "change.onUnchanged", source),
                UseSourceVersions = y.Change?.UseSourceVersions ?? true,
            },
            Target = new FlowTarget
            {
                Endpoint = Require(target.Endpoint, "target.endpoint", source),
                Auth = MapAuth(target.Auth, source),
                Headers = new Dictionary<string, string>(target.Headers ?? [], StringComparer.OrdinalIgnoreCase),
                Protocol = protocol,
                ProtocolOptions = MapOptions(target.ProtocolOptions),
            },
            Reliability = MapReliability(y.Reliability, source),
            Verify = new FlowVerify { Reconcile = y.Verify?.Reconcile ?? false },
        };

        Validate(flow, source);
        return flow;
    }

    private static void Validate(FlowDefinition flow, string source)
    {
        // One per-record source gate: an opaque fingerprint compared for equality, or a last-modified moment that
        // is ordered as well. Declaring both would leave the gate with two answers to the same question.
        if (flow.Source.Fingerprint is not null && flow.Source.LastModified is not null)
        {
            throw new FlowValidationException(
                $"{source}: source.fingerprint and source.lastModified both name the column that says the source row changed; declare one of them.");
        }

        if (flow.Change.Detect == ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{source}: change.detect cannot be lastModified. A document is always decided by the hash of what it renders to; the source row's last-modified column is source.lastModified.");
        }

        if (flow.Change.PayloadDetect == ChangeDetection.LastModified && !DeliveryProtocols.CarriesPayload(flow.Target.Protocol))
        {
            throw new FlowValidationException(
                $"{source}: change.payloadDetect is lastModified, but the {flow.Target.Protocol} protocol delivers no payload files to take the watermark from.");
        }

        if (flow.Reliability.Concurrency < 1)
        {
            throw new FlowValidationException($"{source}: reliability.concurrency must be at least 1.");
        }

        if (flow.Reliability.Retry.Attempts < 1)
        {
            throw new FlowValidationException($"{source}: reliability.retry.attempts must be at least 1.");
        }

        if (flow.Reliability.BatchRecords is < 1 or > 100_000)
        {
            throw new FlowValidationException($"{source}: reliability.batchRecords must be between 1 and 100000.");
        }

        if (flow.Reliability.FanOut is < 0 or > FlowReliability.MaxFanOut)
        {
            throw new FlowValidationException($"{source}: reliability.fanOut must be between 0 and {FlowReliability.MaxFanOut}.");
        }

        if (flow.Reliability.FanOutMinRecords < 1)
        {
            throw new FlowValidationException($"{source}: reliability.fanOutMinRecords must be at least 1.");
        }

        if (flow.Reliability.RenderParallelism is < 0 or > 256)
        {
            throw new FlowValidationException($"{source}: reliability.renderParallelism must be between 0 and 256.");
        }

        // Every OSDU service makes data-partition-id a required header (openapi storage v2, file v2, search v2,
        // workflow v1, schema-service v1). A flow that leaves it out authors a run where every single request comes
        // back 400 with a message about a tenant, which is a slow and confusing way to learn about a typo in the
        // flow. It costs nothing to say so while the document is being read.
        if (!flow.Target.Headers.ContainsKey(PartitionHeader))
        {
            throw new FlowValidationException(
                $"{source}: target.headers must declare '{PartitionHeader}'. Every OSDU service requires it and rejects a request without it.");
        }

        if (string.IsNullOrWhiteSpace(flow.Target.Headers[PartitionHeader]))
        {
            throw new FlowValidationException($"{source}: target.headers.{PartitionHeader} must not be empty.");
        }

        // A submission carries its records, and points at its payload files where they already are (design.md section
        // 3.4): the node opens them with its own identity when the run delivers. Roots bound what it may be pointed at,
        // so they are only meaningful on a flow that takes submissions at all.
        if (flow.Source.ManualSubmissionFileRoots.Count > 0 && !flow.Source.ManualSubmission)
        {
            throw new FlowValidationException(
                $"{source}: source.manualSubmissionFileRoots bounds where a submission may point at payload files, which only a flow declaring source.manualSubmission accepts.");
        }

        foreach (var root in flow.Source.ManualSubmissionFileRoots)
        {
            if (root.Contains('*', StringComparison.Ordinal) || root.Contains("..", StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{source}: source.manualSubmissionFileRoots entry '{root}' must be a plain prefix (a container or folder), with no wildcard and no '..'.");
            }
        }

        if (flow.Target.ProtocolOptions.BatchSize is < 1 or > ProtocolOptions.MaxBatchSize)
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.batchSize must be between 1 and {ProtocolOptions.MaxBatchSize}.");
        }

        if (flow.Target.ProtocolOptions.DdmsRoot is { } ddmsRoot)
        {
            if (flow.Target.Protocol != DeliveryProtocol.OsduWellLog)
            {
                throw new FlowValidationException(
                    $"{source}: target.protocolOptions.ddmsRoot only applies to the osduWellLog protocol; {flow.Target.Protocol} reaches its services under the endpoint already.");
            }

            if (ddmsRoot.Length == 0 || ddmsRoot[0] != '/' || ddmsRoot.Contains("://", StringComparison.Ordinal) || ddmsRoot.Any(char.IsWhiteSpace))
            {
                throw new FlowValidationException(
                    $"{source}: target.protocolOptions.ddmsRoot '{ddmsRoot}' must be a path under the endpoint starting with '/', such as /api/os-wellbore-ddms.");
            }
        }

        if (flow.Target.ProtocolOptions.LegalValidatePath is { } legalPath
            && legalPath[0] != '/'
            && !(Uri.TryCreate(legalPath, UriKind.Absolute, out var legalUrl) && legalUrl.Scheme is "http" or "https"))
        {
            throw new FlowValidationException(
                $"{source}: target.protocolOptions.legalValidatePath '{legalPath}' must be a path under the endpoint starting with '/', or an absolute http(s) URL.");
        }

        // The bulk endpoint replaces the whole bulk, so at most one chunk can go to it; more than one is a session.
        // A flow that asked for a higher threshold was asking for chunks to overwrite each other.
        if (flow.Target.ProtocolOptions.SessionThresholdChunks is < 0 or > 1)
        {
            throw new FlowValidationException(
                $"{source}: target.protocolOptions.sessionThresholdChunks must be 1 (a single chunk goes straight to the bulk endpoint, more open a session) or 0 (always open a session). "
                + "The bulk endpoint replaces the whole bulk on every write, so several chunks sent to it would overwrite each other.");
        }

        if (flow.Target.ProtocolOptions.MaxChunkValues < 0 || flow.Target.ProtocolOptions.MaxChunkColumns < 0)
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.maxChunkValues and maxChunkColumns must not be negative (0 does not check the chunk shape).");
        }

        if (flow.Target.ProtocolOptions.WorkflowPollSeconds < 1 || flow.Target.ProtocolOptions.WorkflowTimeoutMinutes < 1)
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.workflowPollSeconds and workflowTimeoutMinutes must be at least 1.");
        }

        if (flow.Target.ProtocolOptions.DatasetIndexWaitSeconds < 0)
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.datasetIndexWaitSeconds must not be negative (0 does not wait).");
        }

        if (flow.Target.ProtocolOptions.UploadUrlExpiry is { } expiry && !ValidExpiry(expiry))
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.uploadUrlExpiry '{expiry}' must be a whole number of minutes, hours or days, such as 30M, 12H or 2D.");
        }

        // Both are written onto records the target stores (a dataset record per file, the manifest the workflow
        // ingests), so they answer to the same pattern as a mapping's kind, and are checked while the flow is read
        // rather than on the first delivery that needs them.
        foreach (var (key, value) in new[] { ("datasetKind", flow.Target.ProtocolOptions.DatasetKind), ("manifestKind", flow.Target.ProtocolOptions.ManifestKind) })
        {
            if (!IsRecordKind(value))
            {
                throw new FlowValidationException($"{source}: target.protocolOptions.{key} '{value}' must be 'authority:source:entityType:major.minor.patch'.");
            }
        }

        if (flow.Target.ProtocolOptions.ManifestSection is { } section && !ProtocolOptions.ManifestSections.Contains(section, StringComparer.Ordinal))
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.manifestSection '{section}' is not one of {string.Join(", ", ProtocolOptions.ManifestSections)}.");
        }

        if (string.IsNullOrWhiteSpace(flow.Target.ProtocolOptions.WorkflowAppKey))
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.workflowAppKey must not be empty.");
        }

        foreach (var token in Tokens(flow.Source.Work ?? string.Empty))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: source.work uses '{{{token}}}', which is not declared under parameters.");
            }
        }

        if (DeliveryProtocols.CarriesPayload(flow.Target.Protocol) && flow.Target.ProtocolOptions.Payload is { } payload
            && !flow.Source.Payloads.ContainsKey(payload))
        {
            throw new FlowValidationException($"{source}: target.protocolOptions.payload '{payload}' is not declared under source.payloads.");
        }

        foreach (var (name, template) in flow.Source.Payloads)
        {
            if (!template.Contains("{deliveryKey}", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"{source}: source.payloads.{name} must contain '{{deliveryKey}}'.");
            }
        }

        foreach (var token in Tokens(flow.Source.Location))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: source.location uses '{{{token}}}', which is not declared under parameters.");
            }
        }

        foreach (var token in Tokens(flow.Source.KnownState ?? string.Empty))
        {
            if (!flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: source.knownState uses '{{{token}}}', which is not declared under parameters.");
            }
        }

        if (flow.Target.Auth.Type == TargetAuthType.OAuth2ClientCredentials && flow.Target.Auth.Token is null)
        {
            throw new FlowValidationException($"{source}: target.auth of type oauth2ClientCredentials needs a 'token' block with the token endpoint url and body.");
        }

        if (flow.Target.Auth.Type is TargetAuthType.Bearer or TargetAuthType.ApiKeyHeader or TargetAuthType.Basic && string.IsNullOrWhiteSpace(flow.Target.Auth.SecretRef))
        {
            throw new FlowValidationException($"{source}: target.auth of type {flow.Target.Auth.Type} needs secretRef.");
        }

        if (flow.Target.Auth.Type == TargetAuthType.ApiKeyHeader && string.IsNullOrWhiteSpace(flow.Target.Auth.HeaderName))
        {
            throw new FlowValidationException($"{source}: target.auth of type apiKeyHeader needs headerName.");
        }
    }

    internal static TargetAuth MapAuth(TargetAuthYaml? a, string source, string key = "target.auth")
    {
        if (a is null)
        {
            return new TargetAuth { Type = TargetAuthType.None };
        }

        return new TargetAuth
        {
            Type = ParseEnum(a.Type, TargetAuthType.None, key + ".type", source),
            SecretRef = a.SecretRef,
            SecondarySecretRef = a.SecondarySecretRef,
            HeaderName = a.HeaderName,
            ValuePrefix = a.ValuePrefix,
            Token = a.Token is null ? null : new TargetTokenEndpoint
            {
                Url = a.Token.Url,
                DiscoveryUrl = a.Token.DiscoveryUrl,
                Body = a.Token.Body ?? new Dictionary<string, string>(StringComparer.Ordinal),
                BasicAuthClient = a.Token.BasicAuthClient,
                TokenPath = a.Token.TokenPath ?? "access_token",
                ApplyPrefix = a.Token.ApplyPrefix ?? "Bearer ",
            },
        };
    }

    private static ProtocolOptions MapOptions(ProtocolOptionsYaml? o)
    {
        if (o is null)
        {
            return new ProtocolOptions();
        }

        return new ProtocolOptions
        {
            RecordPath = o.RecordPath,
            RecordMethod = o.RecordMethod,
            DataPath = o.DataPath,
            SessionPath = o.SessionPath,
            SessionDataPath = o.SessionDataPath,
            SessionCommitPath = o.SessionCommitPath,
            VerifyPath = o.VerifyPath,
            DeletePath = o.DeletePath,
            PurgePath = o.PurgePath,
            PurgeVersionsPath = o.PurgeVersionsPath,
            BulkDeletePath = o.BulkDeletePath,
            ProbePath = o.ProbePath,
            Payload = o.Payload,
            SessionThresholdChunks = o.SessionThresholdChunks ?? 1,
            MaxChunkValues = o.MaxChunkValues ?? WellboreDdmsBulkLimits.MaxChunkValues,
            MaxChunkColumns = o.MaxChunkColumns ?? WellboreDdmsBulkLimits.MaxChunkColumns,
            PayloadContentType = o.PayloadContentType ?? "application/x-parquet",
            VersionPath = o.VersionPath ?? "recordIdVersions[0]",
            SkipDuplicates = o.SkipDuplicates ?? false,
            VerifyBatchPath = o.VerifyBatchPath,
            DdmsRoot = string.IsNullOrWhiteSpace(o.DdmsRoot) ? null : o.DdmsRoot!.Trim().TrimEnd('/'),
            ValidateLegalTags = o.ValidateLegalTags ?? true,
            LegalValidatePath = string.IsNullOrWhiteSpace(o.LegalValidatePath) ? null : o.LegalValidatePath!.Trim(),
            PreserveDataKeys = o.PreserveDataKeys ?? [],
            BatchSize = o.BatchSize ?? 100,
            UploadUrlPath = o.UploadUrlPath,
            FileMetadataPath = o.FileMetadataPath,
            DatasetKind = string.IsNullOrWhiteSpace(o.DatasetKind) ? "osdu:wks:dataset--File.Generic:1.0.0" : o.DatasetKind!.Trim(),
            UploadHeaders = new Dictionary<string, string>(o.UploadHeaders ?? [], StringComparer.OrdinalIgnoreCase),
            DatasetsProperty = string.IsNullOrWhiteSpace(o.DatasetsProperty) ? "Datasets" : o.DatasetsProperty!.Trim(),
            WorkflowName = string.IsNullOrWhiteSpace(o.WorkflowName) ? "Osdu_ingest" : o.WorkflowName!.Trim(),
            WorkflowRunPath = o.WorkflowRunPath,
            WorkflowStatusPath = o.WorkflowStatusPath,
            WorkflowPollSeconds = o.WorkflowPollSeconds ?? 10,
            WorkflowTimeoutMinutes = o.WorkflowTimeoutMinutes ?? 60,
            DatasetIndexWaitSeconds = o.DatasetIndexWaitSeconds ?? 120,
            ManifestKind = string.IsNullOrWhiteSpace(o.ManifestKind) ? "osdu:wks:Manifest:1.0.0" : o.ManifestKind!.Trim(),
            UploadUrlExpiry = string.IsNullOrWhiteSpace(o.UploadUrlExpiry) ? null : o.UploadUrlExpiry!.Trim(),
            FileDeletePath = o.FileDeletePath,
            ManifestSection = string.IsNullOrWhiteSpace(o.ManifestSection) ? null : o.ManifestSection!.Trim(),
            WorkflowAppKey = string.IsNullOrWhiteSpace(o.WorkflowAppKey) ? "osdu-delivery" : o.WorkflowAppKey!.Trim(),
            WorkflowPayload = new Dictionary<string, string>(o.WorkflowPayload ?? [], StringComparer.Ordinal),
            RecordQueryPath = o.RecordQueryPath,
            SearchQueryPath = o.SearchQueryPath,
        };
    }

    internal static FlowReliability MapReliability(FlowReliabilityYaml? r, string source)
    {
        var defaults = new FlowReliability();
        if (r is null)
        {
            return defaults;
        }

        var retryDefaults = new FlowRetry();
        return new FlowReliability
        {
            Concurrency = r.Concurrency ?? defaults.Concurrency,
            Retry = r.Retry is null ? retryDefaults : new FlowRetry
            {
                Attempts = r.Retry.Attempts ?? retryDefaults.Attempts,
                Backoff = ParseEnum(r.Retry.Backoff, BackoffKind.Exponential, "reliability.retry.backoff", source),
                BaseDelayMs = r.Retry.BaseDelayMs ?? retryDefaults.BaseDelayMs,
                MaxDelayMs = r.Retry.MaxDelayMs ?? retryDefaults.MaxDelayMs,
                HonorRetryAfter = r.Retry.HonorRetryAfter ?? retryDefaults.HonorRetryAfter,
                RecordBaseDelayMinutes = r.Retry.RecordBaseDelayMinutes ?? retryDefaults.RecordBaseDelayMinutes,
                RecordMaxDelayMinutes = r.Retry.RecordMaxDelayMinutes ?? retryDefaults.RecordMaxDelayMinutes,
            },
            SkipStatusCodes = r.SkipStatusCodes ?? [],
            TimeoutSeconds = r.TimeoutSeconds ?? defaults.TimeoutSeconds,
            RateLimitRps = r.RateLimitRps ?? defaults.RateLimitRps,
            VerifyTls = r.VerifyTls ?? defaults.VerifyTls,
            UrlAllowlist = r.UrlAllowlist ?? [],
            MaxResponseBytes = r.MaxResponseBytes ?? defaults.MaxResponseBytes,
            MaxRequestBodyBytes = r.MaxRequestBodyBytes ?? defaults.MaxRequestBodyBytes,
            LeaseSeconds = r.LeaseSeconds ?? defaults.LeaseSeconds,
            BatchSize = r.BatchSize ?? defaults.BatchSize,
            BatchRecords = r.BatchRecords ?? defaults.BatchRecords,
            FanOut = r.FanOut ?? defaults.FanOut,
            FanOutMinRecords = r.FanOutMinRecords ?? defaults.FanOutMinRecords,
            RenderParallelism = r.RenderParallelism ?? defaults.RenderParallelism,
        };
    }

    /// <summary>The file service's expiryTime shape: a whole number of minutes, hours or days.</summary>
    private static bool ValidExpiry(string expiry)
        => expiry.Length >= 2 && expiry[^1] is 'M' or 'H' or 'D' && expiry[..^1].All(char.IsAsciiDigit);

    internal static IEnumerable<string> Tokens(string text)
        => System.Text.RegularExpressions.Regex.Matches(text, @"\{(?<name>[A-Za-z0-9_]+)\}").Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal);

    internal static string Require(string? value, string key, string source)
        => string.IsNullOrWhiteSpace(value) ? throw Missing(key, source) : value!.Trim();

    internal static FlowValidationException Missing(string key, string source) => new($"{source}: '{key}' is required.");

    internal static T ParseEnum<T>(string value, string key, string source)
        where T : struct, Enum
        => Enum.TryParse<T>(value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new FlowValidationException($"{source}: '{key}' value '{value}' is not one of {string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");

    internal static T ParseEnum<T>(string? value, T fallback, string key, string source)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? fallback : ParseEnum<T>(value!, key, source);
}
