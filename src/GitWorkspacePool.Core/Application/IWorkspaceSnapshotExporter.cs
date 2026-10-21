using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Creates a verified, immutable, non-destructive snapshot of a Git worktree.
/// </summary>
public interface IWorkspaceSnapshotExporter
{
    /// <summary>
    /// Captures staged, unstaged, untracked and ignored changes without modifying the worktree.
    /// </summary>
    /// <param name="workspacePath">Trusted, currently allocated Git worktree.</param>
    /// <param name="operationId">Durable owner of the worktree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The published snapshot receipt.</returns>
    Task<WorkspaceSnapshotReceipt> ExportAsync(
        string workspacePath,
        Guid operationId,
        CancellationToken cancellationToken
    );
}
