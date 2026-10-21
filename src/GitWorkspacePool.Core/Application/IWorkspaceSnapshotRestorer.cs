using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Verifies and rehydrates a snapshot into a newly allocated recovery worktree.
/// </summary>
public interface IWorkspaceSnapshotRestorer
{
    /// <summary>
    /// Restores changes into a separate worktree without touching the source session.
    /// </summary>
    /// <param name="record">The journalled snapshot and its original owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new recovery worktree path.</returns>
    Task<string> RestoreAsync(
        WorkspaceSnapshotRecord record,
        CancellationToken cancellationToken
    );
}
