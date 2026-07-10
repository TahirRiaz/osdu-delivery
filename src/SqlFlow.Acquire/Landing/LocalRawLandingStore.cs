using SqlFlow.Core;

namespace SqlFlow.Acquire.Landing;

/// <summary>Lands raw payloads to the local or a UNC filesystem: the on-prem / dev sink and the test sink.</summary>
public sealed class LocalRawLandingStore : IRawLandingStore
{
    public bool CanHandle(string location)
        => !string.IsNullOrWhiteSpace(location) && location.IndexOf("://", StringComparison.Ordinal) < 0;

    public string Combine(string baseLocation, string relativePath)
        => Path.GetFullPath(Path.Combine(baseLocation, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public Task<bool> ExistsAsync(string location, CancellationToken ct = default)
        => Task.FromResult(File.Exists(location));

    public async Task PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        try
        {
            await using var stream = new FileStream(location, mode, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);
            await stream.WriteAsync(content, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (!overwrite && File.Exists(location))
        {
            throw new SqlFlowException($"Landing file '{location}' already exists and overwrite is disabled.", ex);
        }
    }
}
