using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Explicitly marks an owned PR session inactive without releasing any disk space or files.
/// </summary>
public sealed class SuspendWorkspaceSessionService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceSessionJournal _journal;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceDirectories _directories;

    /// <summary>
    /// Initializes the non-destructive suspension workflow.
    /// </summary>
    public SuspendWorkspaceSessionService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceSessionJournal journal,
        IWorkspaceOperationLock locks,
        IWorkspaceGit git,
        IWorkspaceDirectories directories
    )
    {
        _sessions = sessions;
        _journal = journal;
        _locks = locks;
        _git = git;
        _directories = directories;
    }

    /// <summary>
    /// Records a suspended state for a verified, currently active PR session.
    /// </summary>
    /// <param name="operationId">The allocated PR session ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The unchanged, still exclusively reserved workspace path.</returns>
    public async Task<string> SuspendAsync(
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSession session = await _sessions.FindAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("The session does not exist.");

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );

        // Re-read after obtaining the cross-process lock; another action may
        // have changed the session while this request was waiting.
        session = await _sessions.FindAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("The session no longer exists.");

        if (session.State != WorkspaceSessionState.Active)
        {
            throw new InvalidOperationException("Only active sessions may be suspended.");
        }

        if (
            !_directories.Exists(session.WorkspacePath)
            || !await _git.IsRegisteredWorktreeAsync(
                session.SourcePath,
                session.WorkspacePath,
                cancellationToken
            )
        )
        {
            await _journal.RequireRecoveryAsync(session.OperationId, cancellationToken);
            throw new InvalidOperationException(
                "The allocated worktree is missing or unregistered. "
                    + "Session ownership is quarantined for manual recovery."
            );
        }

        await _journal.SuspendAsync(operationId, cancellationToken);
        return session.WorkspacePath;
    }
}
