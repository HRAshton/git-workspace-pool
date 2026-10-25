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

    /// <summary>Verifies an explicitly selected generic branch reservation under the same generation lease.</summary>
    public Task<WorkspaceSession> ReconcileSelectedAsync(
        WorkspaceReadySlotReservation selected,
        GitRepositoryRegistration trustedRepository,
        CancellationToken cancellationToken
    ) => ReconcileCoreAsync(
        selected.Lease.SlotId, selected, trustedRepository.RepositoryIdentity,
        trustedRepository.LocalPath, trustedRepository.WorkspaceRoot, cancellationToken
    );

    internal async Task<WorkspaceSession> ReconcileCoreAsync(
        Guid slotId,
        WorkspaceReadySlotReservation? selected,
        string repositoryKey,
        string sourcePath,
        string workspaceRoot,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSlot slot = await _slots.FindSlotAsync(slotId, cancellationToken)
            ?? throw new InvalidOperationException("Unknown reserved slot.");
        _safety.EnsureSafe(
            sourcePath,
            workspaceRoot,
            slot.WorkspacePath
        );
        string lockedPath = Path.GetFullPath(slot.WorkspacePath);
        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            lockedPath, cancellationToken
        );
        slot = await _slots.FindSlotAsync(slotId, cancellationToken)
            ?? throw new InvalidOperationException("Reserved slot disappeared under lock.");
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            slot.State != WorkspaceSlotState.Reserved
            || !Path.GetFullPath(slot.WorkspacePath).Equals(lockedPath, comparison)
        )
        {
            throw new InvalidOperationException(
                "Reserved slot changed while acquiring its lock; no data was changed."
            );
        }
        _safety.EnsureSafe(
            sourcePath,
            workspaceRoot,
            slot.WorkspacePath
        );

        WorkspaceReadySlotReservation reservation =
            await _allocator.FindReservedAsync(slotId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "No matching Preparing session and Reserved generation."
                );
        if (
            selected is not null
            && (
                reservation.Lease.LeaseId != selected.Lease.LeaseId
                || reservation.Lease.SessionId != selected.Lease.SessionId
                || reservation.Lease.Generation != selected.Lease.Generation
                || reservation.Session.OperationId != selected.Session.OperationId
                || reservation.Session.PullRequestNumber != selected.Session.PullRequestNumber
                || slot.Generation != selected.Lease.Generation
                || !string.Equals(
                    reservation.Session.PinnedCommitSha,
                    selected.Session.PinnedCommitSha,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            throw new InvalidOperationException(
                "Selected PR reservation or pinned commit changed; no data was changed."
            );
        }

        string? expected = reservation.Session.PinnedCommitSha;
        if (
            !slot.RepositoryKey.Equals(
                repositoryKey,
                StringComparison.OrdinalIgnoreCase
            )
            || expected is not { Length: 40 or 64 }
            || !expected.All(Uri.IsHexDigit)
            || !reservation.Session.RepositoryKey.Equals(
                slot.RepositoryKey, StringComparison.OrdinalIgnoreCase
            )
            || !Path.GetFullPath(slot.SourcePath).Equals(
                Path.GetFullPath(sourcePath),
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
