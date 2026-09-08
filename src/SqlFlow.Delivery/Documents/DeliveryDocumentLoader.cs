using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Loads the delivery kind's documents: flows (behind the platform's envelope probe, which dispatches on flowType)
/// and mappings (which the platform never sees). Unknown keys are a hard parse error (design.md section 10.4);
/// every failure is a <see cref="FlowValidationException"/> prefixed with the file path.
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

    public MappingDefinition LoadMapping(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Mapping file not found: '{path}'.");
        }

        return ParseMapping(File.ReadAllText(path), path);
    }

    /// <summary>The discriminator of a document: "delivery" for a flow, "mapping" for a mapping.</summary>
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

        throw new FlowValidationException($"{source}: the document declares neither 'flowType: delivery' nor 'documentType: mapping'.");
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

internal static class FlowMapper
{
    public static FlowDefinition Map(FlowYaml y, string source)
    {
        var name = Require(y.Name, "name", source);
        var src = y.Source ?? throw Missing("source", source);
        var render = y.Render ?? throw Missing("render", source);
        var target = y.Target ?? throw Missing("target", source);

        var protocol = ParseEnum<DeliveryProtocol>(Require(target.Protocol, "target.protocol", source), "target.protocol", source);
        if (!DeliveryProtocols.IsImplemented(protocol))
        {
            throw new FlowValidationException(
                $"{source}: target.protocol '{target.Protocol}' is declared in the protocol vocabulary but not implemented in this version. Implemented: osduRecord, osduWellLog.");
        }

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
                Fingerprint = src.Fingerprint,
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
        if (flow.Reliability.Concurrency < 1)
        {
            throw new FlowValidationException($"{source}: reliability.concurrency must be at least 1.");
        }

        if (flow.Reliability.Retry.Attempts < 1)
        {
            throw new FlowValidationException($"{source}: reliability.retry.attempts must be at least 1.");
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

    private static TargetAuth MapAuth(TargetAuthYaml? a, string source)
    {
        if (a is null)
        {
            return new TargetAuth { Type = TargetAuthType.None };
        }

        return new TargetAuth
        {
            Type = ParseEnum(a.Type, TargetAuthType.None, "target.auth.type", source),
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
            ProbePath = o.ProbePath,
            Payload = o.Payload,
            SessionThresholdChunks = o.SessionThresholdChunks ?? 1,
            PayloadContentType = o.PayloadContentType ?? "application/x-parquet",
            VersionPath = o.VersionPath ?? "recordIdVersions[0]",
            PreserveDataKeys = o.PreserveDataKeys ?? [],
        };
    }

    private static FlowReliability MapReliability(FlowReliabilityYaml? r, string source)
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
        };
    }

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

internal static class MappingMapper
{
    public static MappingDefinition Map(MappingYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var version = FlowMapper.Require(y.Version, "version", source);
        var kind = FlowMapper.Require(y.Kind, "kind", source);
        if (kind.Split(':').Length != 4)
        {
            throw new FlowValidationException($"{source}: kind '{kind}' must be 'authority:source:entityType:version'.");
        }

        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var identity = y.Identity ?? throw FlowMapper.Missing("identity", source);
        var envelope = y.Envelope ?? throw FlowMapper.Missing("envelope", source);
        var acl = envelope.Acl ?? throw FlowMapper.Missing("envelope.acl", source);

        var definitions = (y.Definitions ?? []).ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<MappingProperty>)(kv.Value ?? []).Select((p, i) => MapProperty(p, $"definitions.{kv.Key}[{i}]", source)).ToList(),
            StringComparer.Ordinal);

        var mapping = new MappingDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Version = version,
            Kind = kind,
            Description = y.Description,
            Source = new MappingSource
            {
                System = FlowMapper.Require(src.System, "source.system", source),
                Scopes = src.Scopes ?? [],
            },
            Identity = new MappingIdentity
            {
                NaturalKey = identity.NaturalKey is { Count: > 0 } nk ? nk : throw new FlowValidationException($"{source}: identity.naturalKey must name at least one property."),
                Label = string.IsNullOrWhiteSpace(identity.Label) ? null : identity.Label!.Trim(),
            },
            Envelope = new MappingEnvelope
            {
                LegalTags = envelope.LegalTags is { Count: > 0 } lt ? lt : throw new FlowValidationException($"{source}: envelope.legalTags must list at least one legal tag."),
                OtherRelevantDataCountries = envelope.OtherRelevantDataCountries is { Count: > 0 } c ? c : throw new FlowValidationException($"{source}: envelope.otherRelevantDataCountries must list at least one country."),
                Acl = new MappingAcl
                {
                    Owners = acl.Owners is { Count: > 0 } o ? o : throw new FlowValidationException($"{source}: envelope.acl.owners must list at least one group."),
                    Viewers = acl.Viewers is { Count: > 0 } v ? v : throw new FlowValidationException($"{source}: envelope.acl.viewers must list at least one group."),
                },
                Tags = envelope.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal),
            },
            Parameters = (y.Parameters ?? []).ToDictionary(
                kv => kv.Key,
                kv => new MappingParameter { Required = kv.Value?.Required ?? false, Default = kv.Value?.Default, Description = kv.Value?.Description },
                StringComparer.Ordinal),
            Properties = (y.Properties ?? throw FlowMapper.Missing("properties", source)).Select((p, i) => MapProperty(p, $"properties[{i}]", source)).ToList(),
            Definitions = definitions,
            Fixtures = (y.Fixtures ?? []).Select((f, i) => new MappingFixture
            {
                Name = FlowMapper.Require(f.Name, $"fixtures[{i}].name", source),
                Record = f.Record ?? throw FlowMapper.Missing($"fixtures[{i}].record", source),
                Scopes = (f.Scopes ?? []).ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<IReadOnlyDictionary<string, string?>>)(kv.Value ?? []).Select(r => (IReadOnlyDictionary<string, string?>)r).ToList(),
                    StringComparer.Ordinal),
                Parameters = f.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Expected = FlowMapper.Require(f.Expected, $"fixtures[{i}].expected", source),
            }).ToList(),
        };

        Validate(mapping, source);
        return mapping;
    }

    private static void Validate(MappingDefinition mapping, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in mapping.Properties)
        {
            if (!seen.Add(property.Target))
            {
                throw new FlowValidationException($"{source}: property '{property.Target}' is mapped twice.");
            }
        }

        foreach (var key in mapping.Identity.NaturalKey)
        {
            var property = mapping.Properties.FirstOrDefault(p => p.Target == key)
                ?? throw new FlowValidationException($"{source}: identity.naturalKey names '{key}', which is not a top-level mapped property.");
            if (string.IsNullOrWhiteSpace(property.Source) || property.Collection || property.IsObject)
            {
                throw new FlowValidationException($"{source}: natural key property '{key}' must be a scalar with a source column.");
            }
        }

        if (!mapping.Parameters.ContainsKey(Snapshots.RenderContext.DataPartitionParameter))
        {
            throw new FlowValidationException($"{source}: the mapping must declare the '{Snapshots.RenderContext.DataPartitionParameter}' parameter; record ids and references are minted in that partition.");
        }
    }

    private static MappingProperty MapProperty(MappingPropertyYaml p, string where, string source)
    {
        var target = FlowMapper.Require(p.Target, $"{where}.target", source);
        var transform = FlowMapper.ParseEnum(p.Transform, MappingTransform.None, $"{where}.transform", source);
        var config = p.Config;
        var property = new MappingProperty
        {
            Target = target,
            Source = string.IsNullOrWhiteSpace(p.Source) ? null : p.Source!.Trim(),
            Description = p.Description,
            Transform = transform,
            Config = config is null ? new TransformConfig() : new TransformConfig
            {
                Value = config.Value,
                Delimiter = config.Delimiter,
                Index = config.Index,
                Resolve = config.Resolve,
                Values = new Dictionary<string, string>(config.Values ?? [], StringComparer.OrdinalIgnoreCase),
                Default = config.Default,
                Type = config.Type,
                MatchBy = config.MatchBy ?? [],
                ValueMap = new Dictionary<string, string>(config.ValueMap ?? [], StringComparer.OrdinalIgnoreCase),
                OnMiss = FlowMapper.ParseEnum(config.OnMiss, ReferenceMiss.Hold, $"{where}.config.onMiss", source),
                System = config.System,
                Keys = config.Keys ?? [],
                Format = config.Format,
                InputFormat = config.InputFormat,
            },
            Collection = p.Collection,
            Scope = p.Scope,
            Properties = (p.Properties ?? []).Select((c, i) => MapProperty(c, $"{where}.properties[{i}]", source)).ToList(),
            Definition = p.Definition,
            Examples = (p.Examples ?? []).Select((e, i) => new PropertyExample
            {
                Source = e.Source,
                Row = e.Row ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                Target = e.Target ?? throw FlowMapper.Missing($"{where}.examples[{i}].target", source),
            }).ToList(),
        };

        if (property.Collection && !property.IsObject)
        {
            throw new FlowValidationException($"{source}: {where} ('{target}') is a collection and must declare 'properties' or 'definition'.");
        }

        if (property.Properties.Count > 0 && property.Definition is not null)
        {
            throw new FlowValidationException($"{source}: {where} ('{target}') declares both 'properties' and 'definition'; use one.");
        }

        switch (transform)
        {
            case MappingTransform.Constant when property.Config.Value is null:
                throw new FlowValidationException($"{source}: {where} ('{target}') constant transform needs config.value.");
            case MappingTransform.Split when property.Config.Delimiter is null:
                throw new FlowValidationException($"{source}: {where} ('{target}') split transform needs config.delimiter.");
            case MappingTransform.Equals when property.Config.Resolve is null:
                throw new FlowValidationException($"{source}: {where} ('{target}') equals transform needs config.resolve.");
            case MappingTransform.Map when property.Config.Values.Count == 0:
                throw new FlowValidationException($"{source}: {where} ('{target}') map transform needs config.values.");
            case MappingTransform.Reference when string.IsNullOrWhiteSpace(property.Config.Type):
                throw new FlowValidationException($"{source}: {where} ('{target}') reference transform needs config.type.");
            case MappingTransform.DeliveredReference when string.IsNullOrWhiteSpace(property.Config.Type):
                throw new FlowValidationException($"{source}: {where} ('{target}') deliveredReference transform needs config.type (the entity type).");
            case MappingTransform.Template when string.IsNullOrWhiteSpace(property.Config.Format):
                throw new FlowValidationException($"{source}: {where} ('{target}') template transform needs config.format.");
        }

        if (transform is not (MappingTransform.Constant or MappingTransform.Template or MappingTransform.DeliveredReference)
            && !property.IsObject && property.Source is null)
        {
            throw new FlowValidationException($"{source}: {where} ('{target}') needs a source column.");
        }

        return property;
    }
}
