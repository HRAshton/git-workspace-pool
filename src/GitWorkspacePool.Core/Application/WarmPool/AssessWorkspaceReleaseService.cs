using GitWorkspacePool.Core.Domain;
using GitWorkspacePool.Core.Domain.WarmPool;

namespace GitWorkspacePool.Core.Application.WarmPool;

/// <summary>
/// Read-only, fail-closed eligibility check before a future explicit slot release.
/// Does not change sessions, leases, files, Git registrations or slot state.
/// </summary>
public sealed class AssessWorkspaceReleaseService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceSlotRegistry _slots;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceParkingOperations _git;

    /// <summary>Initializes the release guard with existing ownership ports.</summary>
    public AssessWorkspaceReleaseService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceSlotRegistry slots,
        IWorkspaceOperationLock locks,
        IWorkspaceParkingOperations git
    )
    {
        _sessions = sessions;
        _slots = slots;
        _locks = locks;
        _git = git;
    }

    /// <summary>
    /// Checks a currently parked worktree and its protected lease while holding the
    /// session's OS lock. The next mutating operation must recheck every condition.
    /// </summary>
    public async Task<WorkspaceReleaseAssessment> AssessAsync(
        Guid sessionId,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSession? session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Result(sessionId, null, WorkspaceReleaseBlocker.UnknownSession);
        }

        await using IAsyncDisposable lease = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );
        session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Result(sessionId, null, WorkspaceReleaseBlocker.UnknownSession);
        }

        WorkspaceSlot? slot = await _slots.FindHeldSlotAsync(sessionId, cancellationToken);
        if (slot is null)
        {
            return Result(sessionId, null, WorkspaceReleaseBlocker.MissingLease);
        }

        if (slot.State != WorkspaceSlotState.Protected || slot.Generation < 0)
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.SlotNotProtected);
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            !slot.RepositoryKey.Equals(session.RepositoryKey, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(slot.SourcePath).Equals(
                Path.GetFullPath(session.SourcePath), comparison
            )
            || !Path.GetFullPath(slot.WorkspacePath).Equals(
                Path.GetFullPath(session.WorkspacePath), comparison
            )
        )
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.OwnershipMismatch);
        }

        if (session.State != WorkspaceSessionState.Parked)
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.SessionNotParked);
        }

        string reservedParking = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(session.WorkspacePath))
                ?? throw new InvalidOperationException("Worktree has no parent directory."),
            ".gwp-parked",
            session.OperationId.ToString("N")
        ));

        if (
            session.ParkingPath is null
            || !Path.GetFullPath(session.ParkingPath).Equals(reservedParking, comparison)
            || session.ParkingCommitSha is not { Length: 40 or 64 }
            || !session.ParkingCommitSha.All(Uri.IsHexDigit)
        )
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.InvalidParkingPath);
        }

        try
        {
            _git.EnsureReleasePathsSafe(
                session.SourcePath,
                session.WorkspacePath,
                reservedParking
            );
        }
        catch (InvalidOperationException)
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.UnsafePath);
        }
        catch (IOException)
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.Unverifiable);
        }
        catch (UnauthorizedAccessException)
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.Unverifiable);
        }

        try
        {
            if (
                _git.Exists(session.WorkspacePath)
                || await _git.IsRegisteredAsync(
                    session.SourcePath,
                    session.WorkspacePath,
                    cancellationToken
                )
            )
            {
                return Result(sessionId, slot, WorkspaceReleaseBlocker.OriginalPathOccupied);
            }

            if (
                !_git.Exists(reservedParking)
                || !await _git.IsRegisteredAsync(
                    session.SourcePath,
                    reservedParking,
                    cancellationToken
                )
            )
            {
                return Result(sessionId, slot, WorkspaceReleaseBlocker.ParkedWorktreeMissing);
            }

            string parkedHead = await _git.GetHeadAsync(reservedParking, cancellationToken);
            if (
                parkedHead.Length is not (40 or 64)
                || !parkedHead.All(Uri.IsHexDigit)
            )
            {
                return Result(sessionId, slot, WorkspaceReleaseBlocker.Unverifiable);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException
        )
        {
            return Result(sessionId, slot, WorkspaceReleaseBlocker.Unverifiable);
        }

        return Result(sessionId, slot, WorkspaceReleaseBlocker.None);
    }

    private static WorkspaceReleaseAssessment Result(
        Guid sessionId,
        WorkspaceSlot? slot,
        WorkspaceReleaseBlocker blocker
    ) => new(sessionId, slot?.SlotId, slot?.Generation, blocker);
}
