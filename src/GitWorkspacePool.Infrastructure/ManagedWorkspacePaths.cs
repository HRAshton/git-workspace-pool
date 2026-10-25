
namespace GitWorkspacePool.Infrastructure;

/// <summary>Filesystem safety check for an managed worktree.</summary>
public sealed class ManagedWorkspacePaths
{
    /// <inheritdoc/>
    public void EnsureSafe(string sourceClone, string workspaceRoot, string workspacePath)
    {
        string source = Path.GetFullPath(sourceClone);
        string root = Path.GetFullPath(workspaceRoot);
        string workspace = Path.GetFullPath(workspacePath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            !Path.GetDirectoryName(workspace)!.Equals(root, comparison)
            || source.Equals(root, comparison)
        )
        {
            throw new InvalidOperationException(
                "A managed worktree must be an immediate child of its trusted workspace root."
            );
        }

        WorkspacePathSafety.EnsureSeparated(source, root);
        WorkspacePathSafety.EnsureNoLinksInAncestors(source);
        WorkspacePathSafety.EnsureNoLinksInAncestors(root);
        WorkspacePathSafety.EnsureNoLinksInAncestors(workspace);
    }
}
