namespace GitWorkspacePool.Core.Domain;

/// <summary>An explicitly trusted Git repository, independent of its hosting provider.</summary>
public sealed record GitRepositoryRegistration(string CloneUrl, string LocalPath, string WorkspaceRoot)
{
    /// <summary>Gets explicitly configured baseline branch names. Empty disables background fetching.</summary>
    public IReadOnlyList<string> BaselineBranches { get; init; } = Array.Empty<string>();

    /// <summary>Gets optional bounded regexes matched against advertised remote heads; empty disables refresh.</summary>
    public IReadOnlyList<string>? BaselinePatterns { get; init; }

    /// <summary>Gets the same provider-independent identity used by branch sessions and released slots.</summary>
    public string RepositoryIdentity => GitBranchRequest.GetRepositoryIdentity(CloneUrl);
}
