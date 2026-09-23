using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Documents;

/// <summary>Maps a parsed cache document onto <see cref="CacheDefinition"/> and validates what YAML cannot.</summary>
internal static class CacheMapper
{
    public static CacheDefinition Map(CacheYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase);

        // The search service requires the tenant header on every request; without it a refresh fails on its first
        // search instead of at load.
        if (!headers.TryGetValue(FlowMapper.PartitionHeader, out var partition) || string.IsNullOrWhiteSpace(partition))
        {
            throw new FlowValidationException(
                $"{source}: source.headers must declare a non-empty '{FlowMapper.PartitionHeader}'. Every OSDU service requires it and rejects a request without it.");
        }

        // The partition names the cache the flow fills, so it has to be one a cache can be kept under.
        _ = CacheScope.Normalize(partition, $"{source}: source.headers");

        if (y.MakeCurrent is not null)
        {
            throw new FlowValidationException(
                $"{source}: makeCurrent is not a setting any more: every version a refresh writes becomes the current version of its partition's cache. Remove it.");
        }

        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
            StringComparer.Ordinal);
        var defaultMode = FlowMapper.ParseEnum(y.OnChange, CacheChangeMode.Auto, "onChange", source);
        var types = MapTypes(y.Types, defaultMode, parameters, source);

        // The platform is reached only for the types searched on it, so a flow of lookup tables alone needs no endpoint, and
        // a flow that names one it never searches is told so rather than left holding a setting that does nothing.
        var searched = types.Any(t => t.Origin == CacheOrigin.Osdu);
        var endpoint = string.IsNullOrWhiteSpace(src.Endpoint) ? null : src.Endpoint.Trim();
        if (searched && endpoint is null)
        {
            throw new FlowValidationException($"{source}: source.endpoint is required: the flow declares a type searched on OSDU, by its kind.");
        }

        if (!searched && (endpoint is not null || src.Auth is not null))
        {
            throw new FlowValidationException(
                $"{source}: source.endpoint and source.auth reach the OSDU platform, and the flow declares no type searched there; every type it declares is a lookup table. Remove them.");
        }

        // The database is reached only for the table types, checked the way a delivery flow's connection is: references
        // only, never a literal secret.
        var tables = types.Any(t => t.Origin == CacheOrigin.Table);
        var connection = string.IsNullOrWhiteSpace(src.Connection) ? null : src.Connection.Trim();
        if (tables && connection is null)
        {
            throw new FlowValidationException($"{source}: source.connection is required: the flow declares a type read from an ingestion table.");
        }

        if (!tables && connection is not null)
        {
            throw new FlowValidationException($"{source}: source.connection reaches the ingestion database, and the flow declares no type read from a table there. Remove it.");
        }

        if (connection is not null)
        {
            IngestionConnection.CheckDeclared(connection, source);
        }

        return new CacheDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Parameters = parameters,
            Source = new CacheSource
            {
                Endpoint = endpoint,
                Connection = connection,
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
            },
            Types = types,
            OnChange = defaultMode,
            Reliability = FlowMapper.MapReliability(y.Reliability, source),
        };
    }

    /// <summary>
    /// Maps the declared types. A type's name and entity type are derived from its kind when they are not spelled out, so
    /// the common case is a kind and a list of paths.
    /// </summary>
    private static List<ReferenceTypeSpec> MapTypes(
        IReadOnlyList<CachedTypeYaml>? declared, CacheChangeMode defaultMode, IReadOnlyDictionary<string, FlowParameter> parameters, string source)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: types is required: list the types the cache holds, each an OSDU kind with the paths of a record to keep, a dictionary, or an ingestion table.");
        }

        var types = new List<ReferenceTypeSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var where = $"types[{i}]";
            var type = declared[i];
            var origins = new[] { type.Kind, type.Dictionary, type.Table }.Count(v => !string.IsNullOrWhiteSpace(v));
            if (origins > 1)
            {
                throw new FlowValidationException(
                    $"{source}: {where} names more than one origin; a type has one: a kind searched on OSDU, a dictionary kept in the repository, or a table an ingestion flow loads.");
            }

            if (!string.IsNullOrWhiteSpace(type.Dictionary))
            {
                types.Add(Validated(DictionaryType(type, defaultMode, where, source), where, source));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(type.Table))
            {
                types.Add(Validated(TableType(type, defaultMode, where, source), where, source));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(type.Key))
            {
                throw new FlowValidationException($"{source}: {where} names a key, which only a table type takes: an OSDU record is kept under its id.");
            }

            var kind = string.IsNullOrWhiteSpace(type.Kind)
                ? throw new FlowValidationException(
                    $"{source}: {where} needs a kind (the OSDU kind whose records the type caches), a dictionary (a lookup table kept in the repository) or a table (an ingestion table SQLFlow's flows load).")
                : type.Kind!.Trim();

            if (!OsduKind.IsValid(kind))
            {
                throw new FlowValidationException($"{source}: {where}.kind '{kind}' is not authority:source:entityType:version (wildcards allowed per segment).");
            }

            var entityType = string.IsNullOrWhiteSpace(type.EntityType)
                ? OsduKind.EntityType(kind) ?? throw new FlowValidationException($"{source}: {where} needs an entityType, because kind '{kind}' does not name one.")
                : type.EntityType!.Trim();
            var query = string.IsNullOrWhiteSpace(type.Query) ? "*" : type.Query!.Trim();
            foreach (var token in FlowMapper.Tokens(query))
            {
                if (!parameters.ContainsKey(token))
                {
                    throw new FlowValidationException($"{source}: {where}.query uses '{{{token}}}', which is not declared under parameters.");
                }
            }

            var spec = new ReferenceTypeSpec
            {
                Name = string.IsNullOrWhiteSpace(type.Name) ? ShortNameOf(entityType) : type.Name!.Trim(),
                EntityType = entityType,
                Kind = kind,
                Query = query,
                OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
                Fields = MapFields(type.Fields, $"{where}.fields", source),
            };

            types.Add(Validated(spec, where, source));
        }

        if (types.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Count)
        {
            throw new FlowValidationException($"{source}: types declares the same type name more than once; a mapping reads a type by its name, so each needs its own.");
        }

        return types;
    }

    /// <summary>
    /// A type holding a dictionary document: named after the dictionary unless it says otherwise, kept as a lookup table, and
    /// taking none of an OSDU type's settings, because the document itself names the key and the fields.
    /// </summary>
    private static ReferenceTypeSpec DictionaryType(CachedTypeYaml type, CacheChangeMode defaultMode, string where, string source)
    {
        var dictionary = type.Dictionary!.Trim();
        foreach (var (setting, value) in new (string, object?)[] { ("kind", type.Kind), ("entityType", type.EntityType), ("query", type.Query), ("fields", type.Fields) })
        {
            if (value is not null)
            {
                throw new FlowValidationException(
                    $"{source}: {where} holds dictionary {dictionary}, which takes no '{setting}': the dictionary document names its key and its fields, and it is kept as a lookup table, not searched for.");
            }
        }

        var name = string.IsNullOrWhiteSpace(type.Name) ? dictionary : type.Name!.Trim();
        return new ReferenceTypeSpec
        {
            Name = name,
            EntityType = ReferenceType.LookupEntityType(name),
            Origin = CacheOrigin.Dictionary,
            Dictionary = dictionary,
            OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
        };
    }

    /// <summary>
    /// A type reading an ingestion table: the table, the column each row is keyed by, and the columns kept beside it, each
    /// bare or as <c>{ column: ..., as: ... }</c>. Named after the table unless it says otherwise, and kept as a lookup table.
    /// </summary>
    private static ReferenceTypeSpec TableType(CachedTypeYaml type, CacheChangeMode defaultMode, string where, string source)
    {
        var table = type.Table!.Trim();
        FlowMapper.CheckObject(table, $"{where}.table", source);
        foreach (var (setting, value) in new (string, object?)[] { ("entityType", type.EntityType), ("query", type.Query) })
        {
            if (value is not null)
            {
                throw new FlowValidationException(
                    $"{source}: {where} reads table {table}, which takes no '{setting}': it is kept as a lookup table holding every row of the table, not searched for.");
            }
        }

        var key = string.IsNullOrWhiteSpace(type.Key)
            ? throw new FlowValidationException($"{source}: {where}.key is required: the column of {table} each row is keyed by and a mapping finds the row by.")
            : type.Key!;
        FlowMapper.CheckColumn(key, $"{where}.key", source);
        var name = string.IsNullOrWhiteSpace(type.Name) ? SourceObjectName.Parse(table).Name : type.Name!.Trim();
        return new ReferenceTypeSpec
        {
            Name = name,
            EntityType = ReferenceType.LookupEntityType(name),
            Origin = CacheOrigin.Table,
            Table = table,
            Key = key,
            Fields = MapColumns(type.Fields, $"{where}.fields", table, source),
            OnChange = FlowMapper.ParseEnum(type.OnChange, defaultMode, $"{where}.onChange", source),
        };
    }

    /// <summary>A table type's columns: each a column name, kept under it, or a column with the name to keep it under.</summary>
    private static List<ReferenceFieldSpec> MapColumns(IReadOnlyList<object>? fields, string where, string table, string source)
    {
        if (fields is null || fields.Count == 0)
        {
            throw new FlowValidationException($"{source}: {where} lists no column of {table} to keep beside the key; list the columns a mapping reads.");
        }

        var mapped = new List<ReferenceFieldSpec>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            string? column = null;
            string? name = null;
            switch (fields[i])
            {
                case string bare:
                    column = bare;
                    break;
                case IDictionary<object, object?> entry:
                    foreach (var (settingKey, value) in entry)
                    {
                        switch (settingKey.ToString())
                        {
                            case "column":
                                column = value?.ToString();
                                break;
                            case "as":
                                name = value?.ToString();
                                break;
                            default:
                                throw new FlowValidationException($"{source}: {where}[{i}] has no '{settingKey}' setting; a table column takes 'column' and 'as'.");
                        }
                    }

                    break;
                default:
                    throw new FlowValidationException($"{source}: {where}[{i}] is neither a column name nor a 'column'/'as' pair.");
            }

            if (string.IsNullOrWhiteSpace(column))
            {
                throw new FlowValidationException($"{source}: {where}[{i}] needs a column.");
            }

            FlowMapper.CheckColumn(column, $"{where}[{i}]", source);
            mapped.Add(new ReferenceFieldSpec(column, string.IsNullOrWhiteSpace(name) ? column : name.Trim()));
        }

        return mapped;
    }

    private static ReferenceTypeSpec Validated(ReferenceTypeSpec spec, string where, string source)
    {
        try
        {
            spec.Validate();
        }
        catch (FlowValidationException ex)
        {
            throw new FlowValidationException($"{source}: {where}: {ex.Message}", ex);
        }

        return spec;
    }

    /// <summary>Reads a field list where an entry is either a path or a path with the name to cache it under.</summary>
    private static List<ReferenceFieldSpec> MapFields(IReadOnlyList<object>? fields, string where, string source)
    {
        if (fields is null || fields.Count == 0)
        {
            throw new FlowValidationException($"{source}: {where} lists no paths to cache.");
        }

        var mapped = new List<ReferenceFieldSpec>(fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            switch (fields[i])
            {
                case string path when !string.IsNullOrWhiteSpace(path):
                    mapped.Add(new ReferenceFieldSpec(path));
                    break;
                case IDictionary<object, object?> entry:
                    mapped.Add(MapField(entry, $"{where}[{i}]", source));
                    break;
                default:
                    throw new FlowValidationException($"{source}: {where}[{i}] is neither a path nor a 'path'/'as' pair.");
            }
        }

        return mapped;
    }

    private static ReferenceFieldSpec MapField(IDictionary<object, object?> entry, string where, string source)
    {
        string? path = null;
        string? name = null;
        foreach (var (key, value) in entry)
        {
            switch (key.ToString()?.ToLowerInvariant())
            {
                case "path":
                    path = value?.ToString();
                    break;
                case "as":
                    name = value?.ToString();
                    break;
                default:
                    throw new FlowValidationException($"{source}: {where} has no '{key}' setting; a cached field takes 'path' and 'as'.");
            }
        }

        return string.IsNullOrWhiteSpace(path)
            ? throw new FlowValidationException($"{source}: {where} needs a 'path'.")
            : new ReferenceFieldSpec(path!, name);
    }

    /// <summary>The short name a mapping uses (reference-data--UnitOfMeasure becomes UnitOfMeasure).</summary>
    private static string ShortNameOf(string entityType)
    {
        var separator = entityType.LastIndexOf("--", StringComparison.Ordinal);
        return separator >= 0 ? entityType[(separator + 2)..] : entityType;
    }
}
