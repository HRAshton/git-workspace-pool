using GitWorkspacePool.Core.Application;
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
        IWorkspaceGit git
    )
    {
        _snapshotRoot = Path.GetFullPath(snapshotRoot);
        _recoveryRoot = Path.GetFullPath(recoveryRoot);
        _journal = journal;
        _git = git;
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

        List<WorkspaceRecoveryFinding> findings = [];
        HashSet<string> seen = new(_paths);

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

                try
                {
                    bool registered = await _git.IsRegisteredWorktreeAsync(
                        record.Session.SourcePath,
                        path,
                        cancellationToken
                    );
                    Add(
                        findings,
                        registered
                            ? WorkspaceRecoveryKind.UnmanagedRecoveryWorktree
                            : WorkspaceRecoveryKind.UnregisteredRecoveryDirectory,
                        path,
                        snapshotId
                    );
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException
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

        return findings;
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
                WorkspaceRecoveryKind.UnverifiableRecoveryDirectory => "Git registration could not be confirmed.",
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
