using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Explicit read-verify-commit recovery of a worktree already checked out
/// for a durable Ready-slot reservation. Never creates, cleans or removes files.
/// </summary>
public sealed class ReconcileExplicitReadySlotService
{
    private readonly IWorkspaceReadySlotAllocator _allocator;
    private readonly IWorkspaceSlotRegistry _slots;
    private readonly IWorkspaceReadySlotSafety _safety;
    private readonly IWorkspaceReadySlotCheckoutVerifier _checkout;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceOperationLock _locks;

    /// <summary>Configures locked, generation-checked recovery boundaries.</summary>
    public ReconcileExplicitReadySlotService(
        IWorkspaceReadySlotAllocator allocator,
        IWorkspaceSlotRegistry slots,
        IWorkspaceReadySlotSafety safety,
        IWorkspaceReadySlotCheckoutVerifier checkout,
        IWorkspaceGit git,
        IWorkspaceOperationLock locks
    )
    {
        _allocator = allocator;
        _slots = slots;
        _safety = safety;
        _checkout = checkout;
        _git = git;
        _locks = locks;
    }

    /// <summary>
    /// Activates only an exactly matching, clean, registered checkout that
    /// already exists. Missing, dirty, altered or unpinned worktrees retain
    /// their Reserved/Preparing ownership for manual recovery.
    /// </summary>
    public async Task<WorkspaceSession> ReconcileAsync(
        Guid slotId,
        RepositoryRegistration trustedRepository,
        CancellationToken cancellationToken,
        long? selectedGeneration = null,
        Guid? selectedOwnerId = null
    )
    {
        WorkspaceSlot slot = await _slots.FindSlotAsync(slotId, cancellationToken)
            ?? throw new InvalidOperationException("Unknown reserved slot.");
        _safety.EnsureSafe(
            trustedRepository.LocalPath,
            trustedRepository.WorkspaceRoot,
            slot.WorkspacePath
        );
        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            slot.WorkspacePath, cancellationToken
        );
        _safety.EnsureSafe(
            trustedRepository.LocalPath,
            trustedRepository.WorkspaceRoot,
            slot.WorkspacePath
        );

        WorkspaceReadySlotReservation reservation =
            await _allocator.FindReservedAsync(slotId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "No matching Preparing session and Reserved generation."
                );
        string? expected = reservation.Session.PinnedCommitSha;
        if (
            slot.State != WorkspaceSlotState.Reserved
            || slot.Generation != reservation.Lease.Generation
            || reservation.Lease.SlotId != slotId
            || reservation.Lease.SessionId != reservation.Session.OperationId
            || (selectedGeneration.HasValue
                && reservation.Lease.Generation != selectedGeneration.Value)
            || (selectedOwnerId.HasValue
                && reservation.Session.OperationId != selectedOwnerId.Value)
            || !slot.RepositoryKey.Equals(
                "github:" + trustedRepository.RepositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
            || expected is not { Length: 40 or 64 }
            || !expected.All(Uri.IsHexDigit)
            || !reservation.Session.RepositoryKey.Equals(
                slot.RepositoryKey, StringComparison.OrdinalIgnoreCase
            )
            || !Path.GetFullPath(slot.SourcePath).Equals(
                Path.GetFullPath(trustedRepository.LocalPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || !Path.GetFullPath(reservation.Session.WorkspacePath).Equals(
                Path.GetFullPath(slot.WorkspacePath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || !await _git.IsRegisteredWorktreeAsync(
                slot.SourcePath, slot.WorkspacePath, cancellationToken
            )
            || !await _checkout.IsCleanCheckoutAsync(
                slot.WorkspacePath, expected, cancellationToken
            )
        )
        {
            throw new InvalidOperationException(
                "Interrupted slot checkout cannot be proven clean and owned; no data was changed."
            );
        }

        if (!await _allocator.TryActivateAsync(
            reservation, expected, cancellationToken
        ))
        {
            throw new InvalidOperationException(
                "Reserved slot lease changed during recovery; no data was changed."
            );
        }

        return reservation.Session with { State = WorkspaceSessionState.Active };
    }
}
