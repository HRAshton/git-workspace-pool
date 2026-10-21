namespace GitWorkspacePool.Core.Domain;

/// <summary>
/// Classification of uncommitted or unowned local recovery artifacts.
/// </summary>
public enum WorkspaceRecoveryKind
{
    /// <summary>An export was interrupted before publication.</summary>
    IncompleteSnapshot,

    /// <summary>A snapshot directory is published but is absent from the journal.</summary>
    UnjournalledSnapshot,

    /// <summary>SQLite references snapshot data that cannot be found in storage.</summary>
    MissingSnapshot,

    /// <summary>An isolated Git recovery worktree exists outside any managed session.</summary>
    UnmanagedRecoveryWorktree,

    /// <summary>The recovery directory exists but is not a Git-registered worktree.</summary>
    UnregisteredRecoveryDirectory,

    /// <summary>An unexpected file or directory needs manual inspection.</summary>
    UnknownEntry,

    /// <summary>A junction or symlink was found; the inspector will not follow it.</summary>
    UnsafeReparsePoint,

    /// <summary>Git could not establish whether a recovery worktree is registered.</summary>
    UnverifiableRecoveryDirectory,
}

/// <summary>
/// An informational discovery result. It never authorizes deletion or allocation.
/// </summary>
/// <param name="Kind">The recovery classification.</param>
/// <param name="Path">The local path for manual inspection.</param>
/// <param name="SnapshotId">Associated snapshot identifier, when known.</param>
/// <param name="Description">A human-readable reason this entry requires attention.</param>
public sealed record WorkspaceRecoveryFinding(
    WorkspaceRecoveryKind Kind,
    string Path,
    Guid? SnapshotId,
    string Description
);
