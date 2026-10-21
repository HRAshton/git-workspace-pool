using System.Diagnostics;
using System.Text;
using GitWorkspacePool.Core.Application;

namespace GitWorkspacePool.Tray.Infrastructure;

internal sealed class GitCliWorkspaceGit : IWorkspaceGit
{
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

    public async Task FetchPullRequestAsync(
        string repositoryPath,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        await RunAsync(
            repositoryPath,
            ["fetch", "--no-tags", "origin", $"refs/pull/{pullRequestNumber}/head"],
            cancellationToken
        );
    }

    public async Task CreateWorktreeAsync(
        string repositoryPath,
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        await RunAsync(
            repositoryPath,
            ["worktree", "add", "--detach", workspacePath, "FETCH_HEAD"],
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
