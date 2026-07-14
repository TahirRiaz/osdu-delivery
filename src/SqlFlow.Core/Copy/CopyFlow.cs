using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Copy;

/// <summary>
/// A validated file-copy flow (<c>flowType: cpy</c>): moves files between endpoints, byte-for-byte, in any
/// direction - storage account to storage account, local disk to storage, storage to local disk, local to local,
/// and SFTP to/from any of those. It performs no parsing or reshaping; it optionally zips the matched files into one
/// archive or unzips archives on the way through. It consolidates the estate's lake-to-lake / drop-zone copy
/// runbooks (e.g. baatbooking) into one declarative, testable, schedulable engine, the copy counterpart to the
/// acquisition (<c>acq</c>) engine that fetches from third parties.
/// </summary>
public sealed record CopyFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    public required CopyEndpoint Source { get; init; }

    public required CopyEndpoint Target { get; init; }

    /// <summary>What the flow does with the matched files: copy them verbatim, zip them into one archive, or unzip
    /// each matched archive. Default <see cref="CopyOperation.Copy"/>.</summary>
    public CopyOperation Operation { get; init; } = CopyOperation.Copy;

    public CopyOptions Options { get; init; } = new();
}

/// <summary>The transformation a copy flow applies as it moves files.</summary>
public enum CopyOperation
{
    /// <summary>Copy each matched file to the target verbatim.</summary>
    Copy,

    /// <summary>Bundle every matched source file into a single <c>.zip</c> archive written to the target.</summary>
    Zip,

    /// <summary>Extract each matched source archive (a <c>.zip</c>) and write its entries to the target.</summary>
    Unzip,
}

/// <summary>
/// One side of a copy: where the files are, how to select them (source side), and how to authenticate. The
/// <see cref="Location"/> scheme selects the endpoint implementation: a local/UNC path, an Azure Blob / ADLS Gen2
/// URI (<c>abfss://fs@account.dfs.core.windows.net/path</c> or the https form), or an <c>sftp://host[:port]/path</c>
/// URL. Selection fields (<see cref="Pattern"/>, <see cref="Recursive"/>, <see cref="ModifiedWithinDays"/>) apply
/// only to the source; the target ignores them.
/// </summary>
public sealed record CopyEndpoint
{
    /// <summary>The root location: a local/UNC path, an Azure storage URI, or an <c>sftp://…</c> URL.</summary>
    public required string Location { get; init; }

    /// <summary>File-name glob selecting which files under the root are copied (source side). Default <c>*</c>.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Recurse into subfolders under the root (source side). Default true.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Only copy files modified within this many days (source side); 0 (default) copies everything.</summary>
    public int ModifiedWithinDays { get; init; }

    /// <summary>Optional secret reference to a connection string authenticating an Azure endpoint. When absent the
    /// ambient managed identity / az login (<c>SQLFLOW_AZURE_AUTH</c>) authenticates, so no per-flow secret is needed.</summary>
    public string? ConnectionStringRef { get; init; }

    /// <summary>Optional secret reference to a SAS token authenticating an Azure endpoint.</summary>
    public string? SasTokenRef { get; init; }

    /// <summary>Optional secret reference to a storage account key authenticating an Azure endpoint (the shape the
    /// legacy runbooks used). Prefer managed identity where the drop zone allows it.</summary>
    public string? AccountKeyRef { get; init; }

    /// <summary>Secret reference to the SFTP password (SFTP endpoints).</summary>
    public string? PasswordRef { get; init; }

    /// <summary>Secret reference to the SFTP private key PEM (SFTP endpoints); an alternative to <see cref="PasswordRef"/>.</summary>
    public string? PrivateKeyRef { get; init; }

    /// <summary>Secret reference to the passphrase protecting <see cref="PrivateKeyRef"/> (SFTP endpoints).</summary>
    public string? PassphraseRef { get; init; }

    /// <summary>The SFTP username (SFTP endpoints); not a secret, so authored inline.</summary>
    public string? Username { get; init; }
}

/// <summary>Copy behavior shared across operations.</summary>
public sealed record CopyOptions
{
    /// <summary>Overwrite an existing target file; when false a copy that would collide fails rather than clobber.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Preserve each source file's folder structure (relative to the source root) under the target. When
    /// false every file lands flat under the target root by its name.</summary>
    public bool PreserveStructure { get; init; } = true;

    /// <summary>The archive name for <see cref="CopyOperation.Zip"/>; when absent it is <c>&lt;flow&gt;_&lt;timestamp&gt;.zip</c>.</summary>
    public string? ZipName { get; init; }
}

/// <summary>The outcome of a copy run: what moved, how much, and any error. Never throws for a transfer failure -
/// the runner returns a failed/partial result, so a batch member behaves like a directly-invoked flow.</summary>
public sealed record CopyRunResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>Files matched at the source (before the operation).</summary>
    public int Matched { get; init; }

    /// <summary>Files (or archive entries) written to the target.</summary>
    public int FilesWritten { get; init; }

    public long BytesWritten { get; init; }

    /// <summary>Each written target file, for the run manifest / catalog RunFile rows.</summary>
    public IReadOnlyList<CopyFileResult> Files { get; init; } = [];
}

/// <summary>One file written by a copy run.</summary>
public sealed record CopyFileResult(string Location, long SizeBytes);
