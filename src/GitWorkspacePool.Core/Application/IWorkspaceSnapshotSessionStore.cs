using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Reads trusted workspace ownership and records successfully published snapshots.
/// </summary>
public interface IWorkspaceSnapshotSessionStore
{
    /// <summary>
    /// Finds a session by its immutable operation ID without reserving new ownership.
    /// </summary>
    /// <param name="operationId">The existing session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or null if unknown.</returns>
    Task<WorkspaceSession?> FindAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets sessions available for explicit user-triggered snapshots.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Currently active session records.</returns>
    Task<IReadOnlyList<WorkspaceSession>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically records a published snapshot and its audit event.
    /// </summary>
    /// <param name="receipt">The verified snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordSnapshotAsync(
        WorkspaceSnapshotReceipt receipt,
        CancellationToken cancellationToken
    );
}
