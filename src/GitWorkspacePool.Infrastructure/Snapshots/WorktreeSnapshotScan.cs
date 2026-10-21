namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Stable Git metadata used to select the minimum set of files for a non-destructive snapshot.
/// </summary>
/// <param name="Status">Unmodified NUL-delimited Git status including ignored entries.</param>
/// <param name="Head">The full current commit ID.</param>
/// <param name="IndexPath">The linked worktree's actual Git index file.</param>
/// <param name="ChangedPaths">Workspace-relative changed and ignored paths.</param>
public sealed record WorktreeSnapshotScan(
    string Status,
    string Head,
    string IndexPath,
    IReadOnlyList<string> ChangedPaths
);
