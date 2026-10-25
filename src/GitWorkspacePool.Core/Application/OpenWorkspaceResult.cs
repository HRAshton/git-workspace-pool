using System;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Result of opening an owned Git workspace.
/// </summary>
/// <param name="WorkspacePath">The local worktree directory.</param>
/// <param name="Reused">Whether an existing worktree was reopened unchanged.</param>
public sealed record OpenWorkspaceResult(string WorkspacePath, bool Reused);
