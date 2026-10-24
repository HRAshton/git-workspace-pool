using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Persists branch ownership and immutable checkout intent separately from PR sessions.</summary>
public interface IGitBranchSessionStore
{
    /// <summary>Finds a case-sensitive repository/branch ownership record.</summary>
    Task<GitBranchSession?> FindAsync(GitBranchRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the stable internal reference number used by the shared slot allocator.</summary>
    Task<int> GetReferenceNumberAsync(GitBranchRequest request, CancellationToken cancellationToken);

    /// <summary>Resumes a verified suspended branch without fetching or changing files.</summary>
    Task ResumeAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Reserves a new workspace, or verifies an existing reservation at exactly the same paths.</summary>
    Task<GitBranchSession> ReserveAsync(
        GitBranchRequest request, GitRepositoryRegistration repository, string path, CancellationToken cancellationToken
    );

    /// <summary>Persists the immutable head before worktree creation.</summary>
    Task PinAsync(Guid operationId, string commit, CancellationToken cancellationToken);

    /// <summary>Activates a checkout only after filesystem and Git verification.</summary>
    Task ActivateAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Preserves ambiguous state for manual recovery without touching user files.</summary>
    Task RequireRecoveryAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>Lists unfinished branch operations for read-only diagnostics.</summary>
    Task<IReadOnlyList<GitBranchSession>> ListUnfinishedAsync(CancellationToken cancellationToken);
}
