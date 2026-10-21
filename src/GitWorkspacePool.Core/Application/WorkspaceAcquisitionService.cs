using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Reconciles an exclusive PR worktree with the persistent ownership journal.
/// </summary>
public sealed class WorkspaceAcquisitionService
{
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceDirectories _directories;
    private readonly IWorkspaceSessionJournal _journal;
    private readonly IWorkspaceOperationLock _locks;

    /// <summary>
    /// Initializes the workspace acquisition mechanism.
    /// </summary>
    public WorkspaceAcquisitionService(
        IWorkspaceGit git,
        IWorkspaceDirectories directories,
        IWorkspaceSessionJournal journal,
        IWorkspaceOperationLock locks
    )
    {
        _git = git;
        _directories = directories;
        _journal = journal;
        _locks = locks;
    }

    /// <summary>
    /// Opens or safely recovers a workspace without editing an allocated tree.
    /// </summary>
    public async Task<OpenPullRequestResult> AcquireAsync(
        PullRequestReference request,
        RepositoryRegistration registration,
        CancellationToken cancellationToken
    )
    {
        string folder = $"{request.Provider}-{request.Owner}-{request.Repository}-pr-{request.Number}";
        string workspacePath = Path.GetFullPath(Path.Combine(registration.WorkspaceRoot, folder));

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            workspacePath,
            cancellationToken
        );

        WorkspaceSession session = await _journal.GetOrCreateAsync(
            $"{request.Provider}:{request.RepositoryKey}",
            request.Number,
            registration.LocalPath,
            workspacePath,
            cancellationToken
        );

        bool directoryExists = _directories.Exists(workspacePath);
        bool registered = await _git.IsRegisteredWorktreeAsync(
            registration.LocalPath,
            workspacePath,
            cancellationToken
        );
        bool reused;

        if (session.State == WorkspaceSessionState.RecoveryRequired)
        {
            throw new InvalidOperationException(
                "This workspace is quarantined for manual recovery. "
                    + "It cannot be reactivated automatically."
            );
        }

        if (session.State is WorkspaceSessionState.Active or WorkspaceSessionState.Suspended)
        {
            if (!directoryExists || !registered)
            {
                await _journal.RequireRecoveryAsync(session.OperationId, cancellationToken);
                throw new InvalidOperationException(
                    "An allocated workspace is missing or no longer registered with Git. "
                        + "Ownership is preserved for manual recovery."
                );
            }

            if (session.State == WorkspaceSessionState.Suspended)
            {
                await _journal.ResumeAsync(session.OperationId, cancellationToken);
            }

            reused = true;
        }
        else if (directoryExists && registered)
        {
            if (session.PinnedCommitSha is null)
            {
                await QuarantineAsync(session, cancellationToken);
            }

            string head = await _git.GetWorktreeCommitAsync(workspacePath, cancellationToken);

            if (!head.Equals(session.PinnedCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                await QuarantineAsync(session, cancellationToken);
            }

            await _journal.ActivateAsync(session.OperationId, cancellationToken);
            reused = true;
        }
        else if (!directoryExists && !registered && session.State == WorkspaceSessionState.Preparing)
        {
            string? pinned = session.PinnedCommitSha;

            if (pinned is null)
            {
                await _git.FetchPullRequestAsync(
                    registration.LocalPath,
                    request.Number,
                    cancellationToken
                );
                pinned = await _git.GetPullRequestCommitAsync(
                    registration.LocalPath,
                    request.Number,
                    cancellationToken
                );
                await _journal.PinCommitAsync(session.OperationId, pinned, cancellationToken);
            }

            _directories.EnsureRoot(registration.WorkspaceRoot);
            await _git.CreateWorktreeAsync(
                registration.LocalPath,
                workspacePath,
                pinned,
                cancellationToken
            );

            if (
                !_directories.Exists(workspacePath)
                || !await _git.IsRegisteredWorktreeAsync(
                    registration.LocalPath,
                    workspacePath,
                    cancellationToken
                )
                || !string.Equals(
                    await _git.GetWorktreeCommitAsync(workspacePath, cancellationToken),
                    pinned,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                await QuarantineAsync(session, cancellationToken);
            }

            await _journal.ActivateAsync(session.OperationId, cancellationToken);
            reused = false;
        }
        else
        {
            await QuarantineAsync(session, cancellationToken);
            throw new InvalidOperationException("Workspace recovery is required.");
        }

        return new OpenPullRequestResult(workspacePath, reused);
    }

    private async Task QuarantineAsync(
        WorkspaceSession session,
        CancellationToken cancellationToken
    )
    {
        await _journal.RequireRecoveryAsync(session.OperationId, cancellationToken);
        throw new InvalidOperationException(
            "The workspace cannot be safely reconciled with its operation journal. "
                + "No user files were changed or removed."
        );
    }
}
