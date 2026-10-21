namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Uses the operating system's reported volume availability, without allocating files.
/// </summary>
public sealed class SystemSnapshotDiskSpaceProbe : ISnapshotDiskSpaceProbe
{
    /// <inheritdoc/>
    public long AvailableBytes(string targetPath)
    {
        string absolute = Path.GetFullPath(targetPath);
        string root = Path.GetPathRoot(absolute)
            ?? throw new IOException("Unable to determine snapshot storage volume.");

        DriveInfo drive = new(root);
        if (!drive.IsReady)
        {
            throw new IOException("Snapshot storage volume is unavailable.");
        }

        return drive.AvailableFreeSpace;
    }
}
