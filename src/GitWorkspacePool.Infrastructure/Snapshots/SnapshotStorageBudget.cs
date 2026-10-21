namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Conservative preflight only: files can still exhaust disk space mid-operation.
/// </summary>
internal static class SnapshotStorageBudget
{
    private const long ReserveBytes = 64L * 1024 * 1024;

    public static void RequireSpace(
        ISnapshotDiskSpaceProbe probe,
        string targetPath,
        long minimumPayloadBytes
    )
    {
        if (minimumPayloadBytes < 0)
        {
            throw new InvalidOperationException("Snapshot payload estimate was invalid.");
        }

        long available = probe.AvailableBytes(targetPath);

        if (available < ReserveBytes || minimumPayloadBytes > available - ReserveBytes)
        {
            throw new IOException(
                "Insufficient available storage for a safe snapshot/restore preflight. "
                    + "No destructive recovery action was performed."
            );
        }
    }
}
