namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Injects deterministic failures at durable boundaries during resilience tests.
/// </summary>
/// <remarks>
/// Production uses no checkpoint sink. This interface is not a background task runner
/// and must never be driven by input from Git, the browser or a repository file.
/// </remarks>
public interface ISnapshotCheckpointSink
{
    /// <summary>
    /// Invoked at a documented filesystem durability boundary.
    /// </summary>
    /// <param name="checkpoint">The phase that has just completed.</param>
    /// <param name="operationPath">The pending or published operation directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task, or a controlled test failure.</returns>
    ValueTask OnCheckpointAsync(
        SnapshotCheckpoint checkpoint,
        string operationPath,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Durable side-effect boundaries that must be safe if the application terminates.
/// </summary>
public enum SnapshotCheckpoint
{
    /// <summary>The snapshot index payload has been copied and hashed.</summary>
    ExportIndexCopied,

    /// <summary>All payload checks and the manifest are complete, before publication.</summary>
    ExportReadyToPublish,

    /// <summary>Snapshot directory is published, before its SQLite journal receipt.</summary>
    ExportPublished,

    /// <summary>A fresh recovery worktree has been created and registered.</summary>
    RestoreWorktreeCreated,

    /// <summary>Recovery working files and recorded deletions have been applied.</summary>
    RestoreFilesApplied,

    /// <summary>The saved Git index has been copied and checked, before final Git verification.</summary>
    RestoreIndexApplied,
}
