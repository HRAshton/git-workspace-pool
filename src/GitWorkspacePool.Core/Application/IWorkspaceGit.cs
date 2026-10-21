namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Performs bounded Git operations on a previously registered repository.
/// </summary>
public interface IWorkspaceGit
{
    /// <summary>
    /// Checks whether Git identifies the requested path as a registered worktree.
    /// </summary>
    /// <param name="repositoryPath">The configured source clone.</param>
    /// <param name="workspacePath">The path to inspect.</param>
    /// <param name="cancellationToken">Cancellation requested by the user.</param>
    /// <returns>True if Git reports the worktree.</returns>
    Task<bool> IsRegisteredWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetches only the specified pull request head into a dedicated local ref.
    /// </summary>
    /// <param name="repositoryPath">The configured source clone.</param>
    /// <param name="pullRequestNumber">The pull request number.</param>
    /// <param name="cancellationToken">Cancellation requested by the user.</param>
    /// <returns>The asynchronous fetch operation.</returns>
    Task FetchPullRequestAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a detached worktree at the requested location from its dedicated local PR ref.
    /// </summary>
    /// <param name="repositoryPath">The configured source clone.</param>
    /// <param name="workspacePath">The new managed workspace location.</param>
    /// <param name="pullRequestNumber">The pull request number.</param>
    /// <param name="cancellationToken">Cancellation requested by the user.</param>
    /// <returns>The asynchronous worktree creation operation.</returns>
    Task CreateWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );
}
