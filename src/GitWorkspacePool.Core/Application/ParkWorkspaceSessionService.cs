using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Non-destructively parks and unparks a whole, exclusively owned Git worktree.
/// </summary>
public sealed class ParkWorkspaceSessionService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceParkingJournal _journal;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceParkingOperations _operations;
    private readonly IWorkspaceParkingCheckpointSink? _checkpoints;

    /// <summary>Initializes the parking workflow with narrow persistence and Git ports.</summary>
    public ParkWorkspaceSessionService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceParkingJournal journal,
        IWorkspaceOperationLock locks,
        IWorkspaceParkingOperations operations,
        IWorkspaceParkingCheckpointSink? checkpoints = null
    )
    {
        _sessions = sessions;
        _journal = journal;
        _locks = locks;
        _operations = operations;
        _checkpoints = checkpoints;
    }

    /// <summary>
    /// Relocates a worktree, or reconciles an interrupted relocation, without deleting files.
    /// </summary>
    public async Task<string> ParkAsync(Guid id, CancellationToken cancellationToken)
    {
        WorkspaceSession session = await LoadAsync(id, cancellationToken);
        await using IAsyncDisposable lease = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );
        session = await LoadAsync(id, cancellationToken);

        if (session.State is not (
            WorkspaceSessionState.Active
            or WorkspaceSessionState.Suspended
            or WorkspaceSessionState.Parking
        ))
        {
            throw new InvalidOperationException("Only active, suspended or interrupted parking sessions can be parked.");
        }

        string destination = ParkingDestination(session);
        if (session.State != WorkspaceSessionState.Parking)
        {
            if (
                !_operations.Exists(session.WorkspacePath)
                || !await _operations.IsRegisteredAsync(
                    session.SourcePath, session.WorkspacePath, cancellationToken
                )
                || _operations.Exists(destination)
                || await _operations.IsRegisteredAsync(
                    session.SourcePath, destination, cancellationToken
                )
            )
            {
                await QuarantineAsync(id, cancellationToken);
            }

            // Reject unsupported Git layouts before writing the durable intent.
            await _operations.EnsureMoveSupportedAsync(
                session.WorkspacePath, cancellationToken
            );
            _operations.PrepareParkingDirectory(session.WorkspacePath, destination);
            string head = await _operations.GetHeadAsync(session.WorkspacePath, cancellationToken);
            await _journal.BeginParkingAsync(id, destination, head, cancellationToken);
            await CheckpointAsync(WorkspaceParkingCheckpoint.ParkingIntentRecorded, cancellationToken);
            session = await LoadAsync(id, cancellationToken);
        }

        ValidateIntent(session, destination);
        _operations.PrepareParkingDirectory(session.WorkspacePath, destination);
        bool oldPresent = _operations.Exists(session.WorkspacePath);
        bool newPresent = _operations.Exists(destination);
        bool oldRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, session.WorkspacePath, cancellationToken
        );
        bool newRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, destination, cancellationToken
        );

        if (oldPresent && oldRegistered && !newPresent && !newRegistered)
        {
            await _operations.EnsureMoveSupportedAsync(
                session.WorkspacePath, cancellationToken
            );
            string head = await _operations.GetHeadAsync(session.WorkspacePath, cancellationToken);
            if (!head.Equals(session.ParkingCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                await QuarantineAsync(id, cancellationToken);
            }

            await _operations.MoveAsync(
                session.SourcePath, session.WorkspacePath, destination, cancellationToken
            );
            await CheckpointAsync(WorkspaceParkingCheckpoint.ParkedWorktreeMoved, cancellationToken);
        }
        else if (!(!oldPresent && !oldRegistered && newPresent && newRegistered))
        {
            await QuarantineAsync(id, cancellationToken);
        }

        if (
            _operations.Exists(session.WorkspacePath)
            || await _operations.IsRegisteredAsync(
                session.SourcePath, session.WorkspacePath, cancellationToken
            )
            || !_operations.Exists(destination)
            || !await _operations.IsRegisteredAsync(
                session.SourcePath, destination, cancellationToken
            )
            || !string.Equals(
                await _operations.GetHeadAsync(destination, cancellationToken),
                session.ParkingCommitSha,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            await QuarantineAsync(id, cancellationToken);
        }

        await _journal.CompleteParkingAsync(id, cancellationToken);
        return destination;
    }

    /// <summary>
    /// Returns the same preserved worktree to its reserved original path.
    /// </summary>
    public async Task<string> UnparkAsync(Guid id, CancellationToken cancellationToken)
    {
        WorkspaceSession session = await LoadAsync(id, cancellationToken);
        await using IAsyncDisposable lease = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );
        session = await LoadAsync(id, cancellationToken);
        if (session.State is not (WorkspaceSessionState.Parked or WorkspaceSessionState.Unparking))
        {
            throw new InvalidOperationException("Only parked or interrupted return sessions can be unparked.");
        }

        string destination = ParkingDestination(session);
        ValidateIntent(session, destination);

        bool originalPresent = _operations.Exists(session.WorkspacePath);
        bool parkedPresent = _operations.Exists(destination);
        bool originalRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, session.WorkspacePath, cancellationToken
        );
        bool parkedRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, destination, cancellationToken
        );

        if (session.State == WorkspaceSessionState.Parked)
        {
            if (originalPresent || originalRegistered)
            {
                throw new InvalidOperationException(
                    "Original workspace path is occupied; parked data was not modified."
                );
            }

            if (!parkedPresent || !parkedRegistered)
            {
                await QuarantineAsync(id, cancellationToken);
            }

            // The parked worktree may be edited independently. Reject newly
            // added submodule links before recording the return-move intent.
            await _operations.EnsureMoveSupportedAsync(destination, cancellationToken);
            await _journal.BeginUnparkingAsync(id, cancellationToken);
            await CheckpointAsync(WorkspaceParkingCheckpoint.UnparkingIntentRecorded, cancellationToken);
        }

        session = await LoadAsync(id, cancellationToken);
        originalPresent = _operations.Exists(session.WorkspacePath);
        parkedPresent = _operations.Exists(destination);
        originalRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, session.WorkspacePath, cancellationToken
        );
        parkedRegistered = await _operations.IsRegisteredAsync(
            session.SourcePath, destination, cancellationToken
        );

        string? expectedReturnHead = !originalPresent && !originalRegistered
            && parkedPresent && parkedRegistered
                ? await _operations.GetHeadAsync(destination, cancellationToken)
                : null;

        if (!originalPresent && !originalRegistered && parkedPresent && parkedRegistered)
        {
            await _operations.EnsureMoveSupportedAsync(destination, cancellationToken);
            await _operations.MoveAsync(
                session.SourcePath, destination, session.WorkspacePath, cancellationToken
            );
            await CheckpointAsync(WorkspaceParkingCheckpoint.UnparkedWorktreeMoved, cancellationToken);
        }
        else if (!(originalPresent && originalRegistered && !parkedPresent && !parkedRegistered))
        {
            await QuarantineAsync(id, cancellationToken);
        }

        if (
            !_operations.Exists(session.WorkspacePath)
            || !await _operations.IsRegisteredAsync(
                session.SourcePath, session.WorkspacePath, cancellationToken
            )
            || _operations.Exists(destination)
            || await _operations.IsRegisteredAsync(
                session.SourcePath, destination, cancellationToken
            )
            || (expectedReturnHead is not null
                && !string.Equals(
                    await _operations.GetHeadAsync(session.WorkspacePath, cancellationToken),
                    expectedReturnHead,
                    StringComparison.OrdinalIgnoreCase
                ))
        )
        {
            await QuarantineAsync(id, cancellationToken);
        }

        await _journal.CompleteUnparkingAsync(id, cancellationToken);
        return session.WorkspacePath;
    }

    private async Task<WorkspaceSession> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _sessions.FindAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Workspace session does not exist.");

    private static string ParkingDestination(WorkspaceSession session) =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(session.WorkspacePath)
                ?? throw new InvalidOperationException("Workspace has no parent."),
            ".gwp-parked",
            session.OperationId.ToString("N")
        ));

    private static void ValidateIntent(WorkspaceSession session, string destination)
    {
        if (
            session.ParkingPath is null
            || !Path.GetFullPath(session.ParkingPath).Equals(
                destination,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || session.ParkingCommitSha is not { Length: 40 or 64 }
            || !session.ParkingCommitSha.All(Uri.IsHexDigit)
        )
        {
            throw new InvalidOperationException(
                "Parking intent does not match the owned session and requires inspection."
            );
        }
    }

    private async ValueTask CheckpointAsync(
        WorkspaceParkingCheckpoint checkpoint,
        CancellationToken cancellationToken
    )
    {
        if (_checkpoints is not null)
        {
            await _checkpoints.AfterAsync(checkpoint, cancellationToken);
        }
    }

    private async Task QuarantineAsync(Guid id, CancellationToken cancellationToken)
    {
        await _journal.RequireRecoveryAsync(id, cancellationToken);
        throw new InvalidOperationException(
            "Parking paths or Git registration disagree; files remain for manual recovery."
        );
    }
}
