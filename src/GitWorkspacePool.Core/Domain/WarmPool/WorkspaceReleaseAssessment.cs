namespace GitWorkspacePool.Core.Domain.WarmPool;

/// <summary>
/// Why a parked PR's protected slot cannot advance to a release transaction.
/// A positive assessment is advisory and never authorizes checkout or eviction.
/// </summary>
public enum WorkspaceReleaseBlocker
{
    /// <summary>No blocker was found at the instant of inspection.</summary>
    None,

    /// <summary>The requested PR session does not exist.</summary>
    UnknownSession,

    /// <summary>The original Git worktree has not finished parking.</summary>
    SessionNotParked,

    /// <summary>No exclusive Held lease protects this PR's slot.</summary>
    MissingLease,

    /// <summary>The slot has an unexpected ownership state or generation.</summary>
    SlotNotProtected,

    /// <summary>The slot disagrees with the session's repository, source or path.</summary>
    OwnershipMismatch,

    /// <summary>The parking path is absent or does not match the reserved location.</summary>
    InvalidParkingPath,

    /// <summary>A path contains an unsafe junction or symbolic link.</summary>
    UnsafePath,

    /// <summary>The original slot path or its Git registration is still occupied.</summary>
    OriginalPathOccupied,

    /// <summary>The preserved parked worktree is absent or unregistered.</summary>
    ParkedWorktreeMissing,

    /// <summary>Git or filesystem ownership could not be reliably inspected.</summary>
    Unverifiable,
}

/// <summary>
/// Read-only release preflight result. Every write must perform its own locked revalidation.
/// </summary>
/// <param name="SessionId">Inspected PR owner.</param>
/// <param name="SlotId">Protected slot, if reliably identified.</param>
/// <param name="Generation">Observed lease generation, not a reusable lease token.</param>
/// <param name="Blocker">Reason release must not proceed; None means eligible to revalidate.</param>
public sealed record WorkspaceReleaseAssessment(
    Guid SessionId,
    Guid? SlotId,
    long? Generation,
    WorkspaceReleaseBlocker Blocker
)
{
    /// <summary>Indicates that no blocker was seen during this read-only check.</summary>
    public bool CanBeginRelease => Blocker == WorkspaceReleaseBlocker.None;
}
