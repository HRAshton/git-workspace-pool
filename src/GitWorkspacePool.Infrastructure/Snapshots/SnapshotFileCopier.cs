namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Streams one file with cancellation and optional post-write failure injection.
/// A failure retains the partially written destination for recovery inspection.
/// </summary>
internal static class SnapshotFileCopier
{
    public static async Task CopyAsync(
        string source,
        string destination,
        FileMode destinationMode,
        SnapshotCopyPhase phase,
        ISnapshotCopyProgressSink? progress,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(source);
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(destination);

        await using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.SequentialScan
        );
        await using FileStream output = new(
            destination,
            destinationMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.WriteThrough
        );

        byte[] buffer = new byte[65536];
        long bytesWritten = 0;
        int count;

        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            bytesWritten = checked(bytesWritten + count);
            if (progress is not null)
            {
                await progress.AfterBytesWrittenAsync(phase, bytesWritten, cancellationToken);
            }
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}
