using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Commits an explicitly prepared parked-session slot handoff. No Git mutation.
/// </summary>
public interface IWorkspaceSlotReleaseCommitJournal
{
    /// <summary>Reads a durable preparation, including completed attempts.</summary>
    Task<WorkspaceSlotReleasePreparation?> FindPreparationAsync(
        Guid preparationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Atomically relinquishes a Held lease, retains the parked PR,
    /// marks the slot Ready and commits the exact preparation.
    /// Returns false if generation or owner state changed.
    /// </summary>
    Task<bool> TryCommitPreparedAsync(
        Guid preparationId,
        Guid slotId,
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken
    );
}
