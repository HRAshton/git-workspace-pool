using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Filesystem safety check for an explicit previously released slot.</summary>
public sealed class WorkspaceReadySlotSafety : IWorkspaceReadySlotSafety
{
    /// <inheritdoc/>
    public void EnsureSafe(string sourceClone, string workspaceRoot, string slotPath)
    {
        string source = Path.GetFullPath(sourceClone);
        string root = Path.GetFullPath(workspaceRoot);
        string slot = Path.GetFullPath(slotPath);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            !Path.GetDirectoryName(slot)!.Equals(root, comparison)
            || source.Equals(root, comparison)
        )
        {
            throw new InvalidOperationException(
                "A released slot must be an immediate child of its trusted workspace root."
            );
        }

        WorkspaceSnapshotPathPolicy.EnsureSeparated(source, root);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(source);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(root);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(slot);
    }
}
