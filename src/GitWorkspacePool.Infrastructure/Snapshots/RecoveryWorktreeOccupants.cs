namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Detects unexpected filesystem occupants that Git status may hide inside
/// ignored directories in an isolated snapshot recovery worktree.
/// </summary>
internal static class RecoveryWorktreeOccupants
{
    private const int MaxEntries = 100000;

    public static HashSet<string> Capture(
        string recoveryRoot,
        CancellationToken cancellationToken
    )
    {
        string root = Path.GetFullPath(recoveryRoot);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(root);
        HashSet<string> found = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        Stack<string> directories = new();
        directories.Push(root);
        int inspected = 0;

        while (directories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > MaxEntries)
                {
                    throw new InvalidOperationException(
                        "Recovery filesystem verification exceeded its entry limit."
                    );
                }

                // A linked worktree's root .git gitfile belongs to Git itself.
                // All other .git entries are unexpected and must never be
                // traversed or accepted as snapshot payloads.
                if (directory == root
                    && Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = WorkspaceSnapshotPathPolicy.NormalizeRelative(
                    Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/')
                );
                if (!found.Add(relative))
                {
                    throw new InvalidOperationException(
                        "Recovery filesystem contains aliased or duplicate paths."
                    );
                }

                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Never traverse symlinks or junctions into another tree.
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(child);
                }
            }
        }

        return found;
    }

    public static void RejectUnexpected(
        string recoveryRoot,
        HashSet<string> allowed,
        CancellationToken cancellationToken
    )
    {
        foreach (string relative in Capture(recoveryRoot, cancellationToken))
        {
            if (!allowed.Contains(relative))
            {
                throw new InvalidOperationException(
                    "Recovery worktree contains an unexpected filesystem occupant; "
                        + "the isolated worktree and user files were retained."
                );
            }
        }
    }
}
