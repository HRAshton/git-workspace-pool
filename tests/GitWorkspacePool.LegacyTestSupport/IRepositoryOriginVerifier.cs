using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Verifies that a configured clone belongs to the requested Git remote.
/// </summary>
public interface IRepositoryOriginVerifier
{
    /// <summary>
    /// Checks the actual Git origin before a worktree is allocated.
    /// </summary>
    /// <param name="request">Validated PR identity.</param>
    /// <param name="registration">The configured local clone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task VerifyAsync(
        PullRequestReference request,
        RepositoryRegistration registration,
        CancellationToken cancellationToken
    );
}
