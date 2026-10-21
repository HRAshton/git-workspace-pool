using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Enumerates durable receipts without truncating recovery bookkeeping to the UI's recent list.
/// </summary>
public interface IWorkspaceRecoveryJournal
{
    /// <summary>
    /// Returns all recorded receipts up to a protective limit, or fails closed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recorded snapshot inventory.</returns>
    Task<IReadOnlyList<WorkspaceSnapshotRecord>> ListRecoverySnapshotsAsync(
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists durable restore attempts, including interrupted operations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bounded restore inventory.</returns>
    Task<IReadOnlyList<WorkspaceRestoreOperation>> ListRecoveryRestoresAsync(
        CancellationToken cancellationToken
    );
}
