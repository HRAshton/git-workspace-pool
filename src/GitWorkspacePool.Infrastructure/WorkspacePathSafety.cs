namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Constrains workspace payload paths to literal directories, never junctions or symlink escapes.
/// </summary>
internal static class WorkspacePathSafety
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Workspace path must be relative.");
        }

        string[] segments = ValidateSegments(relative);

        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine([fullRoot, .. segments]));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Workspace path escapes its workspace root.");
        }

        return fullPath;
    }

    /// <summary>
    /// Rejects ambiguous Windows aliases even when the path was supplied by Git or
    /// a manifest. Multiple spellings of one on-disk path are never safe to restore.
    /// </summary>
    public static string NormalizeRelative(string relative)
    {
        return string.Join('/', ValidateSegments(relative));
    }

    private static string[] ValidateSegments(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Workspace path must be relative.");
        }

        string[] segments = relative.Replace('\\', '/').Split('/');

        foreach (string segment in segments)
        {
            if (
                string.IsNullOrEmpty(segment)
                || segment is "." or ".."
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Contains(':')
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || segment.Any(character => character < ' ')
                || IsWindowsDeviceName(segment)
            )
            {
                throw new InvalidOperationException(
                    "Workspace contains an unsafe or ambiguous Windows path."
                );
            }
        }

        return segments;
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal)
                || name.StartsWith("LPT", StringComparison.Ordinal))
            && (name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    public static void EnsureSeparated(string first, string second)
    {
        string firstPrefix = first.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string secondPrefix = second.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (first.Equals(second, comparison) || second.StartsWith(firstPrefix, comparison) || first.StartsWith(secondPrefix, comparison))
            throw new InvalidOperationException("Source clone and workspace root must be disjoint.");
    }

    public static void EnsureNoLinksInAncestors(string path, ISet<string>? inspected = null)
    {
        string current = Path.GetFullPath(path);

        while (true)
        {
            if (inspected is not null && !inspected.Add(current)) return;
            // File.Exists/Directory.Exists can both be false for a dangling
            // symlink. Query attributes directly to identify the link itself.
            try
            {
                EnsureNotReparse(current);
            }
            catch (FileNotFoundException)
            {
                // A nonexistent component is safe only after its parents are checked.
            }
            catch (DirectoryNotFoundException)
            {
                // Continue walking towards a potentially linked ancestor.
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
                "Workspace refused a symlink or junction to avoid escaping its root."
            );
        }
    }
}
