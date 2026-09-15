using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Templates;
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

    public CacheDefinition LoadCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Cache flow file not found: '{path}'.");
        }

        return ParseCache(File.ReadAllText(path), path);
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

    /// <summary>The discriminator of a document: "delivery", "retrieval" or "cache" for a flow, "mapping" for a mapping.</summary>
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

        throw new FlowValidationException($"{source}: the document declares no 'flowType' (delivery, retrieval, cache) and no 'documentType: mapping'.");
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

    public CacheDefinition ParseCache(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var kind = Probe(yaml, source);
        if (!kind.Equals(CacheDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException($"{source}: expected 'flowType: {CacheDefinition.FlowTypeName}', found '{kind}'.");
        }

        var y = Deserialize<CacheYaml>(_strict, yaml, source) ?? throw new FlowValidationException($"{source}: the document is empty.");
        return CacheMapper.Map(y, source);
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

    /// <summary>
    /// The template a mapping document names in its <c>template</c> block, read without validating anything else, or
    /// null when the document is not readable YAML or names no complete template. A document that fails to load still
    /// pins what it names, so the template cannot be deleted from under a mapping that is only waiting for a fix.
    /// </summary>
    public TemplateReference? TemplateNamedBy(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        TemplateProbeYaml? probe;
        try
        {
            probe = _probe.Deserialize<TemplateProbeYaml>(yaml);
        }
        catch (YamlException)
        {
            // Unreadable YAML, or a template block of the wrong shape, names no template. ParseMapping reports why.
            return null;
        }

        var kind = probe?.Template?.Kind?.Trim();
        var version = probe?.Template?.Version?.Trim();
        return string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(version) ? null : new TemplateReference(kind, version);
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

    /// <summary>Only the template block of a mapping document; every other key is ignored.</summary>
    private sealed class TemplateProbeYaml
    {
        public MappingTemplateYaml? Template { get; set; }
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
                // A flow reading from SQL has no drop of its own until a run extracts one under its work location, so its
                // location is its work location unless it says otherwise.
                Location = src.Sql is not null && string.IsNullOrWhiteSpace(src.Location)
                    ? Require(src.Work, "source.work", source)
                    : Require(src.Location, "source.location", source),
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
                Sql = src.Sql is null ? null : MapSql(src.Sql, source),
                Replica = src.Replica is null ? null : MapReplica(src.Replica, name, source),
            },
            Render = new FlowRender
            {
                Mapping = mapping,
                CacheVersion = MapCacheVersion(render, source),
                Parameters = render.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
                MappingsDirectory = string.IsNullOrWhiteSpace(render.Mappings) ? null : render.Mappings!.Trim(),
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

    /// <summary>
    /// The cache version a flow pins, <c>current</c> when it pins none. A flow reads the cache of the partition it delivers to,
    /// so there is no cache to name: a document still naming one under <c>render.cache</c> is refused, rather than read against
    /// a cache other than the one its author meant.
    /// </summary>
    private static string MapCacheVersion(FlowRenderYaml render, string source)
    {
        if (!string.IsNullOrWhiteSpace(render.Cache))
        {
            throw new FlowValidationException(
                $"{source}: render.cache is not a setting any more: a flow reads the cache of the partition it delivers to (target.headers.data-partition-id), which every cache flow of that partition fills. Remove render.cache.");
        }

        return string.IsNullOrWhiteSpace(render.CacheVersion) ? FlowRender.CurrentCacheVersion : render.CacheVersion!.Trim();
    }

    private static FlowSqlSource MapSql(FlowSqlYaml sql, string source)
    {
        FlowSqlWatermark? watermark = null;
        if (sql.Watermark is { } declared)
        {
            watermark = new FlowSqlWatermark
            {
                Column = Require(declared.Column, "source.sql.watermark.column", source),
                Type = ParseEnum<SqlWatermarkType>(Require(declared.Type, "source.sql.watermark.type", source), "source.sql.watermark.type", source),
                OverlapMinutes = declared.OverlapMinutes ?? 0,
                Lookback = declared.Lookback ?? 0,
            };
        }

        return new FlowSqlSource
        {
            Connection = Require(sql.Connection, "source.sql.connection", source),
            Record = Require(sql.Record, "source.sql.record", source),
            Scopes = (sql.Scopes ?? []).ToDictionary(
                kv => kv.Key.Trim(),
                kv => Require(kv.Value, $"source.sql.scopes.{kv.Key}", source),
                StringComparer.Ordinal),
            Watermark = watermark,
            Isolation = ParseEnum(sql.Isolation, SqlIsolation.Snapshot, "source.sql.isolation", source),
            CommandTimeoutSeconds = sql.CommandTimeoutSeconds ?? 0,
            RowsPerFile = sql.RowsPerFile ?? FlowSqlSource.DefaultRowsPerFile,
            PayloadLocationColumn = string.IsNullOrWhiteSpace(sql.PayloadLocationColumn) ? null : sql.PayloadLocationColumn.Trim(),
            PayloadHashColumn = string.IsNullOrWhiteSpace(sql.PayloadHashColumn) ? null : sql.PayloadHashColumn.Trim(),
        };
    }

    /// <summary>
    /// What a SQL source must be for a run to extract from it: a connection that holds no literal secret, child queries
    /// named for datasets, a watermark carried as the flow's source version, parameters that bind as <c>@name</c>, and
    /// payload columns exactly when the protocol streams payload files.
    /// </summary>
    private static void ValidateSql(FlowDefinition flow, FlowSqlSource sql, string source)
    {
        Engine.SqlSource.SqlSourceConnection.CheckDeclared(sql.Connection, source);

        foreach (var name in sql.Scopes.Keys)
        {
            if (!SqlIdentifier().IsMatch(name) || name.Equals(Drops.DropManifest.RootScope, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{source}: source.sql.scopes '{name}' must name a child dataset the mapping repeats (letters, digits and '_'), and not '{Drops.DropManifest.RootScope}', which is source.sql.record.");
            }
        }

        if (sql.CommandTimeoutSeconds < 0)
        {
            throw new FlowValidationException($"{source}: source.sql.commandTimeoutSeconds must not be negative (0 lets a query run as long as the run does).");
        }

        if (sql.RowsPerFile < 1)
        {
            throw new FlowValidationException($"{source}: source.sql.rowsPerFile must be at least 1.");
        }

        if (sql.Watermark is { } watermark)
        {
            if (!flow.Change.UseSourceVersions)
            {
                throw new FlowValidationException(
                    $"{source}: source.sql.watermark is carried from one run to the next as the flow's source version, which change.useSourceVersions: false turns off.");
            }

            var dated = watermark.Type is SqlWatermarkType.DateTime or SqlWatermarkType.DateTimeOffset;
            if (watermark.OverlapMinutes < 0 || watermark.Lookback < 0)
            {
                throw new FlowValidationException($"{source}: source.sql.watermark.overlapMinutes and lookback must not be negative.");
            }

            if (!dated && watermark.OverlapMinutes > 0)
            {
                throw new FlowValidationException(
                    $"{source}: source.sql.watermark.overlapMinutes applies to a datetime or datetimeoffset watermark; a {watermark.Type} watermark looks back with lookback.");
            }

            if (dated && watermark.Lookback > 0)
            {
                throw new FlowValidationException(
                    $"{source}: source.sql.watermark.lookback applies to a number or rowversion watermark; a {watermark.Type} watermark looks back with overlapMinutes.");
            }
        }

        foreach (var name in flow.Parameters.Keys)
        {
            if (name.Equals(FlowSqlSource.WatermarkParameter, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"{source}: a flow reading from SQL binds its watermark as @{FlowSqlSource.WatermarkParameter}, so no parameter may be named '{name}'.");
            }

            if (!SqlIdentifier().IsMatch(name))
            {
                throw new FlowValidationException($"{source}: a flow reading from SQL binds every parameter as @name, so parameter '{name}' must be letters, digits and '_', not starting with a digit.");
            }
        }

        var streamsPayload = DeliveryProtocols.CarriesPayload(flow.Target.Protocol)
            && (flow.Target.ProtocolOptions.Payload is not null || flow.Source.Payloads.Count == 1);
        if (streamsPayload && sql.PayloadLocationColumn is null)
        {
            throw new FlowValidationException(
                $"{source}: the {flow.Target.Protocol} protocol streams payload files, so source.sql.payloadLocationColumn must name the record query's column holding where each record's files are.");
        }

        if (!streamsPayload && (sql.PayloadLocationColumn ?? sql.PayloadHashColumn) is not null)
        {
            throw new FlowValidationException(
                $"{source}: source.sql.payloadLocationColumn and payloadHashColumn describe payload files, which the {flow.Target.Protocol} protocol of this flow does not stream.");
        }

        if (streamsPayload && sql.PayloadHashColumn is null && flow.Change.PayloadDetect != ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{source}: the flow decides payload changes by content hash, so source.sql.payloadHashColumn must name the record query's column holding it; or take the files' modified times instead with change.payloadDetect: lastModified.");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial System.Text.RegularExpressions.Regex SqlIdentifier();

    private static FlowReplica MapReplica(FlowReplicaYaml replica, string flowName, string source)
    {
        var columns = new Dictionary<string, IReadOnlyList<Replica.Schema.ColumnTransform>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, entries) in replica.Columns ?? [])
        {
            var transforms = new List<Replica.Schema.ColumnTransform>();
            var index = 0;
            foreach (var entry in entries ?? [])
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"source.replica.columns.{scope}[{index++}]");
                transforms.Add(new Replica.Schema.ColumnTransform
                {
                    Name = Require(entry?.Name, $"{path}.name", source).Trim(),
                    Expression = string.IsNullOrWhiteSpace(entry!.Expr) ? null : entry.Expr.Trim(),
                    Type = string.IsNullOrWhiteSpace(entry.Type) ? null : entry.Type.Trim(),
                    Alias = string.IsNullOrWhiteSpace(entry.As) ? null : entry.As.Trim(),
                    SortOrder = entry.Order,
                    Virtual = entry.Virtual ?? false,
                    ExcludeFromView = entry.ExcludeFromView ?? false,
                });
            }

            if (!columns.TryAdd(scope.Trim(), transforms))
            {
                throw new FlowValidationException($"{source}: source.replica.columns names scope '{scope}' twice (scope names are compared without case).");
            }
        }

        return new FlowReplica
        {
            Connection = Require(replica.Connection, "source.replica.connection", source),
            Schema = string.IsNullOrWhiteSpace(replica.Schema) ? FlowReplica.DefaultSchema(flowName) : replica.Schema.Trim(),
            InferTypes = replica.InferTypes ?? false,
            OnConvertError = ParseEnum(replica.OnConvertError, Replica.Schema.ConvertErrorMode.Fail, "source.replica.onConvertError", source),
            Threshold = replica.Threshold ?? 1.0,
            Sample = replica.Sample ?? 0,
            PreserveLeadingZeros = replica.PreserveLeadingZeros ?? true,
            Culture = string.IsNullOrWhiteSpace(replica.Culture) ? null : replica.Culture.Trim(),
            AllowTableRewrite = replica.AllowTableRewrite ?? false,
            BatchUpsert = replica.BatchUpsert ?? false,
            RetentionDays = replica.RetentionDays ?? FlowReplica.DefaultRetentionDays,
            Columns = columns,
        };
    }

    /// <summary>
    /// What a replica must be for a load to write it: a connection holding no literal secret, a schema the flow can own, an
    /// inference policy in range, and column transforms that each do something the replica can hold.
    /// </summary>
    private static void ValidateReplica(FlowReplica replica, string source)
    {
        Engine.SqlSource.SqlSourceConnection.CheckDeclared(replica.Connection, source, "source.replica.connection");

        if (!SqlIdentifier().IsMatch(replica.Schema) || replica.Schema.Length > FlowReplica.MaxSchemaLength
            || replica.Schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || replica.Schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
            || replica.Schema.Equals("guest", StringComparison.OrdinalIgnoreCase) || replica.Schema.StartsWith("db_", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: source.replica.schema '{replica.Schema}' must be 1 to {FlowReplica.MaxSchemaLength} letters, digits and '_' (not starting with a digit), and not a system schema (sys, INFORMATION_SCHEMA, guest, db_*)."));
        }

        if (replica.Threshold is <= 0 or > 1 || double.IsNaN(replica.Threshold))
        {
            throw new FlowValidationException($"{source}: source.replica.threshold must be above 0 and at most 1 (the fraction of values that must convert).");
        }

        if (replica.Sample < 0)
        {
            throw new FlowValidationException($"{source}: source.replica.sample must not be negative (0 profiles every row).");
        }

        if (replica.RetentionDays < 0)
        {
            throw new FlowValidationException($"{source}: source.replica.retentionDays must not be negative (0 keeps every submission's record list).");
        }

        if (replica.Culture is { } culture)
        {
            try
            {
                _ = CultureInfo.GetCultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                throw new FlowValidationException($"{source}: source.replica.culture '{culture}' is not a culture name (nb-NO, en-US).");
            }
        }

        foreach (var (scope, transforms) in replica.Columns)
        {
            if (!SqlIdentifier().IsMatch(scope))
            {
                throw new FlowValidationException($"{source}: source.replica.columns scope '{scope}' must be a scope of the drop: letters, digits and '_'.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var transform in transforms)
            {
                var where = $"{source}: source.replica.columns.{scope} '{transform.Name}'";
                if (!names.Add(transform.Name))
                {
                    throw new FlowValidationException($"{where} is declared twice (column names are compared without case).");
                }

                foreach (var name in new[] { transform.Name, transform.Alias }.OfType<string>())
                {
                    if (name.Length > 128 || name.StartsWith(Replica.ReplicaNames.SystemPrefix, StringComparison.Ordinal))
                    {
                        throw new FlowValidationException($"{where}: '{name}' must be at most 128 characters and not start with '{Replica.ReplicaNames.SystemPrefix}', which the replica's own columns use.");
                    }
                }

                if (transform.Virtual && (transform.Expression is null || Replica.Schema.ColumnTransformExpression.ReferencesColumnToken(transform.Expression)))
                {
                    throw new FlowValidationException($"{where} is virtual, so it needs an expr, and one without @ColName: there is no landed column to stand for.");
                }

                if (transform.Expression is not null && transform.Type is null)
                {
                    throw new FlowValidationException($"{where} has an expr but no type; the type is the replica column the expression fills (decimal(18, 4), nvarchar(max)).");
                }

                if (transform.Expression is null && transform.Type is null && transform.Alias is null && !transform.ExcludeFromView)
                {
                    throw new FlowValidationException($"{where} does nothing: give it a type, an expr, an 'as' or excludeFromView.");
                }

                if (transform.Type is { } type)
                {
                    Replica.Schema.SqlDataType parsed;
                    try
                    {
                        parsed = Replica.Schema.SqlDataType.Parse(type);
                    }
                    catch (DeliveryException ex)
                    {
                        throw new FlowValidationException($"{where}: {ex.Message}", ex);
                    }

                    if (parsed.Family == Replica.Schema.SqlTypeFamily.Other)
                    {
                        throw new FlowValidationException($"{where}: type '{type}' is not a type the replica holds (text, whole and decimal numbers, float, money, dates and times, bit, binary, uniqueidentifier).");
                    }
                }
            }
        }
    }

    private static void Validate(FlowDefinition flow, string source)
    {
        if (flow.Source.Sql is { } sql)
        {
            ValidateSql(flow, sql, source);
        }

        if (flow.Source.Replica is { } replica)
        {
            ValidateReplica(replica, source);
        }

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
