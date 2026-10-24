using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>Collects bounded, read-only observations of empty released slots and retained donors.</summary>
public interface IWorkspaceReuseCandidateCollector
{
    /// <summary>Verifies ownership, paths, registration and pinned donor head before returning a candidate.</summary>
    Task<IReadOnlyList<VerifiedReuseCandidate>> CollectAsync(
        GitRepositoryRegistration repository,
        string targetCommit,
        CancellationToken cancellationToken
    );
}
