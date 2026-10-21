namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Versioned description of every copied payload and the original Git index/status.
/// </summary>
public sealed class WorkspaceSnapshotManifest
{
    /// <summary>Manifest version used for future restore compatibility.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Snapshot identity.</summary>
    public required Guid SnapshotId { get; init; }

    /// <summary>Owning worktree operation.</summary>
    public required Guid OperationId { get; init; }

    /// <summary>The original worktree path.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>The commit checked out during the snapshot.</summary>
    public required string HeadCommitSha { get; init; }

    /// <summary>The exact NUL-delimited porcelain status, encoded as Base64 UTF-8.</summary>
    public required string StatusBase64 { get; init; }

    /// <summary>Hash of the linked Git index stored as index.bin.</summary>
    public required string GitIndexSha256 { get; init; }

    /// <summary>Copied file and directory metadata.</summary>
    public required List<WorkspaceSnapshotFile> Files { get; init; }

    /// <summary>Paths missing from the worktree, including tracked deletions.</summary>
    public required List<string> MissingPaths { get; init; }
}

/// <summary>
/// Metadata of one captured file or directory under files/.
/// </summary>
/// <param name="RelativePath">Git-style forward-slash relative path.</param>
/// <param name="IsDirectory">Whether this is a directory.</param>
/// <param name="Sha256">Content hash for a file; null for a directory.</param>
/// <param name="Length">File length or zero for a directory.</param>
/// <param name="Attributes">Original filesystem attributes.</param>
/// <param name="LastWriteUtc">Original last write time.</param>
public sealed record WorkspaceSnapshotFile(
    string RelativePath,
    bool IsDirectory,
    string? Sha256,
    long Length,
    FileAttributes Attributes,
    DateTime LastWriteUtc
);
