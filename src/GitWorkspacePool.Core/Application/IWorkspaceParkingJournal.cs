using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Commits parking intent before Git moves a linked worktree.
/// </summary>
public interface IWorkspaceParkingJournal
{
    /// <summary>Quarantines a conflicting or ambiguous worktree without changing files.</summary>
    Task RequireRecoveryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Lists sessions eligible for parking or a retry.</summary>
    Task<IReadOnlyList<WorkspaceSession>> ListParkCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>Lists sessions eligible for unparking or a retry.</summary>
    Task<IReadOnlyList<WorkspaceSession>> ListUnparkCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>Records an owned destination and immutable HEAD before any move.</summary>
    Task BeginParkingAsync(
        Guid id,
        string parkingPath,
        string headCommit,
        CancellationToken cancellationToken
    );

    /// <summary>Marks a successfully verified move as parked.</summary>
    Task CompleteParkingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Records the return intent before moving back.</summary>
    Task BeginUnparkingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Marks the verified return as active and clears the reserved destination.</summary>
    Task CompleteUnparkingAsync(Guid id, CancellationToken cancellationToken);
}
