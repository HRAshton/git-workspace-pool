using GitWorkspacePool.Infrastructure.Snapshots;

namespace GitWorkspacePool.Infrastructure;

/// <summary>
/// Read-only bounded disk inventory of app-owned snapshot and recovery evidence.
/// It never deletes or alters user files and does not follow symlinks or junctions.
/// </summary>
public sealed class WorkspaceLocalStorageInspector
{
    private const int DefaultMaxEntries = 100000;
    private const int MaxDepth = 128;
    private readonly string _stateRoot;
    private readonly int _maxEntries;

    /// <summary>Creates an inspector confined to the app's local state root.</summary>
    public WorkspaceLocalStorageInspector(string stateRoot, int maxEntries = DefaultMaxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        if (maxEntries is < 1 or > DefaultMaxEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _stateRoot = Path.GetFullPath(stateRoot);
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Computes logical file sizes for app-owned snapshots and isolated restores.
    /// Reports the two stores separately. A failed scan is not a zero-byte finding.
    /// </summary>
    public WorkspaceLocalStorageUsage Inspect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(_stateRoot);
        StorageAreaUsage snapshots = Scan("snapshots", cancellationToken);
        StorageAreaUsage restores = Scan("recovered", cancellationToken);
        return new WorkspaceLocalStorageUsage(snapshots, restores);
    }

    private StorageAreaUsage Scan(string folder, CancellationToken cancellationToken)
    {
        string root = Path.Combine(_stateRoot, folder);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(root);

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(root);
        }
        catch (FileNotFoundException)
        {
            return new StorageAreaUsage(0, 0, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new StorageAreaUsage(0, 0, 0);
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException(
                "App-owned storage root is occupied by a file; usage is unknown."
            );
        }

        long totalBytes = 0;
        int files = 0;
        int directories = 0;
        int inspected = 0;
        Stack<(string Path, int Depth)> pending = new();
        pending.Push((root, 0));

        while (pending.TryPop(out (string Path, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspected > _maxEntries)
                {
                    throw new InvalidOperationException(
                        "Storage evidence inventory exceeds its bounded entry limit."
                    );
                }

                FileAttributes entry = File.GetAttributes(path);
                if ((entry & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Storage evidence contains a symlink or junction; usage is unknown."
                    );
                }

                if ((entry & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= MaxDepth)
                    {
                        throw new InvalidOperationException(
                            "Storage evidence tree exceeds its bounded directory depth."
                        );
                    }

                    directories++;
                    pending.Push((path, current.Depth + 1));
                }
                else
                {
                    totalBytes = checked(totalBytes + new FileInfo(path).Length);
                    files++;
                }
            }
        }

        return new StorageAreaUsage(totalBytes, files, directories);
    }
}

/// <summary>Logical byte count and enumerated occupant count of one app-owned store.</summary>
public sealed record StorageAreaUsage(long Bytes, int Files, int Directories);

/// <summary>Separated non-authoritative app-owned storage totals (never an eviction grant).</summary>
public sealed record WorkspaceLocalStorageUsage(
    StorageAreaUsage Snapshots,
    StorageAreaUsage Recovered
);
