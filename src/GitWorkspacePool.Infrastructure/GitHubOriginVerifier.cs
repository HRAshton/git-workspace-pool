using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Verifies the local clone's GitHub origin before any reservation or fetch.
/// </summary>
public sealed class GitHubOriginVerifier : IRepositoryOriginVerifier
{
    private readonly IWorkspaceGit _git;

    /// <summary>
    /// Initializes the verifier using the Git operations boundary.
    /// </summary>
    public GitHubOriginVerifier(IWorkspaceGit git)
    {
        _git = git;
    }

    /// <inheritdoc/>
    public async Task VerifyAsync(
        PullRequestReference request,
        RepositoryRegistration registration,
        CancellationToken cancellationToken
    )
    {
        string origin = await _git.GetOriginUrlAsync(registration.LocalPath, cancellationToken);

        if (!GitHubRemoteIdentity.Matches(origin, request))
        {
            throw new InvalidOperationException(
                "Configured Git origin does not match this GitHub PR. "
                    + "No workspace was reserved or modified."
            );
        }
    }
}
