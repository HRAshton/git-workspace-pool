using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Runs bounded Git CLI commands without shell expansion.
/// </summary>
public sealed class GitCliWorkspaceGit : IWorkspaceGit, IGitRemoteReader, IGitBranchFetcher, IGitBranchPreview, IGitBaselineFetcher
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
        await EnsureSafeFetchConfigurationAsync(repositoryPath, cancellationToken);
        string output = await RunAsync(
            repositoryPath,
            ["remote", "get-url", "origin"],
            cancellationToken
        );
        return output.Trim();
    }

    /// <inheritdoc/>
    public async Task<string> FetchBranchCommitAsync(
        string repositoryPath, string branch, Guid operationId, CancellationToken cancellationToken
    )
    {
        string targetRef = "refs/git-workspace-pool/branches/" + operationId.ToString("N");
        return await FetchBranchToRefAsync(repositoryPath, branch, targetRef, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> FetchBranchPreviewAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(branch)));
        return FetchBranchToRefAsync(repositoryPath, branch, "refs/git-workspace-pool/previews/" + identity, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string> FetchBaselineCommitAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(branch)));
        return FetchBranchToRefAsync(repositoryPath, branch, "refs/git-workspace-pool/baselines/" + identity, cancellationToken);
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
    public async Task FetchPullRequestAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        // Recheck immediately before fetch even if the caller validated origin
        // earlier. A changed local/conditional Git config must fail closed.
        await EnsureSafeFetchConfigurationAsync(repositoryPath, cancellationToken);
        await RunAsync(
            repositoryPath,
            [
                "fetch",
                "--no-tags",
                "--no-auto-maintenance",
                "--no-recurse-submodules",
                "--no-prune",
                "--refmap=",
                "origin",
                $"refs/pull/{pullRequestNumber}/head:refs/git-workspace-pool/pr/{pullRequestNumber}",
            ],
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<string> GetPullRequestCommitAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        string output = await RunAsync(
            repositoryPath,
            [
                "rev-parse",
                "--verify",
                $"refs/git-workspace-pool/pr/{pullRequestNumber}^{{commit}}",
            ],
            cancellationToken
        );
        return output.Trim();
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

    /// <summary>
    /// Moves a registered worktree through Git so its administrative metadata stays valid.
    /// </summary>
    public static async Task MoveWorktreeAsync(
        string repository,
        string source,
        string destination,
        CancellationToken cancellationToken
    )
    {
        await RunAsync(
            repository,
            ["worktree", "move", source, destination],
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
        CancellationToken cancellationToken
    )
    {
        var result = await BoundedGitProcess.RunAsync(repositoryPath, arguments, 16 * 1024 * 1024, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git failed with exit code {result.ExitCode}: {result.Error.Trim()}");
        }
        return result.Output;
    }
}
