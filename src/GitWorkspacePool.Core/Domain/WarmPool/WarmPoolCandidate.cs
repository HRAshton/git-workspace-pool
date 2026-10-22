namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>
/// A pool slot's verified state as observed by the resource-management module.
/// </summary>
public enum WarmPoolSlotState
{
    /// <summary>The slot may be considered after its on-disk state is revalidated.</summary>
    Ready,

    /// <summary>A PR session currently owns the slot.</summary>
    Allocated,

    /// <summary>A durable transition is still pending.</summary>
    Transitioning,

    /// <summary>The slot has inconsistent state and must not be reused.</summary>
    RecoveryRequired,
}

/// <summary>
/// Immutable observations about one potential reusable worktree.
/// </summary>
/// <param name="SlotId">Persistent candidate slot identity.</param>
/// <param name="RepositoryKey">Canonical repository key, never inferred from a path.</param>
/// <param name="WorktreePath">The currently verified slot location.</param>
/// <param name="HeadCommitSha">The exact checked-out commit.</param>
/// <param name="State">The slot's current ownership state.</param>
/// <param name="Pinned">Whether a user has protected the slot from recycling.</param>
/// <param name="HasUserChanges">True if tracked, ignored or untracked user files must be protected.</param>
/// <param name="EstimatedChangedPaths">The estimated tracked path changes needed for the target.</param>
/// <param name="EstimatedInvalidatedArtifactBytes">Estimated existing artifact bytes lost by switching.</param>
/// <param name="ObservedTransitionTime">Optional observed comparable transition duration.</param>
public sealed record WarmPoolCandidate(
    Guid SlotId,
    string RepositoryKey,
    string WorktreePath,
    string HeadCommitSha,
    WarmPoolSlotState State,
    bool Pinned,
    bool HasUserChanges,
    int EstimatedChangedPaths,
    long EstimatedInvalidatedArtifactBytes,
    TimeSpan? ObservedTransitionTime
);
