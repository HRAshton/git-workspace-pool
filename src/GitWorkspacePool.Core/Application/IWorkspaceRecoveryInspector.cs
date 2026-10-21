using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Read-only inspection of incomplete and unowned recovery artifacts.
/// </summary>
public interface IWorkspaceRecoveryInspector
{
    /// <summary>
    /// Scans managed snapshot/recovery directories without changing Git, files or SQLite.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Findings requiring possible manual inspection.</returns>
    Task<IReadOnlyList<WorkspaceRecoveryFinding>> ScanAsync(CancellationToken cancellationToken);
}
