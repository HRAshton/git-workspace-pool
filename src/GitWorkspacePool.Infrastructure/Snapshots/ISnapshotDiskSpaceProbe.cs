namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Probes free space on the filesystem that will hold a snapshot or recovery worktree.
/// </summary>
public interface ISnapshotDiskSpaceProbe
{
    /// <summary>
    /// Reads currently available bytes on the target volume. The value is only advisory.
    /// </summary>
    /// <param name="targetPath">Absolute path to planned storage.</param>
    /// <returns>Available bytes, or an exception if the volume cannot be inspected.</returns>
    long AvailableBytes(string targetPath);
}
