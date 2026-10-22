namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// Persistent ownership state for a single pull request's managed worktree.
/// </summary>
public enum WorkspaceSessionState
{
    /// <summary>
    /// A worktree is being prepared or needs safe reconciliation after interruption.
    /// </summary>
    Preparing,

    /// <summary>
    /// The workspace is allocated to this pull request and must never be recycled implicitly.
    /// </summary>
    Active,

    /// <summary>
    /// The PR's files remain reserved, unchanged, and unavailable for recycling.
    /// Opening the PR again explicitly resumes the session.
    /// </summary>
    Suspended,

    /// <summary>A durable move-to-parking intent is pending reconciliation.</summary>
    Parking,

    /// <summary>A complete Git worktree is held at its separate reserved parking path.</summary>
    Parked,

    /// <summary>A durable move-back intent is pending reconciliation.</summary>
    Unparking,

    /// <summary>
    /// The PR worktree is safely parked, but the original slot lease was
    /// explicitly relinquished; it must not be unparked to the old slot.
    /// </summary>
    Released,

    /// <summary>
    /// Filesystem and journal state disagree and require explicit inspection.
    /// </summary>
    RecoveryRequired,
}

/// <summary>
/// The durable record of a pull request's exclusive workspace ownership.
/// </summary>
/// <param name="OperationId">Unique identity of the initial allocation operation.</param>
/// <param name="RepositoryKey">Provider-qualified remote repository key.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="SourcePath">Trusted source clone path at reservation time.</param>
/// <param name="WorkspacePath">Exclusive stable workspace path.</param>
/// <param name="State">The current persisted state.</param>
/// <param name="PinnedCommitSha">Exact commit captured before writing a worktree, if known.</param>
public sealed record WorkspaceSession(
    Guid OperationId,
    string RepositoryKey,
    int PullRequestNumber,
    string SourcePath,
    string WorkspacePath,
    WorkspaceSessionState State,
    string? PinnedCommitSha,
    string? ParkingPath = null,
    string? ParkingCommitSha = null
);
