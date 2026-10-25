using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>Small durable ownership registry. Callers hold the repository operation lock.</summary>
public interface IWarmWorkspaceRegistry
{
    /// <summary>Reads the repository inventory without inspecting or modifying directories.</summary>
    IReadOnlyList<WarmWorkspace> List(string repositoryKey);
    /// <summary>Durably reserves a new physical path before worktree creation.</summary>
    void InsertIntent(WarmWorkspace workspace);
    /// <summary>Atomically takes an Available donor and records its requested branch and target.</summary>
    void BeginTransition(string id, string branch, string target);
    /// <summary>Publishes ownership only after checkout and identity verification.</summary>
    void Complete(string id, WarmWorkspaceState state, string head, string? fingerprint);
    /// <summary>Retains uncertain ownership and bytes for manual recovery.</summary>
    void Quarantine(string id);
    /// <summary>Returns an explicitly verified unchanged assignment to Available.</summary>
    void Release(string id);
}

/// <summary>Read-only inspection and verified checkout of real worktrees.</summary>
public interface IWarmWorktreeGit
{
    /// <summary>Verifies the trusted clone root, paths and origin.</summary>
    Task VerifySourceAsync(GitRepositoryRegistration repository, CancellationToken cancellationToken);
    /// <summary>Pins the requested branch to a unique application ref without updating user refs.</summary>
    Task<string> FetchAsync(GitRepositoryRegistration repository, string branch, Guid operation, CancellationToken cancellationToken);
    /// <summary>Checks ownership, paths and bidirectional Git registration without changing HEAD.</summary>
    Task VerifyIdentityAsync(GitRepositoryRegistration repository, WarmWorkspace workspace, CancellationToken cancellationToken);
    /// <summary>Verifies recorded detached HEAD, clean status and supported non-conflicting paths.</summary>
    Task VerifyAvailableAsync(GitRepositoryRegistration repository, WarmWorkspace workspace, string target, CancellationToken cancellationToken);
    /// <summary>Counts actual changed tree paths, never ancestry distance.</summary>
    Task<int> ChangedPathsAsync(string source, string donor, string target, CancellationToken cancellationToken);
    /// <summary>Compares literal artifact-input tree blobs without hashing cache directories.</summary>
    Task<string?> FingerprintAsync(GitRepositoryRegistration repository, string commit, CancellationToken cancellationToken);
    /// <summary>Switches the already-validated directory to an immutable target without overwriting ignored files.</summary>
    Task TransitionAsync(string path, string target, CancellationToken cancellationToken);
    /// <summary>Creates a detached worktree only at a verified unoccupied managed path.</summary>
    Task CreateAsync(GitRepositoryRegistration repository, string path, string target, CancellationToken cancellationToken);
}
