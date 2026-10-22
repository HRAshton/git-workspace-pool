using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Durable, generation-aware ownership registry. It never performs Git checkout or moves.
/// </summary>
public interface IWorkspaceSlotRegistry
{
    /// <summary>Looks up a slot by its immutable identity.</summary>
    Task<WorkspaceSlot?> FindSlotAsync(Guid slotId, CancellationToken cancellationToken);

    /// <summary>Looks up the currently protected slot of a PR session.</summary>
    Task<WorkspaceSlot?> FindHeldSlotAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically leases a previously verified Ready slot with compare-and-swap
    /// generation semantics. Returns null for a stale or ineligible request.
    /// Does not make a worktree Ready or permit checkout.
    /// </summary>
    Task<WorkspaceSlotLease?> TryLeaseReadyAsync(
        Guid slotId,
        Guid sessionId,
        long expectedGeneration,
        CancellationToken cancellationToken
    );
}
