namespace GitWorkspacePool.Core.Domain;

/// <summary>An explicitly trusted Git repository, independent of its hosting provider.</summary>
public sealed record GitRepositoryRegistration(string CloneUrl, string LocalPath, string WorkspaceRoot)
{
    /// <summary>Gets explicitly configured baseline branch names. Used only for manual preparation.</summary>
    public IReadOnlyList<string> BaselineBranches { get; init; } = Array.Empty<string>();

    /// <summary>Gets optional literal repository-relative artifact input paths. Missing or changed inputs prevent reuse.</summary>
    public IReadOnlyList<string> ArtifactInputs { get; init; } = Array.Empty<string>();

    /// <summary>Gets the provider-independent identity shared by trusted registrations and workspace ownership.</summary>
    public string RepositoryIdentity => GitBranchRequest.GetRepositoryIdentity(CloneUrl);
}
