using System.Text.Json;
using GitWorkspacePool.Core.Application;
using GitWorkspacePool.Core.Domain.WarmPool;
using GitWorkspacePool.Core.Application.WarmPool;
using GitWorkspacePool.Core.Domain;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Discovers interrupted snapshot exports and isolated restores without mutation.
/// </summary>
public sealed class WorkspaceRecoveryInspector : IWorkspaceRecoveryInspector
{
    private const int MaxEntriesPerRoot = 10000;

    private readonly string _snapshotRoot;
    private readonly string _recoveryRoot;
    private readonly IWorkspaceRecoveryJournal _journal;
    private readonly IWorkspaceGit _git;
    private readonly IWorkspaceSlotOwnershipAuditor? _slotAudit;
    private readonly StringComparer _paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Initializes an inspector against app-owned storage, the journal, and Git.
    /// </summary>
    public WorkspaceRecoveryInspector(
        string snapshotRoot,
        string recoveryRoot,
        IWorkspaceRecoveryJournal journal,
        IWorkspaceGit git,
        IWorkspaceSlotOwnershipAuditor? slotAudit = null
    )
    {
        _snapshotRoot = Path.GetFullPath(snapshotRoot);
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
        _journal = journal;
        _git = git;
        _slotAudit = slotAudit;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkspaceRecoveryFinding>> ScanAsync(
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureSeparated(_snapshotRoot, _recoveryRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_snapshotRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_recoveryRoot);

        IReadOnlyList<WorkspaceSnapshotRecord> receipts =
            await _journal.ListRecoverySnapshotsAsync(cancellationToken);
        Dictionary<string, WorkspaceSnapshotRecord> byPath = new(_paths);
        Dictionary<Guid, WorkspaceSnapshotRecord> byId = [];

        foreach (WorkspaceSnapshotRecord record in receipts)
        {
            string path = Path.GetFullPath(record.Receipt.Folder);
            if (!IsUnder(_snapshotRoot, path))
            {
                throw new InvalidOperationException(
                    "A snapshot journal entry points outside the configured snapshot root."
                );
            }

            byPath.Add(path, record);
            byId.Add(record.Receipt.SnapshotId, record);
        }

        IReadOnlyList<WorkspaceRestoreOperation> restores =
            await _journal.ListRecoveryRestoresAsync(cancellationToken);
        Dictionary<string, WorkspaceRestoreOperation> restorePaths = new(_paths);

        foreach (WorkspaceRestoreOperation operation in restores)
        {
            string target = Path.GetFullPath(operation.Path);

            if (!IsUnder(_recoveryRoot, target))
            {
                throw new InvalidOperationException(
                    "A restore journal entry points outside the configured recovery root."
                );
            }

            restorePaths.Add(target, operation);
        }

        List<WorkspaceRecoveryFinding> findings = [];
        HashSet<string> seen = new(_paths);
        HashSet<string> seenRestores = new(_paths);

        if (Directory.Exists(_snapshotRoot))
        {
            foreach (string ownerPath in BoundedEntries(_snapshotRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string ownerName = Path.GetFileName(ownerPath);

                if (IsReparse(ownerPath))
                {
                    Add(findings, WorkspaceRecoveryKind.UnsafeReparsePoint, ownerPath, null);
                    continue;
                }

                if (!Directory.Exists(ownerPath) || !Guid.TryParseExact(ownerName, "N", out Guid ownerId))
                {
                    Add(findings, WorkspaceRecoveryKind.UnknownEntry, ownerPath, null);
                    continue;
                }

                foreach (string entry in BoundedEntries(ownerPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(entry);

                    if (IsReparse(entry))
                    {
                        Add(findings, WorkspaceRecoveryKind.UnsafeReparsePoint, entry, null);
                        continue;
                    }

                    if (!Directory.Exists(entry))
                    {
                        Add(findings, WorkspaceRecoveryKind.UnknownEntry, entry, null);
                        continue;
                    }

                    if (name.StartsWith(".pending-", StringComparison.Ordinal)
                        && Guid.TryParseExact(name[".pending-".Length..], "N", out Guid pendingId))
                    {
                        Add(findings, WorkspaceRecoveryKind.IncompleteSnapshot, entry, pendingId);
                        continue;
                    }

                    if (name.StartsWith("snapshot-", StringComparison.Ordinal)
                        && Guid.TryParseExact(name["snapshot-".Length..], "N", out Guid publishedId))
                    {
                        if (byPath.TryGetValue(Path.GetFullPath(entry), out WorkspaceSnapshotRecord? record)
                            && record.Receipt.SnapshotId == publishedId
                            && record.Session.OperationId == ownerId)
                        {
                            seen.Add(Path.GetFullPath(entry));
                        }
                        else
                        {
                            Add(findings, WorkspaceRecoveryKind.UnjournalledSnapshot, entry, publishedId);
                        }

                        continue;
                    }

                    Add(findings, WorkspaceRecoveryKind.UnknownEntry, entry, null);
                }
            }
        }

        foreach (KeyValuePair<string, WorkspaceSnapshotRecord> known in byPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Contains(known.Key))
            {
                Add(
                    findings,
                    WorkspaceRecoveryKind.MissingSnapshot,
                    known.Key,
                    known.Value.Receipt.SnapshotId
                );
            }
        }

        if (Directory.Exists(_recoveryRoot))
        {
            foreach (string path in BoundedEntries(_recoveryRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReparse(path))
                {
                    Add(findings, WorkspaceRecoveryKind.UnsafeReparsePoint, path, null);
                    continue;
                }

                if (!Directory.Exists(path))
                {
                    Add(findings, WorkspaceRecoveryKind.UnknownEntry, path, null);
                    continue;
                }

                string name = Path.GetFileName(path);
                if (
                    !name.StartsWith("restore-", StringComparison.Ordinal)
                    || name.Length != "restore-".Length + 32 + 1 + 32
                    || name["restore-".Length + 32] != '-'
                    || !Guid.TryParseExact(name.Substring("restore-".Length, 32), "N", out Guid snapshotId)
                    || !Guid.TryParseExact(name[("restore-".Length + 33)..], "N", out _)
                    || !byId.TryGetValue(snapshotId, out WorkspaceSnapshotRecord? record)
                )
                {
                    Add(findings, WorkspaceRecoveryKind.UnknownEntry, path, null);
                    continue;
                }

                string fullPath = Path.GetFullPath(path);
                bool journalled = restorePaths.TryGetValue(
                    fullPath,
                    out WorkspaceRestoreOperation? operation
                );

                if (
                    journalled
                    && (operation!.SnapshotId != snapshotId
                        || !fullPath.EndsWith(
                            operation.RestoreId.ToString("N"),
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal
                        ))
                )
                {
                    Add(findings, WorkspaceRecoveryKind.UnknownEntry, path, snapshotId);
                    continue;
                }

                if (journalled)
                {
                    seenRestores.Add(fullPath);
                }

                try
                {
                    bool registered = await _git.IsRegisteredWorktreeAsync(
                        record.Session.SourcePath,
                        path,
                        cancellationToken
                    );
                    WorkspaceRecoveryKind kind;
                    if (!registered)
                    {
                        kind = WorkspaceRecoveryKind.UnregisteredRecoveryDirectory;
                    }
                    else if (!journalled)
                    {
                        kind = WorkspaceRecoveryKind.UnmanagedRecoveryWorktree;
                    }
                    else if (operation!.State == WorkspaceRestoreState.Preparing)
                    {
                        kind = WorkspaceRecoveryKind.InterruptedRestore;
                    }
                    else
                    {
                        // Journal verification is a historical claim. A later Git
                        // checkout in the recovery tree must not be presented as
                        // the original verified snapshot on subsequent inspections.
                        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(
                            Path.Combine(path, ".git")
                        );
                        string expectedHead = await ReadSnapshotHeadAsync(
                            record,
                            cancellationToken
                        );
                        string actualHead = await _git.GetWorktreeCommitAsync(
                            path,
                            cancellationToken
                        );
                        kind = string.Equals(
                            actualHead,
                            expectedHead,
                            StringComparison.OrdinalIgnoreCase
                        )
                            ? WorkspaceRecoveryKind.VerifiedRecoveryWorktree
                            : WorkspaceRecoveryKind.DivergedRecoveryWorktree;
                    }

                    Add(findings, kind, path, snapshotId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException or UnauthorizedAccessException
                )
                {
                    Add(
                        findings,
                        WorkspaceRecoveryKind.UnverifiableRecoveryDirectory,
                        path,
                        snapshotId
                    );
                }
            }
        }

        foreach (KeyValuePair<string, WorkspaceRestoreOperation> restore in restorePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seenRestores.Contains(restore.Key))
            {
                Add(
                    findings,
                    WorkspaceRecoveryKind.MissingRecoveryWorktree,
                    restore.Key,
                    restore.Value.SnapshotId
                );
            }
        }

        IReadOnlyList<WorkspaceSession> parking =
            await _journal.ListRecoveryParkingSessionsAsync(cancellationToken);

        foreach (WorkspaceSession session in parking)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string location = session.ParkingPath ?? session.WorkspacePath;

            if (session.State == WorkspaceSessionState.Parking)
            {
                Add(findings, WorkspaceRecoveryKind.InterruptedParking, location, null);
                continue;
            }

            if (session.State == WorkspaceSessionState.Unparking)
            {
                Add(findings, WorkspaceRecoveryKind.InterruptedUnparking, location, null);
                continue;
            }

            if (session.ParkingPath is null)
            {
                Add(findings, WorkspaceRecoveryKind.MissingParkedWorktree, location, null);
                continue;
            }

            try
            {
                if (
                    !Directory.Exists(location)
                    || (File.GetAttributes(location) & FileAttributes.ReparsePoint) != 0
                    || !await _git.IsRegisteredWorktreeAsync(
                        session.SourcePath,
                        location,
                        cancellationToken
                    )
                )
                {
                    Add(findings, WorkspaceRecoveryKind.MissingParkedWorktree, location, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException
            )
            {
                Add(findings, WorkspaceRecoveryKind.UnverifiableParkedWorktree, location, null);
            }
        }

        IReadOnlyList<WorkspacePreparedRelease> releasePreparations =
            await _journal.ListPendingReleasePreparationsAsync(cancellationToken);

        foreach (WorkspacePreparedRelease preparation in releasePreparations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new WorkspaceRecoveryFinding(
                WorkspaceRecoveryKind.PendingSlotReleasePreparation,
                preparation.WorkspacePath,
                null,
                "Release preparation " + preparation.PreparationId.ToString("N")
                    + " holds slot " + preparation.SlotId.ToString("N")
                    + " generation " + preparation.Generation
                    + ". Cancel explicitly before unparking; do not delete files."
            ));
        }

        if (_slotAudit is not null)
        {
            IReadOnlyList<WorkspaceSlotIntegrityFinding> slotIssues =
                await _slotAudit.AuditAsync(cancellationToken);

            foreach (WorkspaceSlotIntegrityFinding issue in slotIssues)
            {
                findings.Add(new WorkspaceRecoveryFinding(
                    WorkspaceRecoveryKind.SlotOwnershipInconsistent,
                    issue.WorkspacePath,
                    null,
                    "Slot " + issue.SlotId.ToString("N") + ": " + issue.Issue
                ));
            }
        }

        return findings;
    }

    private static async Task<string> ReadSnapshotHeadAsync(
        WorkspaceSnapshotRecord record,
        CancellationToken cancellationToken
    )
    {
        string manifestPath = Path.Combine(record.Receipt.Folder, "manifest.json");
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(manifestPath);

        WorkspaceSnapshotManifest manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            manifest = JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(json)
                ?? throw new InvalidOperationException("Snapshot manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Snapshot manifest cannot be verified during recovery inventory.",
                exception
            );
        }

        if (
            manifest.Version != WorkspaceSnapshotManifest.CurrentVersion
            || manifest.SnapshotId != record.Receipt.SnapshotId
            || manifest.OperationId != record.Session.OperationId
            || string.IsNullOrWhiteSpace(manifest.HeadCommitSha)
            || (manifest.HeadCommitSha.Length is not (40 or 64))
            || !manifest.HeadCommitSha.All(Uri.IsHexDigit)
        )
        {
            throw new InvalidOperationException(
                "Snapshot identity cannot be verified during recovery inventory."
            );
        }

        return manifest.HeadCommitSha;
    }

    private static void Add(
        List<WorkspaceRecoveryFinding> findings,
        WorkspaceRecoveryKind kind,
        string path,
        Guid? snapshotId
    )
    {
        findings.Add(new WorkspaceRecoveryFinding(
            kind,
            path,
            snapshotId,
            kind switch
            {
                WorkspaceRecoveryKind.IncompleteSnapshot => "Snapshot export was interrupted.",
                WorkspaceRecoveryKind.UnjournalledSnapshot => "Published snapshot lacks an SQLite receipt.",
                WorkspaceRecoveryKind.MissingSnapshot => "A journalled snapshot is missing or inaccessible.",
                WorkspaceRecoveryKind.UnmanagedRecoveryWorktree => "An isolated Git recovery worktree exists outside managed sessions.",
                WorkspaceRecoveryKind.UnregisteredRecoveryDirectory => "Recovery directory is not registered as a Git worktree.",
                WorkspaceRecoveryKind.UnsafeReparsePoint => "Symlink or junction; not traversed.",
                WorkspaceRecoveryKind.UnverifiableRecoveryDirectory => "Git identity or snapshot evidence cannot be confirmed; inspect manually.",
                WorkspaceRecoveryKind.InterruptedRestore => "Restore was interrupted before complete verification.",
                WorkspaceRecoveryKind.VerifiedRecoveryWorktree => "Previously verified isolated recovery worktree; Git HEAD matches (payloads were not rehashed during inventory).",
                WorkspaceRecoveryKind.DivergedRecoveryWorktree => "Previously verified recovery worktree has a different Git HEAD; preserve it for manual inspection.",
                WorkspaceRecoveryKind.MissingRecoveryWorktree => "Restore intent exists but the target directory is missing.",
                WorkspaceRecoveryKind.InterruptedParking => "A parking intent was not committed; retry Park for safe reconciliation.",
                WorkspaceRecoveryKind.InterruptedUnparking => "A return-move intent was not committed; retry Unpark for safe reconciliation.",
                WorkspaceRecoveryKind.MissingParkedWorktree => "The parked path is missing, unregistered or points to a reparse location.",
                WorkspaceRecoveryKind.UnverifiableParkedWorktree => "Git registration for the parked tree could not be verified.",
                WorkspaceRecoveryKind.PendingSlotReleasePreparation => "A prepared release retains exclusive slot ownership.",
                _ => "Unknown entry in app-owned recovery storage.",
            }
        ));
    }

    private static IReadOnlyList<string> BoundedEntries(string root)
    {
        // Fail rather than silently omit entries, which could wrongly imply safe cleanup.
        List<string> entries = [];
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            if (entries.Count >= MaxEntriesPerRoot)
            {
                throw new InvalidOperationException(
                    "Recovery inventory exceeds its safe entry limit."
                );
            }

            entries.Add(path);
        }

        return entries;
    }

    private static bool IsReparse(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private bool IsUnder(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(
            prefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );
    }
}
