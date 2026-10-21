using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Commits isolated recovery intent before creating a worktree, then records verification.
/// </summary>
public interface IWorkspaceRestoreJournal
{
    /// <summary>
    /// Reserves a unique destination for a journalled snapshot before filesystem changes.
    /// </summary>
    /// <param name="restoreId">New attempt ID.</param>
    /// <param name="snapshotId">Recorded snapshot ID.</param>
    /// <param name="targetPath">Fresh isolated destination path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReserveRestoreAsync(
        Guid restoreId,
        Guid snapshotId,
        string targetPath,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Marks an attempt verified only after complete Git and file verification.
    /// </summary>
    /// <param name="restoreId">Recorded attempt ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task VerifyRestoreAsync(Guid restoreId, CancellationToken cancellationToken);
}
