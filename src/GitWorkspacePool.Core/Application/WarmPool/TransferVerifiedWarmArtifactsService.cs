using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Opt-in preservation of npm dependencies between an explicitly Released
/// parked owner and a second Active PR that now owns that exact former slot.
/// </summary>
public sealed class TransferVerifiedWarmArtifactsService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceWarmArtifactCopier _copier;

    /// <summary>Composes durable ownership, shared OS locks and fail-closed copy.</summary>
    public TransferVerifiedWarmArtifactsService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceOperationLock locks,
        IWorkspaceGit git,
        IWorkspaceWarmArtifactCopier copier
    )
    {
        _sessions = sessions;
        _locks = locks;
        _git = git;
        _copier = copier;
    }

    /// <summary>Never creates a worktree or changes Git index, HEAD or ownership.</summary>
    public async Task<WarmArtifactTransferReceipt> TransferAsync(
        Guid releasedSessionId,
        Guid activeSessionId,
        CancellationToken cancellationToken
    )
    {
        if (releasedSessionId == activeSessionId)
        {
            throw new InvalidOperationException("Warm artifact transfer requires two PR owners.");
        }

        WorkspaceSession released = await _sessions.FindAsync(
            releasedSessionId, cancellationToken
        ) ?? throw new InvalidOperationException("The released PR owner is missing.");
        WorkspaceSession active = await _sessions.FindAsync(
            activeSessionId, cancellationToken
        ) ?? throw new InvalidOperationException("The destination PR owner is missing.");

        // Lock in stable path order. Do not trust stale UI status after acquiring locks.
        string[] paths = [released.ParkingPath ?? released.WorkspacePath, active.WorkspacePath];
        Array.Sort(paths, OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (Path.GetFullPath(paths[0]).Equals(
            Path.GetFullPath(paths[1]), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        ))
        {
            throw new InvalidOperationException("Source and destination must be distinct.");
        }

        await using IAsyncDisposable first = await _locks.AcquireAsync(paths[0], cancellationToken);
        await using IAsyncDisposable second = await _locks.AcquireAsync(paths[1], cancellationToken);

        released = await _sessions.FindAsync(releasedSessionId, cancellationToken)
            ?? throw new InvalidOperationException("The released PR owner disappeared.");
        active = await _sessions.FindAsync(activeSessionId, cancellationToken)
            ?? throw new InvalidOperationException("The target PR owner disappeared.");

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (
            released.State != WorkspaceSessionState.Released
            || active.State != WorkspaceSessionState.Active
            || released.ParkingPath is null
            || released.ParkingCommitSha is null
            || active.PinnedCommitSha is null
            || !released.RepositoryKey.Equals(active.RepositoryKey, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(released.SourcePath).Equals(
                Path.GetFullPath(active.SourcePath), cmp
            )
            || !Path.GetFullPath(released.WorkspacePath).Equals(
                Path.GetFullPath(active.WorkspacePath), cmp
            )
            || !await _git.IsRegisteredWorktreeAsync(
                released.SourcePath, released.ParkingPath, cancellationToken
            )
            || !await _git.IsRegisteredWorktreeAsync(
                active.SourcePath, active.WorkspacePath, cancellationToken
            )
            || !string.Equals(
                await _git.GetWorktreeCommitAsync(released.ParkingPath, cancellationToken),
                released.ParkingCommitSha, StringComparison.OrdinalIgnoreCase
            )
            || !string.Equals(
                await _git.GetWorktreeCommitAsync(active.WorkspacePath, cancellationToken),
                active.PinnedCommitSha, StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                "The released donor and active destination cannot be proven to own "
                    + "the same slot and registered Git identities."
            );
        }

        return await _copier.CopyAsync(released, active, cancellationToken);
    }
}
