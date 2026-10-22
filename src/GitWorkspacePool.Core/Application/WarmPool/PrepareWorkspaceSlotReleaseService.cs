using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Locks and revalidates a parked PR before durably preparing a slot release.
/// No public method transfers ownership, frees the slot or moves a worktree.
/// </summary>
public sealed class PrepareWorkspaceSlotReleaseService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceOperationLock _locks;
    private readonly AssessWorkspaceReleaseService _assessment;
    private readonly IWorkspaceSlotReleasePreparationJournal _journal;
    private readonly IWorkspaceSlotHandoffCheckpointSink? _checkpoints;

    /// <summary>Composes release validation and durable preparation behind narrow ports.</summary>
    public PrepareWorkspaceSlotReleaseService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceOperationLock locks,
        AssessWorkspaceReleaseService assessment,
        IWorkspaceSlotReleasePreparationJournal journal,
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
    /// Prepares the identified parked session; repeated calls return the same
    /// preparation while the corresponding generation is unchanged.
    /// </summary>
    public async Task<WorkspaceSlotReleasePreparation> PrepareAsync(
        Guid sessionId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSession session = await _sessions.FindAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("The PR session is not registered.");

        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );

        WorkspaceReleaseAssessment assessment = await _assessment.AssessUnderLockAsync(
            sessionId,
            cancellationToken
        );

        if (!assessment.CanBeginRelease
            || assessment.SlotId is null
            || assessment.Generation is null)
        {
            throw new InvalidOperationException(
                "Release preparation refused: " + assessment.Blocker
            );
        }

        WorkspaceSlotReleasePreparation prepared = await _journal.TryPrepareAsync(
            sessionId,
            assessment.SlotId.Value,
            assessment.Generation.Value,
            cancellationToken
        ) ?? throw new InvalidOperationException(
            "The slot ownership or generation changed before the release preparation committed."
        );

        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(
                WorkspaceSlotHandoffCheckpoint.ReleasePrepared,
                session.WorkspacePath,
                cancellationToken
            );
        }

        return prepared;
    }

    /// <summary>
    /// Cancels only a journalled preparation. This does not undo or perform any
    /// filesystem operation; the protected PR remains parked.
    /// </summary>
    public Task<bool> CancelAsync(
        Guid preparationId,
        CancellationToken cancellationToken
    ) => _journal.CancelAsync(preparationId, cancellationToken);
}
