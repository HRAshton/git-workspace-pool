namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>
/// Journalled, non-destructive preparation for a later explicit slot release.
/// </summary>
public enum WorkspaceSlotReleasePreparationState
{
    /// <summary>Preflight passed, but the slot remains Protected and exclusively leased.</summary>
    Prepared,

    /// <summary>The pending preparation was explicitly cancelled.</summary>
    Cancelled,
}

/// <summary>
/// A durable release preparation, not permission to reuse a slot.
/// </summary>
/// <param name="PreparationId">Unique audit record identity.</param>
/// <param name="SlotId">Protected slot that must remain exclusively leased.</param>
/// <param name="SessionId">Owner of the parked PR data.</param>
/// <param name="Generation">Generation captured when preparation was recorded.</param>
/// <param name="State">Prepared or Cancelled; neither state makes the slot Ready.</param>
public sealed record WorkspaceSlotReleasePreparation(
    Guid PreparationId,
    Guid SlotId,
    Guid SessionId,
    long Generation,
    WorkspaceSlotReleasePreparationState State
);
