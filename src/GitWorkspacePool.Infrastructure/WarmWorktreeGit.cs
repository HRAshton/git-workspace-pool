using System.Security.Cryptography;
using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Bounded, no-shell Git adapter for safe physical worktree reuse.</summary>
public sealed class WarmWorktreeGit : IWarmWorktreeGit
{
    private readonly GitCliWorkspaceGit _git = new();
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc/>
    public async Task VerifySourceAsync(GitRepositoryRegistration repository, CancellationToken cancellationToken)
    {
        new ManagedWorkspacePaths().EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, Path.Combine(repository.WorkspaceRoot, "preflight"));
        string origin = await _git.GetOriginUrlAsync(repository.LocalPath, cancellationToken);
        if (!GitBranchRequest.OriginMatches(origin, repository.CloneUrl)) throw new InvalidOperationException("Source origin differs from the trusted registration.");
    }

    /// <inheritdoc/>
    public Task<string> FetchAsync(GitRepositoryRegistration repository, string branch, Guid operation, CancellationToken cancellationToken) =>
        _git.FetchBranchCommitAsync(repository.LocalPath, branch, operation, cancellationToken);

    /// <inheritdoc/>
    public Task VerifyIdentityAsync(GitRepositoryRegistration repository, WarmWorkspace workspace, CancellationToken cancellationToken) =>
        VerifyIdentityCoreAsync(repository, workspace, false, cancellationToken);

    private async Task VerifyIdentityCoreAsync(GitRepositoryRegistration repository, WarmWorkspace workspace, bool requireUnlocked, CancellationToken cancellationToken)
    {
        if (workspace.RepositoryKey != repository.RepositoryIdentity
            || !Path.GetFullPath(workspace.SourcePath).Equals(Path.GetFullPath(repository.LocalPath), PathComparison))
            throw new InvalidOperationException("Workspace source ownership does not match registration.");
        new ManagedWorkspacePaths().EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, workspace.Path);
        if (!Directory.Exists(workspace.Path)) throw new InvalidOperationException("Workspace missing. Manual recovery required.");
        string inventory = await Run(repository.LocalPath, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        string[][] registrations = inventory.Split("\0\0", StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Any(field => field.StartsWith("worktree ", StringComparison.Ordinal)
                && Path.GetFullPath(field[9..]).Equals(Path.GetFullPath(workspace.Path), PathComparison))).ToArray();
        if (registrations.Length != 1 || registrations[0].Any(field => field == "bare"
            || (requireUnlocked && field.StartsWith("locked", StringComparison.Ordinal)) || field.StartsWith("prunable", StringComparison.Ordinal)))
            throw new InvalidOperationException("Worktree registration is missing, locked, prunable or ambiguous. Manual recovery required.");
        string top = (await Run(workspace.Path, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
        if (!Path.GetFullPath(top).Equals(Path.GetFullPath(workspace.Path), PathComparison))
            throw new InvalidOperationException("Git working directory redirects outside the owned path.");
        string sourceCommon = (await Run(repository.LocalPath, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Trim();
        string common = (await Run(workspace.Path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken)).Trim();
        string admin = (await Run(workspace.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).Trim();
        WorkspacePathSafety.EnsureNoLinksInAncestors(sourceCommon);
        WorkspacePathSafety.EnsureNoLinksInAncestors(admin);
        WorkspacePathSafety.EnsureNoLinksInAncestors(Path.Combine(workspace.Path, ".git"));
        if (!Path.GetFullPath(common).Equals(Path.GetFullPath(sourceCommon), PathComparison)
            || !Path.GetDirectoryName(Path.GetFullPath(admin))!.Equals(Path.GetFullPath(Path.Combine(sourceCommon, "worktrees")), PathComparison)
            || !File.Exists(Path.Combine(workspace.Path, ".git")))
            throw new InvalidOperationException("Worktree object store or administrative identity is inconsistent.");
        string backLink = (await File.ReadAllTextAsync(Path.Combine(admin, "gitdir"), cancellationToken)).Trim();
        if (!Path.GetFullPath(backLink).Equals(Path.Combine(Path.GetFullPath(workspace.Path), ".git"), PathComparison))
            throw new InvalidOperationException("Worktree registration backlink is inconsistent.");
    }

    /// <inheritdoc/>
    public async Task VerifyAvailableAsync(GitRepositoryRegistration repository, WarmWorkspace workspace, string target, CancellationToken cancellationToken)
    {
        ValidateCommit(workspace.Head); ValidateCommit(target);
        await VerifyIdentityCoreAsync(repository, workspace, true, cancellationToken);
        await VerifySupportedAsync(workspace.Path, target, cancellationToken);
        if (!string.Equals(await _git.GetWorktreeCommitAsync(workspace.Path, cancellationToken), workspace.Head, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HEAD changed since assignment/preparation. Local commits are retained; release/reuse refused.");
        string symbolic = await Run(workspace.Path, ["rev-parse", "--symbolic-full-name", "HEAD"], cancellationToken);
        if (symbolic.Trim() != "HEAD") throw new InvalidOperationException("A local branch owns this worktree. Release/reuse requires the original detached HEAD.");
        string admin = (await Run(workspace.Path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).Trim();
        foreach (string marker in new[] { "index.lock", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer", "BISECT_LOG" })
            if (File.Exists(Path.Combine(admin, marker)) || Directory.Exists(Path.Combine(admin, marker)))
                throw new InvalidOperationException("A Git operation is in progress. Worktree retained.");
        string flags = await Run(workspace.Path, ["ls-files", "-v", "-z"], cancellationToken);
        if (flags.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("H ", StringComparison.Ordinal)))
            throw new InvalidOperationException("Unsupported index flags or conflicts; cannot prove tracked cleanliness.");
        string status = await Run(workspace.Path, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none"], cancellationToken);
        if (status.Length != 0) throw new InvalidOperationException("Staged, unstaged, conflicted or untracked content prevents release/reuse.");
        _ = await TargetPaths(workspace.Path, workspace.Head, cancellationToken);
        string currentTree = await Run(workspace.Path, ["ls-tree", "-r", "--name-only", "-z", workspace.Head, "--"], cancellationToken);
        HashSet<string> tracked = new(currentTree.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        // Only changed paths can be written/deleted by checkout. Tree-wide metadata checks above
        // remain cheap; avoid one filesystem ancestor walk per unchanged tracked file.
        string changedPaths = await Run(workspace.Path,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", workspace.Head, target, "--"], cancellationToken);
        HashSet<string> inspected = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string path in changedPaths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string full = WorkspacePathSafety.Resolve(workspace.Path, path);
            WorkspacePathSafety.EnsureNoLinksInAncestors(full, inspected);
            string? ancestor = Path.GetDirectoryName(full);
            while (ancestor is not null && !ancestor.Equals(Path.GetFullPath(workspace.Path), PathComparison))
            {
                string relativeAncestor = Path.GetRelativePath(workspace.Path, ancestor).Replace('\\', '/');
                if (File.Exists(ancestor) && !tracked.Contains(relativeAncestor))
                    throw new InvalidOperationException("An ignored file blocks an incoming directory.");
                ancestor = Path.GetDirectoryName(ancestor);
            }
            if (!tracked.Contains(path) && (File.Exists(full) || Directory.Exists(full)))
                throw new InvalidOperationException("An ignored artifact collides with incoming tracked content; nothing was overwritten.");
        }
    }

    /// <inheritdoc/>
    public async Task<int> ChangedPathsAsync(string source, string donor, string target, CancellationToken cancellationToken)
    {
        ValidateCommit(donor); ValidateCommit(target);
        return (await Run(source, ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", donor, target, "--"], cancellationToken))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <inheritdoc/>
    public async Task<string?> FingerprintAsync(GitRepositoryRegistration repository, string commit, CancellationToken cancellationToken)
    {
        ValidateCommit(commit);
        if (repository.ArtifactInputs.Count == 0) return null;
        StringBuilder inputs = new();
        foreach (string path in repository.ArtifactInputs.Order(StringComparer.Ordinal))
        {
            _ = WorkspacePathSafety.Resolve(repository.WorkspaceRoot, path);
            string output = await Run(repository.LocalPath, ["--literal-pathspecs", "ls-tree", "-z", commit, "--", path], cancellationToken);
            if (output.Length == 0 || !output.StartsWith("100", StringComparison.Ordinal)) return null;
            inputs.Append(path).Append('\0').Append(output);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs.ToString())));
    }

    /// <inheritdoc/>
    public async Task TransitionAsync(string path, string target, CancellationToken cancellationToken)
    {
        ValidateCommit(target);
        await Run(path, ["-c", "submodule.recurse=false", "checkout", "--detach", "--no-overwrite-ignore", target, "--"], cancellationToken);
    }

    /// <inheritdoc/>
    public async Task CreateAsync(GitRepositoryRegistration repository, string path, string target, CancellationToken cancellationToken)
    {
        ValidateCommit(target);
        new ManagedWorkspacePaths().EnsureSafe(repository.LocalPath, repository.WorkspaceRoot, path);
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("Checkout destination occupied. No adoption allowed.");
        await VerifySupportedAsync(repository.LocalPath, target, cancellationToken);
        Directory.CreateDirectory(repository.WorkspaceRoot);
        await _git.CreateWorktreeAsync(repository.LocalPath, path, target, cancellationToken);
    }

    private static async Task VerifySupportedAsync(string path, string target, CancellationToken cancellationToken)
    {
        string config = await Run(path, ["config", "--list", "--null"], cancellationToken);
        GitProcessSafety.RejectUnsafeFetchConfiguration(config);
        GitProcessSafety.RejectExecutableFilters(await Run(path, ["config", "--list", "--name-only", "--null"], cancellationToken));
        foreach (string entry in config.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = entry.Split('\n')[0];
            if (name.Equals("core.worktree", StringComparison.OrdinalIgnoreCase)
                || name.Equals("core.sparsecheckout", StringComparison.OrdinalIgnoreCase)
                || name.Equals("extensions.worktreeconfig", StringComparison.OrdinalIgnoreCase)
                || name.Equals("extensions.partialclone", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".promisor", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sparse, partial or worktree-specific configuration is unsupported for managed checkout.");
        }
        _ = await TargetPaths(path, target, cancellationToken);
    }

    private static async Task<string[]> TargetPaths(string path, string target, CancellationToken cancellationToken)
    {
        string[] entries = (await Run(path, ["ls-tree", "-r", "-z", target, "--"], cancellationToken)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<string> paths = [];
        foreach (string entry in entries)
        {
            if (!entry.StartsWith("100644 blob ", StringComparison.Ordinal) && !entry.StartsWith("100755 blob ", StringComparison.Ordinal))
                throw new InvalidOperationException("Submodules and tracked symlinks are unsupported for managed checkout.");
            int separator = entry.IndexOf('\t');
            if (separator < 0) throw new InvalidOperationException("Malformed Git tree.");
            string relative = entry[(separator + 1)..];
            _ = WorkspacePathSafety.NormalizeRelative(relative);
            paths.Add(relative);
        }
        return paths.ToArray();
    }

    private static void ValidateCommit(string commit)
    {
        if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit)) throw new InvalidOperationException("Expected an immutable full commit ID.");
    }

    private static async Task<string> Run(string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await BoundedGitProcess.RunAsync(path, arguments, 16 * 1024 * 1024, timeout.Token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Git refused operation ({result.ExitCode}): {result.Error.Trim()}");
        return result.Output;
    }
}
