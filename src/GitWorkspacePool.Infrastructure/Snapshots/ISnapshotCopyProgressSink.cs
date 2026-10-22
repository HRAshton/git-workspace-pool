namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Optional test-only seam for deterministic failures after physical copy writes.
/// Production never supplies a sink or accepts one from untrusted input.
/// </summary>
public interface ISnapshotCopyProgressSink
{
    /// <summary>Called after a chunk was written, before the file is complete.</summary>
    ValueTask AfterBytesWrittenAsync(
        SnapshotCopyPhase phase,
        long bytesWritten,
        CancellationToken cancellationToken
    );
}

/// <summary>The copy phase for a fault-injection checkpoint.</summary>
public enum SnapshotCopyPhase
{
    /// <summary>Exporting the Git index.</summary>
    ExportIndex,

    /// <summary>Exporting a modified, untracked, or ignored working file.</summary>
    ExportWorkingFile,

    /// <summary>Restoring a working file into a new isolated recovery worktree.</summary>
    RestoreWorkingFile,

    /// <summary>Restoring the saved Git index after verifying the clean checkout index.</summary>
    RestoreIndex,
}
