using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Opens a PR while retaining exclusive, durable ownership of its worktree.
/// </summary>
public sealed class OpenPullRequestHandler
{
    private readonly IRepositoryCatalog _repositories;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceDirectories _directories;
    private readonly IEditorLauncher _editor;
    private readonly IWorkspaceSessionJournal _journal;
    private readonly IWorkspaceOperationLock _locks;

    /// <summary>
    /// Initializes the open-pull-request use case and its safety boundaries.
    /// </summary>
    public OpenPullRequestHandler(
        IRepositoryCatalog repositories,
        IWorkspaceGit git,
        IWorkspaceDirectories directories,
        IEditorLauncher editor,
        IWorkspaceSessionJournal journal,
        IWorkspaceOperationLock locks
    )
    {
        _repositories = repositories;
        _git = git;
        _directories = directories;
        _editor = editor;
        _journal = journal;
        _locks = locks;
    }

    /// <summary>
    /// Reopens an allocated workspace or prepares a new detached worktree.
    /// </summary>
    public async Task<OpenPullRequestResult> HandleAsync(
        string requestUrl,
        CancellationToken cancellationToken
    )
    {
        if (
            !PullRequestReference.TryParse(requestUrl, out PullRequestReference? request)
            || request is null
        )
        {
            throw new ArgumentException("Unsupported pull request URL.", nameof(requestUrl));
        }

        RepositoryRegistration registration = _repositories.Find(request)
            ?? throw new InvalidOperationException(
                $"Register a local clone for {request.RepositoryKey} before opening this PR."
            );

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

        if (session.State == WorkspaceSessionState.Active)
        {
            if (!directoryExists || !registered)
            {
                await _journal.RequireRecoveryAsync(session.OperationId, cancellationToken);
                throw new InvalidOperationException(
                    "An allocated workspace is missing or no longer registered with Git. "
                        + "Ownership is preserved for manual recovery."
                );
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

        await _editor.OpenAsync(workspacePath, cancellationToken);
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
