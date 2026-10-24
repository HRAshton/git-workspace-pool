namespace GitWorkspacePool.Core.Application;

/// <summary>Fetches only explicitly configured ordinary branch baselines, never checking out or running a project.</summary>
public interface IGitBaselineFetcher
{
    /// <summary>Updates the branch's dedicated baseline ref and returns its immutable commit.</summary>
    Task<string> FetchBaselineCommitAsync(string repositoryPath, string branch, CancellationToken cancellationToken);
}
