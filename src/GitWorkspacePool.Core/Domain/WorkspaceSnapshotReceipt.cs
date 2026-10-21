namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// A verified and published, non-destructive copy of user workspace changes.
/// </summary>
/// <param name="SnapshotId">Globally unique snapshot identifier.</param>
/// <param name="OperationId">The worktree owner recorded in the journal.</param>
/// <param name="Folder">The immutable snapshot directory containing its manifest.</param>
/// <param name="FileCount">Number of copied payload files, including the Git index.</param>
/// <param name="CopiedBytes">Total number of payload bytes copied.</param>
public sealed record WorkspaceSnapshotReceipt(
    Guid SnapshotId,
    Guid OperationId,
    string Folder,
    int FileCount,
    long CopiedBytes
);
