using System.Collections.Specialized;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.SourceControl;

namespace SqlFlow.SourceControl;

/// <summary>
/// Scripts a SQL Server database's objects with SMO, the most faithful generator available: every supported
/// category (<see cref="SourceControlObjectTypes"/>) is enumerated and each object emitted as a CREATE script
/// into the legacy per-type folder layout (<c>&lt;database&gt;/&lt;category&gt;/&lt;schema&gt;.&lt;name&gt;.sql</c>).
/// Output is made deterministic (objects sorted, line endings normalized) so an unchanged database re-scripts to
/// byte-identical files and only real changes surface as git diffs. Schemas the flow excludes (the engine's own
/// staging schema by default) are skipped whole, so a snapshot tracks the database's definition and not the work
/// tables a run happened to be holding. SMO runs sequentially against one server connection, so the
/// thread-safety concern that keeps SMO out of the parallel lineage harvester does not apply.
/// </summary>
public sealed class SmoDatabaseScripter : IDatabaseScripter
{
    private readonly ScriptingOptions _schemaOptions;
    private readonly ScriptingOptions _dataOptions;

    public SmoDatabaseScripter()
    {
        // The legacy SmoHelper.SmoScriptingOptions set: full DRI, indexes, triggers, and extended properties, no
        // drops, no data, no permissions/owner/statistics noise. ScriptSchema on; headers off for clean diffs.
        _schemaOptions = new ScriptingOptions
        {
            AllowSystemObjects = false,
            AnsiPadding = false,
            AppendToFile = false,
            IncludeIfNotExists = false,
            ContinueScriptingOnError = false,
            ConvertUserDefinedDataTypesToBaseType = false,
            WithDependencies = false,
            IncludeHeaders = false,
            DriIncludeSystemNames = false,
            Bindings = false,
            NoCollation = false,
            Default = true,
            ScriptDrops = false,
            ExtendedProperties = true,
            LoginSid = false,
            Permissions = false,
            ScriptOwner = false,
            Statistics = false,
            ScriptSchema = true,
            ScriptData = false,
            ChangeTracking = false,
            ScriptDataCompression = false,
            DriAll = true,
            FullTextIndexes = true,
            Indexes = true,
            Triggers = true,
            SchemaQualify = true,
            NoCommandTerminator = true,
        };

        // Data-only: rows as INSERTs, no schema. Used for the opt-in reference/seed tables.
        _dataOptions = new ScriptingOptions
        {
            ScriptSchema = false,
            ScriptData = true,
            NoCommandTerminator = true,
            AllowSystemObjects = false,
        };
    }

    /// <summary>
    /// Scripts the database reached by <paramref name="connectionString"/> (its default catalog unless
    /// <paramref name="database"/> overrides it), honoring the include/exclude category filter and the
    /// data-table list. Returns every scripted object plus any warnings; throws only on a connection or
    /// missing-database error, since those make a snapshot meaningless.
    /// </summary>
    public ScriptedDatabase Script(
        string connectionString, string? database, SourceControlScripting scripting,
        Action<ScriptProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scripting);

        var sqlConnection = new SqlConnection(connectionString);
        var serverConnection = new ServerConnection(sqlConnection);
        try
        {
            var server = new Server(serverConnection);

            // Bulk-load object properties so enumeration does not round-trip per object (the legacy prefetch).
            server.SetDefaultInitFields(true);

            var databaseName = !string.IsNullOrWhiteSpace(database)
                ? database!.Trim()
                : NullIfBlank(new SqlConnectionStringBuilder(connectionString).InitialCatalog)
                  ?? NullIfBlank(serverConnection.DatabaseName)
                  ?? throw new SqlFlowException(
                      "The source-control connection has no default database; set 'source.database' to the database to script.");

            var db = server.Databases[databaseName]
                ?? throw new SqlFlowException(
                    $"Database '{databaseName}' was not found on the server, or the login cannot access it.");

            var categories = SelectedCategories(scripting);
            var excludedSchemas = new HashSet<string>(
                scripting.ExcludeSchemas.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var dataTables = new HashSet<string>(scripting.DataTables, StringComparer.OrdinalIgnoreCase);

            var objects = new List<ScriptedObject>();
            var warnings = new List<string>();
            var scripter = new Scripter(server) { Options = _schemaOptions };

            foreach (var (folder, select) in categories)
            {
                ct.ThrowIfCancellationRequested();
                ScriptCategory(
                    folder, select(db), excludedSchemas, scripter, databaseName, objects, warnings,
                    () => serverConnection.IsOpen, progress, ct);
            }

            if (dataTables.Count > 0)
            {
                ScriptData(db, dataTables, excludedSchemas, server, databaseName, objects, warnings, progress);
            }

            // A stable, total order so the manifest and any diff of it are deterministic.
            objects.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            return new ScriptedDatabase { DatabaseName = databaseName, Objects = objects, Warnings = warnings };
        }
        finally
        {
            if (serverConnection.IsOpen)
            {
                serverConnection.Disconnect();
            }

            sqlConnection.Dispose();
        }
    }

    /// <summary>How often a large category reports mid-walk. Every object would be thousands of events for one
    /// database; a round number keeps the trace readable while still moving visibly.</summary>
    private const int ProgressEvery = 100;

    private static void ScriptCategory(
        string folder, IEnumerable<NamedSmoObject> source, HashSet<string> excludedSchemas, Scripter scripter,
        string databaseName, List<ScriptedObject> objects, List<string> warnings, Func<bool> connectionIsOpen,
        Action<ScriptProgress>? progress, CancellationToken ct)
    {
        // Materialize and order before scripting so the file set is identical run to run.
        var ordered = source
            .Where(o => !IsInExcludedSchema(o, excludedSchemas))
            .OrderBy(o => o is ScriptSchemaObjectBase s ? s.Schema : string.Empty, StringComparer.Ordinal)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
        {
            return;
        }

        var done = 0;
        foreach (var obj in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var schema = obj is ScriptSchemaObjectBase ssob ? ssob.Schema : null;
            try
            {
                var statements = scripter.Script([obj]);
                var sql = JoinBatches(statements);
                if (sql.Length == 0)
                {
                    continue;
                }

                objects.Add(BuildObject(folder, schema, obj.Name, databaseName, sql));
            }
            catch (Exception ex) when (ex is SmoException or SqlException or ExecutionFailureException)
            {
                // One object that cannot be scripted is a warning, not a failed snapshot, and the commonest cause
                // is an object enumerated and then dropped by whoever owns it, which SMO reports as "Invalid
                // object name". A lost connection raises the same exception for every remaining object, though,
                // and scripting nothing would commit the whole database as deleted, so the connection is checked
                // before the walk is allowed to continue.
                if (!connectionIsOpen())
                {
                    throw new SqlFlowException(
                        $"The connection to the scripted database was lost while scripting {folder} {Label(schema, obj.Name)}; " +
                        "the snapshot is abandoned rather than committed with every object it never read.",
                        ex);
                }

                var warning = $"{folder} {Label(schema, obj.Name)}: not scripted ({ex.Message}).";
                warnings.Add(warning);
                progress?.Invoke(new ScriptProgress { Category = folder, Scripted = done, Total = ordered.Count, Message = warning });
            }
            finally
            {
                done++;
                if (done % ProgressEvery == 0 || done == ordered.Count)
                {
                    progress?.Invoke(new ScriptProgress { Category = folder, Scripted = done, Total = ordered.Count });
                }
            }
        }
    }

    private void ScriptData(
        Database db, HashSet<string> dataTables, HashSet<string> excludedSchemas, Server server, string databaseName,
        List<ScriptedObject> objects, List<string> warnings, Action<ScriptProgress>? progress)
    {
        var dataScripter = new Scripter(server) { Options = _dataOptions };
        var scriptedTables = 0;
        foreach (Table table in db.Tables.Cast<Table>().Where(t => !t.IsSystemObject))
        {
            var key = $"{table.Schema}.{table.Name}";
            if (!dataTables.Contains(key))
            {
                continue;
            }

            // An excluded schema is excluded outright: naming one of its tables under scripting.data would
            // otherwise version the rows of a table whose definition the same run just refused to script.
            if (excludedSchemas.Contains(table.Schema))
            {
                warnings.Add($"scripting.data names '{key}', which is in the excluded schema '{table.Schema}'; skipped.");
                continue;
            }

            try
            {
                // SELECT * has no inherent order, so sort the emitted INSERTs for a deterministic snapshot;
                // for pure INSERTs row order is immaterial to the result, only to the diff.
                var inserts = dataScripter.EnumScript(new[] { table.Urn })
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();

                var sql = WrapData(table, inserts);
                objects.Add(BuildObject(SourceControlObjectTypes.DataFolder, table.Schema, table.Name, databaseName, sql));
                scriptedTables++;
                progress?.Invoke(new ScriptProgress
                {
                    Category = SourceControlObjectTypes.DataFolder,
                    Scripted = scriptedTables,
                    Total = dataTables.Count,
                });
            }
            catch (Exception ex) when (ex is SmoException or SqlException)
            {
                var warning = $"Data {Label(table.Schema, table.Name)}: not scripted ({ex.Message}).";
                warnings.Add(warning);
                progress?.Invoke(new ScriptProgress
                {
                    Category = SourceControlObjectTypes.DataFolder,
                    Scripted = scriptedTables,
                    Total = dataTables.Count,
                    Message = warning,
                });
            }
        }

        foreach (var missing in dataTables.Where(t => !db.Tables.Cast<Table>().Any(tbl => string.Equals($"{tbl.Schema}.{tbl.Name}", t, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"scripting.data names '{missing}', which is not a user table in {databaseName}; skipped.");
        }
    }

    /// <summary>Wraps the row inserts in SET IDENTITY_INSERT when the table has an identity column, so the
    /// scripted data restores faithfully.</summary>
    private static string WrapData(Table table, IReadOnlyList<string> inserts)
    {
        var body = string.Join("\n", inserts);
        var hasIdentity = table.Columns.Cast<Column>().Any(c => c.Identity);
        if (!hasIdentity)
        {
            return Normalize(body);
        }

        var qualified = $"[{table.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
        return Normalize(
            $"SET IDENTITY_INSERT {qualified} ON;\n{body}\nSET IDENTITY_INSERT {qualified} OFF;");
    }

    private static ScriptedObject BuildObject(string folder, string? schema, string name, string databaseName, string sql)
    {
        var baseName = schema is null ? name : $"{schema}.{name}";
        var fileName = RunHistoryWriter.SafeName(baseName) + ".sql";
        var relativePath = $"{databaseName}/{folder}/{fileName}";
        return new ScriptedObject
        {
            Folder = folder,
            Schema = schema,
            Name = name,
            RelativePath = relativePath,
            Sql = sql,
        };
    }

    /// <summary>The selected categories in canonical order, after applying include (allowlist) then exclude.</summary>
    private static IReadOnlyList<(string Folder, Func<Database, IEnumerable<NamedSmoObject>> Select)> SelectedCategories(SourceControlScripting scripting)
    {
        var include = new HashSet<string>(scripting.IncludeTypes, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(scripting.ExcludeTypes, StringComparer.OrdinalIgnoreCase);

        bool Wanted(string folder)
            => (include.Count == 0 || include.Contains(folder)) && !exclude.Contains(folder);

        return Categories.Where(c => Wanted(c.Folder)).ToList();
    }

    /// <summary>True when the object belongs to a schema the flow excludes. A schema object is judged by its own
    /// name, so excluding a schema also keeps its CREATE SCHEMA script out of the snapshot; every other category
    /// is judged by the schema it is qualified with. An object that belongs to no schema (a database DDL trigger)
    /// is never excluded this way.</summary>
    private static bool IsInExcludedSchema(NamedSmoObject obj, HashSet<string> excludedSchemas)
    {
        if (excludedSchemas.Count == 0)
        {
            return false;
        }

        return obj switch
        {
            Schema schema => excludedSchemas.Contains(schema.Name),
            ScriptSchemaObjectBase qualified => excludedSchemas.Contains(qualified.Schema),
            _ => false,
        };
    }

    private static readonly (string Folder, Func<Database, IEnumerable<NamedSmoObject>> Select)[] Categories =
    [
        ("Schema", db => db.Schemas.Cast<Schema>().Where(s => !IsSystemSchema(s.Name)).Cast<NamedSmoObject>()),
        ("UserDefinedDataType", db => db.UserDefinedDataTypes.Cast<NamedSmoObject>()),
        ("UserDefinedType", db => db.UserDefinedTypes.Cast<NamedSmoObject>()),
        ("XmlSchemaCollection", db => db.XmlSchemaCollections.Cast<NamedSmoObject>()),
        ("Sequence", db => db.Sequences.Cast<NamedSmoObject>()),
        ("PartitionFunction", db => db.PartitionFunctions.Cast<NamedSmoObject>()),
        ("PartitionScheme", db => db.PartitionSchemes.Cast<NamedSmoObject>()),
        ("Table", db => db.Tables.Cast<Table>().Where(t => !t.IsSystemObject).Cast<NamedSmoObject>()),
        ("View", db => db.Views.Cast<View>().Where(v => !v.IsSystemObject).Cast<NamedSmoObject>()),
        ("StoredProcedure", db => db.StoredProcedures.Cast<StoredProcedure>().Where(p => !p.IsSystemObject).Cast<NamedSmoObject>()),
        ("UserDefinedFunction", db => db.UserDefinedFunctions.Cast<UserDefinedFunction>().Where(f => !f.IsSystemObject).Cast<NamedSmoObject>()),
        ("UserDefinedAggregate", db => db.UserDefinedAggregates.Cast<NamedSmoObject>()),
        ("UserDefinedTableType", db => db.UserDefinedTableTypes.Cast<NamedSmoObject>()),
        ("Synonym", db => db.Synonyms.Cast<NamedSmoObject>()),
        ("Rule", db => db.Rules.Cast<NamedSmoObject>()),
        ("Default", db => db.Defaults.Cast<NamedSmoObject>()),
        ("DatabaseDdlTrigger", db => db.Triggers.Cast<NamedSmoObject>()),
        ("FullTextCatalog", db => db.FullTextCatalogs.Cast<NamedSmoObject>()),
        ("SecurityPolicy", db => db.SecurityPolicies.Cast<NamedSmoObject>()),
    ];

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo", "sys", "INFORMATION_SCHEMA", "guest",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
    };

    private static bool IsSystemSchema(string name) => SystemSchemas.Contains(name);

    private static string JoinBatches(StringCollection statements)
    {
        if (statements.Count == 0)
        {
            return string.Empty;
        }

        var batches = statements.Cast<string>()
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => $"{s}\nGO");
        return Normalize(string.Join("\n", batches));
    }

    /// <summary>Normalizes line endings to LF and guarantees exactly one trailing newline, so re-scripting an
    /// unchanged object yields byte-identical content regardless of the server's line-ending conventions.</summary>
    private static string Normalize(string text)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return lf.TrimEnd('\n') + "\n";
    }

    private static string Label(string? schema, string name)
        => schema is null ? name : $"{schema}.{name}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
