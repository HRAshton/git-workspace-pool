using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Resolves explicitly configured Git repositories without using a provider API.</summary>
public interface IGitBranchCatalog
{
    /// <summary>Finds the single trusted registration for the requested clone URL.</summary>
    GitRepositoryRegistration? Find(GitBranchRequest request);
}
