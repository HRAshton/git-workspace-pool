namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Performs bounded Git operations on a previously registered repository.
/// </summary>
public interface IWorkspaceGit
{
    /// <summary>
    /// Checks whether Git identifies a path as a registered worktree.
    /// </summary>
    /// <param name="repositoryPath">The trusted source clone.</param>
    /// <param name="workspacePath">The target worktree path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the worktree is registered.</returns>
    Task<bool> IsRegisteredWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Gets the full checked-out commit ID for safe recovery reconciliation.
    /// </summary>
    /// <param name="workspacePath">The registered worktree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The checked-out commit ID.</returns>
    Task<string> GetWorktreeCommitAsync(string workspacePath, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a detached worktree from an immutable pinned commit ID.
    /// </summary>
    /// <param name="repositoryPath">The trusted source clone.</param>
    /// <param name="workspacePath">The free workspace path.</param>
    /// <param name="commitSha">The full commit ID already persisted to the journal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        string commitSha,
        CancellationToken cancellationToken
    );
}
