namespace GitWorkspacePool.Core.Application;

/// <summary>Fetches an ordinary branch without switching an existing checkout.</summary>
public interface IGitBranchFetcher
{
    /// <summary>Fetches the branch and returns the immutable commit resolved from a private operation ref.</summary>
    Task<string> FetchBranchCommitAsync(
        string repositoryPath, string branch, Guid operationId, CancellationToken cancellationToken
    );
}
