using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure;

/// <summary>Checks Git index, HEAD and porcelain status without modifying files.</summary>
public sealed class VerifiedReadySlotCheckout : IWorkspaceReadySlotCheckoutVerifier
{
    /// <inheritdoc/>
    public async Task<bool> IsCleanCheckoutAsync(
        string path,
        string expectedHead,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        WorktreeSnapshotScan scan = await new GitWorktreeSnapshotScanner().InspectAsync(
            path, cancellationToken
        );
        return scan.Status.Length == 0
            && scan.Head.Equals(expectedHead, StringComparison.OrdinalIgnoreCase);
    }
}
