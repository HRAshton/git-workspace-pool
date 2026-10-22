namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Fault-injection checkpoints after durable release/slot acquisition boundaries.
/// Production does not install a sink; checkpoints never authorize cleanup.
/// </summary>
public enum WorkspaceSlotHandoffCheckpoint
{
    /// <summary>A release intent is durably Prepared, but still holds its lease.</summary>
    ReleasePrepared,

    /// <summary>A parked PR was released atomically and its old slot is Ready.</summary>
    ReleaseCommitted,

    /// <summary>A second PR and exclusive Reserved lease were committed.</summary>
    SlotReserved,

    /// <summary>The new PR's target HEAD is pinned before Git checkout.</summary>
    CommitPinned,

    /// <summary>Git created the linked worktree, but owner activation has not committed.</summary>
    WorktreeCreated,

    /// <summary>Verified Active/Protected ownership was committed.</summary>
    SlotActivated,
}

/// <summary>Optional test hook for killing the owning process after a durable step.</summary>
public interface IWorkspaceSlotHandoffCheckpointSink
{
    /// <summary>Notifies that the named checkpoint has completed.</summary>
    ValueTask AfterAsync(
        WorkspaceSlotHandoffCheckpoint checkpoint,
        string workspacePath,
        CancellationToken cancellationToken
    );
}
