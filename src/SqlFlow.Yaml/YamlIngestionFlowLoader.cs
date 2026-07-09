using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed relational ingestion document (flowType: ing): the flow itself plus the document-local connection
/// registry and assertion definitions it declares. The connections become an in-memory data-source store and
/// the assertions an in-memory definition store, so the flow runs through the exact same resolver and runners
/// as full mode, with no control database anywhere.
/// </summary>
public sealed record IngestionDocument
{
    public required IngestionFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on source/target).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }

    /// <summary>The document's inline data-quality assertion definitions.</summary>
    public required IReadOnlyList<AssertionDefinition> AssertionDefinitions { get; init; }

    /// <summary>The document's named invokes (the <c>invokes:</c> block), referenced by preInvoke/postInvoke.</summary>
    public IReadOnlyList<Core.Invoke.InvokeDefinition> Invokes { get; init; } = [];

    /// <summary>The document's named Azure service principals (the <c>servicePrincipals:</c> block).</summary>
    public IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; } = [];
}

/// <summary>
/// Loads a relational (table to table) ingestion flow from YAML. YamlDotNet handles the grammar; this class is
/// the mapping/validation layer that turns the parsed document into a validated <see cref="IngestionDocument"/>.
/// Defaults follow the model's documented legacy defaults; dynamic schema evolution is ON unless turned off.
/// </summary>
public sealed class YamlIngestionFlowLoader
{
    private const string SourceConnectionName = "source";
    private const string TargetConnectionName = "target";

    // The unquoted-scalar option types the invoke parameters block (a plain true/42 stays a boolean/number,
    // a quoted scalar a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public IngestionDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public IngestionDocument Parse(string yaml, string source = "<inline>")
    {
        IngestionYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<IngestionYaml>(yaml);
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

    private static IngestionDocument Map(IngestionYaml y, string source)
    {
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        var sourceServer = ResolveEndpointConnection(sourceYaml, "source", SourceConnectionName, connections, source);
        var targetServer = ResolveEndpointConnection(targetYaml, "target", TargetConnectionName, connections, source);

        // The target of an ingestion flow is SQL Server by design (staging, schema evolution, and the upsert
        // are T-SQL); a foreign target is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, targetServer, "target", "an ingestion flow's target", source);

        var (assertionNames, assertionDefinitions) = MapAssertions(y.Assertions, source);
        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);
        var invokes = YamlInvokeParts.MapInvokes(y.Invokes, servicePrincipals, source);
        var name = string.IsNullOrWhiteSpace(y.Name) ? null : y.Name.Trim();

        var load = MapLoad(y.Load);
        var matchKeys = MapMatchKeys(y.MatchKeys, source);
        var systemColumns = MapSystemColumns(y.SystemColumns);
        var versioning = MapVersioning(y.Versioning, source);

        // These are accepted by the schema (and the control-DB / legacy loaders) for fidelity but are not yet
        // implemented by the engine, so a flow that sets one is rejected at validation rather than silently doing
        // nothing. (The engine also guards them at run time for the non-YAML load paths.)
        if (versioning.TemporalHistory)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporalHistory' (SQL Server system-versioned history) is not yet implemented. " +
                "Use 'versioning.scd2' for engine-managed dimension history.");
        }

        if (versioning.InsertUnknownDimensionRow)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.insertUnknownDimensionRow' is not yet implemented; seed the unknown-member row explicitly for now.");
        }

        if (versioning.Scd2.Enabled)
        {
            // SCD2 maintains a version per business key, so it needs the keys; it appends new versions, so a
            // truncate would erase the history.
            if (load.KeyColumns.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: 'versioning.scd2' requires 'load.keyColumns' (the business key the dimension versions by).");
            }

            if (targetYaml.TruncateBeforeLoad ?? false)
            {
                throw new FlowValidationException(
                    $"{source}: 'versioning.scd2' cannot be combined with 'target.truncateBeforeLoad'; truncating would erase the dimension history.");
            }
        }

        // Per-file replace (load.reloadColumn) is a purge-then-insert scoped to the incoming batch. It supersedes
        // the keyed upsert, so it cannot be layered on the mechanisms that assume that upsert: the ordered
        // dataset-upsert loop, SCD2 history, the match-key delete pass, or a full-reload truncate (which already
        // wipes everything, making a per-file purge meaningless). Reject the combinations loudly at parse time.
        if (!string.IsNullOrWhiteSpace(load.ReloadColumn))
        {
            if (!string.IsNullOrWhiteSpace(load.DataSetColumn))
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' (per-file replace) cannot be combined with 'load.dataSetColumn' (the ordered dataset-upsert loop); use one or the other.");
            }

            if (versioning.Scd2.Enabled)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'versioning.scd2'; a per-file purge would erase the dimension history.");
            }

            if (load.MatchKeysInSourceAndTarget)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'load.matchKeysInSourceAndTarget'; both delete target rows and their scopes conflict (per-file vs. whole-source key reconciliation).");
            }

            if (targetYaml.TruncateBeforeLoad ?? false)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'target.truncateBeforeLoad'; truncating already replaces the whole target, so a per-file purge is redundant.");
            }
        }

        // Tag mode soft-deletes by stamping DeletedDate_DW; the column must be on the target. Auto-enable it
        // here (same as the legacy mapper does) so the user does not need to set both flags independently.
        if (load.MatchKeysInSourceAndTarget && matchKeys.Action == MatchKeyAction.Tag)
        {
            systemColumns = systemColumns with { DeletedDate = true };
        }

        var flow = new IngestionFlow
        {
            FlowId = name is null ? 0 : StableFlowId(name),
            SysAlias = name,
            Batch = NullIfBlank(y.Batch),
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description,
            Source = new IngestionSource
            {
                Server = sourceServer,
                Table = ParseObject(sourceYaml, "source", source),
                Filter = NullIfBlank(sourceYaml.Filter),
                FilterIsAppend = sourceYaml.FilterIsAppend ?? true,
                IncrementalClause = NullIfBlank(sourceYaml.IncrementalClause),
                IgnoreColumns = sourceYaml.IgnoreColumns ?? [],
                DataSetColumn = NullIfBlank(sourceYaml.DataSetColumn),
            },
            Target = new IngestionTarget
            {
                Server = targetServer,
                Table = ParseObject(targetYaml, "target", source),
                TruncateBeforeLoad = targetYaml.TruncateBeforeLoad ?? false,
                ColumnStoreIndex = targetYaml.ColumnStoreIndex ?? false,
                IdentityColumn = NullIfBlank(targetYaml.IdentityColumn),
                DesiredIndexes = NullIfBlank(targetYaml.DesiredIndexes),
            },
            Load = load,
            MatchKeys = matchKeys,
            Change = MapChange(y.Change),
            SystemColumns = systemColumns,
            SchemaSync = MapSchema(y.Schema),
            Incremental = MapIncremental(y.Incremental),
            InitLoad = MapInitLoad(y.InitLoad, source),
            Versioning = versioning,
            Process = new ProcessPolicy
            {
                PreProcessOnTarget = NullIfBlank(y.PreProcess),
                PostProcessOnTarget = NullIfBlank(y.PostProcess),
                PreInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PreInvoke, "preInvoke", invokes, source),
                PostInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PostInvoke, "postInvoke", invokes, source),
            },
            VirtualColumns = MapVirtualColumns(y.VirtualColumns, source),
            SurrogateKeys = MapSurrogateKeys(y.SurrogateKeys, connections, source),
            Assertions = assertionNames,
            Transform = YamlFlowLoader.MapInference(y.Transform, source),
        };

        return new IngestionDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
            AssertionDefinitions = assertionDefinitions,
            Invokes = invokes.Values.ToList(),
            ServicePrincipals = servicePrincipals.Values.ToList(),
        };
    }

    /// <summary>Endpoint resolution over the shared parts, with this document's endpoint DTO.</summary>
    private static string ResolveEndpointConnection(
        IngestionEndpointYaml endpoint, string section, string syntheticName,
        Dictionary<string, DataSource> connections, string source)
        => YamlDocumentParts.ResolveEndpointConnection(
            endpoint.Server, endpoint.Connection, endpoint.Provider, section, syntheticName, connections, source);

    private static RelationalObject ParseObject(IngestionEndpointYaml endpoint, string section, string source)
    {
        var raw = NullIfBlank(endpoint.Object) ?? NullIfBlank(endpoint.Table)
            ?? throw new FlowValidationException(
                $"{source}: '{section}.object' is required (a three-part name like Database.Schema.Table, or " +
                "Database.Table for MySQL, where the database is the schema).");

        // A two-part name (no brackets) means database.table: natural for MySQL, where the schema part IS the
        // database; the parts double up so the three-part model stays uniform.
        if (!raw.Contains('[', StringComparison.Ordinal))
        {
            var parts = raw.Split('.', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts.All(p => p.Length > 0))
            {
                raw = $"{parts[0]}.{parts[0]}.{parts[1]}";
            }
        }

        return YamlDocumentParts.ParseQualifiedObject(raw, $"{section}.object", source);
    }

    private static IngestionLoadPolicy MapLoad(IngestionLoadYaml? y)
    {
        if (y is null)
        {
            return new IngestionLoadPolicy();
        }

        return new IngestionLoadPolicy
        {
            KeyColumns = y.KeyColumns ?? [],
            SkipUpdateExisting = y.SkipUpdateExisting ?? false,
            SkipInsertNew = y.SkipInsertNew ?? false,
            MatchKeysInSourceAndTarget = y.MatchKeysInSourceAndTarget ?? false,
            BatchUpsertToAvoidLockEscalation = y.BatchUpsert ?? false,
            BatchUpsertRowCount = y.BatchUpsertRowCount ?? 2000,
            DataSetColumn = string.IsNullOrWhiteSpace(y.DataSetColumn) ? null : y.DataSetColumn.Trim(),
            ReloadColumn = string.IsNullOrWhiteSpace(y.ReloadColumn) ? null : y.ReloadColumn.Trim(),
            StreamData = y.StreamData ?? true,
            Threads = y.Threads is > 0 ? y.Threads : null,
            KeepStagingTable = y.KeepStagingTable ?? false,
            TruncatePreTableOnCompletion = y.TruncateStagingOnCompletion ?? false,
            TruncateSourceWhenConsolidated = y.TruncateSourceWhenConsolidated ?? false,
        };
    }

    private static MatchKeyPolicy MapMatchKeys(IngestionMatchKeysYaml? y, string source)
    {
        if (y is null)
        {
            return new MatchKeyPolicy();
        }

        var action = NullIfBlank(y.Action)?.Trim().ToUpperInvariant() switch
        {
            null or "TAG" => MatchKeyAction.Tag,
            "DELETE" => MatchKeyAction.Delete,
            _ => throw new FlowValidationException(
                $"{source}: 'matchKeys.action' must be 'tag' or 'delete', got '{y.Action}'."),
        };

        var thresholdPercent = y.ThresholdPercent ?? 20;
        if (thresholdPercent is < 0 or > 100)
        {
            throw new FlowValidationException(
                $"{source}: 'matchKeys.thresholdPercent' must be 0 to 100, got {thresholdPercent}.");
        }

        if (y.IgnoreDeletedRowsAfterMonths is not null && string.IsNullOrWhiteSpace(y.DateColumn))
        {
            throw new FlowValidationException(
                $"{source}: 'matchKeys.ignoreDeletedRowsAfterMonths' requires 'matchKeys.dateColumn'.");
        }

        return new MatchKeyPolicy
        {
            Action = action,
            ActionThresholdPercent = thresholdPercent,
            IgnoreDeletedRowsAfterMonths = y.IgnoreDeletedRowsAfterMonths,
            DateColumn = NullIfBlank(y.DateColumn),
            KeyColumns = y.KeyColumns ?? [],
            SourceFilter = NullIfBlank(y.SourceFilter),
            TargetFilter = NullIfBlank(y.TargetFilter),
        };
    }

    private static ChangePolicy MapChange(IngestionChangeYaml? y)
    {
        if (y is null)
        {
            return new ChangePolicy();
        }

        return new ChangePolicy
        {
            HashColumns = y.HashColumns ?? [],
            HashType = NullIfBlank(y.HashType),
            IgnoreColumnsInHash = y.IgnoreColumnsInHash ?? [],
        };
    }

    private static SystemColumnsPolicy MapSystemColumns(IngestionSystemColumnsYaml? y)
    {
        if (y is null)
        {
            return new SystemColumnsPolicy();
        }

        return new SystemColumnsPolicy
        {
            InsertedDate = y.InsertedDate ?? true,
            UpdatedDate = y.UpdatedDate ?? true,
            DeletedDate = y.DeletedDate ?? false,
            RowStatus = y.RowStatus ?? false,
        };
    }

    private static SchemaSyncPolicy MapSchema(IngestionSchemaYaml? y)
    {
        if (y is null)
        {
            return new SchemaSyncPolicy();
        }

        return new SchemaSyncPolicy
        {
            Sync = y.Sync ?? true,
            CleanColumnNames = y.CleanColumnNames ?? false,
            CleanColumnNameRegex = NullIfBlank(y.CleanColumnNameRegex),
            ReplaceInvalidCharsWith = NullIfBlank(y.ReplaceInvalidCharsWith),
            ConvertUnicodeToNonUnicode = y.ConvertUnicodeToNonUnicode ?? false,
            AllowTableRewrite = y.AllowTableRewrite ?? false,
        };
    }

    private static IncrementalPolicy MapIncremental(IngestionIncrementalYaml? y)
    {
        if (y is null)
        {
            return new IncrementalPolicy();
        }

        return new IncrementalPolicy
        {
            Columns = y.Columns ?? [],
            DateColumn = NullIfBlank(y.DateColumn),
            OverlapDays = y.OverlapDays ?? 7,
            FullLoad = y.FullLoad ?? false,
            FetchMinValuesFromSource = y.FetchMinValuesFromSource ?? false,
        };
    }

    private static InitLoadPolicy MapInitLoad(IngestionInitLoadYaml? y, string source)
    {
        if (y is null)
        {
            return new InitLoadPolicy();
        }

        return new InitLoadPolicy
        {
            Enabled = y.Enabled ?? false,
            FromDate = ParseDate(y.FromDate, "initLoad.fromDate", source),
            ToDate = ParseDate(y.ToDate, "initLoad.toDate", source),
            BatchBy = NullIfBlank(y.BatchBy),
            BatchSize = y.BatchSize,
            KeyColumn = NullIfBlank(y.KeyColumn),
            KeyMaxValue = y.KeyMaxValue,
        };
    }

    private static VersioningPolicy MapVersioning(IngestionVersioningYaml? y, string source)
    {
        if (y is null)
        {
            return new VersioningPolicy();
        }

        return new VersioningPolicy
        {
            TemporalHistory = y.TemporalHistory ?? false,
            InsertUnknownDimensionRow = y.InsertUnknownDimensionRow ?? false,
            TokenVersioning = y.TokenVersioning ?? false,
            TokenRetentionDays = y.TokenRetentionDays,
            Scd2 = MapScd2(y.Scd2, source),
        };
    }

    private static Scd2Policy MapScd2(IngestionScd2Yaml? y, string source)
    {
        if (y is null)
        {
            return new Scd2Policy();
        }

        var validFrom = NullIfBlank(y.ValidFromColumn) ?? "ValidFrom_DW";
        var validTo = NullIfBlank(y.ValidToColumn) ?? "ValidTo_DW";
        var current = NullIfBlank(y.CurrentFlagColumn) ?? "IsCurrent_DW";

        // The three period columns must be distinct, or the engine would write one over another.
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { validFrom, validTo, current };
        if (distinct.Count != 3)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.scd2' validFromColumn, validToColumn, and currentFlagColumn must be three distinct names " +
                $"(got '{validFrom}', '{validTo}', '{current}').");
        }

        return new Scd2Policy
        {
            Enabled = y.Enabled ?? false,
            ValidFromColumn = validFrom,
            ValidToColumn = validTo,
            CurrentFlagColumn = current,
            TrackedColumns = y.TrackedColumns?.Select(NullIfBlank).Where(c => c is not null).Select(c => c!).ToList() ?? [],
        };
    }

    private static IReadOnlyList<VirtualColumn> MapVirtualColumns(List<IngestionVirtualColumnYaml>? items, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var columns = new List<VirtualColumn>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var expression = NullIfBlank(item.Expression)
                ?? throw new FlowValidationException($"{source}: 'virtualColumns[{i}].expression' is required.");
            columns.Add(new VirtualColumn
            {
                Name = NullIfBlank(item.Name),
                DataType = NullIfBlank(item.DataType),
                DataTypeExpression = NullIfBlank(item.DataTypeExpression),
                SelectExpression = expression,
            });
        }

        return columns;
    }

    private static (IReadOnlyList<string> Names, IReadOnlyList<AssertionDefinition> Definitions) MapAssertions(
        List<IngestionAssertionYaml>? items, string source)
    {
        if (items is null || items.Count == 0)
        {
            return ([], []);
        }

        var names = new List<string>(items.Count);
        var definitions = new List<AssertionDefinition>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = NullIfBlank(item.Name)
                ?? throw new FlowValidationException($"{source}: 'assertions[{i}].name' is required.");
            var expression = NullIfBlank(item.Expression)
                ?? throw new FlowValidationException($"{source}: 'assertions[{i}].expression' is required.");
            if (!seen.Add(name))
            {
                throw new FlowValidationException($"{source}: assertion '{name}' is declared more than once.");
            }

            names.Add(name);
            definitions.Add(new AssertionDefinition
            {
                Name = name,
                Expression = expression,
                Mode = YamlDocumentParts.ParseExecutionMode(item.Mode, $"assertions[{i}].mode", source),
            });
        }

        return (names, definitions);
    }

    private static IReadOnlyList<SurrogateKeySpec> MapSurrogateKeys(
        List<IngestionSurrogateKeyYaml>? items, Dictionary<string, DataSource> connections, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var specs = new List<SurrogateKeySpec>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var table = NullIfBlank(item.Table)
                ?? throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].table' is required.");
            var column = NullIfBlank(item.Column)
                ?? throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].column' is required.");
            if (item.KeyColumns is null || item.KeyColumns.Count == 0)
            {
                throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].keyColumns' is required.");
            }

            var server = NullIfBlank(item.Server);
            if (server is not null && !connections.ContainsKey(server))
            {
                throw new FlowValidationException(
                    $"{source}: 'surrogateKeys[{i}].server' references '{server}', which is not declared under 'connections:'. " +
                    "Leave it unset to generate keys on the target connection.");
            }

            var surrogateTable = YamlDocumentParts.ParseQualifiedObject(table, $"surrogateKeys[{i}].table", source);

            specs.Add(new SurrogateKeySpec
            {
                Server = server,
                SurrogateTable = surrogateTable,
                SurrogateColumn = column,
                KeyColumns = item.KeyColumns,
                SKeyColumns = item.SKeyColumns ?? [],
                PreProcess = NullIfBlank(item.PreProcess),
                PostProcess = NullIfBlank(item.PostProcess),
            });
        }

        return specs;
    }

    private static DateOnly? ParseDate(string? value, string field, string source)
        => YamlDocumentParts.ParseDate(value, field, source);

    private static int StableFlowId(string name) => YamlDocumentParts.StableFlowId(name);

    private static string? NullIfBlank(string? value) => YamlDocumentParts.NullIfBlank(value);
}
