namespace GitWorkspacePool.Core.Application;

/// <summary>Reads bounded advertised heads without fetching or modifying worktrees.</summary>
public interface IGitRemoteBranchReader
{
    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(string repositoryPath, CancellationToken cancellationToken);
}
