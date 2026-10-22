using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Durable CAS preparation for slot release; it cannot release a lease or publish Ready.
/// </summary>
public interface IWorkspaceSlotReleasePreparationJournal
{
    /// <summary>
    /// Records the exact parked owner, protected slot and generation atomically.
    /// Returns the existing preparation on an identical retry, or null for a
    /// stale/ineligible request. All ownership remains unchanged.
    /// </summary>
    Task<WorkspaceSlotReleasePreparation?> TryPrepareAsync(
        Guid sessionId,
        Guid slotId,
        long expectedGeneration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Explicitly cancels an uncommitted preparation. Never modifies user files.
    /// </summary>
    Task<bool> CancelAsync(Guid preparationId, CancellationToken cancellationToken);

    /// <summary>Lists pending preparations without silently truncating the inventory.</summary>
    Task<IReadOnlyList<WorkspaceSlotReleasePreparation>> ListPreparedAsync(
        CancellationToken cancellationToken
    );
}
