namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>
/// Durable slot ownership state. Ready is never inferred from an empty directory.
/// </summary>
public enum WorkspaceSlotState
{
    /// <summary>An existing PR owns the path; recycling is prohibited.</summary>
    Protected,

    /// <summary>A separately verified released slot may be reserved.</summary>
    Ready,

    /// <summary>A generation-specific lease was granted; no other owner can acquire it.</summary>
    Reserved,

    /// <summary>Unverifiable filesystem or journal state forbids reuse.</summary>
    RecoveryRequired,
}

/// <summary>
/// Persisted slot identity, generation and repository binding.
/// </summary>
/// <param name="SlotId">Immutable slot identifier independent from its PR owner.</param>
/// <param name="RepositoryKey">Canonical repository identity.</param>
/// <param name="SourcePath">Original trusted clone.</param>
/// <param name="WorkspacePath">Canonical original slot path.</param>
/// <param name="Generation">Monotonically increasing ownership generation.</param>
/// <param name="State">Current slot state.</param>
/// <param name="HeadCommitSha">Last known HEAD, not a proof of cleanliness.</param>
public sealed record WorkspaceSlot(
    Guid SlotId,
    string RepositoryKey,
    string SourcePath,
    string WorkspacePath,
    long Generation,
    WorkspaceSlotState State,
    string? HeadCommitSha
);

/// <summary>
/// One durable generation-specific exclusive slot lease.
/// </summary>
/// <param name="LeaseId">Unique acquisition attempt identity.</param>
/// <param name="SlotId">Slot whose generation was reserved.</param>
/// <param name="SessionId">The owning PR session.</param>
/// <param name="Generation">Granted generation, invalidating stale requests.</param>
public sealed record WorkspaceSlotLease(
    Guid LeaseId,
    Guid SlotId,
    Guid SessionId,
    long Generation
);
