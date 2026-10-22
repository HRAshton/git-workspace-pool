using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Finalizes an explicitly prepared parked-slot release. Retains the parked
/// worktree and snapshot history; cannot allocate, clean or delete files.
/// </summary>
public sealed class FinalizeWorkspaceSlotReleaseService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceOperationLock _locks;
    private readonly AssessWorkspaceReleaseService _assessment;
    private readonly IWorkspaceSlotReleaseCommitJournal _journal;
    private readonly IWorkspaceSlotHandoffCheckpointSink? _checkpoints;

    /// <summary>Composes locked Git preflight with an atomic journal commit.</summary>
    public FinalizeWorkspaceSlotReleaseService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceOperationLock locks,
        AssessWorkspaceReleaseService assessment,
        IWorkspaceSlotReleaseCommitJournal journal,
        IWorkspaceSlotHandoffCheckpointSink? checkpoints = null
    )
    {
        _sessions = sessions;
        _locks = locks;
        _assessment = assessment;
        _journal = journal;
        _checkpoints = checkpoints;
    }

    /// <summary>
    /// Finalizes a specific release preparation, and returns its existing
    /// result on a committed retry without changing any ownership.
    /// </summary>
    public async Task<WorkspaceSlotReleasePreparation> FinalizeAsync(
        Guid preparationId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSlotReleasePreparation preparation =
            await _journal.FindPreparationAsync(preparationId, cancellationToken)
                ?? throw new InvalidOperationException("Release preparation does not exist.");

        if (preparation.State == WorkspaceSlotReleasePreparationState.Committed)
        {
            return preparation;
        }

        if (preparation.State != WorkspaceSlotReleasePreparationState.Prepared)
        {
            throw new InvalidOperationException("A cancelled release cannot be finalized.");
        }

        WorkspaceSession session = await _sessions.FindAsync(
            preparation.SessionId,
            cancellationToken
        ) ?? throw new InvalidOperationException("The prepared session no longer exists.");

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );

        WorkspaceReleaseAssessment assessment = await _assessment.AssessUnderLockAsync(
            preparation.SessionId,
            cancellationToken
        );
        if (
            !assessment.CanBeginRelease
            || assessment.SlotId != preparation.SlotId
            || assessment.Generation != preparation.Generation
        )
        {
            throw new InvalidOperationException(
                "The protected parked owner or Git state changed before slot release."
            );
        }

        if (!await _journal.TryCommitPreparedAsync(
            preparationId,
            preparation.SlotId,
            preparation.SessionId,
            preparation.Generation,
            cancellationToken
        ))
        {
            throw new InvalidOperationException(
                "The prepared release no longer matches the durable slot lease."
            );
        }

        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.ReleaseCommitted,
                session.WorkspacePath,
                cancellationToken
            );
        }

        return preparation with { State = WorkspaceSlotReleasePreparationState.Committed };
    }
}
