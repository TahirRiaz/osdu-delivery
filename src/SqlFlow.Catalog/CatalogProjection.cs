using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Catalog;

/// <summary>
/// Pure projections from the git/disk source of truth into shadow entities: a pipeline from its estate fields,
/// and a run from its on-disk <c>run.json</c> envelope. No EF, no IO, no clock - everything is passed in - so the
/// mapping is unit-testable in isolation. The header fields follow the stable run.json contract; metric fields
/// are read best-effort from the kind-specific result and left null when a kind does not report them.
/// </summary>
public static class CatalogProjection
{
    /// <summary>Projects a discovered estate flow into a pipeline row (Active, stamped at <paramref name="nowUtc"/>).
    /// The YAML and definition JSON are passed already secret-redacted by the caller.</summary>
    public static CatalogPipeline Pipeline(
        Guid repoId, string name, string kind, string? batch, string relativePath,
        string? sourceServer, string? targetServer, string contentHash, string yaml, string definitionJson, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind ?? string.Empty,
            Batch = NullIfBlank(batch),
            RelativePath = relativePath ?? string.Empty,
            SourceServer = NullIfBlank(sourceServer),
            TargetServer = NullIfBlank(targetServer),
            ContentHash = contentHash ?? string.Empty,
            Yaml = yaml ?? string.Empty,
            DefinitionJson = definitionJson ?? string.Empty,
            Active = true,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
        };
    }

    /// <summary>
    /// Projects the authored (YAML) column transforms of a flow into declared pipeline-column rows: one per
    /// authored <see cref="ColumnTransform"/>, in declaration order, with the <c>@ColName</c> placeholder resolved
    /// to the source column reference so the stored expression is the exact SQL the view uses. Pure - no database,
    /// no connection - so the estate's "which transforms are set" is queryable straight from the source of truth.
    /// A flow with no authored transforms yields no rows.
    /// </summary>
    public static IReadOnlyList<CatalogPipelineColumn> PipelineColumnsDeclared(
        Guid repoId, Guid pipelineId, TypeInferencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rows = new List<CatalogPipelineColumn>(policy.Columns.Count);
        var ordinal = 0;
        foreach (var transform in policy.Columns)
        {
            ordinal++;
            var reference = $"[{transform.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
            var expression = transform.Virtual
                ? transform.Expression
                : transform.Expression is not null
                    ? ColumnTransformExpression.Substitute(transform.Expression, reference)
                    : transform.Type is not null
                        ? $"CAST({reference} AS {transform.Type})"
                        : null;

            rows.Add(new CatalogPipelineColumn
            {
                RepoId = repoId,
                PipelineId = pipelineId,
                Kind = PipelineColumnKinds.Declared,
                Ordinal = ordinal,
                ColumnName = string.IsNullOrWhiteSpace(transform.Alias) ? transform.Name : transform.Alias!,
                SourceColumn = transform.Virtual ? null : transform.Name,
                Expression = expression,
                DataType = transform.Type,
                SortOrder = transform.SortOrder,
                IsVirtual = transform.Virtual,
                ExcludeFromView = transform.ExcludeFromView,
                Converted = transform.Expression is not null || transform.Type is not null,
            });
        }

        return rows;
    }

    /// <summary>
    /// Projects a run's generated transformation view into detected pipeline-column rows, read best-effort from
    /// the run.json result (<c>result.transformView.columns</c>: the resolved projection the run refreshed the
    /// view with). One row per view column, in view order, carrying the resolved type and SELECT expression, so
    /// the catalog records exactly what the engine detected/applied, distinct from what the YAML declared. A run
    /// that generated no view yields no rows.
    /// </summary>
    public static IReadOnlyList<CatalogPipelineColumn> PipelineColumnsDetected(
        JsonElement root, Guid repoId, Guid pipelineId)
    {
        if (Prop(root, "result") is not { } result
            || Prop(result, "transformView") is not { ValueKind: JsonValueKind.Object } view
            || Prop(view, "columns") is not { ValueKind: JsonValueKind.Array } columns)
        {
            return [];
        }

        var rows = new List<CatalogPipelineColumn>();
        foreach (var column in columns.EnumerateArray())
        {
            var name = Str(column, "columnName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            rows.Add(new CatalogPipelineColumn
            {
                RepoId = repoId,
                PipelineId = pipelineId,
                Kind = PipelineColumnKinds.Detected,
                Ordinal = rows.Count + 1,
                ColumnName = name,
                SourceColumn = null,
                Expression = NullIfBlank(Str(column, "selectExpression")),
                DataType = NullIfBlank(Str(column, "dataType")),
                SortOrder = null,
                IsVirtual = false,
                ExcludeFromView = false,
                Converted = Bool(column, "converted") ?? false,
            });
        }

        return rows;
    }

    /// <summary>The lowercase hex SHA-256 of a text, for change detection of a flow document.</summary>
    public static string Hash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    /// <summary>Projects a lineage object node into a (global) catalog object row, stamped at <paramref name="nowUtc"/>.</summary>
    public static CatalogObject MapObject(LineageObjectNode node, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new CatalogObject
        {
            Key = node.Key,
            ServerRef = node.ServerRef,
            Database = NullIfBlank(node.Database),
            Schema = NullIfBlank(node.Schema),
            Name = node.Name,
            Kind = node.Kind.ToString(),
            // A captured module body (sys.sql_modules) can embed a literal credential, so it is run through the
            // same redactor as the YAML/definition JSON before it rests in the catalog (guard null: only redact
            // when a body was captured).
            Definition = node.Definition is null ? null : NullIfBlank(SecretHygiene.RedactedMessage(node.Definition)),
            // The generating DDL is redacted the same way as the module body: a hook or a generated statement
            // can embed a literal credential.
            Script = node.Script is null ? null : NullIfBlank(SecretHygiene.RedactedMessage(node.Script)),
            ScriptTier = node.ScriptTier?.ToString(),
            ScriptUpdatedUtc = node.Script is null ? null : nowUtc,
            FirstSeenUtc = nowUtc,
            LastSeenUtc = nowUtc,
        };
    }

    /// <summary>Projects a lineage edge into a repo-scoped catalog edge; <paramref name="objectName"/> is the
    /// display name of the edge's object, denormalized for listing.</summary>
    public static CatalogLineageEdge Edge(LineageEdge edge, Guid repoId, string objectName)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return new CatalogLineageEdge
        {
            RepoId = repoId,
            Flow = NullIfBlank(edge.Flow),
            PipelineId = string.IsNullOrWhiteSpace(edge.Flow) ? null : CatalogIdentity.Pipeline(repoId, edge.Flow),
            ViaModule = NullIfBlank(edge.ViaModule),
            Relation = edge.Relation.ToString(),
            ObjectKey = edge.ObjectKey,
            ObjectName = objectName ?? edge.ObjectKey,
            Tier = edge.Tier.ToString(),
        };
    }

    /// <summary>Projects a flow-level lineage dependency into a repo-scoped catalog row; <paramref name="viaObjects"/>
    /// is the mediating objects' display names, comma-joined.</summary>
    public static CatalogFlowDependency FlowDependency(LineageFlowDependency dependency, Guid repoId, string viaObjects)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        return new CatalogFlowDependency
        {
            RepoId = repoId,
            FromFlow = dependency.FromFlow,
            ToFlow = dependency.ToFlow,
            FromPipelineId = CatalogIdentity.Pipeline(repoId, dependency.FromFlow),
            ToPipelineId = CatalogIdentity.Pipeline(repoId, dependency.ToFlow),
            ViaObjects = viaObjects ?? string.Empty,
        };
    }

    /// <summary>The file rows of a run, read best-effort from the run.json result: a file flow's
    /// <c>processedFiles</c> (read inputs) and an export flow's <c>files</c> (written outputs). Both are the
    /// per-run file drill-down; export files carry no column count.</summary>
    public static IReadOnlyList<CatalogRunFile> RunFiles(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "result") is not { } result)
        {
            return [];
        }

        var list = new List<CatalogRunFile>();

        // File flows: processedFiles[{ name, path, rows, columns, sizeBytes }].
        if (Prop(result, "processedFiles") is { ValueKind: JsonValueKind.Array } processed)
        {
            foreach (var file in processed.EnumerateArray())
            {
                var name = Str(file, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new CatalogRunFile
                {
                    RunId = runId,
                    RepoId = repoId,
                    Name = name,
                    Path = NullIfBlank(Str(file, "path")),
                    Rows = Long(file, "rows") ?? 0,
                    Columns = Int(Long(file, "columns")),
                    SizeBytes = Long(file, "sizeBytes") ?? 0,
                });
            }
        }

        // Export flows: files[{ path, rows, bytes }] (no column count); the file name is the path's last segment.
        if (Prop(result, "files") is { ValueKind: JsonValueKind.Array } exported)
        {
            foreach (var file in exported.EnumerateArray())
            {
                var path = Str(file, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                list.Add(new CatalogRunFile
                {
                    RunId = runId,
                    RepoId = repoId,
                    Name = LastSegment(path),
                    Path = path,
                    Rows = Long(file, "rows") ?? 0,
                    Columns = 0,
                    SizeBytes = Long(file, "bytes") ?? 0,
                });
            }
        }

        return list;
    }

    /// <summary>Every generated SQL statement of a run, in execution order: the kind-specific <c>sqlTrace</c>
    /// (ing/exp/sp/hc, which already includes the surrogate-key statements) and a file flow's <c>ddlExecuted</c>
    /// (file flows have no sqlTrace). This is the central, queryable trace log; ordinals are assigned in
    /// projection order. Surrogate-key statements are NOT read from the surrogate-key result: they are already in
    /// the ingestion <c>sqlTrace</c>, so reading both would double them.</summary>
    public static IReadOnlyList<CatalogRunStatement> RunStatements(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "result") is not { } result)
        {
            return [];
        }

        var list = new List<CatalogRunStatement>();

        void Add(string step, string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return;
            }

            list.Add(new CatalogRunStatement
            {
                RunId = runId,
                RepoId = repoId,
                Ordinal = list.Count + 1,
                Step = Truncate(step, 128),
                Sql = sql,
            });
        }

        if (Prop(result, "sqlTrace") is { ValueKind: JsonValueKind.Array } trace)
        {
            foreach (var entry in trace.EnumerateArray())
            {
                Add(Str(entry, "step") ?? "sql", Str(entry, "sql"));
            }
        }

        if (Prop(result, "ddlExecuted") is { ValueKind: JsonValueKind.Array } ddl)
        {
            foreach (var statement in ddl.EnumerateArray())
            {
                if (statement.ValueKind == JsonValueKind.String)
                {
                    Add("schema.ddl", statement.GetString());
                }
            }
        }

        return list;
    }

    /// <summary>The surrogate-key generation outcomes of a run (ingestion flows), read best-effort from the result.</summary>
    public static IReadOnlyList<CatalogRunSurrogateKey> RunSurrogateKeys(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "result") is not { } result || Prop(result, "surrogateKeys") is not { ValueKind: JsonValueKind.Array } items)
        {
            return [];
        }

        var list = new List<CatalogRunSurrogateKey>();
        foreach (var item in items.EnumerateArray())
        {
            var table = Str(item, "surrogateTable");
            if (string.IsNullOrWhiteSpace(table))
            {
                continue;
            }

            list.Add(new CatalogRunSurrogateKey
            {
                RunId = runId,
                RepoId = repoId,
                SurrogateKeyId = Int(Long(item, "surrogateKeyId")),
                SurrogateTable = table,
                SurrogateColumn = Str(item, "surrogateColumn") ?? string.Empty,
                IsRemote = Bool(item, "isRemote") ?? false,
                KeysGenerated = Long(item, "keysGenerated") ?? 0,
                RowsStamped = Long(item, "rowsStamped") ?? 0,
                Executed = Bool(item, "executed") ?? false,
                Error = NullIfBlank(Str(item, "error")),
            });
        }

        return list;
    }

    /// <summary>The per-metric health-check summaries of a run (hc flows), read best-effort from the result.</summary>
    public static IReadOnlyList<CatalogRunHealthCheckMetric> RunHealthCheckMetrics(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "result") is not { } result || Prop(result, "metricResults") is not { ValueKind: JsonValueKind.Array } items)
        {
            return [];
        }

        var list = new List<CatalogRunHealthCheckMetric>();
        foreach (var item in items.EnumerateArray())
        {
            var name = Str(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new CatalogRunHealthCheckMetric
            {
                RunId = runId,
                RepoId = repoId,
                Name = name,
                SeriesPoints = Int(Long(item, "seriesPoints")),
                ImputedPoints = Int(Long(item, "imputedPoints")),
                ImmaturePoints = Int(Long(item, "immaturePoints")),
                Anomalies = Int(Long(item, "anomalies")),
                LevelShifts = Int(Long(item, "levelShifts")),
                ModelTrained = Bool(item, "modelTrained") ?? false,
                ModelTrainer = NullIfBlank(Str(item, "modelTrainer")),
                Error = NullIfBlank(Str(item, "error")),
            });
        }

        return list;
    }

    /// <summary>The assertion-result rows of a run, read best-effort from the run.json result.</summary>
    public static IReadOnlyList<CatalogRunAssertion> RunAssertions(JsonElement root, Guid runId, Guid repoId)
    {
        if (Prop(root, "result") is not { } result || Prop(result, "assertions") is not { ValueKind: JsonValueKind.Array } items)
        {
            return [];
        }

        var list = new List<CatalogRunAssertion>();
        foreach (var item in items.EnumerateArray())
        {
            var name = Str(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new CatalogRunAssertion
            {
                RunId = runId,
                RepoId = repoId,
                Name = name,
                Result = Str(item, "result") ?? string.Empty,
                AssertedValue = Str(item, "assertedValue") ?? string.Empty,
                Evaluated = Bool(item, "evaluated") ?? false,
                Error = NullIfBlank(Str(item, "error")),
            });
        }

        return list;
    }

    /// <summary>
    /// Projects a parsed <c>run.json</c> document into a run row attributed to <paramref name="repoId"/> (the repo
    /// whose estate the artifact was found under), or null when the envelope lacks the required header (a corrupt
    /// or foreign file). The pipeline link is the same repo-scoped identity the registry sync assigns, so the run
    /// joins to its pipeline. Property lookups are case-insensitive so a casing change in the writer never silently
    /// drops fields.
    /// </summary>
    public static CatalogRun? RunFromJson(JsonElement root, Guid repoId)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // runId must be a GUID string; a missing, non-string, or non-GUID value is a corrupt/foreign artifact.
        // (TryGetGuid throws on a non-String element, so the ValueKind guard is required, not just a null check.)
        if (Prop(root, "runId") is not { ValueKind: JsonValueKind.String } runIdElement || !runIdElement.TryGetGuid(out var runId))
        {
            return null;
        }

        var flowName = Str(root, "flowName");
        var flowKind = Str(root, "flowKind");
        if (string.IsNullOrWhiteSpace(flowName) || string.IsNullOrWhiteSpace(flowKind))
        {
            return null;
        }

        var result = Prop(root, "result");
        var success = Bool(root, "success") ?? false;

        return new CatalogRun
        {
            RunId = runId,
            PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = flowKind,
            Success = success,
            // A run read straight from its on-disk artifact is already finished, so it is born in a terminal state.
            Status = success ? RunStatuses.Succeeded : RunStatuses.Failed,
            SchemaVersion = Int(Long(root, "schemaVersion")),
            WrittenUtc = Date(root, "writtenUtc") ?? default,
            Error = NullIfBlank(Str(root, "error")),
            StartUtc = result is { } r1 ? Date(r1, "startTimeUtc") : null,
            EndUtc = result is { } r2 ? Date(r2, "endTimeUtc") : null,
            DurationSeconds = DurationSeconds(result),
            RowsLoaded = result is { } r3 ? Long(r3, "rowsLoaded") ?? Long(r3, "totalRows") : null,
            RowsInserted = result is { } r4 ? Long(r4, "rowsInserted") : null,
            RowsUpdated = result is { } r5 ? Long(r5, "rowsUpdated") : null,
            RowsDeleted = result is { } r6 ? Long(r6, "rowsDeleted") : null,
            Host = NullIfBlank(Str(root, "host")),
        };
    }

    private static double? DurationSeconds(JsonElement? result)
    {
        if (result is not { } r)
        {
            return null;
        }

        if (Long(r, "durationSeconds") is { } seconds)
        {
            return seconds;
        }

        // The file flow reports milliseconds; convert so every kind lands in one unit.
        if (Prop(r, "totalMs") is { } ms && ms.ValueKind == JsonValueKind.Number && ms.TryGetDouble(out var totalMs))
        {
            return totalMs / 1000.0;
        }

        return null;
    }

    private static JsonElement? Prop(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? Str(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;

    private static bool? Bool(JsonElement element, string name)
        => Prop(element, name) is { } p && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

    private static long? Long(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out var v) ? v : null;

    private static DateTime? Date(JsonElement element, string name)
        => Prop(element, name) is { ValueKind: JsonValueKind.String } p && p.TryGetDateTime(out var v) ? v : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A long count saturated into int range, so an oversized run.json value never silently overflows.</summary>
    private static int Int(long? value) => (int)Math.Min(value ?? 0, int.MaxValue);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>The last path segment (file name) of a forward- or back-slashed path; the whole path when it has
    /// no separator or ends in one.</summary>
    private static string LastSegment(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        var segment = i >= 0 ? path[(i + 1)..] : path;
        return string.IsNullOrEmpty(segment) ? path : segment;
    }
}
