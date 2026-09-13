using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

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

        var parameters = (y.Parameters ?? []).ToDictionary(
            kv => kv.Key,
            kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
            StringComparer.Ordinal);
        var defaultMode = FlowMapper.ParseEnum(y.OnChange, CacheChangeMode.Approve, "onChange", source);

        return new CacheDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Parameters = parameters,
            Source = new CacheSource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = headers,
            },
            Types = MapTypes(y.Types, defaultMode, parameters, source),
            OnChange = defaultMode,
            MakeCurrent = y.MakeCurrent ?? true,
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
                $"{source}: types is required: list the OSDU types the cache holds, each with its kind and the paths of a record to keep.");
        }

        var types = new List<ReferenceTypeSpec>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var where = $"types[{i}]";
            var type = declared[i];
            var kind = string.IsNullOrWhiteSpace(type.Kind)
                ? throw new FlowValidationException($"{source}: {where}.kind is required: the OSDU kind whose records the type caches.")
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

            try
            {
                spec.Validate();
            }
            catch (FlowValidationException ex)
            {
                throw new FlowValidationException($"{source}: {where}: {ex.Message}", ex);
            }

            types.Add(spec);
        }

        if (types.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != types.Count)
        {
            throw new FlowValidationException($"{source}: types declares the same type name more than once; a mapping reads a type by its name, so each needs its own.");
        }

        return types;
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
