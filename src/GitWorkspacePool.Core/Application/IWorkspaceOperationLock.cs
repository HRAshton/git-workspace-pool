namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Serializes operations on a workspace across application instances and processes.
/// </summary>
public interface IWorkspaceOperationLock
{
    /// <summary>
    /// Acquires an OS-released exclusive lock for a managed workspace path.
    /// </summary>
    /// <param name="workspacePath">The canonical workspace path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock whose disposal releases ownership.</returns>
    Task<IAsyncDisposable> AcquireAsync(
        string workspacePath,
        CancellationToken cancellationToken
    );
}
