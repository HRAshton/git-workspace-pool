namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Read-only verification of a newly created linked worktree before recovery
/// can promote an interrupted Reserved generation to Protected.
/// </summary>
public interface IWorkspaceReadySlotCheckoutVerifier
{
    /// <summary>Checks that Git HEAD matches and all Git status entries are clean.</summary>
    Task<bool> IsCleanCheckoutAsync(
        string path,
        string expectedHead,
        CancellationToken cancellationToken
    );
}
