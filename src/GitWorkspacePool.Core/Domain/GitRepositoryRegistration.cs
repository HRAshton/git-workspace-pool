namespace GitWorkspacePool.Core.Domain;

/// <summary>An explicitly trusted HTTPS Git repository, independent of its hosting provider.</summary>
public sealed record GitRepositoryRegistration(string CloneUrl, string LocalPath, string WorkspaceRoot)
{
    /// <summary>Gets the same provider-independent identity used by branch sessions and released slots.</summary>
    public string RepositoryIdentity => GitBranchRequest.GetRepositoryIdentity(CloneUrl);
}
