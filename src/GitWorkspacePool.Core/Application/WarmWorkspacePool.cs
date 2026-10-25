using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Serializes selection, durable intent, physical checkout and publication per source clone.</summary>
public sealed class WarmWorkspacePool(
    IWarmWorkspaceRegistry registry, IWarmWorktreeGit git, IWorkspaceOperationLock locks)
{
    private const int ShortlistLimit = 8;

    /// <summary>Reopens an assignment unchanged or reuses a safe populated worktree in place.</summary>
    public async Task<WarmWorkspace> OpenAsync(GitRepositoryRegistration repository, string branch, CancellationToken cancellationToken)
    {
        if (!GitBranchRequest.IsSafeBranch(branch)) throw new ArgumentException("Invalid branch.");
        await using IAsyncDisposable held = await locks.AcquireAsync(repository.LocalPath, cancellationToken);
        await git.VerifySourceAsync(repository, cancellationToken);
        RecoverInterrupted(repository);
        IReadOnlyList<WarmWorkspace> entries = registry.List(repository.RepositoryIdentity);
        WarmWorkspace? owner = entries.SingleOrDefault(row => row.Branch == branch);
        if (owner is not null)
        {
            if (owner.State != WarmWorkspaceState.Assigned) throw new InvalidOperationException("This branch requires manual recovery. Its directory is retained.");
            try { await git.VerifyIdentityAsync(repository, owner, cancellationToken); }
            catch { registry.Quarantine(owner.Id); throw; }
            return owner;
        }

        string target = await git.FetchAsync(repository, branch, Guid.NewGuid(), cancellationToken);
        ValidateCommit(target);
        string? fingerprint = await git.FingerprintAsync(repository, target, cancellationToken);
        List<(WarmWorkspace Workspace, int Delta)> ranked = [];
        foreach (WarmWorkspace candidate in entries.Where(row => row.State == WarmWorkspaceState.Available))
        {
            try
            {
                if (repository.ArtifactInputs.Count > 0
                    && (fingerprint is null || candidate.ArtifactFingerprint != fingerprint)) continue;
                int delta = await git.ChangedPathsAsync(repository.LocalPath, candidate.Head, target, cancellationToken);
                ranked.Add((candidate, delta));
            }
            catch (InvalidOperationException) { /* Unknown cost is ineligible, never zero. */ }
        }
        foreach ((WarmWorkspace candidate, _) in ranked
            .OrderBy(item => item.Workspace.Head == target ? 0 : 1)
            .ThenBy(item => item.Delta).ThenBy(item => item.Workspace.Id, StringComparer.Ordinal).Take(ShortlistLimit))
        {
            try { await git.VerifyAvailableAsync(repository, candidate, target, cancellationToken); }
            catch (InvalidOperationException) { registry.Quarantine(candidate.Id); continue; }
            registry.BeginTransition(candidate.Id, branch, target);
            return await MutateAsync(repository, candidate with
            {
                State = WarmWorkspaceState.Transitioning, Branch = branch, Target = target,
            }, fingerprint, false, cancellationToken);
        }
        return await CreateAsync(repository, branch, target, fingerprint, false, cancellationToken);
    }

    /// <summary>Manually prepares a new available worktree, without building or restoring repository code.</summary>
    public async Task<WarmWorkspace> PrepareAsync(GitRepositoryRegistration repository, string branch, CancellationToken cancellationToken)
    {
        if (!GitBranchRequest.IsSafeBranch(branch)) throw new ArgumentException("Invalid branch.");
        await using IAsyncDisposable held = await locks.AcquireAsync(repository.LocalPath, cancellationToken);
        await git.VerifySourceAsync(repository, cancellationToken);
        RecoverInterrupted(repository);
        string target = await git.FetchAsync(repository, branch, Guid.NewGuid(), cancellationToken);
        ValidateCommit(target);
        return await CreateAsync(repository, null, target,
            await git.FingerprintAsync(repository, target, cancellationToken), true, cancellationToken);
    }

    /// <summary>Releases only an unchanged detached assignment. No reset, cleanup or move occurs.</summary>
    public async Task ReleaseAsync(GitRepositoryRegistration repository, string id, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable held = await locks.AcquireAsync(repository.LocalPath, cancellationToken);
        await git.VerifySourceAsync(repository, cancellationToken);
        RecoverInterrupted(repository);
        WarmWorkspace workspace = registry.List(repository.RepositoryIdentity).Single(row => row.Id == id);
        if (workspace.State != WarmWorkspaceState.Assigned) throw new InvalidOperationException("Only an Assigned workspace can be released.");
        await git.VerifyAvailableAsync(repository, workspace, workspace.Head, cancellationToken);
        registry.Release(id);
    }

    private void RecoverInterrupted(GitRepositoryRegistration repository)
    {
        foreach (WarmWorkspace row in registry.List(repository.RepositoryIdentity).Where(row => row.State == WarmWorkspaceState.Transitioning))
            registry.Quarantine(row.Id);
    }

    private async Task<WarmWorkspace> CreateAsync(GitRepositoryRegistration repository, string? branch,
        string target, string? fingerprint, bool prepare, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(repository.WorkspaceRoot, "warm-" + id));
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("New workspace path is occupied; no adoption is allowed.");
        WarmWorkspace intent = new(id, repository.RepositoryIdentity, System.IO.Path.GetFullPath(repository.LocalPath),
            path, WarmWorkspaceState.Transitioning, branch, target, target, null);
        registry.InsertIntent(intent);
        return await MutateAsync(repository, intent, fingerprint, true, cancellationToken, prepare);
    }

    private async Task<WarmWorkspace> MutateAsync(GitRepositoryRegistration repository, WarmWorkspace intent,
        string? fingerprint, bool fresh, CancellationToken cancellationToken, bool prepare = false)
    {
        try
        {
            if (fresh) await git.CreateAsync(repository, intent.Path, intent.Target!, cancellationToken);
            else if (intent.Head != intent.Target) await git.TransitionAsync(intent.Path, intent.Target!, cancellationToken);
            WarmWorkspace result = intent with { Head = intent.Target!, Target = null,
                State = prepare ? WarmWorkspaceState.Available : WarmWorkspaceState.Assigned, ArtifactFingerprint = fingerprint };
            await git.VerifyAvailableAsync(repository, result, result.Head, cancellationToken);
            registry.Complete(intent.Id, result.State, result.Head, fingerprint);
            return result;
        }
        catch
        {
            registry.Quarantine(intent.Id);
            throw;
        }
    }

    private static void ValidateCommit(string commit)
    {
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit)) throw new InvalidOperationException("Git did not return a full commit ID.");
    }
}
