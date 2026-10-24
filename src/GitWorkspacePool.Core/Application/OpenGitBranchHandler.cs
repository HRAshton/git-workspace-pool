using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Creates or reopens a pinned branch worktree without a PR, API, reset or cleanup.</summary>
public sealed class OpenGitBranchHandler
{
    private readonly IGitBranchCatalog _catalog;
    private readonly IGitRemoteReader _remotes;
    private readonly IGitBranchFetcher _fetch;
    private readonly IWorkspaceGit _git;
    private readonly IGitBranchSessionStore _store;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceDirectories _directories;
    private readonly IWorkspaceReadySlotSafety _safety;
    private readonly IWorkspaceReadySlotCheckoutVerifier _checkout;
    private readonly IEditorLauncher _editor;

    /// <summary>Composes repository trust, durable intent, path verification and editor launch.</summary>
    public OpenGitBranchHandler(
        IGitBranchCatalog catalog, IGitRemoteReader remotes, IGitBranchFetcher fetch,
        IWorkspaceGit git, IGitBranchSessionStore store, IWorkspaceOperationLock locks,
        IWorkspaceDirectories directories, IWorkspaceReadySlotSafety safety,
        IWorkspaceReadySlotCheckoutVerifier checkout, IEditorLauncher editor
    )
    {
        _catalog = catalog; _remotes = remotes; _fetch = fetch; _git = git; _store = store;
        _locks = locks; _directories = directories; _safety = safety; _checkout = checkout; _editor = editor;
    }

    /// <summary>Opens the exact registered repository/branch and preserves an existing owner's edits.</summary>
    public async Task<OpenPullRequestResult> HandleAsync(GitBranchRequest request, CancellationToken cancellationToken)
    {
        if (!request.IsValid) throw new ArgumentException("Invalid Git clone URL or branch name.");
        GitRepositoryRegistration repository = _catalog.Find(request)
            ?? throw new InvalidOperationException("Register this clone URL in GitRepositories before opening its branch.");
        if (!GitBranchRequest.OriginMatches(repository.CloneUrl, request.CloneUrl))
            throw new InvalidOperationException("Repository registration does not match the requested Git URL.");
        string path = Path.GetFullPath(Path.Combine(repository.WorkspaceRoot, request.WorkspaceName));
        _safety.EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, path);
        await using IAsyncDisposable ownership = await _locks.AcquireAsync(path, cancellationToken);
        _safety.EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, path);
        await VerifyOriginAsync(repository, cancellationToken);
        GitBranchSession? previous = await _store.FindAsync(request, cancellationToken);
        bool exists = _directories.Exists(path);
        bool registered = await _git.IsRegisteredWorktreeAsync(repository.LocalPath, path, cancellationToken);
        if (previous is null && (exists || registered))
            throw new InvalidOperationException("The target is already occupied or registered. No existing worktree will be adopted.");
        GitBranchSession session = await _store.ReserveAsync(request, repository, path, cancellationToken);
        if (session.State == WorkspaceSessionState.RecoveryRequired)
            throw new InvalidOperationException("This branch workspace requires manual recovery. Its files and ownership are retained.");
        if (session.State == WorkspaceSessionState.Preparing && session.PinnedCommit is not null
            && (session.PinnedCommit.Length is not (40 or 64) || !session.PinnedCommit.All(Uri.IsHexDigit)))
        {
            await QuarantineAsync(session, cancellationToken);
        }
        bool reused;
        if (session.State == WorkspaceSessionState.Active)
        {
            if (!exists || !registered) await QuarantineAsync(session, cancellationToken);
            reused = true;
        }
        else if (exists && registered)
        {
            // A killed checkout is adopted only if it is still clean at the persisted head.
            if (session.PinnedCommit is null
                || !await _checkout.IsCleanCheckoutAsync(path, session.PinnedCommit, cancellationToken))
                await QuarantineAsync(session, cancellationToken);
            await _store.ActivateAsync(session.OperationId, cancellationToken);
            reused = true;
        }
        else if (!exists && !registered)
        {
            string? commit = session.PinnedCommit;
            if (commit is null)
            {
                await VerifyOriginAsync(repository, cancellationToken);
                commit = await _fetch.FetchBranchCommitAsync(repository.LocalPath, request.Branch,
                    session.OperationId, cancellationToken);
                if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
                    throw new InvalidOperationException("Git did not return a full immutable commit ID.");
                await _store.PinAsync(session.OperationId, commit, cancellationToken);
            }
            _safety.EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, path);
            _directories.EnsureRoot(repository.WorkspaceRoot);
            await _git.CreateWorktreeAsync(repository.LocalPath, path, commit, cancellationToken);
            _safety.EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, path);
            if (!_directories.Exists(path)
                || !await _git.IsRegisteredWorktreeAsync(repository.LocalPath, path, cancellationToken)
                || !string.Equals(await _git.GetWorktreeCommitAsync(path, cancellationToken), commit,
                    StringComparison.OrdinalIgnoreCase))
                await QuarantineAsync(session, cancellationToken);
            await _store.ActivateAsync(session.OperationId, cancellationToken);
            reused = false;
        }
        else
        {
            await QuarantineAsync(session, cancellationToken);
            throw new InvalidOperationException("Branch workspace recovery is required.");
        }
        await _editor.OpenAsync(path, cancellationToken);
        return new OpenPullRequestResult(path, reused);
    }

    private async Task VerifyOriginAsync(GitRepositoryRegistration repository, CancellationToken cancellationToken)
    {
        string origin = await _remotes.GetOriginUrlAsync(repository.LocalPath, cancellationToken);
        if (!GitBranchRequest.OriginMatches(origin, repository.CloneUrl))
            throw new InvalidOperationException("Configured clone's origin does not match the requested repository.");
    }

    private async Task QuarantineAsync(GitBranchSession session, CancellationToken cancellationToken)
    {
        await _store.RequireRecoveryAsync(session.OperationId, cancellationToken);
        throw new InvalidOperationException("Branch checkout cannot be safely reconciled. No files were removed or reset.");
    }
}
