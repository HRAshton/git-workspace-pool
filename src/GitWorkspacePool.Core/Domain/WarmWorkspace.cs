namespace GitWorkspacePool.Core.Domain;

/// <summary>Durable ownership of a physical, populated worktree.</summary>
public sealed record WarmWorkspace(
    string Id, string RepositoryKey, string SourcePath, string Path, WarmWorkspaceState State,
    string? Branch, string Head, string? Target, string? ArtifactFingerprint);

/// <summary>Only Available worktrees may be reassigned.</summary>
public enum WarmWorkspaceState
{
    /// <summary>A clean populated worktree explicitly eligible for reuse.</summary>
    Available,
    /// <summary>Owned by a branch request and protected from reassignment.</summary>
    Assigned,
    /// <summary>Durable intent exists; Git mutation may be incomplete.</summary>
    Transitioning,
    /// <summary>Uncertain ownership retained for explicit manual recovery.</summary>
    Quarantined,
}
