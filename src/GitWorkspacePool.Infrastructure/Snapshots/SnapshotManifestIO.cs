using System.Text.Json;

namespace GitWorkspacePool.Infrastructure.Snapshots;

/// <summary>
/// Bounds untrusted on-disk snapshot manifest reads and published manifests.
/// </summary>
internal static class SnapshotManifestIO
{
    // Match the pre-existing recovery diagnostic limit. Export must honor it,
    // otherwise the app can publish snapshots diagnostics cannot verify.
    public const int MaxBytes = 16 * 1024 * 1024;

    public static void RequireWithinLimit(string path)
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidOperationException(
                "Snapshot manifest exceeds the 16 MiB size limit."
            );
        }
    }

    public static async Task<WorkspaceSnapshotManifest> ReadAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshotPathPolicy.EnsureNoLinksInAncestors(path);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );

        if (stream.Length > MaxBytes)
        {
            throw new InvalidOperationException(
                "Snapshot manifest exceeds the 16 MiB size limit."
            );
        }

        // Use the same handle for measuring and reading. A concurrent
        // enlargement cannot silently bypass the pre-deserialization cap.
        byte[] contents = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(contents, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidOperationException(
                "Snapshot manifest changed during bounded reading.",
                exception
            );
        }

        byte[] extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken) != 0)
        {
            throw new InvalidOperationException(
                "Snapshot manifest changed or grew during bounded reading."
            );
        }

        return JsonSerializer.Deserialize<WorkspaceSnapshotManifest>(contents)
            ?? throw new InvalidOperationException("Snapshot manifest is empty.");
    }
}
