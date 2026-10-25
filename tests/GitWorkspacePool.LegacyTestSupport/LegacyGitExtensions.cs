using GitWorkspacePool.Infrastructure;

namespace GitWorkspacePool.Core.Application;

public interface ILegacyWorkspaceGit
{
    Task FetchPullRequestAsync(string repositoryPath, int number, CancellationToken cancellationToken);
    Task<string> GetPullRequestCommitAsync(string repositoryPath, int number, CancellationToken cancellationToken);
}

public static class LegacyGitExtensions
{
    public static async Task FetchPullRequestAsync(this IWorkspaceGit git, string repositoryPath, int number, CancellationToken token)
    {
        if (git is ILegacyWorkspaceGit fake) { await fake.FetchPullRequestAsync(repositoryPath, number, token); return; }
        if (git is not GitCliWorkspaceGit) throw new InvalidOperationException("Unsupported legacy test adapter.");
        _ = await new GitCliWorkspaceGit().GetOriginUrlAsync(repositoryPath, token);
        _ = await RunAsync(repositoryPath, ["fetch", "--no-tags", "--no-auto-maintenance", "--no-recurse-submodules", "--no-prune", "--refmap=",
            "origin", $"refs/pull/{number}/head:refs/git-workspace-pool/pr/{number}"], token);
    }

    public static Task<string> GetPullRequestCommitAsync(this IWorkspaceGit git, string repositoryPath, int number, CancellationToken token)
    {
        if (git is ILegacyWorkspaceGit fake) return fake.GetPullRequestCommitAsync(repositoryPath, number, token);
        if (git is not GitCliWorkspaceGit) throw new InvalidOperationException("Unsupported legacy test adapter.");
        return RunAsync(repositoryPath, ["rev-parse", "--verify", $"refs/git-workspace-pool/pr/{number}^{{commit}}"], token);
    }

    private static async Task<string> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var result = await BoundedGitProcess.RunAsync(directory, arguments, 16 * 1024 * 1024, token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Git failed with exit code {result.ExitCode}: {result.Error.Trim()}");
        return result.Output.Trim();
    }
}
