namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// A trusted, user-configured local repository and workspace root.
/// </summary>
/// <param name="RepositoryKey">The GitHub owner/repository identifier.</param>
/// <param name="LocalPath">Path to the existing local Git clone.</param>
/// <param name="WorkspaceRoot">Root directory used for managed worktrees.</param>
public sealed record RepositoryRegistration(
    string RepositoryKey,
    string LocalPath,
    string WorkspaceRoot
);
