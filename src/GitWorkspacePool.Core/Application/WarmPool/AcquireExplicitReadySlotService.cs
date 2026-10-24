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
    private readonly IWorkspaceSlotHandoffCheckpointSink? _checkpoints;

    /// <summary>Composes generation CAS with trusted Git and filesystem operations.</summary>
    public AcquireExplicitReadySlotService(
        IWorkspaceReadySlotAllocator allocator,
        IWorkspaceSlotRegistry slots,
        IWorkspaceReadySlotSafety safety,
        IWorkspaceGit git,
        IWorkspaceDirectories files,
        IWorkspaceSessionJournal sessions,
        IWorkspaceOperationLock locks,
        IWorkspaceSlotHandoffCheckpointSink? checkpoints = null
    )
    {
        _allocator = allocator;
        _slots = slots;
        _safety = safety;
        _git = git;
        _files = files;
        _sessions = sessions;
        _locks = locks;
        _checkpoints = checkpoints;
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
        if (!request.RepositoryKey.Equals(trustedRepository.RepositoryKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The request does not belong to the trusted repository.");
        }
        return await AcquireCoreAsync(
            request.Provider + ":" + request.RepositoryKey, request.Number,
            trustedRepository.LocalPath, trustedRepository.WorkspaceRoot,
            slotId, expectedGeneration,
            async (_, token) =>
            {
                await _git.FetchPullRequestAsync(trustedRepository.LocalPath, request.Number, token);
                return await _git.GetPullRequestCommitAsync(trustedRepository.LocalPath, request.Number, token);
            }, cancellationToken
        );
    }

    /// <summary>Acquires a released slot for an ordinary Git branch using the same generation and ownership rules.</summary>
    public Task<OpenPullRequestResult> AcquireBranchAsync(
        GitBranchRequest request,
        GitRepositoryRegistration trustedRepository,
        int referenceNumber,
        Guid slotId,
        long expectedGeneration,
        IGitBranchFetcher fetcher,
        CancellationToken cancellationToken,
        string? pinnedPreviewCommit = null
    )
    {
        if (!request.IsValid || !GitBranchRequest.OriginMatches(trustedRepository.CloneUrl, request.CloneUrl))
        {
            throw new InvalidOperationException("The branch does not belong to the trusted repository.");
        }
        if (pinnedPreviewCommit is not null
            && (pinnedPreviewCommit.Length is not (40 or 64) || !pinnedPreviewCommit.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("Preview commit must be a full immutable Git ID.");
        }
        return AcquireCoreAsync(
            request.RepositoryIdentity, referenceNumber,
            trustedRepository.LocalPath, trustedRepository.WorkspaceRoot,
            slotId, expectedGeneration,
            (operationId, token) => pinnedPreviewCommit is not null
                ? Task.FromResult(pinnedPreviewCommit)
                : fetcher.FetchBranchCommitAsync(trustedRepository.LocalPath, request.Branch, operationId, token), cancellationToken
        );
    }

    private async Task<OpenPullRequestResult> AcquireCoreAsync(
        string repositoryKey,
        int referenceNumber,
        string sourcePath,
        string workspaceRoot,
        Guid slotId,
        long expectedGeneration,
        Func<Guid, CancellationToken, Task<string>> fetchCommit,
        CancellationToken cancellationToken
    )
    {
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
                Path.GetFullPath(sourcePath), comparison
            )
        )
        {
            throw new InvalidOperationException("The released slot has stale or untrusted identity.");
        }

        _safety.EnsureSafe(
            sourcePath,
            workspaceRoot,
            slot.WorkspacePath
        );

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            slot.WorkspacePath,
            cancellationToken
        );

        // Recheck under the OS lock before reserving, then commit ownership
        // before running Git. A foreign file or registration is never removed.
        _safety.EnsureSafe(
            sourcePath,
            workspaceRoot,
            slot.WorkspacePath
        );
        if (
            _files.Exists(slot.WorkspacePath)
            || await _git.IsRegisteredWorktreeAsync(
                sourcePath,
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
            referenceNumber,
            sourcePath,
            cancellationToken
        ) ?? throw new InvalidOperationException(
            "The Ready slot was leased or session ownership changed; no checkout occurred."
        );

        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.SlotReserved,
                slot.WorkspacePath,
                cancellationToken
            );
        }

        // No rollback or deletion after this durable reservation.
        // Any Git failure leaves Preparing/Reserved state and evidence.
        string commit = await fetchCommit(reservation.Session.OperationId, cancellationToken);
        await _sessions.PinCommitAsync(
            reservation.Session.OperationId,
            commit,
            cancellationToken
        );
        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.CommitPinned,
                slot.WorkspacePath,
                cancellationToken
            );
        }

        _safety.EnsureSafe(
            sourcePath,
            workspaceRoot,
            slot.WorkspacePath
        );
        if (
            _files.Exists(slot.WorkspacePath)
            || await _git.IsRegisteredWorktreeAsync(
                sourcePath,
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
            sourcePath,
            slot.WorkspacePath,
            commit,
            cancellationToken
        );
        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.WorktreeCreated,
                slot.WorkspacePath,
                cancellationToken
            );
        }

        if (
            !_files.Exists(slot.WorkspacePath)
            || !await _git.IsRegisteredWorktreeAsync(
                sourcePath,
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

        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.SlotActivated,
                slot.WorkspacePath,
                cancellationToken
            );
        }

        // A reused location has a new Git checkout, not recycled build files.
        return new OpenPullRequestResult(slot.WorkspacePath, false);
    }
}
