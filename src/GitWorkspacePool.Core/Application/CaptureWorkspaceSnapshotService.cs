using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Coordinates explicit snapshots without changing the allocation or Git state.
/// </summary>
public sealed class CaptureWorkspaceSnapshotService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceSnapshotExporter _exporter;

    /// <summary>
    /// Initializes the snapshot use case from narrow safety boundaries.
    /// </summary>
    public CaptureWorkspaceSnapshotService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceOperationLock locks,
        IWorkspaceGit git,
        IWorkspaceSnapshotExporter exporter
    )
    {
        _sessions = sessions;
        _locks = locks;
        _git = git;
        _exporter = exporter;
    }

    /// <summary>
    /// Takes a verified snapshot only for an existing, active, registered workspace.
    /// </summary>
    /// <param name="operationId">The preexisting session owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The immutable snapshot's location and identity.</returns>
    public async Task<WorkspaceSnapshotReceipt> CaptureAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSession session = await _sessions.FindAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("The workspace owner does not exist.");

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );

        // Recheck under the inter-process lock: the session could have changed while waiting.
        session = await _sessions.FindAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("Workspace ownership disappeared.");

        if (session.State != WorkspaceSessionState.Active)
        {
            throw new InvalidOperationException("Only active workspaces can be snapshotted.");
        }

        if (
            !await _git.IsRegisteredWorktreeAsync(
                session.SourcePath,
                session.WorkspacePath,
                cancellationToken
            )
        )
        {
            throw new InvalidOperationException("Git does not recognize this owned worktree.");
        }

        WorkspaceSnapshotReceipt receipt = await _exporter.ExportAsync(
            session.WorkspacePath,
            operationId,
            cancellationToken
        );

        await _sessions.RecordSnapshotAsync(receipt, cancellationToken);
        return receipt;
    }
}
