using System.IO.Enumeration;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The AWS S3 transport: lists objects under a prefix, keeps those matching the glob and the modified-within window,
/// and lands each object's bytes verbatim. Options (in <c>source.options</c>): <c>bucket</c> (or the <c>s3://bucket</c>
/// base url), <c>prefix</c>, <c>region</c>, <c>accessKey</c> and <c>secretKey</c> (secret references), <c>pattern</c>
/// (glob, default <c>*</c>), <c>modifiedWithinDays</c>, <c>take</c> (most-recent N).
/// </summary>
public sealed class S3Transport : IAcquireTransport
{
    private readonly TimeProvider _time;

    public S3Transport(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.S3;

    public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var options = fetch.Source.Options;
        var bucket = BucketFrom(fetch.Source, options);
        var prefix = options.GetString("prefix", string.Empty);
        var pattern = options.GetString("pattern", "*");
        var take = options.GetInt("take", 0);
        var region = options.GetString("region", "eu-west-1");
        var modifiedWithinDays = options.GetInt("modifiedWithinDays", 0);
        var cutoff = modifiedWithinDays > 0 ? _time.GetUtcNow().AddDays(-modifiedWithinDays) : (DateTimeOffset?)null;

        var accessKey = await ResolveRequired(fetch, options, "accessKey", ct).ConfigureAwait(false);
        var secretKey = await ResolveRequired(fetch, options, "secretKey", ct).ConfigureAwait(false);

        using var client = new AmazonS3Client(accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        var matched = new List<S3Object>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
                ContinuationToken = continuationToken,
            }, ct).ConfigureAwait(false);

            foreach (var obj in response.S3Objects)
            {
                var name = LastSegment(obj.Key);
                if (name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
                {
                    continue;
                }

                if (cutoff is { } c && obj.LastModified.ToUniversalTime() < c)
                {
                    continue;
                }

                matched.Add(obj);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        IEnumerable<S3Object> ordered = take > 0
            ? matched.OrderByDescending(o => o.LastModified).Take(take)
            : matched.OrderBy(o => o.Key, StringComparer.Ordinal);

        foreach (var obj in ordered)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await client.GetObjectAsync(bucket, obj.Key, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            fetch.Pages++;
            var name = LastSegment(obj.Key);
            await fetch.Landing.LandAsync(
                new LandedItem(buffer.ToArray(), response.Headers.ContentType, name, RecordCount: -1, Headers: null),
                fetch.Vars.Clone().WithString("filename", name).WithString("key", obj.Key), ct).ConfigureAwait(false);
        }

        fetch.Log.Log(RunLogLevel.Info, "s3", $"listed {matched.Count} matching object(s) under 's3://{bucket}/{prefix}'.");
    }

    private static string BucketFrom(AcquireSource source, IReadOnlyDictionary<string, string?> options)
    {
        if (options.TryGetValue("bucket", out var bucket) && !string.IsNullOrWhiteSpace(bucket))
        {
            return bucket!;
        }

        if (Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == "s3")
        {
            return uri.Host;
        }

        throw new SqlFlowException("An S3 source requires a 'bucket' option or an 's3://bucket' base url.");
    }

    private static async Task<string> ResolveRequired(AcquireFetch fetch, IReadOnlyDictionary<string, string?> options, string key, CancellationToken ct)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new SqlFlowException($"An S3 source requires '{key}' in source.options.");
        }

        return await fetch.Secrets.ResolveAsync(value!, ct).ConfigureAwait(false);
    }

    private static string LastSegment(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }
}
