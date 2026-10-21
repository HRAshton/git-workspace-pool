using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Restores a journalled snapshot without altering its original session.
/// </summary>
public sealed class RestoreWorkspaceSnapshotService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceSnapshotRestorer _restorer;

    /// <summary>
    /// Creates a restore use case from the snapshot repository and restoration port.
    /// </summary>
    public RestoreWorkspaceSnapshotService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceSnapshotRestorer restorer
    )
    {
        _sessions = sessions;
        _restorer = restorer;
    }

    /// <summary>
    /// Restores the specified published snapshot into a fresh, separate Git worktree.
    /// </summary>
    /// <param name="snapshotId">ID of a previously published snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new recovery worktree path.</returns>
    public async Task<string> RestoreAsync(
        Guid snapshotId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotRecord record = await _sessions.GetSnapshotAsync(
            snapshotId,
            cancellationToken
        ) ?? throw new InvalidOperationException("The selected snapshot is not journalled.");

        return await _restorer.RestoreAsync(record, cancellationToken);
    }
}
