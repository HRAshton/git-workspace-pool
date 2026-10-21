namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// Durable state for a separate recovery worktree, not ownership of an active PR session.
/// </summary>
public enum WorkspaceRestoreState
{
    /// <summary>Intent committed; complete restore verification is still pending.</summary>
    Preparing,

    /// <summary>Git status and recovered file contents were verified.</summary>
    Verified,
}

/// <summary>
/// A recorded isolated recovery attempt.
/// </summary>
/// <param name="RestoreId">Unique attempt identifier.</param>
/// <param name="SnapshotId">Source snapshot.</param>
/// <param name="Path">Reserved recovery path.</param>
/// <param name="State">Durable operation state.</param>
public sealed record WorkspaceRestoreOperation(
    Guid RestoreId,
    Guid SnapshotId,
    string Path,
    WorkspaceRestoreState State
);
