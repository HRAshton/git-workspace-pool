using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Core.Application;

/// <summary>
/// Explicitly reopens a released PR's retained parked Git worktree in an editor.
/// Never moves files, reclaims the former slot or changes durable ownership.
/// </summary>
public sealed class OpenReleasedParkedWorkspaceService
{
    private readonly IWorkspaceSnapshotSessionStore _sessions;
    private readonly IWorkspaceOperationLock _locks;
    private readonly IWorkspaceParkingOperations _git;
    private readonly IEditorLauncher _editor;

    /// <summary>Composes trusted persisted ownership, a Git check and the editor port.</summary>
    public OpenReleasedParkedWorkspaceService(
        IWorkspaceSnapshotSessionStore sessions,
        IWorkspaceOperationLock locks,
        IWorkspaceParkingOperations git,
        IEditorLauncher editor
    )
    {
        _sessions = sessions;
        _locks = locks;
        _git = git;
        _editor = editor;
    }

    /// <summary>
    /// Opens only the exact retained parked worktree of an explicitly Released
    /// PR. The original slot can belong to someone else; it is never inspected,
    /// reclaimed, moved or mutated by this operation.
    /// </summary>
    public async Task<string> OpenAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        WorkspaceSession session = await _sessions.FindAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("The requested PR session does not exist.");

        // This is the same lock that serializes parking and release of the
        // original PR session. Recheck its state after acquiring the lock.
        await using IAsyncDisposable ownership = await _locks.AcquireAsync(
            session.WorkspacePath,
            cancellationToken
        );
        session = await _sessions.FindAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("The PR session disappeared.");

        if (session.State != WorkspaceSessionState.Released)
        {
            throw new InvalidOperationException(
                "Only an explicitly Released PR can be opened from retained parking."
            );
        }

        string original = Path.GetFullPath(session.WorkspacePath);
        string parked = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(original)
                ?? throw new InvalidOperationException("The original slot has no parent."),
            ".gwp-parked",
            session.OperationId.ToString("N")
        ));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            session.ParkingPath is null
            || !Path.GetFullPath(session.ParkingPath).Equals(parked, comparison)
            || session.ParkingCommitSha is not { Length: 40 or 64 }
            || !session.ParkingCommitSha.All(Uri.IsHexDigit)
        )
        {
            throw new InvalidOperationException(
                "The retained parked path or expected Git HEAD is not trustworthy."
            );
        }

        _git.EnsureReleasePathsSafe(session.SourcePath, original, parked);
        if (
            !_git.Exists(parked)
            || !await _git.IsRegisteredAsync(
                session.SourcePath,
                parked,
                cancellationToken
            )
        )
        {
            throw new InvalidOperationException(
                "The retained parked Git worktree is missing or unregistered. "
                    + "No editor was opened and no files were modified."
            );
        }

        // A user may commit more work directly in the retained parked tree.
        // Do not mistake legitimate HEAD advancement for a lost lease: the
        // immutable parked path and Git registration establish its identity.
        string currentHead = await _git.GetHeadAsync(parked, cancellationToken);
        if (currentHead.Length is not (40 or 64) || !currentHead.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "The retained parked worktree has an unverifiable Git HEAD."
            );
        }

        // The user explicitly chooses to edit this retained worktree in place.
        // Neither PR ownership nor its snapshots are changed.
        await _editor.OpenAsync(parked, cancellationToken);
        return parked;
    }
}
