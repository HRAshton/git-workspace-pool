using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Runs bounded Git CLI commands without shell expansion.
/// </summary>
public sealed class GitCliWorkspaceGit : IWorkspaceGit, IGitRemoteReader, IGitBranchFetcher
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
        if (!GitBranchRequest.IsSafeBranch(branch)) throw new ArgumentException("Invalid Git branch name.");
        await EnsureSafeFetchConfigurationAsync(repositoryPath, cancellationToken);
        string sourceRef = "refs/heads/" + branch;
        await RunAsync(repositoryPath, ["check-ref-format", sourceRef], cancellationToken);
        string targetRef = "refs/git-workspace-pool/branches/" + operationId.ToString("N");
        // Each durable operation has its own ref. Do not read shared FETCH_HEAD or a remote tracking branch.
        await RunAsync(repositoryPath,
            ["-c", "http.followRedirects=false", "fetch", "--no-tags", "--no-recurse-submodules",
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
        ProcessStartInfo startInfo = new("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        GitProcessSafety.Configure(startInfo);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch Git.");

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git failed with exit code {process.ExitCode}: {error.Trim()}"
                );
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
    }
}
