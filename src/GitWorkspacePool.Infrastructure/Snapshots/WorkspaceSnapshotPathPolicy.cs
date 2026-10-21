namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Constrains snapshot payload paths to literal directories, never junctions or symlink escapes.
/// </summary>
internal static class WorkspaceSnapshotPathPolicy
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Snapshot path must be relative.");
        }

        string[] segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (
            segments.Length == 0
            || segments.Any(segment =>
                segment is "." or ".."
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Contains(':')
            )
        )
        {
            throw new InvalidOperationException("Snapshot contains an unsafe Git path.");
        }

        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Snapshot path escapes its workspace root.");
        }

        return fullPath;
    }

    public static void EnsureSeparated(string workspaceRoot, string snapshotRoot)
    {
        string workspacePrefix = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string snapshotPrefix = snapshotRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            workspaceRoot.Equals(snapshotRoot, comparison)
            || snapshotRoot.StartsWith(workspacePrefix, comparison)
            || workspaceRoot.StartsWith(snapshotPrefix, comparison)
        )
        {
            throw new InvalidOperationException(
                "Snapshot storage must be separate from the managed worktree."
            );
        }
    }

    public static void EnsureNoLinksInAncestors(string path)
    {
        string current = Path.GetFullPath(path);

        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                EnsureNotReparse(current);
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = parent;
        }
    }

    public static void EnsureNotReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Snapshot refused a symlink or junction to avoid escaping its root."
            );
        }
    }
}
