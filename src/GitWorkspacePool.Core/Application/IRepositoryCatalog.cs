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
    /// <param name="request">The validated request.</param>
    /// <returns>A configured clone, if present.</returns>
    RepositoryRegistration? Find(PullRequestReference request);
}
