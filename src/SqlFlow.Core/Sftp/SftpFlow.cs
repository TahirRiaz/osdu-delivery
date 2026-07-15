using SqlFlow.Core.Files;
using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Sftp;

/// <summary>
/// A validated SFTP transfer flow (<c>flowType: sftp</c>): downloads files from an SFTP server into the data lake
/// (or a local path), or uploads files from the lake/local to the server, byte-for-byte. It is the dedicated
/// interface to SFTP, the file counterpart to the <c>api</c> (REST) flow type: one protocol, one flow type. It
/// performs no parsing; the downstream json/csv/xml file flows ingest what it lands.
/// </summary>
public sealed record SftpFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    public required SftpServer Server { get; init; }

    /// <summary>Which way files move: down from the server into <see cref="Local"/> (default), or up to the server.</summary>
    public SftpDirection Direction { get; init; } = SftpDirection.Download;

    /// <summary>The non-SFTP side: a data-lake URI (<c>abfss://…</c> / https) or a local/UNC path. On a download it is
    /// the target files are written to; on an upload it is the source files are read from.</summary>
    public required string Local { get; init; }

    /// <summary>The directory on the SFTP server (the remote root). On download it is listed; on upload it is written to.</summary>
    public string RemotePath { get; init; } = ".";

    /// <summary>File-name glob selecting which files transfer. Default <c>*</c>.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Recurse into subdirectories under the root. Default true.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>Only transfer files modified within this many days (0 = all).</summary>
    public int ModifiedWithinDays { get; init; }

    /// <summary>Overwrite an existing destination file; when false a collision fails rather than clobbers.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Preserve the source's folder structure under the destination root; flat by file name otherwise.</summary>
    public bool PreserveStructure { get; init; } = true;

    /// <summary>Optional explicit declaration of the file set(s) this transfer produces, for lineage. A download's
    /// output is <see cref="Local"/>, so when this is empty lineage binds that single folder to the downstream
    /// ingestion. Declare outputs when one download drops several distinct file sets (e.g. several vendor objects
    /// into several subfolders) that feed different ingestions: each entry binds independently, by folder, file-name
    /// glob, or path regex, to every ingestion that reads it, so every consumer of a downloaded file gets an edge
    /// from this flow.</summary>
    public IReadOnlyList<FileOutput> Outputs { get; init; } = [];
}

/// <summary>The direction an SFTP flow moves files.</summary>
public enum SftpDirection
{
    /// <summary>Server -> lake/local.</summary>
    Download,

    /// <summary>Lake/local -> server.</summary>
    Upload,
}

/// <summary>The SFTP server and its credentials. Secret material is always a <c>${...}</c> reference, never inline.</summary>
public sealed record SftpServer
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    /// <summary>The SFTP username (a coordinate, not a secret).</summary>
    public required string Username { get; init; }

    /// <summary>Secret reference to the password; an alternative to <see cref="PrivateKeyRef"/>.</summary>
    public string? PasswordRef { get; init; }

    /// <summary>Secret reference to the private-key PEM.</summary>
    public string? PrivateKeyRef { get; init; }

    /// <summary>Secret reference to the passphrase protecting <see cref="PrivateKeyRef"/>.</summary>
    public string? PassphraseRef { get; init; }
}

/// <summary>The outcome of an SFTP run: what moved, how much, any error. Never throws for a transfer failure.</summary>
public sealed record SftpRunResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double DurationSeconds { get; init; }
    public int Matched { get; init; }
    public int FilesTransferred { get; init; }
    public long BytesTransferred { get; init; }
    public IReadOnlyList<SftpFileResult> Files { get; init; } = [];
}

/// <summary>One file transferred by an SFTP run.</summary>
public sealed record SftpFileResult(string Location, long SizeBytes);
