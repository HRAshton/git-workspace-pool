using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Resolves only explicitly registered local repositories.
/// </summary>
public interface IRepositoryCatalog
{
    /// <summary>
    /// Finds a configured clone for a pull request.
    /// </summary>
    RepositoryRegistration? Find(PullRequestReference request);
}

/// <summary>
/// Performs bounded Git operations on a previously registered repository.
/// </summary>
public interface IWorkspaceGit
{
    /// <summary>
    /// Checks whether Git identifies the requested path as a registered worktree.
    /// </summary>
    Task<bool> IsRegisteredWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetches only the specified pull request head.
    /// </summary>
    Task FetchPullRequestAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a detached worktree at the requested location from FETCH_HEAD.
    /// </summary>
    Task CreateWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Runs the configured editor on a prepared directory.
/// </summary>
public interface IEditorLauncher
{
    /// <summary>
    /// Opens a directory in the editor without executing build commands.
    /// </summary>
    Task OpenAsync(string workspacePath, CancellationToken cancellationToken);
}

/// <summary>
/// Restricts workspace directory operations to managed roots.
/// </summary>
public interface IWorkspaceDirectories
{
    /// <summary>
    /// Checks if the directory already exists.
    /// </summary>
    bool Exists(string path);

    /// <summary>
    /// Ensures an explicitly configured root exists.
    /// </summary>
    void EnsureRoot(string root);
}
