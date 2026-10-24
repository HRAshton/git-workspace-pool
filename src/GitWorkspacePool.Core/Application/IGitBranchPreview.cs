namespace GitWorkspacePool.Core.Application;

/// <summary>Fetches a branch target for explicit preview without reserving or changing a worktree.</summary>
public interface IGitBranchPreview
{
    /// <summary>Uses a bounded, stable preview ref distinct from durable operation refs.</summary>
    Task<string> FetchBranchPreviewAsync(string repositoryPath, string branch, CancellationToken cancellationToken);
}
