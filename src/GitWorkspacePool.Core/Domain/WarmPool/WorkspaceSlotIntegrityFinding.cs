namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>
/// Persistent slot registry inconsistency that must never authorize release or checkout.
/// </summary>
public enum WorkspaceSlotIntegrityIssue
{
    /// <summary>A Protected slot lacks an exclusive Held lease.</summary>
    ProtectedLeaseMissing,

    /// <summary>A Ready slot still has an owner and must not be allocated.</summary>
    ReadyWithHeldLease,

    /// <summary>A Reserved slot has no durable exclusive lease.</summary>
    ReservedLeaseMissing,

    /// <summary>A Held lease's generation disagrees with the corresponding slot.</summary>
    LeaseGenerationMismatch,

    /// <summary>The lease's session identity or protected path disagrees with the slot.</summary>
    LeaseOwnerMismatch,
}

/// <summary>
/// Read-only registry finding. No finding grants permission to modify files.
/// </summary>
/// <param name="SlotId">Immutable slot identifier.</param>
/// <param name="WorkspacePath">Recorded original slot path, not a new destination.</param>
/// <param name="Issue">Detected integrity problem.</param>
/// <param name="SessionId">Lease holder if known.</param>
public sealed record WorkspaceSlotIntegrityFinding(
    Guid SlotId,
    string WorkspacePath,
    WorkspaceSlotIntegrityIssue Issue,
    Guid? SessionId
);
