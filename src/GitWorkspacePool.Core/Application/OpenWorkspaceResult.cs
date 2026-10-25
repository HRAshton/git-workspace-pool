namespace GitWorkspacePool.Core.Application;

/// <summary>The prepared, consistently assigned worktree to open in the editor.</summary>
/// <param name="WorkspacePath">The physical managed directory.</param>
public sealed record OpenWorkspaceResult(string WorkspacePath);
