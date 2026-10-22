using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Opt-in, narrowly scoped cold checkout in an explicitly released empty slot.
/// This is not automatic warm-pool ranking, recycling or artifact preservation.
/// </summary>
public sealed class AcquireExplicitReadySlotService
{
    private readonly IWorkspaceReadySlotAllocator _allocator;
    private readonly IWorkspaceSlotRegistry _slots;
    private readonly IWorkspaceReadySlotSafety _safety;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceDirectories _files;
    private readonly IWorkspaceSessionJournal _sessions;
    private readonly IWorkspaceOperationLock _locks;

    /// <summary>Composes generation CAS with trusted Git and filesystem operations.</summary>
    public AcquireExplicitReadySlotService(
        IWorkspaceReadySlotAllocator allocator,
        IWorkspaceSlotRegistry slots,
        IWorkspaceReadySlotSafety safety,
        IWorkspaceGit git,
        IWorkspaceDirectories files,
        IWorkspaceSessionJournal sessions,
        IWorkspaceOperationLock locks
    )
    {
        _allocator = allocator;
        _slots = slots;
        _safety = safety;
        _git = git;
        _files = files;
        _sessions = sessions;
        _locks = locks;
    }

    /// <summary>
    /// Acquires the exact requested Ready slot only. All interruptions leave
    /// the durable Preparing/Reserved lease for explicit recovery, never cleanup.
    /// </summary>
    public async Task<OpenPullRequestResult> AcquireAsync(
        PullRequestReference request,
        RepositoryRegistration trustedRepository,
        Guid slotId,
        long expectedGeneration,
        CancellationToken cancellationToken
    )
    {
        string repositoryKey = request.Provider + ":" + request.RepositoryKey;
        if (!request.RepositoryKey.Equals(
            trustedRepository.RepositoryKey,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            throw new InvalidOperationException("The PR does not belong to the trusted repository.");
        }

        WorkspaceSlot slot = await _slots.FindSlotAsync(slotId, cancellationToken)
            ?? throw new InvalidOperationException("The requested slot does not exist.");
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            slot.State != WorkspaceSlotState.Ready
            || slot.Generation != expectedGeneration
            || !slot.RepositoryKey.Equals(repositoryKey, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(slot.SourcePath).Equals(
                Path.GetFullPath(trustedRepository.LocalPath), comparison
            )
        )
        {
            throw new InvalidOperationException("The released slot has stale or untrusted identity.");
        }

        _safety.EnsureSafe(
            trustedRepository.LocalPath,
            trustedRepository.WorkspaceRoot,
            slot.WorkspacePath
        );

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            slot.WorkspacePath,
            cancellationToken
        );

        // Recheck under the OS lock before reserving, then commit ownership
        // before running Git. A foreign file or registration is never removed.
        _safety.EnsureSafe(
            trustedRepository.LocalPath,
            trustedRepository.WorkspaceRoot,
            slot.WorkspacePath
        );
        if (
            _files.Exists(slot.WorkspacePath)
            || await _git.IsRegisteredWorktreeAsync(
                trustedRepository.LocalPath,
                slot.WorkspacePath,
                cancellationToken
            )
        )
        {
            throw new InvalidOperationException(
                "Released slot has an occupied or registered destination; no files were changed."
            );
        }

        WorkspaceReadySlotReservation reservation = await _allocator.TryReserveAsync(
            slotId,
            expectedGeneration,
            repositoryKey,
            request.Number,
            trustedRepository.LocalPath,
            cancellationToken
        ) ?? throw new InvalidOperationException(
            "The Ready slot was leased or session ownership changed; no checkout occurred."
        );

        // No rollback or deletion after this durable reservation.
        // Any Git failure leaves Preparing/Reserved state and evidence.
        await _git.FetchPullRequestAsync(
            trustedRepository.LocalPath,
            request.Number,
            cancellationToken
        );
        string commit = await _git.GetPullRequestCommitAsync(
            trustedRepository.LocalPath,
            request.Number,
            cancellationToken
        );
        await _sessions.PinCommitAsync(
            reservation.Session.OperationId,
            commit,
            cancellationToken
        );

        _safety.EnsureSafe(
            trustedRepository.LocalPath,
            trustedRepository.WorkspaceRoot,
            slot.WorkspacePath
        );
        if (
            _files.Exists(slot.WorkspacePath)
            || await _git.IsRegisteredWorktreeAsync(
                trustedRepository.LocalPath,
                slot.WorkspacePath,
                cancellationToken
            )
        )
        {
            throw new InvalidOperationException(
                "The reserved slot became occupied before checkout; ownership remains reserved."
            );
        }

        await _git.CreateWorktreeAsync(
            trustedRepository.LocalPath,
            slot.WorkspacePath,
            commit,
            cancellationToken
        );

        if (
            !_files.Exists(slot.WorkspacePath)
            || !await _git.IsRegisteredWorktreeAsync(
                trustedRepository.LocalPath,
                slot.WorkspacePath,
                cancellationToken
            )
            || !string.Equals(
                await _git.GetWorktreeCommitAsync(
                    slot.WorkspacePath,
                    cancellationToken
                ),
                commit,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                "The reserved slot checkout could not be verified; data was retained."
            );
        }

        if (!await _allocator.TryActivateAsync(reservation, commit, cancellationToken))
        {
            throw new InvalidOperationException(
                "The verified checkout did not match its durable slot generation."
            );
        }

        // A reused location has a new Git checkout, not recycled build files.
        return new OpenPullRequestResult(slot.WorkspacePath, false);
    }
}
