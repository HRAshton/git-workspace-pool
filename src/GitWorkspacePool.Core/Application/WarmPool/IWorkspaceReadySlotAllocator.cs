using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>Explicit, generation-checked acquisition of a previously released slot.</summary>
public interface IWorkspaceReadySlotAllocator
{
    /// <summary>
    /// Atomically reserves a Ready slot, creates the new Preparing PR session,
    /// and issues exactly one Held lease. It never checks out a worktree.
    /// </summary>
    Task<WorkspaceReadySlotReservation?> TryReserveAsync(
        Guid slotId,
        long expectedGeneration,
        string repositoryKey,
        int pullRequestNumber,
        string sourcePath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Commits the verified Git checkout, PR activation and slot protection
    /// in one transaction. A stale lease or preparation is never activated.
    /// </summary>
    Task<bool> TryActivateAsync(
        WorkspaceReadySlotReservation reservation,
        string commitSha,
        CancellationToken cancellationToken
    );
}

/// <summary>A single persisted PR owner and generation-specific slot lease.</summary>
public sealed record WorkspaceReadySlotReservation(
    WorkspaceSession Session,
    WorkspaceSlotLease Lease
);
