namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Versioned description of every copied payload and the original Git index/status.
/// </summary>
public sealed class WorkspaceSnapshotManifest
{
    /// <summary>Manifest version used for future restore compatibility.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Required on disk: a missing version must not default to a newer format.</summary>
    public required int Version { get; init; }

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

    /// <summary>Git index stage-zero blob payloads preserved independently from the working tree.</summary>
    public required List<WorkspaceSnapshotFile> StagedFiles { get; init; }

    /// <summary>Paths missing from the worktree, including tracked deletions.</summary>
    public required List<string> MissingPaths { get; init; }
}

