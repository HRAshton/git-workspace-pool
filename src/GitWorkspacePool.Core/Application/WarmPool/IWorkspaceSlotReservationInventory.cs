namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Bounded, read-only inspection of unfinished Ready-slot allocations.
/// Not a permission to resume, clean, adopt or delete a worktree.
/// </summary>
public interface IWorkspaceSlotReservationInventory
{
    /// <summary>
    /// Returns durable Reserved/Preparing generations for manual recovery.
    /// Inconsistent leases remain visible through the separate ownership audit.
    /// </summary>
    Task<IReadOnlyList<WorkspaceReadySlotReservation>> ListReservedAsync(
        CancellationToken cancellationToken
    );
}
