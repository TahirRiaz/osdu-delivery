using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>Maps a parsed retrieval document onto <see cref="RetrievalDefinition"/> and validates what YAML cannot.</summary>
internal static partial class RetrievalMapper
{
    private static readonly HashSet<string> RunTokens = new(StringComparer.Ordinal) { "run", "date" };

    public static RetrievalDefinition Map(RetrievalYaml y, string source)
    {
        var name = FlowMapper.Require(y.Name, "name", source);
        var src = y.Source ?? throw FlowMapper.Missing("source", source);
        var target = y.Target ?? throw FlowMapper.Missing("target", source);

        var kinds = new List<string>();
        if (!string.IsNullOrWhiteSpace(src.Kind))
        {
            kinds.Add(src.Kind!.Trim());
        }

        foreach (var kind in src.Kinds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(kind))
            {
                kinds.Add(kind.Trim());
            }
        }

        if (kinds.Count == 0)
        {
            throw new FlowValidationException($"{source}: source.kind (one kind) or source.kinds (a list) is required.");
        }

        foreach (var kind in kinds)
        {
            if (!KindPattern().IsMatch(kind))
            {
                throw new FlowValidationException($"{source}: source kind '{kind}' is not authority:source:entityType:version (wildcards allowed per segment).");
            }
        }

        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)
        {
            throw new FlowValidationException($"{source}: source.kinds lists the same kind more than once.");
        }

        var flow = new RetrievalDefinition
        {
            SourcePath = source == "<inline>" ? null : source,
            Name = name,
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description!.Trim(),
            Batch = string.IsNullOrWhiteSpace(y.Batch) ? null : y.Batch!.Trim(),
            Parameters = (y.Parameters ?? []).ToDictionary(
                kv => kv.Key,
                kv => new FlowParameter { Required = kv.Value.Required, Default = kv.Value.Default, Description = kv.Value.Description },
                StringComparer.Ordinal),
            Source = new RetrievalSource
            {
                Endpoint = FlowMapper.Require(src.Endpoint, "source.endpoint", source),
                Auth = FlowMapper.MapAuth(src.Auth, source, "source.auth"),
                Headers = new Dictionary<string, string>(src.Headers ?? [], StringComparer.OrdinalIgnoreCase),
                Kinds = kinds,
                Query = string.IsNullOrWhiteSpace(src.Query) ? null : src.Query!.Trim(),
                ReturnedFields = (src.ReturnedFields ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList(),
                PageSize = src.PageSize ?? RetrievalSource.MaxPageSize,
                SearchPath = string.IsNullOrWhiteSpace(src.SearchPath) ? RetrievalSource.DefaultSearchPath : src.SearchPath!.Trim(),
                QueryPath = string.IsNullOrWhiteSpace(src.QueryPath) ? RetrievalSource.DefaultQueryPath : src.QueryPath!.Trim(),
                Incremental = MapIncremental(src.Incremental, source),
                FetchRecords = src.FetchRecords ?? false,
                RecordQueryPath = string.IsNullOrWhiteSpace(src.RecordQueryPath) ? RetrievalSource.DefaultRecordQueryPath : src.RecordQueryPath!.Trim(),
                FetchParallelism = src.FetchParallelism ?? 4,
                ProbePath = string.IsNullOrWhiteSpace(src.ProbePath) ? RetrievalSource.DefaultProbePath : src.ProbePath!.Trim(),
            },
            Target = new RetrievalTarget
            {
                Location = FlowMapper.Require(target.Location, "target.location", source),
                Format = string.IsNullOrWhiteSpace(target.Format) ? RetrievalTarget.JsonLines : target.Format!.Trim().ToLowerInvariant(),
                Compression = string.IsNullOrWhiteSpace(target.Compression) ? RetrievalTarget.NoCompression : target.Compression!.Trim().ToLowerInvariant(),
                RollRecords = target.RollRecords ?? 100_000,
                Manifest = string.IsNullOrWhiteSpace(target.Manifest) ? "manifest.json" : target.Manifest!.Trim(),
            },
            Reliability = FlowMapper.MapReliability(y.Reliability, source),
        };

        Validate(flow, source);
        return flow;
    }

    private static RetrievalIncremental? MapIncremental(RetrievalIncrementalYaml? i, string source)
    {
        if (i is null)
        {
            return null;
        }

        DateTime? since = null;
        if (!string.IsNullOrWhiteSpace(i.Since))
        {
            if (!DateTime.TryParse(i.Since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                throw new FlowValidationException($"{source}: source.incremental.since '{i.Since}' is not a date and time (use RFC 3339, such as 2026-01-01T00:00:00Z).");
            }

            since = parsed;
        }

        return new RetrievalIncremental
        {
            Field = string.IsNullOrWhiteSpace(i.Field) ? "modifyTime" : i.Field!.Trim(),
            Since = since,
            LagMinutes = i.LagMinutes ?? 5,
        };
    }

    private static void Validate(RetrievalDefinition flow, string source)
    {
        var s = flow.Source;
        if (s.PageSize is < 1 or > RetrievalSource.MaxPageSize)
        {
            throw new FlowValidationException($"{source}: source.pageSize must be between 1 and {RetrievalSource.MaxPageSize}.");
        }

        if (s.FetchParallelism is < 1 or > RetrievalSource.MaxFetchParallelism)
        {
            throw new FlowValidationException($"{source}: source.fetchParallelism must be between 1 and {RetrievalSource.MaxFetchParallelism}.");
        }

        if (s.Incremental is { LagMinutes: < 0 })
        {
            throw new FlowValidationException($"{source}: source.incremental.lagMinutes must not be negative.");
        }

        if (s.Incremental is { } incremental && incremental.Field.Any(char.IsWhiteSpace))
        {
            throw new FlowValidationException($"{source}: source.incremental.field '{incremental.Field}' must be a field path without whitespace.");
        }

        foreach (var path in new[] { ("source.searchPath", s.SearchPath), ("source.queryPath", s.QueryPath), ("source.recordQueryPath", s.RecordQueryPath), ("source.probePath", s.ProbePath) })
        {
            if (!path.Item2.StartsWith('/'))
            {
                throw new FlowValidationException($"{source}: {path.Item1} must be a path under the endpoint, starting with '/'.");
            }
        }

        var t = flow.Target;
        if (t.Format != RetrievalTarget.JsonLines)
        {
            throw new FlowValidationException($"{source}: target.format '{t.Format}' is not supported; a retrieval writes {RetrievalTarget.JsonLines}.");
        }

        if (t.Compression is not (RetrievalTarget.NoCompression or RetrievalTarget.GzipCompression))
        {
            throw new FlowValidationException($"{source}: target.compression must be {RetrievalTarget.NoCompression} or {RetrievalTarget.GzipCompression}.");
        }

        if (t.RollRecords < 1)
        {
            throw new FlowValidationException($"{source}: target.rollRecords must be at least 1.");
        }

        if (t.Manifest.Contains('/') || t.Manifest.Contains('\\'))
        {
            throw new FlowValidationException($"{source}: target.manifest is a file name inside the run's directory, not a path.");
        }

        foreach (var token in FlowMapper.Tokens(t.Location))
        {
            if (!RunTokens.Contains(token) && !flow.Parameters.ContainsKey(token))
            {
                throw new FlowValidationException($"{source}: target.location uses '{{{token}}}', which is neither a run token ({{run}}, {{date}}) nor declared under parameters.");
            }
        }

        if (flow.Reliability.Concurrency < 1)
        {
            throw new FlowValidationException($"{source}: reliability.concurrency must be at least 1.");
        }
    }

    [GeneratedRegex(@"^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$")]
    private static partial Regex KindPattern();
}
