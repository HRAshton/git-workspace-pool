namespace GitWorkspacePool.Core.Domain;

/// <summary>Durable ownership of one repository/branch workspace. PR sessions remain independent.</summary>
public sealed record GitBranchSession(
    Guid OperationId, string CloneUrl, string Branch, string SourcePath, string WorkspacePath,
    WorkspaceSessionState State, string? PinnedCommit
);
