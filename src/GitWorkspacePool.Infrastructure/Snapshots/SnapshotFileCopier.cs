using System.Security.Cryptography;

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
        CancellationToken cancellationToken,
        long maxBytes = long.MaxValue,
        string? expectedDestinationSha256 = null
    )
    {
        if (maxBytes < 0)
        {
            throw new InvalidOperationException("Snapshot file byte budget is exhausted.");
        }

        if (expectedDestinationSha256 is not null && destinationMode != FileMode.Open)
        {
            throw new ArgumentException(
                "Verified existing files must be opened without truncation.",
                nameof(destinationMode)
            );
        }

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
            expectedDestinationSha256 is null ? FileAccess.Write : FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.WriteThrough
        );

        if (expectedDestinationSha256 is not null)
        {
            // Keep the same exclusive Windows file handle across verification
            // and truncation, so a competing editor cannot replace its bytes.
            string actual = Convert.ToHexString(
                await SHA256.HashDataAsync(output, cancellationToken)
            );
            if (!actual.Equals(expectedDestinationSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Recovery checkout file changed since clean validation; "
                        + "intervening user data was not overwritten."
                );
            }

            output.Position = 0;
            output.SetLength(0);
        }

        byte[] buffer = new byte[65536];
        long bytesWritten = 0;
        int count;

        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (count > maxBytes - bytesWritten)
            {
                throw new InvalidOperationException(
                    "Snapshot file exceeds the remaining byte budget during copying."
                );
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            bytesWritten += count;
            if (progress is not null)
            {
                await progress.AfterBytesWrittenAsync(phase, bytesWritten, cancellationToken);
            }
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }
}
