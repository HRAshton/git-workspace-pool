using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Runs bounded Git CLI commands without shell expansion.
/// </summary>
public sealed class GitCliWorkspaceGit : IWorkspaceGit, IGitRemoteReader
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
        string output = await RunAsync(
            repositoryPath,
            ["remote", "get-url", "origin"],
            cancellationToken
        );
        return output.Trim();
    }

    /// <inheritdoc/>
    public async Task FetchPullRequestAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
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
        await RunAsync(
            repositoryPath,
            ["worktree", "add", "--detach", workspacePath, commitSha],
            cancellationToken
        );
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
