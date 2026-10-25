using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Runs bounded Git CLI commands without shell expansion.
/// </summary>
public sealed class GitCliWorkspaceGit
{
    /// <inheritdoc/>
    public async Task<bool> IsRegisteredWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        string output = await RunAsync(
            repositoryPath,
            ["worktree", "list", "--porcelain", "-z"],
            cancellationToken
        );

        string expected = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(field => field.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(field => field["worktree ".Length..])
            .Any(path =>
                Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(expected, StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <inheritdoc/>
    public async Task<string> GetOriginUrlAsync(
        string repositoryPath,
        CancellationToken cancellationToken
    )
    {
        await ValidateSourceCloneAsync(repositoryPath, cancellationToken);
        await EnsureSafeFetchConfigurationAsync(repositoryPath, cancellationToken);
        string output = await RunAsync(
            repositoryPath,
            ["remote", "get-url", "origin"],
            cancellationToken
        );
        return output.Trim();
    }

    /// <summary>Requires the selected source to be an actual normal or bare clone root, not a discovered parent or linked checkout.</summary>
    public async Task ValidateSourceCloneAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        WorkspacePathSafety.EnsureNoLinksInAncestors(source);
        string gitDirectory = (await RunAsync(source, ["rev-parse", "--absolute-git-dir"], cancellationToken)).Trim();
        WorkspacePathSafety.EnsureNoLinksInAncestors(gitDirectory);
        string bare = (await RunAsync(source, ["rev-parse", "--is-bare-repository"], cancellationToken)).Trim();
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (bare == "true")
        {
            if (Path.GetFullPath(gitDirectory).Equals(source, comparison)) return;
        }
        else if (bare == "false")
        {
            string top = (await RunAsync(source, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
            if (Path.GetFullPath(top).Equals(source, comparison)
                && Path.GetFullPath(gitDirectory).Equals(Path.Combine(source, ".git"), comparison)
                && Directory.Exists(gitDirectory)) return;
        }
        throw new InvalidOperationException("Choose the root of a normal clone or a bare object store, not a subfolder or linked worktree.");
    }

    /// <inheritdoc/>
    public async Task<string> FetchBranchCommitAsync(
        string repositoryPath, string branch, Guid operationId, CancellationToken cancellationToken
    )
    {
        string targetRef = "refs/git-workspace-pool/branches/" + operationId.ToString("N");
        return await FetchBranchToRefAsync(repositoryPath, branch, targetRef, cancellationToken);
    }

    /// <summary>Creates a new bare source clone only at an absent, independently selected path.</summary>
    public async Task CloneBareAsync(string cloneUrl, string destination, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        _ = GitBranchRequest.NormalizeCloneUrl(cloneUrl);
        string path = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Choose a clone folder.");
        WorkspacePathSafety.EnsureNoLinksInAncestors(path);
        if (Directory.Exists(path) || File.Exists(path)) throw new InvalidOperationException("Clone destination is occupied; nothing was overwritten.");
        Directory.CreateDirectory(parent);
        await EnsureSafeFetchConfigurationAsync(parent, cancellationToken);
        await RunAsync(parent, ["-c", "http.followRedirects=false", "clone", "--progress", "--bare", "--no-hardlinks", "--", cloneUrl, path], cancellationToken, progress);
        if (!GitBranchRequest.OriginMatches(await GetOriginUrlAsync(path, cancellationToken), cloneUrl))
            throw new InvalidOperationException("New clone origin did not match; clone evidence retained.");
    }

    private async Task<string> FetchBranchToRefAsync(
        string repositoryPath, string branch, string targetRef, CancellationToken cancellationToken
    )
    {
        if (!GitBranchRequest.IsSafeBranch(branch)) throw new ArgumentException("Invalid Git branch name.");
        await EnsureSafeFetchConfigurationAsync(repositoryPath, cancellationToken);
        string sourceRef = "refs/heads/" + branch;
        await RunAsync(repositoryPath, ["check-ref-format", sourceRef], cancellationToken);
        await RunAsync(repositoryPath,
            ["-c", "http.followRedirects=false", "fetch", "--no-tags", "--no-recurse-submodules", "--no-auto-maintenance", "--no-prune", "--refmap=",
                "--no-write-fetch-head", "origin", $"+{sourceRef}:{targetRef}"], cancellationToken);
        return (await RunAsync(repositoryPath,
            ["rev-parse", "--verify", targetRef + "^{commit}"], cancellationToken)).Trim();
    }

    /// <inheritdoc/>
    public async Task<string> GetWorktreeCommitAsync(
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        string output = await RunAsync(
            workspacePath,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken
        );
        return output.Trim();
    }

    /// <inheritdoc/>
    public async Task CreateWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        string commitSha,
        CancellationToken cancellationToken
    )
    {
        // Worktree checkout can invoke filter drivers configured by the clone.
        // Fail closed before Git creates a checkout if any arbitrary driver exists.
        string filterConfig = await RunAsync(
            repositoryPath,
            ["config", "--list", "--name-only", "--null"],
            cancellationToken
        );
        GitProcessSafety.RejectExecutableFilters(filterConfig);

        await RunAsync(
            repositoryPath,
            ["worktree", "add", "--detach", workspacePath, commitSha],
            cancellationToken
        );
    }

    private static async Task EnsureSafeFetchConfigurationAsync(
        string repositoryPath,
        CancellationToken cancellationToken
    )
    {
        string names = await RunAsync(
            repositoryPath,
            ["config", "--list", "--null"],
            cancellationToken
        );
        GitProcessSafety.RejectUnsafeFetchConfiguration(names);
    }

    private static async Task<string> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null
    )
    {
        var result = await BoundedGitProcess.RunAsync(repositoryPath, arguments, 16 * 1024 * 1024, cancellationToken, progress);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git failed with exit code {result.ExitCode}: {result.Error.Trim()}");
        }
        return result.Output;
    }
}
